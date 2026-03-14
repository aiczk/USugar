using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
    // ── Extern Method Call ──

    HExpr EmitExternMethodCall(IInvocationOperation op, IMethodSymbol target)
    {
        // Generic GetComponent<T>() / GetComponentInChildren<T>() / GetComponentsInChildren<T>() etc.
        // Udon VM uses non-generic form with typeof(T) parameter.
        if (target.IsGenericMethod && target.Name.StartsWith("GetComponent")
            && target.TypeArguments.Length == 1)
        {
            return EmitGetComponentGeneric(op, target);
        }

        HExpr instanceVal = null;
        if (!target.IsStatic)
        {
            if (op.Instance is IInstanceReferenceOperation)
                instanceVal = LoadField(_ctx.DeclareThisOnce(GetUdonType(target.ContainingType)), GetUdonType(target.ContainingType));
            else if (op.Instance != null)
                instanceVal = VisitExpression(op.Instance);
        }

        // For out/ref params: pass the original variable's LoadField directly.
        // Udon VM extern writes to the pushed address, so the original variable
        // is updated in-place. No copy-back needed.
        var argVals = new List<HExpr>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(VisitExpression(op.Arguments[i].Value));

        // Build args list for extern call
        var externArgs = new List<HExpr>();
        if (instanceVal != null)
            externArgs.Add(instanceVal);
        externArgs.AddRange(argVals);

        // Extern signature
        var sig = BuildExternCallSignature(target, op.Instance?.Type);

        if (!target.ReturnsVoid)
        {
            var returnType = GetUdonType(target.ReturnType);
            return ExternCall(sig, externArgs, returnType);
        }
        else
        {
            EmitExternVoid(sig, externArgs);
            return null;
        }
    }

    // ── GetComponent<T> ──

    HExpr EmitGetComponentGeneric(IInvocationOperation op, IMethodSymbol target)
    {
        var typeArg = target.TypeArguments[0];
        return ExternResolver.IsUdonSharpBehaviour(typeArg) ? EmitGetComponentShim(op, target) : EmitGetComponentExtern(op, target);
    }

    // Existing logic for Unity Component types (Transform, Collider, etc.)
    // Uses the __T / __TArray generic extern form (matches UdonSharp behavior).
    HExpr EmitGetComponentExtern(IInvocationOperation op, IMethodSymbol target)
    {
        // Evaluate instance and arguments first
        HExpr instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = LoadField(_ctx.DeclareThisOnce("UnityEngineTransform"), "UnityEngineTransform");
        else if (op.Instance != null)
            instanceVal = VisitExpression(op.Instance);

        // Evaluate explicit arguments (e.g., GetComponentInChildren<T>(bool includeInactive))
        var argVals = new List<HExpr>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(VisitExpression(op.Arguments[i].Value));

        // __T externs use UnityEngineComponent as containing type.
        // If instance is a GameObject (not a Component), get .transform first.
        instanceVal = EnsureComponentInstance(op.Instance, instanceVal);

        // Build extern args: instance + explicit args + typeof(T)
        var externArgs = new List<HExpr>();
        if (instanceVal != null)
            externArgs.Add(instanceVal);

        // Push explicit arguments FIRST, then SystemType (matches UdonSharp push order for __T externs)
        externArgs.AddRange(argVals);

        // typeof(T) as SystemType constant (after explicit args)
        var typeArgName = GetUdonType(target.TypeArguments[0]);
        var typeConstValue = typeArgName == "VRCUdonCommonInterfacesIUdonEventReceiver" ? "VRCUdonUdonBehaviour" : typeArgName;
        var typeConst = Const(typeConstValue, "SystemType");
        externArgs.Add(typeConst);

        // Result type — typed as T for __T externs
        var isPlural = target.Name.StartsWith("GetComponents");
        var typeArgUdon = GetUdonType(target.TypeArguments[0]);
        string tempType;
        if (isPlural && typeArgUdon == "VRCUdonCommonInterfacesIUdonEventReceiver")
            tempType = "UnityEngineComponentArray";
        else
            tempType = isPlural ? $"{typeArgUdon}Array" : typeArgUdon;

        // Build extern name with __T form
        const string containingType = "UnityEngineComponent";
        var methodName = target.Name;
        var retPlaceholder = isPlural ? "__TArray" : "__T";
        var explicitParams = target.OriginalDefinition.Parameters;
        string externSig;
        if (explicitParams.Length > 0)
        {
            var paramStr = string.Join("_", explicitParams.Select(p => GetUdonType(p.Type)));
            externSig = $"{containingType}.__{methodName}__{paramStr}{retPlaceholder}";
        }
        else
        {
            externSig = $"{containingType}.__{methodName}{retPlaceholder}";
        }

        return ExternCall(externSig, externArgs, tempType);
    }

    // ── GetComponent<T> USB Shim ──
    // Inline shim for USB-derived types: GetComponents(typeof(UdonBehaviour)) + __refl_typeid filter

    HExpr EmitGetComponentShim(IInvocationOperation op, IMethodSymbol target)
    {
        var isSingular = !target.Name.StartsWith("GetComponents");

        // Evaluate instance
        HExpr instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = LoadField(_ctx.DeclareThisOnce("UnityEngineTransform"), "UnityEngineTransform");
        else if (op.Instance != null)
            instanceVal = VisitExpression(op.Instance);

        // Evaluate explicit arguments (bool includeInactive)
        var argVals = new List<HExpr>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(VisitExpression(op.Arguments[i].Value));

        // If instance is a GameObject, get .transform for Component-typed extern
        instanceVal = EnsureComponentInstance(op.Instance, instanceVal);

        // Determine which non-generic GetComponents extern to call
        var fetchExtern = ResolveShimFetchExtern(target.Name, op.Arguments.Length > 0);

        // Build args: instance + typeof(UdonBehaviour) + optional args
        var fetchArgs = new List<HExpr>();
        if (instanceVal != null)
            fetchArgs.Add(instanceVal);
        var udonBehaviourType = Const("VRCUdonUdonBehaviour", "SystemType");
        fetchArgs.Add(udonBehaviourType);
        fetchArgs.AddRange(argVals);

        // Call GetComponents → ComponentArray (store to field so it's evaluated once)
        var allComponentsField = _ctx.DeclareTemp("UnityEngineComponentArray");
        EmitStoreField(allComponentsField, ExternCall(fetchExtern, fetchArgs, "UnityEngineComponentArray"));
        var allComponents = LoadField(allComponentsField, "UnityEngineComponentArray");

        // Compute target type ID at compile time
        var targetTypeName = target.TypeArguments[0].ToDisplayString();
        long targetTypeId = UasmEmitter.ComputeTypeId(targetTypeName);
        var targetIdConst = Const(targetTypeId, "SystemInt64");

        // Inheritance: if derived USB types exist, use __refl_typeids + Array.IndexOf
        bool useTypeIds = HasInheritedUsbTypes(target.TypeArguments[0]);
        var reflKeyConst = useTypeIds
            ? Const("__refl_typeids", "SystemString")
            : Const("__refl_typeid", "SystemString");

        return isSingular
            ? EmitShimSingular(allComponents, targetIdConst, reflKeyConst, useTypeIds)
            : EmitShimPlural(allComponents, targetIdConst, reflKeyConst, useTypeIds);
    }

    /// <summary>
    /// If the instance is a GameObject, emit .transform to get a Component-typed instance.
    /// GetComponent __T externs and shim GetComponents externs use UnityEngineComponent
    /// as containing type, which requires the instance to be Component-typed in the heap.
    /// </summary>
    HExpr EnsureComponentInstance(IOperation instanceOp, HExpr instanceVal)
    {
        if (instanceVal == null || instanceOp == null)
            return instanceVal;
        var instanceUdon = GetUdonType(instanceOp.Type);
        if (instanceUdon != "UnityEngineGameObject")
            return instanceVal;
        return ExternCall(
            "UnityEngineGameObject.__get_transform__UnityEngineTransform",
            new List<HExpr> { instanceVal },
            "UnityEngineTransform");
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

    HExpr EmitShimSingular(HExpr allComponents, HExpr targetIdConst, HExpr reflKeyConst, bool useTypeIds)
    {
        // Get array length (store to field so it's not re-evaluated each iteration)
        var lenField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(lenField, ExternCall(
            "UnityEngineComponentArray.__get_Length__SystemInt32",
            new List<HExpr> { allComponents }, "SystemInt32"));

        // Loop index (mutable across control flow)
        var idxField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(idxField, Const(0, "SystemInt32"));

        // Result field (null initially — returns null if no match found)
        var resultField = _ctx.DeclareTemp("VRCUdonCommonInterfacesIUdonEventReceiver");

        // while (idx < len)
        _builder.EmitWhile(
            ExternCall(
                "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<HExpr> { LoadField(idxField, "SystemInt32"), LoadField(lenField, "SystemInt32") },
                "SystemBoolean"),
            b =>
            {
                // element = allComponents[idx]
                var curIdx = LoadField(idxField, "SystemInt32");
                var elementVal = ExternCall(
                    "UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent",
                    new List<HExpr> { allComponents, curIdx },
                    "UnityEngineComponent");

                // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
                var idValueVal = ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<HExpr> { elementVal, reflKeyConst },
                    "SystemObject");

                // Null check: if (idValue != null)
                var nullConst = Const(null, "SystemObject");
                var notNullVal = ExternCall(
                    "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                    new List<HExpr> { idValueVal, nullConst },
                    "SystemBoolean");

                _builder.EmitIf(notNullVal, thenB =>
                {
                    // Type check
                    var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

                    _builder.EmitIf(matchVal, matchB =>
                    {
                        // Match! result = element, break out of loop
                        EmitStoreField(resultField, elementVal);
                        _builder.EmitBreak();
                    });
                });

                // idx++
                var oneConst = Const(1, "SystemInt32");
                var curIdx2 = LoadField(idxField, "SystemInt32");
                var nextIdxVal = ExternCall(
                    "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { curIdx2, oneConst },
                    "SystemInt32");
                EmitStoreField(idxField, nextIdxVal);
            });

        return LoadField(resultField, "VRCUdonCommonInterfacesIUdonEventReceiver");
    }

    HExpr EmitShimPlural(HExpr allComponents, HExpr targetIdConst, HExpr reflKeyConst, bool useTypeIds)
    {
        // Get array length (store to field so it's not re-evaluated each iteration)
        var lenField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(lenField, ExternCall(
            "UnityEngineComponentArray.__get_Length__SystemInt32",
            new List<HExpr> { allComponents }, "SystemInt32"));

        var zeroConst = Const(0, "SystemInt32");
        var oneConst = Const(1, "SystemInt32");

        // === Pass 1: Count matches ===
        var countField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(countField, zeroConst);
        var idx1Field = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(idx1Field, zeroConst);

        // while (idx1 < len)
        _builder.EmitWhile(
            ExternCall(
                "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<HExpr> { LoadField(idx1Field, "SystemInt32"), LoadField(lenField, "SystemInt32") },
                "SystemBoolean"),
            b =>
            {
                EmitShimTypeCheckBody(allComponents, idx1Field, reflKeyConst, targetIdConst, useTypeIds,
                    matchAction: () =>
                    {
                        // count++
                        var curCount = LoadField(countField, "SystemInt32");
                        var newCountVal = ExternCall(
                            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                            new List<HExpr> { curCount, oneConst },
                            "SystemInt32");
                        EmitStoreField(countField, newCountVal);
                    });

                // idx1++
                var curIdx1 = LoadField(idx1Field, "SystemInt32");
                var nextIdx1Val = ExternCall(
                    "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { curIdx1, oneConst },
                    "SystemInt32");
                EmitStoreField(idx1Field, nextIdx1Val);
            });

        // === Allocate result array ===
        var countVal = LoadField(countField, "SystemInt32");
        var resultArr = ExternCall(
            "UnityEngineComponentArray.__ctor__SystemInt32__UnityEngineComponentArray",
            new List<HExpr> { countVal },
            "UnityEngineComponentArray");

        // === Pass 2: Fill result array ===
        var idx2Field = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(idx2Field, zeroConst);
        var writeIdxField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(writeIdxField, zeroConst);

        // while (idx2 < len)
        _builder.EmitWhile(
            ExternCall(
                "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<HExpr> { LoadField(idx2Field, "SystemInt32"), LoadField(lenField, "SystemInt32") },
                "SystemBoolean"),
            b =>
            {
                // element = allComponents[idx2]
                var curIdx2Loop = LoadField(idx2Field, "SystemInt32");
                var elementVal = ExternCall(
                    "UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent",
                    new List<HExpr> { allComponents, curIdx2Loop },
                    "UnityEngineComponent");

                // Type check
                var idValueVal = ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<HExpr> { elementVal, reflKeyConst },
                    "SystemObject");

                var nullConst = Const(null, "SystemObject");
                var notNullVal = ExternCall(
                    "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                    new List<HExpr> { idValueVal, nullConst },
                    "SystemBoolean");

                _builder.EmitIf(notNullVal, thenB =>
                {
                    var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

                    _builder.EmitIf(matchVal, matchB =>
                    {
                        // result[writeIdx] = element
                        var curWriteIdx = LoadField(writeIdxField, "SystemInt32");
                        EmitExternVoid("UnityEngineComponentArray.__Set__SystemInt32_UnityEngineComponent__SystemVoid",
                            new List<HExpr> { resultArr, curWriteIdx, elementVal });

                        // writeIdx++
                        var curWriteIdx2 = LoadField(writeIdxField, "SystemInt32");
                        var newWriteVal = ExternCall(
                            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                            new List<HExpr> { curWriteIdx2, oneConst },
                            "SystemInt32");
                        EmitStoreField(writeIdxField, newWriteVal);
                    });
                });

                // idx2++
                var curIdx2 = LoadField(idx2Field, "SystemInt32");
                var nextIdx2Val = ExternCall(
                    "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { curIdx2, oneConst },
                    "SystemInt32");
                EmitStoreField(idx2Field, nextIdx2Val);
            });

        return resultArr;
    }

    /// <summary>
    /// Returns an HExpr that evaluates to true if the idValue matches the targetId.
    /// Handles both single-id and array-of-ids cases.
    /// </summary>
    HExpr EmitShimTypeMatchExpr(HExpr idValueVal, HExpr targetIdConst, bool useTypeIds)
    {
        if (useTypeIds)
        {
            // Array.IndexOf(__refl_typeids, targetId) != -1
            var indexResult = ExternCall(
                "SystemArray.__IndexOf__SystemArray_SystemObject__SystemInt32",
                new List<HExpr> { idValueVal, targetIdConst },
                "SystemInt32");

            var negOneConst = Const(-1, "SystemInt32");
            return ExternCall(
                "SystemInt32.__op_Inequality__SystemInt32_SystemInt32__SystemBoolean",
                new List<HExpr> { indexResult, negOneConst },
                "SystemBoolean");
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdVal = ExternCall(
                "SystemConvert.__ToInt64__SystemObject__SystemInt64",
                new List<HExpr> { idValueVal },
                "SystemInt64");

            // typeId == targetId
            return ExternCall(
                "SystemInt64.__op_Equality__SystemInt64_SystemInt64__SystemBoolean",
                new List<HExpr> { typeIdVal, targetIdConst },
                "SystemBoolean");
        }
    }

    /// <summary>
    /// Emit the type-check body for shim loops (pass 1 count).
    /// Calls matchAction if the element's type ID matches.
    /// </summary>
    void EmitShimTypeCheckBody(HExpr allComponents, string idxField, HExpr reflKeyConst,
        HExpr targetIdConst, bool useTypeIds, System.Action matchAction)
    {
        // element = allComponents[idx]
        var idxVal = LoadField(idxField, "SystemInt32");
        var elementVal = ExternCall(
            "UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent",
            new List<HExpr> { allComponents, idxVal },
            "UnityEngineComponent");

        // idValue = behaviour.GetProgramVariable(reflKey)
        var idValueVal = ExternCall(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
            new List<HExpr> { elementVal, reflKeyConst },
            "SystemObject");

        // Null check
        var nullConst = Const(null, "SystemObject");
        var notNullVal = ExternCall(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { idValueVal, nullConst },
            "SystemBoolean");

        _builder.EmitIf(notNullVal, thenB =>
        {
            var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

            _builder.EmitIf(matchVal, matchB =>
            {
                matchAction();
            });
        });
    }

    // ── Interface Call ──

    HExpr EmitInterfaceCall(IInvocationOperation op, IMethodSymbol target)
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

        var instanceVal = VisitExpression(op.Instance);

        // SetProgramVariable for each argument
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var argVal = VisitExpression(op.Arguments[i].Value);
            var paramName = ifaceMl.ParamIds[i];
            var paramNameConst = Const(paramName, "SystemString");
            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<HExpr> { instanceVal, paramNameConst, argVal });
        }

        // SendCustomEvent with interface export name
        var exportName = ifaceMl.ExportName;
        var eventNameConst = Const(exportName, "SystemString");
        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<HExpr> { instanceVal, eventNameConst });

        // GetProgramVariable for return value
        if (!target.ReturnsVoid)
        {
            var retName = ifaceMl.ReturnId;
            var retNameConst = Const(retName, "SystemString");
            var returnType = GetUdonType(target.ReturnType);
            return ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<HExpr> { instanceVal, retNameConst },
                returnType);
        }

        return null;
    }

    // ── Cross-Class Call ──

    HExpr EmitCrossClassCall(IInvocationOperation op, IMethodSymbol target)
    {
        var (exportName, paramIds, retId) = GetCalleeLayout(target);
        var instanceVal = VisitExpression(op.Instance);

        // SetProgramVariable for each argument
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var argVal = VisitExpression(op.Arguments[i].Value);
            var paramNameConst = Const(paramIds[i], "SystemString");
            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<HExpr> { instanceVal, paramNameConst, argVal });
        }

        // SendCustomEvent
        var eventNameConst = Const(exportName, "SystemString");
        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<HExpr> { instanceVal, eventNameConst });

        // GetProgramVariable for return value
        if (!target.ReturnsVoid && retId != null)
        {
            var retNameConst = Const(retId, "SystemString");
            var returnType = GetUdonType(target.ReturnType);
            return ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<HExpr> { instanceVal, retNameConst },
                returnType);
        }

        return null;
    }

    // ── User Method Call ──

    HExpr EmitUserMethodCall(IInvocationOperation op, IMethodSymbol target)
    {
        var idx = _methodIndices[target];
        var paramIds = _methodParamVarIds[target];

        // Self-recursive call: save current parameter values before overwriting
        bool isSelfRecursive = _currentMethod != null
            && SymbolEqualityComparer.Default.Equals(target, _currentMethod);
        string[] savedParamFields = null;
        if (isSelfRecursive && paramIds.Length > 0)
        {
            savedParamFields = new string[paramIds.Length];
            for (int i = 0; i < paramIds.Length; i++)
            {
                var paramType = _ctx.GetFieldType(paramIds[i]);
                var savedField = _ctx.DeclareTemp(paramType);
                EmitStoreField(savedField, LoadField(paramIds[i], paramType));
                savedParamFields[i] = savedField;
            }
        }

        // Build args list
        var args = new List<HExpr>();
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var param = target.Parameters[i];
            var argOp = op.Arguments[i].Value;

            // Delegate parameter with lambda arg: hoist with convention vars
            if (_delegateParamConventions.TryGetValue((idx, param.Ordinal), out var convention)
                && UnwrapLambdaFromArg(argOp, out var lambda))
            {
                HoistLambdaForDelegateParam(lambda, convention);
                // Use FuncRef to pass the function's entry address
                args.Add(FuncRef(_methodFunctions[lambda.Symbol].Name));
            }
            else
            {
                args.Add(VisitExpression(argOp));
            }
        }

        var result = EmitCallToMethod(target, args);

        // Self-recursive call: restore parameter values after return
        if (isSelfRecursive && savedParamFields != null)
        {
            for (int i = 0; i < paramIds.Length; i++)
            {
                var paramType = _ctx.GetFieldType(paramIds[i]);
                EmitStoreField(paramIds[i], LoadField(savedParamFields[i], paramType));
            }
        }

        // Copy-out for ref/out params
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var param = target.Parameters[i];
            if (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref)
            {
                var argTarget = op.Arguments[i].Value;
                // Read back the param field value after call
                var paramType = _ctx.GetFieldType(paramIds[i]);
                var paramVal = LoadField(paramIds[i], paramType);
                AssignToTarget(argTarget, paramVal);
            }
        }

        return result;
    }

    // ── Ref/Out copy-back helper ──

    void AssignToTarget(IOperation target, HExpr value)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                // var x in deconstruction — declares a new local
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _ctx.DeclareLocal(localRef.Local.Name, udonType);
                    _localVarIds[localRef.Local] = localId;
                    EmitStoreField(localId, value);
                }
                break;

            case ILocalReferenceOperation existingLocal:
                string existingId = _localVarIds.TryGetValue(existingLocal.Local, out var cap) ? cap : null;
                if (existingId == null)
                {
                    // New local from tuple deconstruction (var (a, b) pattern)
                    var udonType = GetUdonType(existingLocal.Type);
                    existingId = _ctx.DeclareLocal(existingLocal.Local.Name, udonType);
                    _localVarIds[existingLocal.Local] = existingId;
                }
                EmitStoreField(existingId, value);
                break;

            case IFieldReferenceOperation fieldRef when fieldRef.Instance is IInstanceReferenceOperation:
                EmitStoreField(fieldRef.Field.Name, value);
                break;

            case IArrayElementReferenceOperation arrayElem:
                var arrayVal = VisitExpression(arrayElem.ArrayReference);
                var indexVal = VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                    new List<HExpr> { arrayVal, indexVal, value });
                break;

            case IFieldReferenceOperation fieldRef
                when fieldRef.Instance != null
                && !(fieldRef.Instance is IInstanceReferenceOperation)
                && ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
                var instanceVal = VisitExpression(fieldRef.Instance);
                var nameConst = Const(fieldRef.Field.Name, "SystemString");
                EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                    new List<HExpr> { instanceVal, nameConst, value });
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
