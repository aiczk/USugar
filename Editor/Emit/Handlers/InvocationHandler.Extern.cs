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
                instanceId = _ctx.Vars.DeclareThisOnce(GetUdonType(target.ContainingType));
            else if (op.Instance != null)
                instanceId = VisitExpression(op.Instance);
        }

        var argIds = new string[op.Arguments.Length];
        for (int i = 0; i < op.Arguments.Length; i++)
            argIds[i] = VisitExpression(op.Arguments[i].Value);

        // Now emit all PUSHes contiguously
        if (instanceId != null)
            _ctx.Module.AddPush(instanceId);
        foreach (var argId in argIds)
            _ctx.Module.AddPush(argId);

        // Restore hint and consume for result
        _ctx.TargetHint = savedHint;
        string resultId = null;
        if (!target.ReturnsVoid)
        {
            var returnType = GetUdonType(target.ReturnType);
            resultId = ConsumeTargetHintOrTemp(returnType);
            _ctx.Module.AddPush(resultId);
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
            instanceId = _ctx.Vars.DeclareThisOnce("UnityEngineTransform");
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
            _ctx.Module.AddPush(instanceId);

        // Push explicit arguments FIRST, then SystemType (matches UdonSharp push order for __T externs)
        foreach (var argId in argIds)
            _ctx.Module.AddPush(argId);

        // Push typeof(T) as SystemType constant (after explicit args).
        // IUdonEventReceiver → VRCUdonUdonBehaviour: the Udon type registry doesn't have
        // IUdonEventReceiver as a SystemType constant. UdonBehaviour implements the interface,
        // so using VRCUdonUdonBehaviour as the type token gives the same GetComponent result.
        // ExternResolver can't handle this because it operates on extern signatures, not type tokens.
        var typeArgName = GetUdonType(target.TypeArguments[0]);
        var typeConstValue = typeArgName == "VRCUdonCommonInterfacesIUdonEventReceiver" ? "VRCUdonUdonBehaviour" : typeArgName;
        var typeConstId = _ctx.Vars.DeclareConst("SystemType", typeConstValue);
        _ctx.Module.AddPush(typeConstId);

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
        _ctx.Module.AddPush(resultId);

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
            instanceId = _ctx.Vars.DeclareThisOnce("UnityEngineTransform");
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
            _ctx.Module.AddPush(instanceId);
        var udonBehaviourType = _ctx.Vars.DeclareConst("SystemType", "VRCUdonUdonBehaviour");
        _ctx.Module.AddPush(udonBehaviourType);
        foreach (var argId in argIds)
            _ctx.Module.AddPush(argId);

        // Call GetComponents → ComponentArray
        var allComponents = _ctx.Vars.DeclareTemp("UnityEngineComponentArray");
        _ctx.Module.AddPush(allComponents);
        AddExternChecked(fetchExtern);

        // Compute target type ID at compile time
        var targetTypeName = target.TypeArguments[0].ToDisplayString();
        long targetTypeId = UasmEmitter.ComputeTypeId(targetTypeName);
        var targetIdConst = _ctx.Vars.DeclareConst("SystemInt64", targetTypeId.ToString());

        // Inheritance: if derived USB types exist, use __refl_typeids + Array.IndexOf
        bool useTypeIds = HasInheritedUsbTypes(target.TypeArguments[0]);
        var reflKeyConst = useTypeIds
            ? _ctx.Vars.DeclareConst("SystemString", "__refl_typeids")
            : _ctx.Vars.DeclareConst("SystemString", "__refl_typeid");

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
        var transformId = _ctx.Vars.DeclareTemp("UnityEngineTransform");
        _ctx.Module.AddPush(instanceId);
        _ctx.Module.AddPush(transformId);
        AddExternChecked("UnityEngineGameObject.__get_transform__UnityEngineTransform");
        return transformId;
    }

    bool HasInheritedUsbTypes(ITypeSymbol targetType)
    {
        foreach (var kvp in _ctx.Planner.AllLayouts)
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
        var lenId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(allComponents);
        _ctx.Module.AddPush(lenId);
        AddExternChecked("UnityEngineComponentArray.__get_Length__SystemInt32");

        // Loop index
        var idxId = _ctx.Vars.DeclareTemp("SystemInt32");
        var zeroConst = _ctx.Vars.DeclareConst("SystemInt32", "0");
        _ctx.Module.AddCopy(zeroConst, idxId);

        // Result (null initially)
        var resultId = _ctx.Vars.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");

        var loopLabel = _ctx.Module.DefineLabel("__gc_shim_loop");
        var nextLabel = _ctx.Module.DefineLabel("__gc_shim_next");
        var endLabel = _ctx.Module.DefineLabel("__gc_shim_end");

        _ctx.Module.MarkLabel(loopLabel);

        // if (idx >= len) → end
        var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(condId);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(endLabel);

        // element = allComponents[idx]
        var elementId = _ctx.Vars.DeclareTemp("UnityEngineComponent");
        _ctx.Module.AddPush(allComponents);
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(elementId);
        AddExternChecked("UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent");

        // Cast Component → IUdonEventReceiver (COPY to differently-typed temp)
        var behaviourId = _ctx.Vars.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");
        _ctx.Module.AddCopy(elementId, behaviourId);

        // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
        var idValueId = _ctx.Vars.DeclareTemp("SystemObject");
        _ctx.Module.AddPush(behaviourId);
        _ctx.Module.AddPush(reflKeyConst);
        _ctx.Module.AddPush(idValueId);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");

        // Null check: if (idValue == null) → next
        var nullConst = _ctx.Vars.DeclareConst("SystemObject", "null");
        var notNullId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(idValueId);
        _ctx.Module.AddPush(nullConst);
        _ctx.Module.AddPush(notNullId);
        AddExternChecked("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean");
        _ctx.Module.AddPush(notNullId);
        _ctx.Module.AddJumpIfFalse(nextLabel);

        if (useTypeIds)
        {
            EmitTypeIdsArrayCheck(idValueId, targetIdConst, nextLabel);
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdId = _ctx.Vars.DeclareTemp("SystemInt64");
            _ctx.Module.AddPush(idValueId);
            _ctx.Module.AddPush(typeIdId);
            AddExternChecked("SystemConvert.__ToInt64__SystemObject__SystemInt64");

            // if (typeId != targetId) → next
            var matchId = _ctx.Vars.DeclareTemp("SystemBoolean");
            _ctx.Module.AddPush(typeIdId);
            _ctx.Module.AddPush(targetIdConst);
            _ctx.Module.AddPush(matchId);
            AddExternChecked("SystemInt64.__op_Equality__SystemInt64_SystemInt64__SystemBoolean");
            _ctx.Module.AddPush(matchId);
            _ctx.Module.AddJumpIfFalse(nextLabel);
        }

        // Match! result = behaviour
        _ctx.Module.AddCopy(behaviourId, resultId);
        _ctx.Module.AddJump(endLabel);

        // next: idx++
        _ctx.Module.MarkLabel(nextLabel);
        var oneConst = _ctx.Vars.DeclareConst("SystemInt32", "1");
        var nextIdxId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(nextIdxId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _ctx.Module.AddCopy(nextIdxId, idxId);
        _ctx.Module.AddJump(loopLabel);

        _ctx.Module.MarkLabel(endLabel);
        return resultId;
    }

    string EmitShimPlural(string allComponents, string targetIdConst, string reflKeyConst, bool useTypeIds)
    {
        // Get array length
        var lenId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(allComponents);
        _ctx.Module.AddPush(lenId);
        AddExternChecked("UnityEngineComponentArray.__get_Length__SystemInt32");

        var zeroConst = _ctx.Vars.DeclareConst("SystemInt32", "0");
        var oneConst = _ctx.Vars.DeclareConst("SystemInt32", "1");

        // === Pass 1: Count matches ===
        var countId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddCopy(zeroConst, countId);
        var idx1Id = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddCopy(zeroConst, idx1Id);

        var count_loop = _ctx.Module.DefineLabel("__gc_count_loop");
        var count_next = _ctx.Module.DefineLabel("__gc_count_next");
        var count_end = _ctx.Module.DefineLabel("__gc_count_end");
        _ctx.Module.MarkLabel(count_loop);

        // if (idx >= len) → count_end
        var cond1 = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(idx1Id);
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(cond1);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _ctx.Module.AddPush(cond1);
        _ctx.Module.AddJumpIfFalse(count_end);

        EmitShimTypeCheck(allComponents, idx1Id, reflKeyConst, targetIdConst, count_next, useTypeIds);

        // countId++
        var newCount = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(countId);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(newCount);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _ctx.Module.AddCopy(newCount, countId);

        // count_next: idx1++
        _ctx.Module.MarkLabel(count_next);
        var nextIdx1 = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(idx1Id);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(nextIdx1);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _ctx.Module.AddCopy(nextIdx1, idx1Id);
        _ctx.Module.AddJump(count_loop);
        _ctx.Module.MarkLabel(count_end);

        // === Allocate result array ===
        var resultId = _ctx.Vars.DeclareTemp("UnityEngineComponentArray");
        _ctx.Module.AddPush(countId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked("UnityEngineComponentArray.__ctor__SystemInt32__UnityEngineComponentArray");

        // === Pass 2: Fill result array ===
        var idx2Id = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddCopy(zeroConst, idx2Id);
        var writeIdx = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddCopy(zeroConst, writeIdx);

        var fill_loop = _ctx.Module.DefineLabel("__gc_fill_loop");
        var fill_next = _ctx.Module.DefineLabel("__gc_fill_next");
        var fill_end = _ctx.Module.DefineLabel("__gc_fill_end");
        _ctx.Module.MarkLabel(fill_loop);

        // if (idx >= len) → fill_end
        var cond2 = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(idx2Id);
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(cond2);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _ctx.Module.AddPush(cond2);
        _ctx.Module.AddJumpIfFalse(fill_end);

        var matchBeh = EmitShimTypeCheck(allComponents, idx2Id, reflKeyConst, targetIdConst, fill_next, useTypeIds);

        // result[writeIdx] = element
        _ctx.Module.AddPush(resultId);
        _ctx.Module.AddPush(writeIdx);
        _ctx.Module.AddPush(matchBeh);
        AddExternChecked("UnityEngineComponentArray.__Set__SystemInt32_UnityEngineComponent__SystemVoid");

        // writeIdx++
        var newWrite = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(writeIdx);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(newWrite);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _ctx.Module.AddCopy(newWrite, writeIdx);

        // fill_next: idx2++
        _ctx.Module.MarkLabel(fill_next);
        var nextIdx2 = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(idx2Id);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(nextIdx2);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _ctx.Module.AddCopy(nextIdx2, idx2Id);
        _ctx.Module.AddJump(fill_loop);
        _ctx.Module.MarkLabel(fill_end);

        return resultId;
    }

    // Shared type-check logic for shim loops. Returns the behaviourId for matched element.
    string EmitShimTypeCheck(string allComponents, string idxId, string reflKeyConst, string targetIdConst, int nextLabel, bool useTypeIds)
    {
        // element = allComponents[idx]
        var elementId = _ctx.Vars.DeclareTemp("UnityEngineComponent");
        _ctx.Module.AddPush(allComponents);
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(elementId);
        AddExternChecked("UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent");

        // Cast Component → IUdonEventReceiver
        var behaviourId = _ctx.Vars.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");
        _ctx.Module.AddCopy(elementId, behaviourId);

        // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
        var idValueId = _ctx.Vars.DeclareTemp("SystemObject");
        _ctx.Module.AddPush(behaviourId);
        _ctx.Module.AddPush(reflKeyConst);
        _ctx.Module.AddPush(idValueId);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");

        // Null check
        var nullConst = _ctx.Vars.DeclareTemp("SystemObject");
        var notNullId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(idValueId);
        _ctx.Module.AddPush(nullConst);
        _ctx.Module.AddPush(notNullId);
        AddExternChecked("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean");
        _ctx.Module.AddPush(notNullId);
        _ctx.Module.AddJumpIfFalse(nextLabel);

        if (useTypeIds)
        {
            EmitTypeIdsArrayCheck(idValueId, targetIdConst, nextLabel);
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdId = _ctx.Vars.DeclareTemp("SystemInt64");
            _ctx.Module.AddPush(idValueId);
            _ctx.Module.AddPush(typeIdId);
            AddExternChecked("SystemConvert.__ToInt64__SystemObject__SystemInt64");

            // if (typeId != targetId) → next
            var matchId = _ctx.Vars.DeclareTemp("SystemBoolean");
            _ctx.Module.AddPush(typeIdId);
            _ctx.Module.AddPush(targetIdConst);
            _ctx.Module.AddPush(matchId);
            AddExternChecked("SystemInt64.__op_Equality__SystemInt64_SystemInt64__SystemBoolean");
            _ctx.Module.AddPush(matchId);
            _ctx.Module.AddJumpIfFalse(nextLabel);
        }

        return behaviourId;
    }

    // Array.IndexOf(__refl_typeids, targetId) != -1
    void EmitTypeIdsArrayCheck(string idValueId, string targetIdConst, int nextLabel)
    {
        // Box Int64 → Object for Array.IndexOf(Array, Object)
        var targetIdObj = _ctx.Vars.DeclareTemp("SystemObject");
        _ctx.Module.AddCopy(targetIdConst, targetIdObj);

        var indexResult = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(idValueId);    // __refl_typeids as Object (actually Int64[])
        _ctx.Module.AddPush(targetIdObj);
        _ctx.Module.AddPush(indexResult);
        AddExternChecked("SystemArray.__IndexOf__SystemArray_SystemObject__SystemInt32");

        var negOneConst = _ctx.Vars.DeclareConst("SystemInt32", "-1");
        var foundId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(indexResult);
        _ctx.Module.AddPush(negOneConst);
        _ctx.Module.AddPush(foundId);
        AddExternChecked("SystemInt32.__op_Inequality__SystemInt32_SystemInt32__SystemBoolean");
        _ctx.Module.AddPush(foundId);
        _ctx.Module.AddJumpIfFalse(nextLabel);
    }

    // ── Interface Call ──

    string EmitInterfaceCall(IInvocationOperation op, IMethodSymbol target)
    {
        if (target.Parameters.Any(p => p.Type.IsTupleType))
            throw new System.NotSupportedException(
                $"Tuple parameters on interface method '{target.ContainingType.Name}.{target.Name}' are not supported. "
                + "Tuple parameters are only supported for same-class method calls.");
        if (target.ReturnType.IsTupleType)
            throw new System.NotSupportedException(
                $"Tuple return from interface method '{target.ContainingType.Name}.{target.Name}' is not supported. "
                + "Tuple returns are only supported for same-class method calls.");

        // Use LayoutPlanner to get the interface's canonical naming
        var ifaceType = target.ContainingType as INamedTypeSymbol;
        MethodLayout ifaceMl = null;
        if (ifaceType != null)
        {
            var ifaceLayout = _ctx.Planner.GetLayout(ifaceType);
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
            var paramNameConst = _ctx.Vars.DeclareConst("SystemString", paramName);
            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(paramNameConst);
            _ctx.Module.AddPush(argId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
        }

        // SendCustomEvent with interface export name
        var exportName = ifaceMl.ExportName;
        var eventNameConst = _ctx.Vars.DeclareConst("SystemString", exportName);
        _ctx.Module.AddPush(instanceId);
        _ctx.Module.AddPush(eventNameConst);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");

        // GetProgramVariable for return value
        if (!target.ReturnsVoid)
        {
            var retName = ifaceMl.ReturnId;
            var retNameConst = _ctx.Vars.DeclareConst("SystemString", retName);
            var returnType = GetUdonType(target.ReturnType);
            var resultId = _ctx.Vars.DeclareTemp(returnType);
            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(retNameConst);
            _ctx.Module.AddPush(resultId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
            return resultId;
        }

        return null;
    }

    // ── Cross-Class Call ──

    string EmitCrossClassCall(IInvocationOperation op, IMethodSymbol target)
    {
        if (target.Parameters.Any(p => p.Type.IsTupleType))
            throw new System.NotSupportedException(
                $"Tuple parameters on cross-behaviour method '{target.ContainingType.Name}.{target.Name}' are not supported. "
                + "Tuple parameters are only supported for same-class method calls.");
        if (target.ReturnType.IsTupleType)
            throw new System.NotSupportedException(
                $"Tuple return from cross-behaviour method '{target.ContainingType.Name}.{target.Name}' is not supported. "
                + "Tuple returns are only supported for same-class method calls.");

        var (exportName, paramIds, retId) = GetCalleeLayout(target);
        var instanceId = VisitExpression(op.Instance);

        // SetProgramVariable for each argument
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var argId = VisitExpression(op.Arguments[i].Value);
            var paramNameConst = _ctx.Vars.DeclareConst("SystemString", paramIds[i]);

            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(paramNameConst);
            _ctx.Module.AddPush(argId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
        }

        // SendCustomEvent
        var eventNameConst = _ctx.Vars.DeclareConst("SystemString", exportName);
        _ctx.Module.AddPush(instanceId);
        _ctx.Module.AddPush(eventNameConst);
        AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");

        // GetProgramVariable for return value
        if (!target.ReturnsVoid && retId != null)
        {
            var retNameConst = _ctx.Vars.DeclareConst("SystemString", retId);
            var returnType = GetUdonType(target.ReturnType);
            var resultId = _ctx.Vars.DeclareTemp(returnType);

            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(retNameConst);
            _ctx.Module.AddPush(resultId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");

            return resultId;
        }

        return null;
    }

    // ── User Method Call ──

    List<(string current, string saved)> SaveMethodParameterState(IMethodSymbol method)
    {
        var savedSlots = new List<(string current, string saved)>();
        if (method == null)
            return savedSlots;

        if (_ctx.MethodParamVarIds.TryGetValue(method, out var currentParamIds))
        {
            for (int i = 0; i < currentParamIds.Length; i++)
            {
                if (currentParamIds[i] == null) continue;
                var paramType = _ctx.Vars.GetDeclaredType(currentParamIds[i]);
                var savedId = _ctx.Vars.DeclareTemp(paramType);
                _ctx.Module.AddCopy(currentParamIds[i], savedId);
                savedSlots.Add((currentParamIds[i], savedId));
            }
        }

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (!TryGetMethodTupleParamVarIds(method, i, out var tupleParamIds))
                continue;

            for (int ei = 0; ei < tupleParamIds.Length; ei++)
            {
                var paramType = _ctx.Vars.GetDeclaredType(tupleParamIds[ei]);
                var savedId = _ctx.Vars.DeclareTemp(paramType);
                _ctx.Module.AddCopy(tupleParamIds[ei], savedId);
                savedSlots.Add((tupleParamIds[ei], savedId));
            }
        }

        return savedSlots;
    }

    void RestoreMethodParameterState(List<(string current, string saved)> savedSlots)
    {
        foreach (var (current, saved) in savedSlots)
            _ctx.Module.AddCopy(saved, current);
    }

    void CopyArgumentToMethodParameter(IMethodSymbol target, int ordinal, IOperation argOp, string scalarParamId)
    {
        if (TryGetMethodTupleParamVarIds(target, ordinal, out var tupleParamIds))
        {
            CopyTupleValueToSlots(argOp, tupleParamIds, $"argument for parameter '{target.Parameters[ordinal].Name}'");
            return;
        }

        var argId = VisitExpression(argOp);
        _ctx.Module.AddCopy(argId, scalarParamId);
    }

    string EmitUserMethodCall(IInvocationOperation op, IMethodSymbol target, int targetLabel)
    {
        // Save hint and clear it while evaluating sub-expressions
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;

        var idx = _ctx.MethodIndices[target];
        var paramIds = _ctx.MethodParamVarIds[target];

        // Save current method's parameter values before any user method call.
        // This protects against mutual recursion (A→B→A) corrupting A's params,
        // not just self-recursion. Overhead is 2N COPY per call (N = param count).
        var savedCurrentParams = SaveMethodParameterState(_ctx.CurrentMethod);

        // Copy arguments to param vars
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var param = target.Parameters[i];
            var argOp = op.Arguments[i].Value;

            // Delegate parameter with lambda arg: hoist with convention vars
            if (_ctx.DelegateParamConventions.TryGetValue((idx, param.Ordinal), out var convention)
                && UnwrapLambdaFromArg(argOp, out var lambda))
            {
                HoistLambdaForDelegateParam(lambda, convention);
                var labelConst = _ctx.Vars.DeclareConst("SystemUInt32",
                    _ctx.MethodLabels[lambda.Symbol].ToString());
                _ctx.Module.AddCopy(labelConst, paramIds[i]);
            }
            else
            {
                CopyArgumentToMethodParameter(target, i, argOp, paramIds[i]);
            }
        }

        // Restore hint so EmitCallByLabel can consume it for the return value
        _ctx.TargetHint = savedHint;
        var result = EmitCallByLabel(target, targetLabel);

        // Restore current method's parameter values after return
        RestoreMethodParameterState(savedCurrentParams);

        // Copy-out for ref/out params
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var param = target.Parameters[i];
            if (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref)
            {
                var argTarget = op.Arguments[i].Value;
                if (TryGetMethodTupleParamVarIds(target, i, out var tupleParamIds))
                {
                    if (!TryGetTupleTargetVarIds(argTarget, out var tupleTargetIds))
                        throw new System.NotSupportedException(
                            $"Unsupported ref/out tuple target for parameter '{param.Name}': {argTarget.GetType().Name}");

                    if (tupleTargetIds.Length != tupleParamIds.Length)
                        throw new System.InvalidOperationException(
                            $"Tuple arity mismatch in ref/out copy-back for parameter '{param.Name}'.");

                    for (int ei = 0; ei < tupleParamIds.Length; ei++)
                        if (tupleParamIds[ei] != tupleTargetIds[ei])
                            _ctx.Module.AddCopy(tupleParamIds[ei], tupleTargetIds[ei]);
                }
                else
                    AssignToTarget(argTarget, paramIds[i]);
            }
        }

        return result;
    }

    // ── Extern Signature Helpers ──

    string BuildExternCallSignature(IMethodSymbol method, ITypeSymbol instanceType = null)
    {
        ITypeSymbol containingTypeSym = method.ContainingType;

        // Interface method on a type parameter: use the concrete type as containing type
        // e.g., IComparable<T>.CompareTo(T) with T=int → SystemInt32.__CompareTo__SystemInt32__SystemInt32
        if (containingTypeSym.TypeKind == TypeKind.Interface && instanceType != null
            && _ctx.TypeParamMap != null
            && instanceType is ITypeParameterSymbol tp
            && _ctx.TypeParamMap.TryGetValue(tp, out var concreteType))
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
