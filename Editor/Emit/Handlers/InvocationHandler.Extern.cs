using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
    // ── Extern Method Call ──

    string EmitExternMethodCall(IInvocationOperation op, IMethodSymbol target)
    {
        // Generic GetComponent<T>() / GetComponentInChildren<T>() / GetComponentsInChildren<T>() etc.
        // Udon VM uses non-generic form with typeof(T) parameter.
        if (target.IsGenericMethod && target.Name.StartsWith("GetComponent")
            && target.TypeArguments.Length == 1)
        {
            return EmitGetComponentGeneric(op, target);
        }

        // Save hint and clear it while evaluating sub-expressions
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;

        string instanceId = null;
        if (!target.IsStatic)
        {
            if (op.Instance is IInstanceReferenceOperation)
                instanceId = _vars.DeclareThisOnce(GetUdonType(target.ContainingType));
            else if (op.Instance != null)
                instanceId = VisitExpression(op.Instance);
        }

        var argIds = new string[op.Arguments.Length];
        for (int i = 0; i < op.Arguments.Length; i++)
            argIds[i] = VisitExpression(op.Arguments[i].Value);

        // Now emit all PUSHes contiguously
        if (instanceId != null)
            _module.AddPush(instanceId);
        foreach (var argId in argIds)
            _module.AddPush(argId);

        // Restore hint and consume for result
        _ctx.TargetHint = savedHint;
        string resultId = null;
        if (!target.ReturnsVoid)
        {
            var returnType = GetUdonType(target.ReturnType);
            resultId = ConsumeTargetHintOrTemp(returnType);
            _module.AddPush(resultId);
        }

        // Extern signature
        var sig = BuildExternCallSignature(target, op.Instance?.Type);
        AddExternChecked(sig);

        return resultId;
    }

    // ── GetComponent<T> ──

    string EmitGetComponentGeneric(IInvocationOperation op, IMethodSymbol target)
    {
        var typeArg = target.TypeArguments[0];
        return ExternResolver.IsUdonSharpBehaviour(typeArg) ? EmitGetComponentShim(op, target) : EmitGetComponentExtern(op, target);
    }

    // Existing logic for Unity Component types (Transform, Collider, etc.)
    // Uses the __T / __TArray generic extern form (matches UdonSharp behavior).
    string EmitGetComponentExtern(IInvocationOperation op, IMethodSymbol target)
    {
        // Save hint and clear it while evaluating sub-expressions
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;

        // Evaluate instance and arguments first (avoid interleaved PUSH/EXTERN)
        string instanceId = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceId = _vars.DeclareThisOnce("UnityEngineTransform");
        else if (op.Instance != null)
            instanceId = VisitExpression(op.Instance);

        // Evaluate explicit arguments (e.g., GetComponentInChildren<T>(bool includeInactive))
        var argIds = new string[op.Arguments.Length];
        for (int i = 0; i < op.Arguments.Length; i++)
            argIds[i] = VisitExpression(op.Arguments[i].Value);

        // __T externs use UnityEngineComponent as containing type.
        // If instance is a GameObject (not a Component), get .transform first.
        instanceId = EnsureComponentInstance(op.Instance, instanceId);

        // Push instance
        if (instanceId != null)
            _module.AddPush(instanceId);

        // Push explicit arguments FIRST, then SystemType (matches UdonSharp push order for __T externs)
        foreach (var argId in argIds)
            _module.AddPush(argId);

        // Push typeof(T) as SystemType constant (after explicit args).
        // IUdonEventReceiver → VRCUdonUdonBehaviour: the Udon type registry doesn't have
        // IUdonEventReceiver as a SystemType constant. UdonBehaviour implements the interface,
        // so using VRCUdonUdonBehaviour as the type token gives the same GetComponent result.
        // ExternResolver can't handle this because it operates on extern signatures, not type tokens.
        var typeArgName = GetUdonType(target.TypeArguments[0]);
        var typeConstValue = typeArgName == "VRCUdonCommonInterfacesIUdonEventReceiver" ? "VRCUdonUdonBehaviour" : typeArgName;
        var typeConstId = _vars.DeclareConst("SystemType", typeConstValue);
        _module.AddPush(typeConstId);

        // Result type — typed as T for __T externs
        var isPlural = target.Name.StartsWith("GetComponents");
        var typeArgUdon = GetUdonType(target.TypeArguments[0]);
        string tempType;
        if (isPlural && typeArgUdon == "VRCUdonCommonInterfacesIUdonEventReceiver")
            tempType = "UnityEngineComponentArray";
        else
            tempType = isPlural ? $"{typeArgUdon}Array" : typeArgUdon;
        // Restore hint and consume for result
        _ctx.TargetHint = savedHint;
        var resultId = ConsumeTargetHintOrTemp(tempType);
        _module.AddPush(resultId);

        // Build extern name with __T form
        const string containingType = "UnityEngineComponent";
        var methodName = target.Name;
        var retPlaceholder = isPlural ? "__TArray" : "__T";
        var explicitParams = target.OriginalDefinition.Parameters;
        if (explicitParams.Length > 0)
        {
            var paramStr = string.Join("_", explicitParams.Select(p => GetUdonType(p.Type)));
            AddExternChecked($"{containingType}.__{methodName}__{paramStr}{retPlaceholder}");
        }
        else
        {
            AddExternChecked($"{containingType}.__{methodName}{retPlaceholder}");
        }

        return resultId;
    }

    // ── GetComponent<T> USB Shim ──
    // Inline shim for USB-derived types: GetComponents(typeof(UdonBehaviour)) + __refl_typeid filter

    string EmitGetComponentShim(IInvocationOperation op, IMethodSymbol target)
    {
        var isSingular = !target.Name.StartsWith("GetComponents");

        // Evaluate instance
        string instanceId = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceId = _vars.DeclareThisOnce("UnityEngineTransform");
        else if (op.Instance != null)
            instanceId = VisitExpression(op.Instance);

        // Evaluate explicit arguments (bool includeInactive)
        var argIds = new string[op.Arguments.Length];
        for (int i = 0; i < op.Arguments.Length; i++)
            argIds[i] = VisitExpression(op.Arguments[i].Value);

        // If instance is a GameObject, get .transform for Component-typed extern
        instanceId = EnsureComponentInstance(op.Instance, instanceId);

        // Determine which non-generic GetComponents extern to call
        var fetchExtern = ResolveShimFetchExtern(target.Name, op.Arguments.Length > 0);

        // Push instance + typeof(UdonBehaviour) + optional args
        if (instanceId != null)
            _module.AddPush(instanceId);
        var udonBehaviourType = _vars.DeclareConst("SystemType", "VRCUdonUdonBehaviour");
        _module.AddPush(udonBehaviourType);
        foreach (var argId in argIds)
            _module.AddPush(argId);

        // Call GetComponents → ComponentArray
        var allComponents = _vars.DeclareTemp("UnityEngineComponentArray");
        _module.AddPush(allComponents);
        AddExternChecked(fetchExtern);

        // Compute target type ID at compile time
        var targetTypeName = target.TypeArguments[0].ToDisplayString();
        long targetTypeId = UasmEmitter.ComputeTypeId(targetTypeName);
        var targetIdConst = _vars.DeclareConst("SystemInt64", targetTypeId.ToString());

        // Inheritance: if derived USB types exist, use __refl_typeids + Array.IndexOf
        bool useTypeIds = HasInheritedUsbTypes(target.TypeArguments[0]);
        var reflKeyConst = useTypeIds
            ? _vars.DeclareConst("SystemString", "__refl_typeids")
            : _vars.DeclareConst("SystemString", "__refl_typeid");

        return isSingular ? EmitShimSingular(allComponents, targetIdConst, reflKeyConst, useTypeIds) : EmitShimPlural(allComponents, targetIdConst, reflKeyConst, useTypeIds);
    }

    /// <summary>
    /// If the instance is a GameObject, emit .transform to get a Component-typed instance.
    /// GetComponent __T externs and shim GetComponents externs use UnityEngineComponent
    /// as containing type, which requires the instance to be Component-typed in the heap.
    /// </summary>
    string EnsureComponentInstance(IOperation instanceOp, string instanceId)
    {
        if (instanceId == null || instanceOp == null)
            return instanceId;
        var instanceUdon = GetUdonType(instanceOp.Type);
        if (instanceUdon != "UnityEngineGameObject")
            return instanceId;
        var transformId = _vars.DeclareTemp("UnityEngineTransform");
        _module.AddPush(instanceId);
        _module.AddPush(transformId);
        AddExternChecked("UnityEngineGameObject.__get_transform__UnityEngineTransform");
        return transformId;
    }

    bool HasInheritedUsbTypes(ITypeSymbol targetType)
    {
        foreach (var kvp in _planner.AllLayouts)
        {
            var typeSymbol = kvp.Key;
            if (SymbolEqualityComparer.Default.Equals(typeSymbol, targetType))
                continue;
            var current = typeSymbol.BaseType;
            while (current != null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, targetType))
                    return true;
                current = current.BaseType;
            }
        }
        return false;
    }

    static string ResolveShimFetchExtern(string methodName, bool hasBoolArg)
    {
        // Map singular→plural, all use non-generic SystemType overload
        var baseName = methodName;
        if (baseName == "GetComponent")
            baseName = "GetComponents";
        else if (baseName == "GetComponentInChildren")
            baseName = "GetComponentsInChildren";
        else if (baseName == "GetComponentInParent")
            baseName = "GetComponentsInParent";

        if (hasBoolArg)
            return $"UnityEngineComponent.__{baseName}__SystemType_SystemBoolean__UnityEngineComponentArray";
        return $"UnityEngineComponent.__{baseName}__SystemType__UnityEngineComponentArray";
    }

    string EmitShimSingular(string allComponents, string targetIdConst, string reflKeyConst, bool useTypeIds)
    {
        // Get array length
        var lenId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(allComponents);
        _module.AddPush(lenId);
        AddExternChecked("UnityEngineComponentArray.__get_Length__SystemInt32");

        // Loop index
        var idxId = _vars.DeclareTemp("SystemInt32");
        var zeroConst = _vars.DeclareConst("SystemInt32", "0");
        _module.AddCopy(zeroConst, idxId);

        // Result (null initially)
        var resultId = _vars.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");

        var loopLabel = _module.DefineLabel("__gc_shim_loop");
        var nextLabel = _module.DefineLabel("__gc_shim_next");
        var endLabel = _module.DefineLabel("__gc_shim_end");

        _module.MarkLabel(loopLabel);

        // if (idx >= len) → end
        var condId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(idxId);
        _module.AddPush(lenId);
        _module.AddPush(condId);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _module.AddPush(condId);
        _module.AddJumpIfFalse(endLabel);

        // element = allComponents[idx]
        var elementId = _vars.DeclareTemp("UnityEngineComponent");
        _module.AddPush(allComponents);
        _module.AddPush(idxId);
        _module.AddPush(elementId);
        AddExternChecked("UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent");

        // Cast Component → IUdonEventReceiver (COPY to differently-typed temp)
        var behaviourId = _vars.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");
        _module.AddCopy(elementId, behaviourId);

        // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
        var idValueId = _vars.DeclareTemp("SystemObject");
        _module.AddPush(behaviourId);
        _module.AddPush(reflKeyConst);
        _module.AddPush(idValueId);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");

        // Null check: if (idValue == null) → next
        var nullConst = _vars.DeclareConst("SystemObject", "null");
        var notNullId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(idValueId);
        _module.AddPush(nullConst);
        _module.AddPush(notNullId);
        AddExternChecked("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean");
        _module.AddPush(notNullId);
        _module.AddJumpIfFalse(nextLabel);

        if (useTypeIds)
        {
            EmitTypeIdsArrayCheck(idValueId, targetIdConst, nextLabel);
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdId = _vars.DeclareTemp("SystemInt64");
            _module.AddPush(idValueId);
            _module.AddPush(typeIdId);
            AddExternChecked("SystemConvert.__ToInt64__SystemObject__SystemInt64");

            // if (typeId != targetId) → next
            var matchId = _vars.DeclareTemp("SystemBoolean");
            _module.AddPush(typeIdId);
            _module.AddPush(targetIdConst);
            _module.AddPush(matchId);
            AddExternChecked("SystemInt64.__op_Equality__SystemInt64_SystemInt64__SystemBoolean");
            _module.AddPush(matchId);
            _module.AddJumpIfFalse(nextLabel);
        }

        // Match! result = behaviour
        _module.AddCopy(behaviourId, resultId);
        _module.AddJump(endLabel);

        // next: idx++
        _module.MarkLabel(nextLabel);
        var oneConst = _vars.DeclareConst("SystemInt32", "1");
        var nextIdxId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(idxId);
        _module.AddPush(oneConst);
        _module.AddPush(nextIdxId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _module.AddCopy(nextIdxId, idxId);
        _module.AddJump(loopLabel);

        _module.MarkLabel(endLabel);
        return resultId;
    }

    string EmitShimPlural(string allComponents, string targetIdConst, string reflKeyConst, bool useTypeIds)
    {
        // Get array length
        var lenId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(allComponents);
        _module.AddPush(lenId);
        AddExternChecked("UnityEngineComponentArray.__get_Length__SystemInt32");

        var zeroConst = _vars.DeclareConst("SystemInt32", "0");
        var oneConst = _vars.DeclareConst("SystemInt32", "1");

        // === Pass 1: Count matches ===
        var countId = _vars.DeclareTemp("SystemInt32");
        _module.AddCopy(zeroConst, countId);
        var idx1Id = _vars.DeclareTemp("SystemInt32");
        _module.AddCopy(zeroConst, idx1Id);

        var count_loop = _module.DefineLabel("__gc_count_loop");
        var count_next = _module.DefineLabel("__gc_count_next");
        var count_end = _module.DefineLabel("__gc_count_end");
        _module.MarkLabel(count_loop);

        // if (idx >= len) → count_end
        var cond1 = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(idx1Id);
        _module.AddPush(lenId);
        _module.AddPush(cond1);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _module.AddPush(cond1);
        _module.AddJumpIfFalse(count_end);

        EmitShimTypeCheck(allComponents, idx1Id, reflKeyConst, targetIdConst, count_next, useTypeIds);

        // countId++
        var newCount = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(countId);
        _module.AddPush(oneConst);
        _module.AddPush(newCount);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _module.AddCopy(newCount, countId);

        // count_next: idx1++
        _module.MarkLabel(count_next);
        var nextIdx1 = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(idx1Id);
        _module.AddPush(oneConst);
        _module.AddPush(nextIdx1);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _module.AddCopy(nextIdx1, idx1Id);
        _module.AddJump(count_loop);
        _module.MarkLabel(count_end);

        // === Allocate result array ===
        var resultId = _vars.DeclareTemp("UnityEngineComponentArray");
        _module.AddPush(countId);
        _module.AddPush(resultId);
        AddExternChecked("UnityEngineComponentArray.__ctor__SystemInt32__UnityEngineComponentArray");

        // === Pass 2: Fill result array ===
        var idx2Id = _vars.DeclareTemp("SystemInt32");
        _module.AddCopy(zeroConst, idx2Id);
        var writeIdx = _vars.DeclareTemp("SystemInt32");
        _module.AddCopy(zeroConst, writeIdx);

        var fill_loop = _module.DefineLabel("__gc_fill_loop");
        var fill_next = _module.DefineLabel("__gc_fill_next");
        var fill_end = _module.DefineLabel("__gc_fill_end");
        _module.MarkLabel(fill_loop);

        // if (idx >= len) → fill_end
        var cond2 = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(idx2Id);
        _module.AddPush(lenId);
        _module.AddPush(cond2);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _module.AddPush(cond2);
        _module.AddJumpIfFalse(fill_end);

        var matchBeh = EmitShimTypeCheck(allComponents, idx2Id, reflKeyConst, targetIdConst, fill_next, useTypeIds);

        // result[writeIdx] = element
        _module.AddPush(resultId);
        _module.AddPush(writeIdx);
        _module.AddPush(matchBeh);
        AddExternChecked("UnityEngineComponentArray.__Set__SystemInt32_UnityEngineComponent__SystemVoid");

        // writeIdx++
        var newWrite = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(writeIdx);
        _module.AddPush(oneConst);
        _module.AddPush(newWrite);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _module.AddCopy(newWrite, writeIdx);

        // fill_next: idx2++
        _module.MarkLabel(fill_next);
        var nextIdx2 = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(idx2Id);
        _module.AddPush(oneConst);
        _module.AddPush(nextIdx2);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _module.AddCopy(nextIdx2, idx2Id);
        _module.AddJump(fill_loop);
        _module.MarkLabel(fill_end);

        return resultId;
    }

    // Shared type-check logic for shim loops. Returns the behaviourId for matched element.
    string EmitShimTypeCheck(string allComponents, string idxId, string reflKeyConst, string targetIdConst, int nextLabel, bool useTypeIds)
    {
        // element = allComponents[idx]
        var elementId = _vars.DeclareTemp("UnityEngineComponent");
        _module.AddPush(allComponents);
        _module.AddPush(idxId);
        _module.AddPush(elementId);
        AddExternChecked("UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent");

        // Cast Component → IUdonEventReceiver
        var behaviourId = _vars.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");
        _module.AddCopy(elementId, behaviourId);

        // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
        var idValueId = _vars.DeclareTemp("SystemObject");
        _module.AddPush(behaviourId);
        _module.AddPush(reflKeyConst);
        _module.AddPush(idValueId);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");

        // Null check
        var nullConst = _vars.DeclareTemp("SystemObject");
        var notNullId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(idValueId);
        _module.AddPush(nullConst);
        _module.AddPush(notNullId);
        AddExternChecked("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean");
        _module.AddPush(notNullId);
        _module.AddJumpIfFalse(nextLabel);

        if (useTypeIds)
        {
            EmitTypeIdsArrayCheck(idValueId, targetIdConst, nextLabel);
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdId = _vars.DeclareTemp("SystemInt64");
            _module.AddPush(idValueId);
            _module.AddPush(typeIdId);
            AddExternChecked("SystemConvert.__ToInt64__SystemObject__SystemInt64");

            // if (typeId != targetId) → next
            var matchId = _vars.DeclareTemp("SystemBoolean");
            _module.AddPush(typeIdId);
            _module.AddPush(targetIdConst);
            _module.AddPush(matchId);
            AddExternChecked("SystemInt64.__op_Equality__SystemInt64_SystemInt64__SystemBoolean");
            _module.AddPush(matchId);
            _module.AddJumpIfFalse(nextLabel);
        }

        return behaviourId;
    }

    // Array.IndexOf(__refl_typeids, targetId) != -1
    void EmitTypeIdsArrayCheck(string idValueId, string targetIdConst, int nextLabel)
    {
        // Box Int64 → Object for Array.IndexOf(Array, Object)
        var targetIdObj = _vars.DeclareTemp("SystemObject");
        _module.AddCopy(targetIdConst, targetIdObj);

        var indexResult = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(idValueId);    // __refl_typeids as Object (actually Int64[])
        _module.AddPush(targetIdObj);
        _module.AddPush(indexResult);
        AddExternChecked("SystemArray.__IndexOf__SystemArray_SystemObject__SystemInt32");

        var negOneConst = _vars.DeclareConst("SystemInt32", "-1");
        var foundId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(indexResult);
        _module.AddPush(negOneConst);
        _module.AddPush(foundId);
        AddExternChecked("SystemInt32.__op_Inequality__SystemInt32_SystemInt32__SystemBoolean");
        _module.AddPush(foundId);
        _module.AddJumpIfFalse(nextLabel);
    }

    // ── Interface Call ──

    string EmitInterfaceCall(IInvocationOperation op, IMethodSymbol target)
    {
        // Use LayoutPlanner to get the interface's canonical naming
        var ifaceType = target.ContainingType as INamedTypeSymbol;
        MethodLayout ifaceMl = null;
        if (ifaceType != null)
        {
            var ifaceLayout = _planner.GetLayout(ifaceType);
            ifaceLayout.Methods.TryGetValue(target, out ifaceMl);
        }
        if (ifaceMl == null)
            throw new System.InvalidOperationException(
                $"Cannot resolve interface method layout for '{target.ContainingType?.Name ?? "(unknown)"}.{target.Name}'.");

        var instanceId = VisitExpression(op.Instance);

        // SetProgramVariable for each argument
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var argId = VisitExpression(op.Arguments[i].Value);
            var paramName = ifaceMl.ParamIds[i];
            var paramNameConst = _vars.DeclareConst("SystemString", paramName);
            _module.AddPush(instanceId);
            _module.AddPush(paramNameConst);
            _module.AddPush(argId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
        }

        // SendCustomEvent with interface export name
        var exportName = ifaceMl.ExportName;
        var eventNameConst = _vars.DeclareConst("SystemString", exportName);
        _module.AddPush(instanceId);
        _module.AddPush(eventNameConst);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");

        // GetProgramVariable for return value
        if (!target.ReturnsVoid)
        {
            var retName = ifaceMl.ReturnId;
            var retNameConst = _vars.DeclareConst("SystemString", retName);
            var returnType = GetUdonType(target.ReturnType);
            var resultId = _vars.DeclareTemp(returnType);
            _module.AddPush(instanceId);
            _module.AddPush(retNameConst);
            _module.AddPush(resultId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
            return resultId;
        }

        return null;
    }

    // ── Cross-Class Call ──

    string EmitCrossClassCall(IInvocationOperation op, IMethodSymbol target)
    {
        var (exportName, paramIds, retId) = GetCalleeLayout(target);
        var instanceId = VisitExpression(op.Instance);

        // SetProgramVariable for each argument
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var argId = VisitExpression(op.Arguments[i].Value);
            var paramNameConst = _vars.DeclareConst("SystemString", paramIds[i]);

            _module.AddPush(instanceId);
            _module.AddPush(paramNameConst);
            _module.AddPush(argId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
        }

        // SendCustomEvent
        var eventNameConst = _vars.DeclareConst("SystemString", exportName);
        _module.AddPush(instanceId);
        _module.AddPush(eventNameConst);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");

        // GetProgramVariable for return value
        if (!target.ReturnsVoid && retId != null)
        {
            var retNameConst = _vars.DeclareConst("SystemString", retId);
            var returnType = GetUdonType(target.ReturnType);
            var resultId = _vars.DeclareTemp(returnType);

            _module.AddPush(instanceId);
            _module.AddPush(retNameConst);
            _module.AddPush(resultId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");

            return resultId;
        }

        return null;
    }

    // ── User Method Call ──

    string EmitUserMethodCall(IInvocationOperation op, IMethodSymbol target, int targetLabel)
    {
        // Save hint and clear it while evaluating sub-expressions
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;

        var idx = _methodIndices[target];
        var paramIds = _methodParamVarIds[target];

        // Save current method's parameter values before any user method call.
        // This protects against mutual recursion (A→B→A) corrupting A's params,
        // not just self-recursion. Overhead is 2N COPY per call (N = param count).
        string[] savedCurrentParams = null;
        string[] currentParamIds = null;
        if (_currentMethod != null
            && _methodParamVarIds.TryGetValue(_currentMethod, out currentParamIds)
            && currentParamIds.Length > 0)
        {
            savedCurrentParams = new string[currentParamIds.Length];
            for (int i = 0; i < currentParamIds.Length; i++)
            {
                var paramType = _vars.GetDeclaredType(currentParamIds[i]);
                savedCurrentParams[i] = _vars.DeclareTemp(paramType);
                _module.AddCopy(currentParamIds[i], savedCurrentParams[i]);
            }
        }

        // Copy arguments to param vars
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var param = target.Parameters[i];
            var argOp = op.Arguments[i].Value;

            // Delegate parameter with lambda arg: hoist with convention vars
            if (_delegateParamConventions.TryGetValue((idx, param.Ordinal), out var convention)
                && UnwrapLambdaFromArg(argOp, out var lambda))
            {
                HoistLambdaForDelegateParam(lambda, convention);
                var labelConst = _vars.DeclareConst("SystemUInt32",
                    _methodLabels[lambda.Symbol].ToString());
                _module.AddCopy(labelConst, paramIds[i]);
            }
            else
            {
                var argId = VisitExpression(argOp);
                _module.AddCopy(argId, paramIds[i]);
            }
        }

        // Restore hint so EmitCallByLabel can consume it for the return value
        _ctx.TargetHint = savedHint;
        var result = EmitCallByLabel(target, targetLabel);

        // Restore current method's parameter values after return
        if (savedCurrentParams != null)
        {
            for (int i = 0; i < currentParamIds.Length; i++)
                _module.AddCopy(savedCurrentParams[i], currentParamIds[i]);
        }

        // Copy-out for ref/out params
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var param = target.Parameters[i];
            if (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref)
            {
                var argTarget = op.Arguments[i].Value;
                AssignToTarget(argTarget, paramIds[i]);
            }
        }

        return result;
    }

    // ── Ref/Out copy-back helper ──

    void AssignToTarget(IOperation target, string valueId)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                // var x in deconstruction — declares a new local
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _vars.DeclareLocal(localRef.Local.Name, udonType);
                    _localVarIds[localRef.Local] = localId;
                    _module.AddCopy(valueId, localId);
                }
                break;

            case ILocalReferenceOperation existingLocal:
                var existingId = _vars.Lookup(existingLocal.Local.Name)
                    ?? (_localVarIds.TryGetValue(existingLocal.Local, out var cap) ? cap : null);
                if (existingId == null)
                {
                    // New local from tuple deconstruction (var (a, b) pattern)
                    var udonType = GetUdonType(existingLocal.Type);
                    existingId = _vars.DeclareLocal(existingLocal.Local.Name, udonType);
                    _localVarIds[existingLocal.Local] = existingId;
                }
                _module.AddCopy(valueId, existingId);
                break;

            case IFieldReferenceOperation fieldRef when fieldRef.Instance is IInstanceReferenceOperation:
                _module.AddCopy(valueId, fieldRef.Field.Name);
                break;

            case IArrayElementReferenceOperation arrayElem:
                var arrayId = VisitExpression(arrayElem.ArrayReference);
                var indexId = VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                _module.AddPush(arrayId);
                _module.AddPush(indexId);
                _module.AddPush(valueId);
                AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
                break;

            case IFieldReferenceOperation fieldRef
                when fieldRef.Instance != null
                && !(fieldRef.Instance is IInstanceReferenceOperation)
                && ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
                var instanceId = VisitExpression(fieldRef.Instance);
                var nameConst = _vars.DeclareConst("SystemString", fieldRef.Field.Name);
                _module.AddPush(instanceId);
                _module.AddPush(nameConst);
                _module.AddPush(valueId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                break;

            case IDiscardOperation:
                break; // _ = expr → discard

            default:
                throw new System.NotSupportedException(
                    $"Unsupported deconstruction target element: {target.GetType().Name}");
        }
    }

    // ── Extern Signature Helpers ──

    string BuildExternCallSignature(IMethodSymbol method, ITypeSymbol instanceType = null)
    {
        ITypeSymbol containingTypeSym = method.ContainingType;

        // Interface method on a type parameter: use the concrete type as containing type
        // e.g., IComparable<T>.CompareTo(T) with T=int → SystemInt32.__CompareTo__SystemInt32__SystemInt32
        if (containingTypeSym.TypeKind == TypeKind.Interface && instanceType != null
            && _typeParamMap != null
            && instanceType is ITypeParameterSymbol tp
            && _typeParamMap.TryGetValue(tp, out var concreteType))
            containingTypeSym = concreteType;

        var containingType = GetUdonType(containingTypeSym);

        // Object.Instantiate → VRCInstantiate (Udon VM redirect)
        if (containingType == "UnityEngineObject" && method.Name == "Instantiate")
            containingType = "VRCInstantiate";

        var methodName = $"__{method.Name}";

        string buildSig(IMethodSymbol m)
        {
            var pts = m.Parameters.Select(p =>
            {
                var tn = GetUdonType(p.Type);
                if (p.RefKind == RefKind.Out || p.RefKind == RefKind.Ref)
                    tn += "Ref";
                return tn;
            }).ToArray();
            var rt = GetUdonType(m.ReturnType);
            return ExternResolver.BuildMethodSignature(containingType, methodName, pts, rt);
        }

        var sig = buildSig(method);

        // Generic static Array methods (IndexOf<T>, LastIndexOf<T>, BinarySearch<T>, Reverse<T>):
        // UdonSharp resolves these to the non-generic overload (Array, object) instead of (T[], T).
        // The TArray/T version exists but causes HeapTypeMismatch (reads String[] as Object[]).
        if (method.IsGenericMethod && containingType == "SystemArray")
        {
            var nonGenericPts = method.OriginalDefinition.Parameters.Select(p =>
            {
                var t = p.Type;
                switch (t)
                {
                    case ITypeParameterSymbol:
                        return "SystemObject";
                    case IArrayTypeSymbol { ElementType: ITypeParameterSymbol }:
                        return "SystemArray";
                }
                var tn = GetUdonType(t);
                if (p.RefKind is RefKind.Out or RefKind.Ref) tn += "Ref";
                return tn;
            }).ToArray();
            var rt = GetUdonType(method.ReturnType);
            sig = ExternResolver.BuildMethodSignature(containingType, methodName, nonGenericPts, rt);
        }
        // Other generic extern methods: try concrete types first, fall back to OriginalDefinition
        else if (method.IsGenericMethod && ExternResolver.IsExternValid != null && !ExternResolver.IsExternValid(sig))
        {
            var origSig = buildSig(method.OriginalDefinition);
            if (ExternResolver.IsExternValid(origSig))
                sig = origSig;
        }

        return sig;
    }
}
