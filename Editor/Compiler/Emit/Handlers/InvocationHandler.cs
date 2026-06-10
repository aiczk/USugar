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

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IInvocationOperation op => VisitInvocation(op),
        IObjectCreationOperation op => VisitObjectCreation(op),
        IPropertyReferenceOperation op => VisitPropertyReference(op),
        IInterpolatedStringOperation op => VisitInterpolatedString(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── VisitInvocation ──

    CLeaf VisitInvocation(IInvocationOperation op)
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

        // Nullable<T>.GetValueOrDefault() / GetValueOrDefault(fallback) → the value, else the fallback/default.
        if (op.Instance != null && target.Name == "GetValueOrDefault"
            && EmitContext.IsNullableT(target.ContainingType, out var govUnderlying))
            return EmitNullableGetValueOrDefault(op, govUnderlying);

        // Virtual dispatch through `this`: a call to a virtual/override/abstract method must bind to the
        // most-derived override in the COMPILED type, even when the call site is in an INHERITED base
        // method whose static target is the base declaration. base.M() and calls on other objects
        // (cross-behaviour) are excluded. Without this a base method runs the base body, not the override.
        if ((target.IsVirtual || target.IsOverride || target.IsAbstract)
            && target.MethodKind == MethodKind.Ordinary
            && op.Instance is IInstanceReferenceOperation iref
            && iref.Syntax is not Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax
            && ResolveMostDerivedOverride(target) is { } derivedOverride
            && !SymbolEqualityComparer.Default.Equals(derivedOverride, target))
            target = derivedOverride;

        switch (target.MethodKind)
        {
            // Delegate invocation: a() where a is Action/Func
            case MethodKind.DelegateInvoke:
                return VisitDelegateInvocation(op);
            // Local function call. Recursion (including captured-by-reference outer locals, which stay
            // shared per C# closure semantics) is handled by EmitCallToMethod's software-stack spill/reload.
            case MethodKind.LocalFunction
                when _methodFunctions.ContainsKey(target):
                return EmitUserMethodCall(op, target);
        }

        // User-struct instance method: v.Method(...) — receiver object[] passed as synthetic param0.
        if (!target.IsStatic && target.MethodKind == MethodKind.Ordinary
            && target.ContainingType is INamedTypeSymbol structRecv && EmitContext.IsUserStruct(structRecv)
            && _methodFunctions.ContainsKey(target.OriginalDefinition))
            return EmitStructInstanceCall(op, target.OriginalDefinition);

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
            var args = new List<CLeaf>();
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
                var args = new List<CLeaf>();
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
            && !target.IsStatic
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

    /// <summary>Most-derived override of <paramref name="baseMethod"/> reachable from the compiled type
    /// (_classSymbol), or baseMethod itself if none — mirrors C# virtual dispatch for a `this` call whose
    /// static target is a base declaration.</summary>
    IMethodSymbol ResolveMostDerivedOverride(IMethodSymbol baseMethod)
    {
        var def = baseMethod.OriginalDefinition;
        for (var t = _classSymbol; t != null; t = t.BaseType)
            foreach (var m in t.GetMembers(baseMethod.Name).OfType<IMethodSymbol>())
                for (IMethodSymbol o = m; o != null; o = o.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, def))
                        return m;
        return baseMethod;
    }

    // Nullable<T>.GetValueOrDefault: HasValue ? Value : (fallback arg or default(T)).
    CLeaf EmitNullableGetValueOrDefault(IInvocationOperation op, ITypeSymbol underlying)
    {
        var uType = GetUdonType(underlying);
        // For an aggregate (struct/tuple) underlying, the present value is a boxed object[] aliasing the
        // nullable's storage — deep-clone it out (value semantics). default(T) for an aggregate is a fresh
        // zero-initialized struct, NOT null, so use EmitNewAggregate rather than the scalar value default.
        var aggType = ResolveType(underlying) as INamedTypeSymbol;
        bool aggResult = aggType != null && EmitContext.IsAggregateType(aggType);
        // nv is the boxed nullable (SystemObject) bound once under ANF — re-readable for the HasValue test
        // and the present-value branch without a snapshot slot.
        var nv = VisitExpression(op.Instance);
        var resultSlot = _ctx.AllocTemp(uType);
        var fallback = op.Arguments.Length > 0
            ? VisitExpression(op.Arguments[0].Value)
            : (aggResult ? EmitNewAggregate(aggType) : EmitValueTypeDefault(uType));
        EmitAssign(resultSlot, fallback);
        _builder.EmitIf(EmitNullableHasValue(nv),
            _ => EmitAssign(resultSlot, aggResult ? EmitDeepCloneAggregate(nv, aggType) : nv));
        return SlotRef(resultSlot);
    }

    // User-struct instance method call: receiver object[] passed (uncloned) as synthetic param0
    // so `this`-field mutations reflect back to the caller's local (value-type by-ref `this` semantics).
    CLeaf EmitStructInstanceCall(IInvocationOperation op, IMethodSymbol target)
    {
        // Recursion (including the receiver) is handled by EmitCallToMethod's software-stack spill/reload.
        var args = new List<CLeaf> { LoadInstanceRaw(op.Instance) };
        for (var i = 0; i < op.Arguments.Length; i++)
            args.Add(VisitExpression(op.Arguments[i].Value));
        return EmitCallToMethod(target, args);
    }

    // ── Delegate Invocation (design §2.6: single unified dispatch for ALL invoke shapes) ──

    CLeaf VisitDelegateInvocation(IInvocationOperation op)
    {
        var delegateType = op.TargetMethod.ContainingType as INamedTypeSymbol;

        // ?.Invoke: NullableHandler already evaluated the receiver to the BUNDLE leaf, guarded
        // bundle-null (C#-strict silent skip — args are NOT evaluated on null, fcd06), and pushed the
        // leaf; dispatch runs inside its non-null branch with silent guard-failure arms.
        if (op.Instance is IConditionalAccessInstanceOperation && _conditionalAccessStack.Count > 0)
            return EmitDelegateDispatch(_conditionalAccessStack.Peek(), delegateType, op, isConditional: true);

        // Every other shape — field / local / param / array element / property / struct member /
        // call result / object[] cast-back — yields the bundle reference through the generic visit.
        return EmitDelegateDispatch(VisitExpression(op.Instance), delegateType, op, isConditional: false);
    }

    /// <summary>
    /// §2.6 unified delegate dispatch: full guard ladder (bundle-null / target-null / target-identity +
    /// addr≠0 self-vs-cross / method-null) around the self JUMP_INDIRECT and cross SendCustomEvent arms.
    /// Null-invoke deviation (§2.6/§8-8): LogError + skip + default(T) result — never a VM halt, never
    /// P5d's silent jump-to-0. ?.Invoke is C#-strict instead: silent skip, no LogError.
    /// Clobber-window invariants (§3.3, pinned): (1) all argument expressions are fully evaluated to ANF
    /// scratch slots BEFORE the first conv store; (2) between the conv stores and the JUMP/SendCustomEvent
    /// only pure guard externs run; (4) the conv ret is materialized to a fresh slot immediately after
    /// dispatch — never returned as a raw LoadField leaf (fcd11/12).
    /// </summary>
    CLeaf EmitDelegateDispatch(CLeaf bundle, INamedTypeSymbol delegateType, IInvocationOperation op, bool isConditional)
    {
        var invoke = delegateType.DelegateInvokeMethod;
        // §3.4-1: the conv-var declaration side re-validates ref/out — a delegate VALUE received from
        // elsewhere (param/field/cast-back) never went through this class's creation-site validation,
        // and a copy-in-only conv protocol would silently drop ref/out write-backs.
        DelegateAbi.ValidateNoRefOutParams(invoke);
        var (convArgs, convRet) = GetConventionFieldNames(delegateType, _typeParamMap);

        // The __dlgc_ conv vars are a signature-keyed cross-program byte contract (§3.2). Bridges declare
        // the same names for their own sigs; the dispatch site declares-on-first-use for foreign sigs.
        for (int i = 0; i < convArgs.Length; i++)
            _ctx.TryDeclareVar(convArgs[i], GetUdonType(invoke.Parameters[i].Type));
        string retType = null;
        if (!invoke.ReturnsVoid)
        {
            retType = GetUdonType(invoke.ReturnType);
            _ctx.TryDeclareVar(convRet, retType);
        }

        // C# evaluation order: a plain d(args) runs the argument side effects even when d is null (the
        // NRE follows them). For ?.Invoke this whole sequence sits inside NullableHandler's non-null
        // branch, so args are correctly unevaluated on a null bundle.
        var argExprs = new List<CLeaf>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argExprs.Add(VisitExpression(op.Arguments[i].Value));

        // retSlot pre-initialized to default(T): every guard-failure arm falls through with it (§2.6).
        int retSlot = -1;
        if (retType != null)
        {
            retSlot = _ctx.AllocTemp(retType);
            EmitAssign(retSlot, DefaultConst(retType));
        }

        // Guard-failure arm: LogError (NRE deviation, exact message per §2.6) — or silent for ?.Invoke.
        System.Action<CoreBuilder> failArm = null;
        if (!isConditional)
            failArm = _ =>
                EmitExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                    new List<CLeaf> { Const(
                        $"USugar: NullReferenceException — invoked a null delegate ({_classSymbol.Name}.{DescribeDelegateReceiver(op.Instance)})",
                        "SystemString") });

        void EmitGuardedDispatch()
        {
            // tgt is a SystemObject temp fed to externs directly — no Convert needed (P1/P5a).
            var tgt = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CLeaf> { bundle, Const(DelegateAbi.Target, "SystemInt32") }, "SystemObject");
            // target-null guard: unset element, or the in-game security filter nulling bundle[0].
            var tOk = ExternCall("UnityEngineObject.__op_Inequality__UnityEngineObject_UnityEngineObject__SystemBoolean",
                new List<CLeaf> { tgt, Const(null, "SystemObject") }, "SystemBoolean");
            _builder.EmitIf(tOk, _ =>
            {
                // Conv stores: the FINAL writes before dispatch (§3.3 clobber discipline).
                for (int i = 0; i < argExprs.Count && i < convArgs.Length; i++)
                    EmitStoreField(convArgs[i], argExprs[i]);

                var adr = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<CLeaf> { bundle, Const(DelegateAbi.Addr, "SystemInt32") }, "SystemUInt32");
                var mtd = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<CLeaf> { bundle, Const(DelegateAbi.Method, "SystemInt32") }, "SystemString");
                var thisType = GetUdonType(_classSymbol);
                var thisRef = LoadField(_ctx.DeclareThisOnce(thisType), thisType);
                // Self/cross is decided by TARGET IDENTITY only (P6) — addr≠0 merely qualifies the
                // fast path (addr is meaningless across program boundaries; 0-addr JUMP_INDIRECT would
                // silently jump to bytecode 0, P5d — addr is only ever read inside this guard).
                var isSelf = ExternCall("UnityEngineObject.__op_Equality__UnityEngineObject_UnityEngineObject__SystemBoolean",
                    new List<CLeaf> { tgt, thisRef }, "SystemBoolean");
                var hasAddr = ExternCall("SystemUInt32.__op_Inequality__SystemUInt32_SystemUInt32__SystemBoolean",
                    new List<CLeaf> { adr, Const(0u, "SystemUInt32") }, "SystemBoolean");
                var selfFast = ExternCall("SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                    new List<CLeaf> { isSelf, hasAddr }, "SystemBoolean");
                _builder.EmitIf(selfFast,
                    _ =>
                    {
                        // SELF: JUMP_INDIRECT into the bridge __body (EmitCallIndirect verbatim, P5b).
                        EmitInternalVoid("__indirect", new List<CLeaf> { adr });
                        // Immediate conv-ret materialization (§3.3-4, fcd11/12 invariant).
                        if (retType != null)
                            EmitAssign(retSlot, LoadField(convRet, retType));
                    },
                    _ =>
                    {
                        // CROSS — includes a foreign-minted bundle with target==this && addr==0, which
                        // correctly falls to a self-addressed SendCustomEvent.
                        // method-null guard: hand-rolled object[] bundles cast back to a delegate (§2.6).
                        var mOk = ExternCall("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                            new List<CLeaf> { mtd, Const(null, "SystemObject") }, "SystemBoolean");
                        _builder.EmitIf(mOk, _ =>
                        {
                            for (int i = 0; i < convArgs.Length; i++)
                            {
                                var argType = GetUdonType(invoke.Parameters[i].Type);
                                EmitExternVoid(
                                    "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                                    new List<CLeaf> { tgt, Const(convArgs[i], "SystemString"), LoadField(convArgs[i], argType) });
                            }
                            EmitExternVoid(
                                "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
                                new List<CLeaf> { tgt, mtd });
                            if (retType != null)
                                EmitAssign(retSlot, ExternCall(
                                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                                    new List<CLeaf> { tgt, Const(convRet, "SystemString") }, "SystemObject"));
                        }, failArm);
                    });
            }, failArm);
        }

        if (isConditional)
        {
            // Bundle-null was already guarded by the conditional access (silent skip, args unevaluated).
            EmitGuardedDispatch();
        }
        else
        {
            var nb = ExternCall("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                new List<CLeaf> { bundle, Const(null, "SystemObject") }, "SystemBoolean");
            _builder.EmitIf(nb, _ => EmitGuardedDispatch(), failArm);
        }

        return retSlot >= 0 ? SlotRef(retSlot) : null;
    }

    /// <summary>default(T) constant for the dispatch retSlot pre-init (§2.6). Non-primitive Udon types
    /// (objects, arrays, bundles, SDK structs) approximate with null — only observable on the
    /// null-invoke deviation path, which is already a documented deviation (§8-8).</summary>
    CConst DefaultConst(string udonType) => udonType switch
    {
        "SystemBoolean" => Const(false, udonType),
        "SystemInt32" => Const(0, udonType),
        "SystemUInt32" => Const(0u, udonType),
        "SystemInt64" => Const(0L, udonType),
        "SystemUInt64" => Const(0UL, udonType),
        "SystemInt16" => Const((short)0, udonType),
        "SystemUInt16" => Const((ushort)0, udonType),
        "SystemSByte" => Const((sbyte)0, udonType),
        "SystemByte" => Const((byte)0, udonType),
        "SystemSingle" => Const(0f, udonType),
        "SystemDouble" => Const(0d, udonType),
        "SystemDecimal" => Const(0m, udonType),
        "SystemChar" => Const('\0', udonType),
        _ => Const(null, udonType),
    };

    /// <summary>Best-effort member name for the null-invoke LogError message ({Class}.{member}).</summary>
    static string DescribeDelegateReceiver(IOperation instance)
    {
        var i = instance != null ? UnwrapConversions(instance) : null;
        return i switch
        {
            IFieldReferenceOperation f => f.Field.Name,
            IPropertyReferenceOperation p => p.Property.Name,
            ILocalReferenceOperation l => l.Local.Name,
            IParameterReferenceOperation pr => pr.Parameter.Name,
            _ => "delegate",
        };
    }

    // ── Generic Monomorphization ──

    void RegisterGenericSpecialization(IMethodSymbol constructed)
    {
        if (_methodFunctions.ContainsKey(constructed)) return;

        var slot = _ctx.RegisterMethod(constructed, i => i.ToString());
        var idx = slot.Index;

        var typeArgPart = string.Join("_", constructed.TypeArguments.Select(ExternResolver.GetUdonTypeName));
        var name = $"__{idx}_{SanitizeId(constructed.Name)}_{typeArgPart}";
        var func = _module.AddFunction(name);
        _methodFunctions[constructed] = func;

        var gsParamIds = new string[constructed.Parameters.Length];
        for (int pi = 0; pi < constructed.Parameters.Length; pi++)
        {
            var param = constructed.Parameters[pi];
            var paramId = $"__{idx}_{param.Name}__param";
            _ctx.DeclareVar(paramId, GetUdonType(param.Type));
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
    }

    // ── Classification helpers ──

    bool IsForeignStatic(IMethodSymbol method)
    {
        // Extension methods: ReducedFrom holds the original static definition
        var resolved = method.ReducedFrom ?? method;
        if (!resolved.IsStatic) return false;
        if (resolved.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        // A static method has no instance, so a call to one on another user UdonSharpBehaviour subclass cannot
        // be a cross-program SendCustomEvent — it must be inlined like any other foreign static. (The base
        // UdonSharpBehaviour and SDK behaviours have no syntax and are already excluded above.)
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
