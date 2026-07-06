using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler : HandlerBase, IExpressionHandler
{
    public InvocationHandler(EmitContext ctx) : base(ctx) { }

    public OperationKind[] HandledKinds { get; } = new[]
    {
        OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.PropertyReference, OperationKind.InterpolatedString,
    };

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IInvocationOperation op => VisitInvocation(op),
        IObjectCreationOperation op => VisitObjectCreation(op),
        IPropertyReferenceOperation op => VisitPropertyReference(op),
        IInterpolatedStringOperation op => VisitInterpolatedString(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── VisitInvocation ──

    /// <summary>Wave-9 round-4 [X1] + round-5 [X8]/[X12]: delegate value equality through .Equals.
    /// Instance a.Equals(b) and static object.Equals(a, b) on delegate bundles are VALUE equality in
    /// the CLR — route both through the same (target, method) element comparison as the `==` operator
    /// (CompareDelegates, §2.5). A DIFFERENT delegate type compares false regardless of target/method
    /// (GetType() inequality) — emit the evaluations (C# evaluates both operands) and the constant.
    /// Mixed delegate/non-delegate operands stay loud (§8-3). [X12]: the static form previously fell
    /// through to an object.Equals extern comparing the two bundle object[] REFERENCES — reference
    /// inequality where the CLR sees value equality.</summary>
    bool TryEmitDelegateEquals(IInvocationOperation op, out CLeaf result)
    {
        result = null;
        var target = op.TargetMethod;
        if (target.Name != "Equals") return false;

        // Instance form: a.Equals(b) on a delegate-typed receiver (round-4 [X1]). Extern resolution
        // mapped the System.Func receiver onto UnityEngineComponent and emitted a nonexistent
        // __Equals__SystemObject__SystemBoolean extern (loud crash on legal C#).
        if (op.Instance != null && !target.IsStatic
            && op.Arguments.Length == 1 && IsDelegateTyped(op.Instance.Type))
        {
            var eqArg = UnwrapConversions(op.Arguments[0].Value);
            if (!IsDelegateTyped(eqArg.Type))
                throw new System.NotSupportedException(
                    "Delegate .Equals(...) with a non-delegate argument is not supported. "
                    + "Compare two delegate values (or use ==).");
            if (!SymbolEqualityComparer.Default.Equals(op.Instance.Type, eqArg.Type))
            {
                VisitExpression(op.Instance);
                VisitExpression(eqArg);
                result = Const(false, "SystemBoolean");
                return true;
            }
            result = CompareDelegates(VisitExpression(op.Instance), VisitExpression(eqArg), isNotEquals: false);
            return true;
        }

        // Static form: object.Equals(a, b) with delegate-typed operands (round-5 [X12]).
        if (target.IsStatic && target.ContainingType.SpecialType == SpecialType.System_Object
            && op.Arguments.Length == 2)
        {
            var lhs = UnwrapConversions(op.Arguments[0].Value);
            var rhs = UnwrapConversions(op.Arguments[1].Value);
            var lhsDlg = IsDelegateTyped(lhs.Type);
            var rhsDlg = IsDelegateTyped(rhs.Type);
            if (!lhsDlg && !rhsDlg) return false; // not a delegate comparison — existing extern path
            if (lhsDlg != rhsDlg)
                throw new System.NotSupportedException(
                    "object.Equals(...) mixing a delegate and a non-delegate operand is not supported. "
                    + "Compare two delegate values (or use ==).");
            if (!SymbolEqualityComparer.Default.Equals(lhs.Type, rhs.Type))
            {
                VisitExpression(lhs);
                VisitExpression(rhs);
                result = Const(false, "SystemBoolean");
                return true;
            }
            result = CompareDelegates(VisitExpression(lhs), VisitExpression(rhs), isNotEquals: false);
            return true;
        }

        return false;
    }

    CLeaf VisitInvocation(IInvocationOperation op)
    {
        // Wave-9 round-5 [X8]: the delegate-Equals arms run BEFORE the erasing-channel argument
        // guard — the operands are consumed HERE by the value comparison, never laundered through
        // Equals' erasing System.Object parameter, but the guard saw that parameter first and
        // loud-rejected a legal comparison whose argument was a delegate-typed PARAM.
        if (TryEmitDelegateEquals(op, out var dlgEqResult))
            return dlgEqResult;

        // Resolve type parameters in generic method type arguments (e.g., Min<T> → Min<int>)
        var target = SubstituteMethodTypeArgs(op.TargetMethod);

        // B67: user-enum.ToString() → synthesized value→name helper (the inherited Enum.ToString would
        // resolve to the underlying integer's ToString and print the number). Flags enums reject inside.
        if (target.Name == "ToString" && target.Parameters.Length == 0 && op.Instance != null
            && ExternResolver.IsUserEnum(ResolveType(op.Instance.Type)))
            return TryEmitEnumToString(VisitExpression(op.Instance), op.Instance.Type);

        // Nullable<T>.GetValueOrDefault() / GetValueOrDefault(fallback) → the value, else the fallback/default.
        if (op.Instance != null && target.Name == "GetValueOrDefault"
            && EmitPolicy.IsNullableT(target.ContainingType, out var govUnderlying))
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
            // Round-9 [Y9]: non-generic local function called BEFORE its declaration statement
            // (C# allows forward references within the enclosing body, but registration used to
            // happen only when StatementHandler reached the ILocalFunctionOperation — the earlier
            // call site then died with 'Method not found in layout'). Register on demand; the
            // declaration-site registration above stays first so declaration-first shapes keep
            // their index allocation order byte-identical.
            case MethodKind.LocalFunction
                when !target.IsGenericMethod:
                RegisterLocalFunction(target);
                return EmitUserMethodCall(op, target);
            // B68/B69: a GENERIC local function is monomorphized per call site regardless of its HOST —
            // the compiled behaviour, a foreign static helper class, or a user struct. The generic
            // monomorphization arm further below is gated on the container being _classSymbol (own members),
            // so a foreign-hosted generic LF used to fall through to the extern path and mint a bogus
            // `{Host}.__Lf__{T}` extern (B68) or hit the user-struct-member guard (B69). Register+jump here
            // by MethodKind, before any container-gated arm; RegisterGenericSpecialization is container-
            // agnostic and idempotent, so the behaviour-host path stays byte-identical.
            case MethodKind.LocalFunction:
                RegisterGenericSpecialization(target);
                return EmitUserMethodCall(op, target);
        }

        // User-struct instance method: v.Method(...) — receiver object[] passed as synthetic param0.
        // Feature G: dispatch by the CONSTRUCTED symbol (Box<int>.Get(), not Box<T>.Get()) — target is
        // already the right constructed spec here, whether from an outer call site (Roslyn hands us
        // the concretely-typed receiver's member directly) or a self/cross-struct-method call inside
        // another generic struct method's own body (SubstituteMethodTypeArgs re-closes it above). But a
        // method reached ONLY via such an internal self/sibling reference was never pre-collected
        // (CollectStructMethodsInOperation deliberately skips the open form) — register it on demand,
        // like a generic method's own on-demand arm below (wave-14 residual gap).
        if (!target.IsStatic && target.MethodKind == MethodKind.Ordinary
            && target.ContainingType is INamedTypeSymbol structRecv && EmitPolicy.IsObjectArrayEmulated(structRecv))
        {
            // CA-M1: a v1 class instance method rides the SAME param0-receiver path. The receiver bundle
            // flows by reference (EmitStructInstanceCall's defensive copy stays gated on IsAggregateType,
            // which is false for a class — so mutations through the receiver are visible to every alias).
            var structTarget = ResolveStructMember(target);
            // B56: a struct-hosted generic method must record its instantiation so a nested closure/LF
            // referencing the method's T finds the owner in the closure-compose (the class arm does this
            // via RegisterGenericSpecialization; the struct path registers the spec separately).
            if (structTarget.IsGenericMethod)
                RegisterFirstGenericSpec(structTarget);
            return EmitStructInstanceCall(op, structTarget);
        }

        // CA-M1: Object-inherited method on a v1 class receiver (target owner = System.Object, i.e. the
        // method is NOT overridden — an override carries the `override` keyword and is rejected at the
        // declaration). Equals(object) → reference compare on the two object[] bundles (unoverridden
        // object.Equals IS reference equality for a class). GetHashCode / ToString / GetType → loud reject
        // (no stable hash, no member-name synthesis, no runtime type identity for a reference bundle in v1).
        if (!target.IsStatic && op.Instance?.Type is INamedTypeSymbol clsRecv && EmitPolicy.IsUserClassType(clsRecv)
            && target.ContainingType.SpecialType == SpecialType.System_Object)
        {
            if (target.Name == "Equals" && op.Arguments.Length == 1)
            {
                var lhs = VisitExpression(op.Instance);
                var rhs = VisitExpression(op.Arguments[0].Value);
                return ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
                    new List<CLeaf> { lhs, rhs }, "SystemBoolean");
            }
            throw new System.NotSupportedException(
                $"'{clsRecv.Name}.{target.Name}()' is not supported on a v1 user class: class ABI v1 gives a "
                + "reference bundle no stable hash, no member-name synthesis, and no runtime type identity. "
                + "Use reference equality (== / Equals) or format the class's fields directly.");
        }

        // Receiver identity (predates fcd-stage1): an instance method of THIS class family invoked
        // through a NON-this receiver (same-class field/local, base-typed local, cast) used to
        // direct-JUMP the locally registered function — the receiver was NEVER read, so the call
        // self-executed on the caller's heap (VM-verified: a NULL receiver silently returned the
        // caller's own value where the CLR throws), and a base-TYPED receiver ran the never-exported
        // base-instance copy (base body, or the bodiless abstract stub's stale 0/null return) instead
        // of the override (VM 3/0 vs CLR 5). Route through the cross-behaviour path: SetProgramVariable
        // + SendCustomEvent executes on the RECEIVER's program, and the override-chain-ROOT export name
        // (GetCalleeLayout normalization) dispatches that program's own most-derived override — true
        // virtual dispatch. Non-public targets have no exported entry point (SendCustomEvent would
        // silently no-op), generic targets have no per-specialization layout, and ref/out params cannot
        // round-trip through SetProgramVariable — all loud per design §8-3.
        if (!target.IsStatic && target.MethodKind == MethodKind.Ordinary
            && op.Instance != null && op.Instance is not IInstanceReferenceOperation
            && ExternResolver.IsUdonSharpBehaviour(target.ContainingType)
            && target.ContainingType.Name != "UdonSharpBehaviour"
            && (SymbolEqualityComparer.Default.Equals(target.ContainingType, _classSymbol)
                || IsBaseInstanceMethod(target)))
        {
            if (target.IsGenericMethod || target.Parameters.Any(p => p.RefKind != RefKind.None)
                || (target.DeclaredAccessibility != Accessibility.Public
                    && !LayoutPlanner.UdonEventNames.ContainsKey(target.Name)))
                throw new System.NotSupportedException(
                    $"Instance method '{target.Name}' of the compiled class family is called through a "
                    + "non-this receiver, which dispatches cross-program (SetProgramVariable + "
                    + "SendCustomEvent) and so needs a public, non-generic target without ref/out "
                    + "parameters. Make the method public, or call it through 'this'.");
            return EmitCrossClassCall(op, target);
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

        // Wave-9 round-8 [Y11]: INHERITED generic callee whose call site carries OPEN type args
        // (`P2<T>(x)` inside the derived class's own generic body). The phase-1 base-copy collector
        // only registers CLOSED call-site symbols (an open form has no single monomorphization), so
        // when the enclosing specialization's map closes the symbol HERE (SubstituteMethodTypeArgs
        // above), register it as an on-demand generic specialization — EmitMethod resolves the body
        // from the base declaration's own syntax tree, exactly like a same-class spec. Without this
        // the call fell through to the cross-class/extern arms (decon: 'Method P2 not found in
        // layout for MB1Base'; direct: bogus IUdonEventReceiver extern — loud ICE on legal C#).
        if (target.IsGenericMethod && !target.TypeArguments.Any(ta => ta is ITypeParameterSymbol)
            && IsBaseInstanceMethod(target))
        {
            RegisterGenericSpecialization(target);
            return EmitUserMethodCall(op, target);
        }

        // Generic foreign static method → monomorphize and emit as internal call
        if (target.IsGenericMethod && IsForeignStatic(target))
        {
            GuardRefOutArguments(op, target); // round-8 [R6]: Q2/Q5/R4 parity
            var constructed = target.ReducedFrom != null
                ? target.ReducedFrom.OriginalDefinition.Construct(target.TypeArguments.ToArray())
                : target.OriginalDefinition.Construct(target.TypeArguments.ToArray());
            RegisterGenericSpecialization(constructed);
            var args = new List<CLeaf>();
            if (target.ReducedFrom != null && op.Instance != null)
            {
                args.Add(VisitExpression(op.Instance));
            }
            Dictionary<int, System.Action<CLeaf>> genPrepared = null;
            List<(int slot, System.Func<CLeaf> read)> genDeferred = null;
            for (var i = 0; i < op.Arguments.Length; i++)
            {
                // Wave-9 round-8 [Y12]: evaluate leg-bearing ref/out lvalue legs once (copy-back
                // reuses them). Round-9 [Y16]: defer the value read past later effectful args.
                var (val, deferredRead, store) = EvaluateCallArgument(op, i);
                if (store != null)
                    (genPrepared ??= new Dictionary<int, System.Action<CLeaf>>())[i] = store;
                if (deferredRead != null)
                {
                    (genDeferred ??= new List<(int, System.Func<CLeaf>)>()).Add((args.Count, deferredRead));
                    args.Add(null);
                }
                else
                    args.Add(val);
            }
            if (genDeferred != null)
                foreach (var (slot, read) in genDeferred)
                    args[slot] = read();
            var genResult = EmitCallToMethod(constructed, args);
            // Round-8 [R6]: this arm used to drop the ref/out copy-back (DiffFuzz: ref=9 vs VM 1).
            // Reduced-extension argument ordinals shift by 1 onto the original's params (this=0).
            EmitRefOutCopyBack(op, constructed,
                target.ReducedFrom != null && op.Instance != null ? 1 : 0, genPrepared);
            return genResult;
        }

        // Foreign static method → inlined as internal call (resolve extension method original form)
        {
            var original = target.ReducedFrom ?? target;
            if (IsForeignStatic(target) && _methodFunctions.ContainsKey(original))
            {
                GuardRefOutArguments(op, target); // round-8 [R6]: Q2/Q5/R4 parity
                var args = new List<CLeaf>();
                // Extension method: instance is the first (this) parameter
                if (target.ReducedFrom != null && op.Instance != null)
                {
                    args.Add(VisitExpression(op.Instance));
                }
                Dictionary<int, System.Action<CLeaf>> fsPrepared = null;
                List<(int slot, System.Func<CLeaf> read)> fsDeferred = null;
                for (var i = 0; i < op.Arguments.Length; i++)
                {
                    // Wave-9 round-8 [Y12]: evaluate leg-bearing ref/out lvalue legs once (copy-back
                    // reuses them). Round-9 [Y16]: defer the value read past later effectful args.
                    var (val, deferredRead, store) = EvaluateCallArgument(op, i);
                    if (store != null)
                        (fsPrepared ??= new Dictionary<int, System.Action<CLeaf>>())[i] = store;
                    if (deferredRead != null)
                    {
                        (fsDeferred ??= new List<(int, System.Func<CLeaf>)>()).Add((args.Count, deferredRead));
                        args.Add(null);
                    }
                    else
                        args.Add(val);
                }
                if (fsDeferred != null)
                    foreach (var (slot, read) in fsDeferred)
                        args[slot] = read();
                var fsResult = EmitCallToMethod(original, args);
                // Round-8 [R6]: this arm used to drop the ref/out copy-back (DiffFuzz: ref=6 vs VM 1).
                EmitRefOutCopyBack(op, original,
                    target.ReducedFrom != null && op.Instance != null ? 1 : 0, fsPrepared);
                return fsResult;
            }
        }

        // Wave-14 r4: a NON-generic static method on a CONSTRUCTED generic user type (Helper<U>.Boost,
        // U = the enclosing generic context's type argument) is monomorphized per closed containing-type
        // exactly like a generic method — but target.IsGenericMethod is false so the generic-foreign-static
        // arm above skips it, and its closed form is never pre-registered: Phase-1's foreign-static
        // collector only walks class/base bodies (a generic struct/method body is emitted on demand, in
        // OPEN form), so the closed Helper<float>.Boost has no Phase-1 CFunction and the plain
        // foreign-static arm's ContainsKey misses. Fell through to a bogus SystemObjectArray.__Boost__
        // extern. Register the closed spec on demand (RegisterGenericSpecialization composes the containing
        // type's type-arg map in EmitMethod, so a T-dependent body monomorphizes correctly) and JUMP to it
        // — static, so no receiver and argument ordinals start at 0.
        if (IsForeignStatic(target) && target.ReducedFrom == null && !target.IsGenericMethod
            && target.ContainingType is INamedTypeSymbol fsGenCt && fsGenCt.IsGenericType
            && !fsGenCt.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
        {
            GuardRefOutArguments(op, target);
            RegisterGenericSpecialization(target);
            var args = new List<CLeaf>();
            Dictionary<int, System.Action<CLeaf>> gsPrepared = null;
            List<(int slot, System.Func<CLeaf> read)> gsDeferred = null;
            for (var i = 0; i < op.Arguments.Length; i++)
            {
                var (val, deferredRead, store) = EvaluateCallArgument(op, i);
                if (store != null)
                    (gsPrepared ??= new Dictionary<int, System.Action<CLeaf>>())[i] = store;
                if (deferredRead != null)
                {
                    (gsDeferred ??= new List<(int, System.Func<CLeaf>)>()).Add((args.Count, deferredRead));
                    args.Add(null);
                }
                else
                    args.Add(val);
            }
            if (gsDeferred != null)
                foreach (var (slot, read) in gsDeferred)
                    args[slot] = read();
            var gsResult = EmitCallToMethod(target, args);
            EmitRefOutCopyBack(op, target, 0, gsPrepared);
            return gsResult;
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

    // ResolveMostDerivedOverride moved to HandlerBase (round-9: StatementHandler's TCO gate needs
    // the same virtual-dispatch resolution — see VisitReturn).

    // Nullable<T>.GetValueOrDefault: HasValue ? Value : (fallback arg or default(T)).
    CLeaf EmitNullableGetValueOrDefault(IInvocationOperation op, ITypeSymbol underlying)
    {
        var uType = GetUdonType(underlying);
        // For an aggregate (struct/tuple) underlying, the present value is a boxed object[] aliasing the
        // nullable's storage — deep-clone it out (value semantics). default(T) for an aggregate is a fresh
        // zero-initialized struct, NOT null, so use EmitNewAggregate rather than the scalar value default.
        var aggType = ResolveType(underlying) as INamedTypeSymbol;
        bool aggResult = aggType != null && EmitPolicy.IsAggregateType(aggType);
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
        GuardRefOutArguments(op, target); // round-8 [R5]: Q2/Q5/R4 parity with EmitUserMethodCall

        // Recursion (including the receiver) is handled by EmitCallToMethod's software-stack spill/reload.
        var recv = LoadInstanceRaw(op.Instance);
        // Round-8 [R1]/[R7] (corrects the round-7 [Q4] over-clone, which was calibrated against a
        // wrong hand-computed oracle): Roslyn defensive-copies the receiver of a non-readonly
        // struct method when the chain is a READONLY access path — a value-typed FIELD link from a
        // foreach iteration variable (DiffFuzz: direct ref=1112 mutates the local, nested
        // s.inner.Bump() ref=102 copies) or a readonly FIELD link anywhere in the chain (DiffFuzz:
        // readonly rs.Bump();rs.Bump() ref=0 vs the live-storage 20). Chains through array
        // elements keep live storage (the helper stops there, reference semantics, CLR-equal).
        if (!target.IsReadOnly && ReceiverNeedsDefensiveCopy(op.Instance)
            && op.Instance?.Type is INamedTypeSymbol recvAgg && EmitPolicy.IsAggregateType(recvAgg))
            recv = EmitDeepCloneAggregate(recv, recvAgg);
        var args = new List<CLeaf> { recv };
        Dictionary<int, System.Action<CLeaf>> structPrepared = null;
        List<(int slot, System.Func<CLeaf> read)> structDeferred = null;
        for (var i = 0; i < op.Arguments.Length; i++)
        {
            // Wave-9 round-8 [Y12]: evaluate leg-bearing ref/out lvalue legs once (copy-back
            // reuses them). Round-9 [Y16]: defer the value read past later effectful args.
            var (val, deferredRead, store) = EvaluateCallArgument(op, i);
            if (store != null)
                (structPrepared ??= new Dictionary<int, System.Action<CLeaf>>())[i] = store;
            if (deferredRead != null)
            {
                (structDeferred ??= new List<(int, System.Func<CLeaf>)>()).Add((args.Count, deferredRead));
                args.Add(null);
            }
            else
                args.Add(val);
        }
        if (structDeferred != null)
            foreach (var (slot, read) in structDeferred)
                args[slot] = read();
        var result = EmitCallToMethod(target, args);
        // Round-8 [R5]: this path used to drop the ref/out copy-back entirely (DiffFuzz: ref-arg
        // ref=136 vs VM 106, out-arg ref=10 vs 0). Param ids are ordinal-indexed (receiver separate).
        EmitRefOutCopyBack(op, target, 0, structPrepared);
        return result;
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
        DelegateAbi.ValidateNoUserClassSignature(invoke); // §2-1: a class cannot ride the __dlgc_ channel
        var (convArgs, convRet, convEnv) = GetConventionFieldNames(delegateType, _typeParamMap);

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
        // Stage 2 §5.1: every dispatch site unconditionally stages DelegateAbi.Env → __dlgc_{sig}__env, so
        // declare it on first use here (a capture-free target sends null; the bridge's null guard is
        // the backstop). Declared at the dispatch site only — never in a capture-free bridge (§1.3).
        _ctx.TryDeclareVar(convEnv, EnvEmit.EnvType);

        // C# evaluation order: a plain d(args) runs the argument side effects even when d is null (the
        // NRE follows them). For ?.Invoke this whole sequence sits inside NullableHandler's non-null
        // branch, so args are correctly unevaluated on a null bundle.
        // Wave-9 round-2 [W1]: evaluate in TEXTUAL order (C# evaluation order) but slot each value at
        // its PARAMETER's ordinal — IInvocationOperation.Arguments is in call-site order for named/
        // reordered args, so indexing conv slots by textual position bound names positionally
        // (s(right: k, left: 9) DiffFuzz ref=902 vs VM 209). Mirrors EmitUserMethodCall's by-ordinal
        // slotting; the side-effect order itself was already correct (trace fields matched).
        var argExprs = new CLeaf[invoke.Parameters.Length];
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var argParam = op.Arguments[i].Parameter;
            var ordinal = argParam != null && argParam.Ordinal >= 0 ? argParam.Ordinal : i;
            var val = VisitExpression(op.Arguments[i].Value);
            if (ordinal < argExprs.Length) argExprs[ordinal] = val;
        }

        // §4.3: this dispatch can re-enter the containing function (synthetic-SCC cycle member,
        // non-tail site — pre-computed by BuildRecursionInfo). Flag BOTH dispatch arms Reentrant so
        // InsertRecursionSpills wraps them with the __recurStack frame spill/reload; tail dispatches
        // stay unflagged so bundle-driven deep tail recursion never spills (§4.4).
        bool reentrant = MarkReentrantDispatch(op);

        return EmitDelegateDispatchCore(bundle, invoke, convArgs, convRet, convEnv, retType, _typeParamMap,
            argExprs, isConditional, reentrant, DescribeDelegateReceiver(op.Instance));
    }

    /// <summary>
    /// Multicast design (2026-07-03 §1.2/§1.6) shared guard-ladder core, extracted from
    /// <see cref="EmitDelegateDispatch(CLeaf, INamedTypeSymbol, IInvocationOperation, bool)"/> so the
    /// synthetic fan-out bridge dispatches invocation-list elements through the IDENTICAL dispatch body
    /// single-cast call sites use (dispatch itself carries zero multicast awareness — this is the basis
    /// for single-cast byte-identity). <paramref name="argExprsByOrdinal"/> arrive ALREADY evaluated
    /// (the caller resolved them from either real call-site arguments or fan-out snapshot locals);
    /// <paramref name="reentrant"/> is decided by the caller (the fan-out element-dispatch site always
    /// passes false — reentrancy wiring is A-M3 scope, §1.6). <paramref name="typeParamMap"/> is used
    /// explicitly (never the ambient ctx map) so this stays correct when called from post-body-emission
    /// synthetic passes, mirroring EmitPendingDelegateBridges' resolvedTypeParamMap snapshot use.
    /// </summary>
    CLeaf EmitDelegateDispatchCore(CLeaf bundle, IMethodSymbol invoke, string[] convArgs, string convRet, string convEnv,
        string retType, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        CLeaf[] argExprs, bool isConditional, bool reentrant, string receiverDescription)
    {
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
                        $"USugar: NullReferenceException — invoked a null delegate ({_classSymbol.Name}.{receiverDescription})",
                        "SystemString") });

        void EmitGuardedDispatch()
        {
            var kind = ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                new List<CLeaf> { bundle, Const(DelegateAbi.Kind, "SystemInt32") }, "SystemString");
            var kindOk = ExternCall("SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
                new List<CLeaf> { kind, Const(DelegateAbi.KindTag, "SystemString") }, "SystemBoolean");
            _builder.EmitIf(kindOk, _ =>
            {
            // tgt is a SystemObject temp fed to externs directly — no Convert needed (P1/P5a).
            var tgt = ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                new List<CLeaf> { bundle, Const(DelegateAbi.Target, "SystemInt32") }, "SystemObject");
            // target-null guard: unset element, or the in-game security filter nulling DelegateAbi.Target.
            var tOk = ExternCall("UnityEngineObject.__op_Inequality__UnityEngineObject_UnityEngineObject__SystemBoolean",
                new List<CLeaf> { tgt, Const(null, "SystemObject") }, "SystemBoolean");
            _builder.EmitIf(tOk, _ =>
            {
                // Conv stores: the FINAL writes before dispatch (§3.3 clobber discipline).
                for (int i = 0; i < argExprs.Length && i < convArgs.Length; i++)
                    if (argExprs[i] != null)
                        EmitStoreField(convArgs[i], argExprs[i]);

                // Stage 2 §5.1: stage DelegateAbi.Env into the env conv global (unconditional, both arms —
                // the SELF arm's bridge reads this field directly; the CROSS arm SPVs it below). A
                // capture-free target sends null; the receiving bridge's null-env guard is the backstop.
                EmitStoreField(convEnv, ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                    new List<CLeaf> { bundle, Const(DelegateAbi.Env, "SystemInt32") }, EnvEmit.EnvType));

                var adr = ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                    new List<CLeaf> { bundle, Const(DelegateAbi.Addr, "SystemInt32") }, "SystemUInt32");
                var mtd = ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
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
                        EmitInternalVoid("__indirect", new List<CLeaf> { adr }, reentrant);
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
                                var argType = ExternResolver.GetUdonTypeName(invoke.Parameters[i].Type, typeParamMap);
                                EmitExternVoid(
                                    "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                                    new List<CLeaf> { tgt, Const(convArgs[i], "SystemString"), LoadField(convArgs[i], argType) });
                            }
                            // Stage 2 §5.1: forward the staged env to the receiver alongside the conv args
                            // (a missing-symbol SPV on a capture-free receiver is a proven silent no-op).
                            EmitExternVoid(
                                "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                                new List<CLeaf> { tgt, Const(convEnv, "SystemString"), LoadField(convEnv, EnvEmit.EnvType) });
                            EmitExternVoid(
                                "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
                                new List<CLeaf> { tgt, mtd }, reentrant);
                            if (retType != null)
                                EmitAssign(retSlot, ExternCall(
                                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                                    new List<CLeaf> { tgt, Const(convRet, "SystemString") }, "SystemObject"));
                        }, failArm);
                    });
            }, failArm);
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

    /// <summary>
    /// Multicast design (2026-07-03 §1.2/§1.6) fan-out per-element dispatch entry point: called by
    /// UasmEmitter's synthetic __dlg_fanout_{sig} bridge for EACH invocation-list element bundle,
    /// through the SAME guard ladder as a single-cast call site — dispatch itself carries zero
    /// multicast awareness, which is why single-cast dispatch bytes never move (§1.2/§6 gate).
    /// <paramref name="argExprsByOrdinal"/> are the bridge's fan-out-local snapshot slots (already
    /// re-staged this iteration by the caller). UNCONDITIONALLY reentrant (A-M3, §1.6): fan-out(sig) is
    /// always its own sig escape target by construction, and a sig-matched escape-set membership check
    /// only narrows the false case to an under-approximation hole (a multicast composed entirely from
    /// foreign-received bundles) — over-spilling here is strictly conservative and cheap (§8-3
    /// direction). InsertRecursionSpills protects the fan-out's OWN live-across-the-call slots (i/n/
    /// list/args-snapshot/ret) via its existing generic per-function slot-liveness machinery — no new
    /// spill category, since that pass is driven by CFunction.ReentrantSiteCount and post-coalesce
    /// slot liveness, not by the caller having an IMethodSymbol.
    /// </summary>
    internal CLeaf EmitFanoutElementDispatch(CLeaf bundle, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap, CLeaf[] argExprsByOrdinal)
    {
        // Stage 1.75 §2.3: use invoke DIRECTLY (not invoke.ContainingType.DelegateInvokeMethod) — a
        // wrapper's inner dispatch passes the WRAPPED bundle's own native protocol here, which for a
        // third-party-hinge inner bundle is a PLAIN method (e.g. GetStr), never itself a genuine
        // delegate Invoke method. Byte-identical for the pre-existing fan-out caller (invoke there
        // already IS the delegate's own Invoke method, so the old round-trip was a no-op derivation).
        var (convArgs, convRet, convEnv) = GetConventionFieldNames(invoke, typeParamMap);
        string retType = invoke.ReturnsVoid ? null : ExternResolver.GetUdonTypeName(invoke.ReturnType, typeParamMap);
        return EmitDelegateDispatchCore(bundle, invoke, convArgs, convRet, convEnv, retType, typeParamMap,
            argExprsByOrdinal, isConditional: false, reentrant: true, receiverDescription: "multicast fan-out");
    }

    /// <summary>Multicast design §1.4: exposes the existing element-equality leg (target+method+env,
    /// §2.5) for the synthetic combine/remove helpers' LastContiguousMatch search — reused verbatim,
    /// never re-derived, so the `-=` removal semantics can never drift from `==`'s element leg.</summary>
    internal CLeaf EmitDelegateElementEquals(CLeaf a, CLeaf b) => CompareDelegates(a, b, isNotEquals: false);

    /// <summary>default(T) constant for the dispatch retSlot pre-init (§2.6). Non-primitive Udon types
    /// (objects, arrays, bundles, SDK structs) approximate with null — only observable on the
    /// null-invoke deviation path, which is already a documented deviation (§8-8).</summary>
    CConst DefaultConst(string udonType) => DefaultConst(_builder, udonType);

    /// <summary>Shared default(T) const builder (dispatch retSlot pre-init + Stage 2 §5.1 bridge
    /// null-env arm). Static so UasmEmitter's bridge emission reuses the same mapping.</summary>
    internal static CConst DefaultConst(CoreBuilder b, string udonType) => udonType switch
    {
        "SystemBoolean" => b.Const(false, udonType),
        "SystemInt32" => b.Const(0, udonType),
        "SystemUInt32" => b.Const(0u, udonType),
        "SystemInt64" => b.Const(0L, udonType),
        "SystemUInt64" => b.Const(0UL, udonType),
        "SystemInt16" => b.Const((short)0, udonType),
        "SystemUInt16" => b.Const((ushort)0, udonType),
        "SystemSByte" => b.Const((sbyte)0, udonType),
        "SystemByte" => b.Const((byte)0, udonType),
        "SystemSingle" => b.Const(0f, udonType),
        "SystemDouble" => b.Const(0d, udonType),
        "SystemDecimal" => b.Const(0m, udonType),
        "SystemChar" => b.Const('\0', udonType),
        _ => b.Const(null, udonType),
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

    // IsResolvedConcreteNonBehaviour moved to HandlerBase (wave-9 round-4 [X4]/[X5]/[X9]: the
    // interface-receiver accessor gates in the assignment handlers share it).

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
