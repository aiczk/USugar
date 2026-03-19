using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler : HandlerBase, IExpressionHandler
{
    public InvocationHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IInvocationOperation
            or IObjectCreationOperation
            or IPropertyReferenceOperation
            or IInterpolatedStringOperation;

    public HExpr Handle(IOperation expression) => expression switch
    {
        IInvocationOperation op => VisitInvocation(op),
        IObjectCreationOperation op => VisitObjectCreation(op),
        IPropertyReferenceOperation op => VisitPropertyReference(op),
        IInterpolatedStringOperation op => VisitInterpolatedString(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── VisitInvocation ──

    HExpr VisitInvocation(IInvocationOperation op)
    {
        var target = op.TargetMethod;

        // Resolve type parameters in generic method type arguments (e.g., Min<T> → Min<int>)
        if (target.IsGenericMethod && _typeParamMap != null)
        {
            var needsSub = false;
            foreach (var ta in target.TypeArguments)
            {
                if (ta is not ITypeParameterSymbol tp || !_typeParamMap.ContainsKey(tp))
                    continue;

                needsSub = true;
                break;
            }

            if (needsSub)
            {
                var newTypeArgs = target.TypeArguments.Select(ta => ta is ITypeParameterSymbol tp2 && _typeParamMap.TryGetValue(tp2, out var sub) ? sub : ta).ToArray();
                target = target.OriginalDefinition.Construct(newTypeArgs);
            }
        }

        switch (target.MethodKind)
        {
            // Delegate invocation: a() where a is Action/Func
            case MethodKind.DelegateInvoke:
                return VisitDelegateInvocation(op);
            // Local function call
            case MethodKind.LocalFunction
                when _methodFunctions.ContainsKey(target):
                if (_currentMethod != null
                    && SymbolEqualityComparer.Default.Equals(target, _currentMethod))
                    throw new System.NotSupportedException(
                        $"Recursive local function '{target.Name}' is not supported. " +
                        "Udon VM's flat heap shares parameter variables across call frames. " +
                        "Use a loop instead of recursion.");
                return EmitUserMethodCall(op, target);
        }

        // User-defined generic method → monomorphize
        if (target.IsGenericMethod && SymbolEqualityComparer.Default.Equals(target.OriginalDefinition.ContainingType, _classSymbol))
        {
            RegisterGenericSpecialization(target);
            return EmitUserMethodCall(op, target);
        }

        // User-defined method in the same class
        if (SymbolEqualityComparer.Default.Equals(target.ContainingType, _classSymbol) && _methodFunctions.ContainsKey(target))
        {
            return EmitUserMethodCall(op, target);
        }

        // Base class instance method (emitted locally)
        if (_methodFunctions.ContainsKey(target) && IsBaseInstanceMethod(target))
            return EmitUserMethodCall(op, target);

        // Generic foreign static method → monomorphize and emit as internal call
        if (target.IsGenericMethod && IsForeignStatic(target))
        {
            var constructed = target.ReducedFrom != null
                ? target.ReducedFrom.OriginalDefinition.Construct(target.TypeArguments.ToArray())
                : target.OriginalDefinition.Construct(target.TypeArguments.ToArray());
            RegisterGenericSpecialization(constructed);
            var args = new List<HExpr>();
            if (target.ReducedFrom != null && op.Instance != null)
            {
                args.Add(VisitExpression(op.Instance));
            }
            for (var i = 0; i < op.Arguments.Length; i++)
            {
                args.Add(VisitExpression(op.Arguments[i].Value));
            }
            return EmitCallToMethod(constructed, args);
        }

        // Foreign static method → inlined as internal call (resolve extension method original form)
        {
            var original = target.ReducedFrom ?? target;
            if (IsForeignStatic(target) && _methodFunctions.ContainsKey(original))
            {
                var args = new List<HExpr>();
                // Extension method: instance is the first (this) parameter
                if (target.ReducedFrom != null && op.Instance != null)
                {
                    args.Add(VisitExpression(op.Instance));
                }
                for (var i = 0; i < op.Arguments.Length; i++)
                {
                    args.Add(VisitExpression(op.Arguments[i].Value));
                }
                return EmitCallToMethod(original, args);
            }
        }

        // Cross-class UdonSharpBehaviour call → SetProgramVariable + SendCustomEvent
        // Only for calls on other instances (fields), not on 'this' (base class methods like RequestSerialization).
        // Exclude methods declared on UdonSharpBehaviour itself (SendCustomEvent, SetProgramVariable, etc.)
        // — those are Udon VM interface methods that must be compiled as externs.
        if (ExternResolver.IsUdonSharpBehaviour(target.ContainingType)
            && op.Instance is not IInstanceReferenceOperation
            && target.ContainingType.Name != "UdonSharpBehaviour")
            return EmitCrossClassCall(op, target);

        // Interface method call → SendCustomEvent dispatch
        // Skip when instance is a type parameter resolved to a concrete non-UdonBehaviour type
        // (e.g., IComparable<T>.CompareTo with T=int → use extern, not SendCustomEvent)
        if (target.ContainingType.TypeKind == TypeKind.Interface
            && op.Instance != null
            && !IsResolvedConcreteNonBehaviour(op.Instance?.Type))
            return EmitInterfaceCall(op, target);

        // Virtual methods on UdonSharpBehaviour (OnDeserialization, Interact, etc.)
        // have no Udon VM implementation. base.X() or direct calls should be no-op.
        if (target.ContainingType.Name == "UdonSharpBehaviour"
            && (target.IsVirtual || target.IsOverride || target.IsAbstract))
            return null;

        // Extern method call
        return EmitExternMethodCall(op, target);
    }

    // ── Delegate Invocation ──

    HExpr VisitDelegateInvocation(IInvocationOperation op)
    {
        // ── Delegate FIELD invocation via conditional access (?.Invoke()) ──
        if (op.Instance is IConditionalAccessInstanceOperation
            && _conditionalAccessStack.Count > 0
            && _conditionalAccessStack.Peek().DelegateFieldName != null)
        {
            return EmitDelegateFieldInvocation(op, _conditionalAccessStack.Peek().DelegateFieldName,
                op.TargetMethod.ContainingType as INamedTypeSymbol);
        }

        // ── Delegate FIELD invocation ──
        if (op.Instance is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef
            && fieldRef.Field.Type is INamedTypeSymbol dlgFieldType
            && dlgFieldType.DelegateInvokeMethod != null
            && _delegateFields.Contains(fieldRef.Field.Name))
        {
            return EmitDelegateFieldInvocation(op, fieldRef.Field.Name, dlgFieldType);
        }

        // Delegate parameter invocation via JUMP_INDIRECT
        if (op.Instance is IParameterReferenceOperation paramRef2
            && _currentMethod != null
            && _methodSlots.TryGetValue(_currentMethod, out var currentSlot)
            && _delegateParamConventions.TryGetValue((currentSlot.Index, paramRef2.Parameter.Ordinal), out var convention))
        {
            // Collect args as HExprs
            var args = new List<HExpr>();
            for (int i = 0; i < op.Arguments.Length; i++)
                args.Add(VisitExpression(op.Arguments[i].Value));

            // Store args to convention fields
            for (int i = 0; i < args.Count && i < convention.ArgVarIds.Length; i++)
                EmitStoreField(convention.ArgVarIds[i], args[i]);

            // Get the method pointer (param var holding the lambda's address)
            var methodPtr = LoadParam(paramRef2.Parameter);

            // Determine return type
            string retType = convention.RetVarId != null
                ? _ctx.GetFieldType(convention.RetVarId)
                : null;

            // Emit indirect call through delegate
            // In HIR, indirect calls are represented as InternalCall("__indirect", [methodPtr], retType)
            // The ABI lowering pass will expand this to JUMP_INDIRECT with convention fields.
            var callRetType = retType ?? "SystemVoid";
            var indirectCall = InternalCall("__indirect", new List<HExpr> { methodPtr }, callRetType);

            if (retType != null)
            {
                // Side-effect: the call itself
                EmitExprStmt(indirectCall);
                // Read back the return value from the convention return field
                return LoadField(convention.RetVarId, retType);
            }
            else
            {
                EmitExprStmt(indirectCall);
                return null;
            }
        }

        // op.Instance is the delegate local reference (e.g., 'a' in a())
        if (op.Instance is ILocalReferenceOperation localRef
            && _delegateVarMap.TryGetValue(localRef.Local, out var targetMethod))
        {
            var args = new List<HExpr>();
            for (int i = 0; i < op.Arguments.Length; i++)
                args.Add(VisitExpression(op.Arguments[i].Value));

            return EmitCallToMethod(targetMethod, args);
        }
        throw new System.NotSupportedException("Cannot resolve delegate target");
    }

    /// <summary>
    /// Emit delegate field invocation logic (self-path via JUMP_INDIRECT, cross-path via SendCustomEvent).
    /// Shared by direct invocation (_callback.Invoke()) and conditional access (_callback?.Invoke()).
    /// </summary>
    HExpr EmitDelegateFieldInvocation(IInvocationOperation op, string fieldName, INamedTypeSymbol delegateType)
    {
        var invoke = delegateType.DelegateInvokeMethod;
        var (convArgs, convRet) = GetConventionFieldNames(delegateType);

        string retType = null;
        if (!invoke.ReturnsVoid)
            retType = GetUdonType(invoke.ReturnType);

        // 1. Evaluate all args ONCE (before branching to avoid double-evaluation)
        var argExprs = new List<HExpr>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argExprs.Add(VisitExpression(op.Arguments[i].Value));

        // 2. Write args to LOCAL convention fields
        for (int i = 0; i < argExprs.Count && i < convArgs.Length; i++)
            EmitStoreField(convArgs[i], argExprs[i]);

        // Load bundle fields
        var bundle = new DelegateBundle(fieldName);
        var target = LoadField(bundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver");
        var addr = LoadField(bundle.Addr, "SystemUInt32");
        var thisRef = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));

        // 3. Condition: target == this && addr != 0
        var isSelf = ExternCall(
            "UnityEngineObject.__op_Equality__UnityEngineObject_UnityEngineObject__SystemBoolean",
            new List<HExpr> { target, thisRef }, "SystemBoolean");
        var hasAddr = ExternCall(
            "SystemUInt32.__op_Inequality__SystemUInt32_SystemUInt32__SystemBoolean",
            new List<HExpr> { addr, Const(0u, "SystemUInt32") }, "SystemBoolean");
        var selfFast = ExternCall(
            "SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
            new List<HExpr> { isSelf, hasAddr }, "SystemBoolean");

        // For Func<T>: temp to receive result from both paths
        string resultVar = null;
        if (retType != null)
            resultVar = _ctx.DeclareLocal("__dlg_ret", retType);

        // 4-5. Branch: self (JUMP_INDIRECT) vs cross (SendCustomEvent)
        _builder.EmitIf(selfFast,
            // ── Self path: JUMP_INDIRECT (convention fields already written locally) ──
            _ =>
            {
                var indirectResult = InternalCall("__indirect", new List<HExpr> { addr }, retType ?? "SystemVoid");
                EmitExprStmt(indirectResult);
                if (retType != null)
                {
                    // Read return from convention ret field
                    EmitStoreField(resultVar, LoadField(convRet, retType));
                }
            },
            // ── Cross path: SetProgramVariable + SendCustomEvent ──
            _ =>
            {
                // Write convention fields on target via SetProgramVariable
                for (int i = 0; i < convArgs.Length; i++)
                {
                    var argType = GetUdonType(invoke.Parameters[i].Type);
                    EmitExternVoid(
                        "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<HExpr> { target, Const(convArgs[i], "SystemString"), LoadField(convArgs[i], argType) });
                }
                // SendCustomEvent with dynamic method name
                var method = LoadField(bundle.Method, "SystemString");
                EmitExternVoid(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
                    new List<HExpr> { target, method });
                // GetProgramVariable for return (Func only)
                if (retType != null)
                {
                    var retVal = ExternCall(
                        "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                        new List<HExpr> { target, Const(convRet, "SystemString") }, "SystemObject");
                    EmitStoreField(resultVar, retVal);
                }
            }
        );

        if (retType != null)
            return LoadField(resultVar, retType);
        return null;
    }

    // ── Generic Monomorphization ──

    void RegisterGenericSpecialization(IMethodSymbol constructed)
    {
        if (_methodFunctions.ContainsKey(constructed)) return;

        var slot = _ctx.RegisterMethod(constructed, i => i.ToString());
        var idx = slot.Index;

        var typeArgPart = string.Join("_", constructed.TypeArguments.Select(ExternResolver.GetUdonTypeName));
        var name = $"__{idx}_{SanitizeId(constructed.Name)}_{typeArgPart}";
        var func = _hirModule.AddFunction(name);
        _methodFunctions[constructed] = func;

        var gsParamIds = new string[constructed.Parameters.Length];
        for (int pi = 0; pi < constructed.Parameters.Length; pi++)
        {
            var param = constructed.Parameters[pi];
            var isDelegateParam = param.Type is INamedTypeSymbol nt2 && nt2.DelegateInvokeMethod != null;
            var udonType = isDelegateParam ? "SystemUInt32" : GetUdonType(param.Type);
            var paramId = $"__{idx}_{param.Name}__param";
            _ctx.DeclareVar(paramId, udonType);
            gsParamIds[pi] = paramId;
        }
        _methodParamVarIds[constructed] = gsParamIds;
        foreach (var pid in gsParamIds) func.ParamFieldNames.Add(pid);

        if (!constructed.ReturnsVoid)
        {
            var retType = GetUdonType(constructed.ReturnType);
            var retId = $"__{idx}_{SanitizeId(constructed.Name)}__ret";
            _ctx.DeclareVar(retId, retType);
            func.ReturnType = retType;
            func.ReturnSlots.Add(new ReturnSlot(retId, retType));
            _methodReturns[constructed] = new[] { new ReturnSlot(retId, retType) };
        }

        _pendingGenericSpecs.Add(constructed);
        DeclareDelegateConventionVars(constructed, idx);
    }

    void DeclareDelegateConventionVars(IMethodSymbol method, int idx)
    {
        foreach (var param in method.Parameters)
        {
            if (param.Type is not INamedTypeSymbol namedType || namedType.DelegateInvokeMethod == null)
                continue;

            var invoke = namedType.DelegateInvokeMethod;
            var argVarIds = new string[invoke.Parameters.Length];
            for (int j = 0; j < invoke.Parameters.Length; j++)
            {
                var argType = GetUdonType(invoke.Parameters[j].Type);
                argVarIds[j] = _ctx.DeclareVar($"__dlg_{idx}_{param.Name}_a{j}", argType);
            }
            string retVarId = null;
            if (!invoke.ReturnsVoid)
            {
                var retType = GetUdonType(invoke.ReturnType);
                retVarId = _ctx.DeclareVar($"__dlg_{idx}_{param.Name}_ret", retType);
            }
            _delegateParamConventions[(idx, param.Ordinal)] = new DelegateConvention
            {
                ArgVarIds = argVarIds, RetVarId = retVarId
            };
        }
    }

    // ── Lambda / Delegate Helpers ──

    static bool UnwrapLambdaFromArg(IOperation op, out IAnonymousFunctionOperation lambda)
    {
        while (true)
        {
            lambda = null;
            if (op is IDelegateCreationOperation { Target: IAnonymousFunctionOperation l })
            {
                lambda = l;
                return true;
            }

            if (op is not IConversionOperation conv)
                return false;
            op = conv.Operand;
        }
    }

    void HoistLambdaForDelegateParam(IAnonymousFunctionOperation lambda, DelegateConvention convention)
    {
        var symbol = lambda.Symbol;
        if (_methodFunctions.ContainsKey(symbol)) return;

        var slot = _ctx.RegisterMethod(symbol, i => i.ToString());
        var idx = slot.Index;
        var func = _hirModule.AddFunction($"__{idx}_lambda");
        _methodFunctions[symbol] = func;

        // Convention vars are used instead of standard param/ret vars.
        // Store convention arg var IDs as param IDs for consistency.
        _methodParamVarIds[symbol] = convention.ArgVarIds ?? System.Array.Empty<string>();

        // Override: use convention vars instead of standard param/ret vars
        _lambdaConventionOverrides[symbol] = convention;
        if (convention.RetVarId != null)
        {
            _methodReturns[symbol] = new[] { new ReturnSlot(convention.RetVarId, _ctx.GetFieldType(convention.RetVarId)) };
        }

        _pendingLocalFunctions.Add((symbol, func));
    }

    // ── Classification helpers ──

    bool IsForeignStatic(IMethodSymbol method)
    {
        // Extension methods: ReducedFrom holds the original static definition
        var resolved = method.ReducedFrom ?? method;
        if (!resolved.IsStatic) return false;
        if (resolved.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        if (ExternResolver.IsUdonSharpBehaviour(resolved.ContainingType)) return false;
        if (SymbolEqualityComparer.Default.Equals(resolved.ContainingType, _classSymbol)) return false;
        if (IsExternNamespace(resolved.ContainingType.ContainingNamespace)) return false;
        return true;
    }

    bool IsBaseInstanceMethod(IMethodSymbol method)
    {
        if (method.IsStatic) return false;
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, _classSymbol)) return false;
        if (USugarCompilerHelper.IsFrameworkNamespace(method.ContainingType.ContainingNamespace)) return false;
        if (method.ContainingType.Name == "UdonSharpBehaviour") return false;
        // Check ancestor chain
        var bt = _classSymbol.BaseType;
        while (bt != null)
        {
            if (SymbolEqualityComparer.Default.Equals(bt, method.ContainingType)) return true;
            bt = bt.BaseType;
        }
        return false;
    }

    bool IsResolvedConcreteNonBehaviour(ITypeSymbol type)
    {
        switch (type)
        {
            case null:
            // Type parameter: resolve via TypeParamMap
            case ITypeParameterSymbol when _typeParamMap == null:
                return false;
            case ITypeParameterSymbol tp:
            {
                if (!_typeParamMap.TryGetValue(tp, out var concrete)) return false;
                return !ExternResolver.IsUdonSharpBehaviour(concrete);
            }
        }

        // Concrete type: if not a UdonSharpBehaviour, interface calls should use extern
        if (type.TypeKind == TypeKind.Interface) return false; // can't determine yet
        return !ExternResolver.IsUdonSharpBehaviour(type);
    }

    /// <summary>
    /// Like IsFrameworkNamespace but excludes UdonSharp — types in UdonSharp.* that are not
    /// UdonSharpBehaviour may be user-defined helper classes with generic methods to inline.
    /// </summary>
    static bool IsExternNamespace(INamespaceSymbol ns)
    {
        if (ns == null || ns.IsGlobalNamespace) return false;
        var root = ns;
        while (root.ContainingNamespace is { IsGlobalNamespace: false })
            root = root.ContainingNamespace;
        return root.Name is "UnityEngine" or "VRC" or "TMPro" or "System";
    }
}
