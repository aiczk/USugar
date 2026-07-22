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
        OperationKind.TypeParameterObjectCreation, OperationKind.AnonymousObjectCreation,
    };

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IInvocationOperation op => VisitInvocation(op),
        IObjectCreationOperation op => VisitObjectCreation(op),
        IPropertyReferenceOperation op => VisitPropertyReference(op),
        IInterpolatedStringOperation op => VisitInterpolatedString(op),
        ITypeParameterObjectCreationOperation op => VisitTypeParameterObjectCreation(op),
        IAnonymousObjectCreationOperation op => VisitAnonymousObjectCreation(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── VisitInvocation ──

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

        // Tier-2 equality cell (audit 2026-07-17): SDK-enum instance .Equals(object). The inherited
        // Object/ValueType/Enum owner resolves (B59 concrete / [V3] type-param) to the receiver's own
        // Udon type (UnityEngineKeyCode …), which has NO registered __Equals extern, and ResolveExtern's
        // Component-owner fallback then silently adopted UnityEngineComponent.__Equals — whose wrapper
        // reads the receiver as UnityEngine.Object, so the real VM throws HeapTypeMismatchException at
        // runtime on legal C# (runtime differential tests), laundered past the
        // extern census because the adopted extern IS registered. An SDK enum's box keeps its REAL type
        // identity on the VM heap, so the null-safe STATIC object.Equals extern IS C#'s Enum.Equals
        // (same type AND same value) for every argument shape, including cross-type — route it there.
        // (EmitEnumToUnderlying + underlying equality would erase the type check here and answer true
        // for equal-valued DIFFERENT SDK enums.) User enums stay on their pinned underlying-primitive
        // extern (erased tag; VM-verified Match — EqualityMatrixCellTests / EqMatrixCellsVmTests).
        if (target.Name == "Equals" && !target.IsStatic && target.Parameters.Length == 1
            && op.Instance != null
            && target.ContainingType.SpecialType is SpecialType.System_Object
                or SpecialType.System_ValueType or SpecialType.System_Enum
            && ResolveType(op.Instance.Type) is INamedTypeSymbol sdkEnumRecv
            && sdkEnumRecv.TypeKind == TypeKind.Enum && !ExternResolver.IsUserEnum(sdkEnumRecv))
            return ExternCall("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
                new List<CLeaf> { VisitExpression(op.Instance), VisitExpression(op.Arguments[0].Value) },
                StorageTypes.Boolean);

        // Nullable<T>.GetValueOrDefault() / GetValueOrDefault(fallback) → the value, else the fallback/default.
        if (op.Instance != null && target.Name == "GetValueOrDefault"
            && EmitPolicy.IsNullableT(target.ContainingType, out var govUnderlying))
        {
            var uType = GetStorageTypeName(govUnderlying);
            // For an aggregate (struct/tuple) underlying, the present value is a boxed object[] aliasing the
            // nullable's storage — deep-clone it out (value semantics). default(T) for an aggregate is a fresh
            // zero-initialized struct, NOT null, so mint through AggregateAbi rather than using scalar default.
            var aggType = ResolveType(govUnderlying) as INamedTypeSymbol;
            bool aggResult = aggType != null && TypeClassifier.IsAggregateValue(aggType);
            var nv = VisitExpression(op.Instance);
            var fallback = op.Arguments.Length > 0
                ? VisitExpression(op.Arguments[0].Value)
                : (aggResult
                    ? AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout(aggType), _ctx.Aggregates.GetLayout, GetStorageTypeName)
                    : EmitValueTypeDefault(uType));
            return NullableAbi.EmitGetValueOrDefault(_builder, nv, new StorageType(uType), fallback,
                present => aggResult
                    ? AggregateAbi.DeepClone(_builder, present, aggType, _ctx.Aggregates.GetLayout)
                    // CW18: the present box may carry a plain-int tag (small-underlying drift) — a raw
                    // copy into the strict uType slot faults the next typed read; re-tag tolerantly.
                    : RetagSmallNullablePresent(present, govUnderlying));
        }

        // CW17: Nullable<T> OVERRIDES ToString/Equals/GetHashCode, so the bound target's ContainingType is
        // Nullable<T> — escaping the [V3]/B59/B60 Object-method re-routes (keyed on SpecialType) and falling
        // through to an INSTANCE SystemObject extern on the raw box, which NREs the VM on the null
        // representation. Lower the C# semantics over the boxed ABI: ToString() is "" when null,
        // GetHashCode() is 0 when null, and Equals(object) is exactly the null-safe STATIC
        // object.Equals(box, arg) — both-null true, one-null false, else boxed value equality.
        if (op.Instance != null && EmitPolicy.IsNullableT(target.ContainingType, out var nulUnder)
            && target.Name is "ToString" or "Equals" or "GetHashCode")
        {
            // An aggregate underlying boxes as its object[] bundle: the SystemObject extern would print/
            // hash/compare the ARRAY REFERENCE, not the value (C#: the struct's own semantics) — loud
            // reject, mirroring the bare user-struct receiver's object-method polarity.
            if (ResolveType(nulUnder) is INamedTypeSymbol nulAgg && TypeClassifier.IsAggregateValue(nulAgg))
                throw new System.NotSupportedException(
                    $"'{target.Name}' on a nullable of struct/tuple type "
                    + $"'{nulUnder.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                    + "the value boxes as its object[] bundle, so the object-method extern would use the array "
                    + "reference, not the value. Test HasValue and use the unwrapped value's members instead.");

            var nulBox = VisitExpression(op.Instance);
            switch (target.Name)
            {
                case "ToString":
                    // A user enum prints its member NAME (B67) — route the present value through the same
                    // synthesized helper as the bare receiver (re-tagged: the helper's param is strict-typed).
                    if (ExternResolver.IsUserEnum(ResolveType(nulUnder)))
                        return NullableAbi.EmitGetValueOrDefault(_builder, nulBox, StorageTypes.String,
                            Const("", StorageTypes.String),
                            present => TryEmitEnumToString(RetagSmallNullablePresent(present, nulUnder), nulUnder));
                    return NullableAbi.EmitGetValueOrDefault(_builder, nulBox, StorageTypes.String,
                        Const("", StorageTypes.String),
                        present => ExternCall("SystemObject.__ToString__SystemString",
                            new List<CLeaf> { present }, StorageTypes.String));
                case "GetHashCode":
                    return NullableAbi.EmitGetValueOrDefault(_builder, nulBox, StorageTypes.Int32,
                        Const(0, StorageTypes.Int32),
                        present => ExternCall("SystemObject.__GetHashCode__SystemInt32",
                            new List<CLeaf> { present }, StorageTypes.Int32));
                default: // Equals(object)
                    return ExternCall("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
                        new List<CLeaf> { nulBox, VisitExpression(op.Arguments[0].Value) }, StorageTypes.Boolean);
            }
        }

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

        // M4b: a direct .ToString() rooted at the System.Object slot on a v1 class receiver — the third
        // lifted surface (with the interpolation hole and the concat operand), one shared lowering
        // (EmitClassToStringDispatch): override arms direct-call, no-override arms print the runtime
        // type-name constant, null is the LogError + "" deviation (C# would NRE). A base.ToString()
        // bound DIRECTLY to object.ToString is non-virtual in C# yet still prints the RUNTIME type name
        // (Object.ToString reads GetType()), so it takes the same chain with the override arms disabled;
        // a base.ToString() bound to a USER override falls through to the ordinary direct-call path.
        // WaveJoint R2 [A02]: when the enclosing class's parent IS System.Object, `base` carries the
        // static type object — the receiver family is the ENCLOSING class (the method body being
        // emitted), so resolve the dispatch family from _currentMethod there; the user-base form keeps
        // reading the receiver's own type (byte-identical path).
        if (ClassAbi.IsObjectToStringSlot(target) && op.Instance != null)
        {
            bool tsBase = op.Instance is IInstanceReferenceOperation
                { Syntax: Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax };
            var tsFamily = tsBase && op.Instance.Type?.SpecialType == SpecialType.System_Object
                ? _currentMethod?.ContainingType : op.Instance.Type;
            if (ResolveType(tsFamily) is INamedTypeSymbol tsRecvTy && TypeClassifier.IsUserClass(tsRecvTy)
                && (!tsBase || target.ContainingType.SpecialType == SpecialType.System_Object))
                return EmitClassToStringDispatch(tsRecvTy, LoadInstanceRaw(op.Instance),
                    nullIsError: true, useOverrides: !tsBase);
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
            && target.ContainingType is INamedTypeSymbol structRecv && TypeClassifier.IsObjectArrayEmulated(structRecv))
        {
            // CA-v2b-2: a runtime-polymorphic call on a user-class receiver lowers to an inline typeobj-
            // ReferenceEquals chain of direct calls (a sealed/singleton dispatch set devirtualizes to one
            // direct call). Fires for a base-typed variable AND for `this` (an inherited/base-ctor method's
            // `this.M()` must dispatch on the RUNTIME type — bundle[0], written before the ctor chain, charter
            // #6); `base.M()` and non-user-class receivers are excluded. The predicate is shared with the
            // recursion-graph enumerator (VirtualDispatch.IsDispatchSite) so spilling can never drift from
            // dispatch. The receiver's type is resolved through the monomorphization map here.
            if (ResolveType(op.Instance?.Type) is INamedTypeSymbol recvTy
                && VirtualDispatch.IsDispatchSite(target, op.Instance, recvTy))
            {
                var targets = _ctx.VirtualDispatch.ResolveTargets(recvTy, target);
                AssertClosedVirtualDispatch(recvTy, targets, target);
                if (!recvTy.IsSealed && targets.Count >= 2)
                    return EmitVirtualChain(op, targets);
                if (targets.Count >= 1)
                    target = targets[0].Impl; // devirt: singleton/sealed → direct call to the one impl
                else
                    // Closed-world: no minted class implements the slot, so no instance can exist and the
                    // receiver must be null (CLR: NRE). Falling through to a direct base-impl call would
                    // silently EXECUTE code on the null bundle — LogError + default instead (§2.6 polarity).
                    return EmitUnreachableVirtualCall(op, recvTy, target);
            }

            // CA-M1: a v1 class instance method rides the SAME param0-receiver path. The receiver bundle
            // flows by reference (EmitStructInstanceCall's defensive copy stays gated on IsAggregateValue,
            // which is false for a class — so mutations through the receiver are visible to every alias).
            var structTarget = ResolveStructMember(target);
            // B56: a struct-hosted generic method must record its instantiation so a nested closure/LF
            // referencing the method's T finds the owner in the closure-compose (the class arm does this
            // via RegisterGenericSpecialization; the struct path registers the spec separately).
            return EmitStructInstanceCall(op, structTarget);
        }

        // CA-M1: Object-inherited method on a v1 class receiver (target owner = System.Object).
        // Equals(object) → reference compare on the two object[] bundles (unoverridden object.Equals IS
        // reference equality for a class). ToString never reaches here (the M4b slot intercept above).
        // GetHashCode / GetType → loud reject (no stable hash, no System.Type identity for a bundle).
        if (ClassAbi.IsObjectMethodOnUserClass(target, op.Instance?.Type))
        {
            var clsRecv = (INamedTypeSymbol)op.Instance.Type;
            if (target.Name == "Equals" && op.Arguments.Length == 1)
            {
                var lhs = VisitExpression(op.Instance);
                var rhs = VisitExpression(op.Arguments[0].Value);
                return ClassAbi.EmitObjectEquals(_builder, lhs, rhs);
            }
            throw new System.NotSupportedException(ClassAbi.UnsupportedObjectMethodMessage(clsRecv, target));
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
        // virtual dispatch. Reachable non-public targets receive an internal entry point during program
        // registration. Generic targets have no per-specialization layout, and ref/out params cannot
        // round-trip through SetProgramVariable, so those shapes remain loud rejects.
        if (!target.IsStatic && target.MethodKind == MethodKind.Ordinary
            && op.Instance != null && op.Instance is not IInstanceReferenceOperation
            && ExternResolver.IsUdonSharpBehaviour(target.ContainingType)
            && target.ContainingType.Name != "UdonSharpBehaviour"
            && (SymbolEqualityComparer.Default.Equals(target.ContainingType, _classSymbol)
                || UasmEmitter.IsBaseInstanceMethod(target, _classSymbol)))
        {
            if (target.IsGenericMethod || target.Parameters.Any(p => p.RefKind != RefKind.None))
                throw new System.NotSupportedException(
                    $"Instance method '{target.Name}' of the compiled class family is called through a "
                    + "non-this receiver, which dispatches cross-program (SetProgramVariable + "
                    + "SendCustomEvent) and so needs a non-generic target without ref/out parameters.");
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
        if (_methodFunctions.ContainsKey(target) && UasmEmitter.IsBaseInstanceMethod(target, _classSymbol))
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
            && UasmEmitter.IsBaseInstanceMethod(target, _classSymbol))
        {
            RegisterGenericSpecialization(target);
            return EmitUserMethodCall(op, target);
        }

        // Generic foreign static method → monomorphize and emit as internal call
        if (target.IsGenericMethod && UasmEmitter.IsForeignStatic(target, _classSymbol))
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
            var genPrepared = MarshalArguments(op, args);
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
            if (UasmEmitter.IsForeignStatic(target, _classSymbol) && _methodFunctions.ContainsKey(original))
            {
                GuardRefOutArguments(op, target); // round-8 [R6]: Q2/Q5/R4 parity
                var args = new List<CLeaf>();
                // Extension method: instance is the first (this) parameter
                if (target.ReducedFrom != null && op.Instance != null)
                {
                    args.Add(VisitExpression(op.Instance));
                }
                var fsPrepared = MarshalArguments(op, args);
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
        if (UasmEmitter.IsForeignStatic(target, _classSymbol) && target.ReducedFrom == null && !target.IsGenericMethod
            && target.ContainingType is INamedTypeSymbol fsGenCt && fsGenCt.IsGenericType
            && !fsGenCt.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
        {
            GuardRefOutArguments(op, target);
            RegisterGenericSpecialization(target);
            var args = new List<CLeaf>();
            var gsPrepared = MarshalArguments(op, args);
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
        if (target.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } localInterface
            && _planner.InterfaceIsLocalUserClassOnly(localInterface))
        {
            var targets = _ctx.VirtualDispatch.ResolveInterfaceTargets(localInterface, target);
            if (targets.Count >= 2)
                return EmitVirtualChain(op, targets);
            if (targets.Count == 1)
                return EmitStructInstanceCall(op, ResolveStructMember(targets[0].Impl));
            return EmitUnreachableVirtualCall(op, localInterface, target);
        }

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

    // User-struct instance method call: receiver object[] passed (uncloned) as synthetic param0
    // so `this`-field mutations reflect back to the caller's local (value-type by-ref `this` semantics).
    /// <summary>CA-v2b-2: inline typeobj-dispatch for a runtime-polymorphic call with ≥2 concrete targets.
    /// Evaluate the receiver and args ONCE (C# semantics) into scratch slots, placed by parameter ORDINAL
    /// (named/reordered args bound positionally, the w4 family — CW3), read the receiver's typeobj
    /// (bundle[0]), then emit an `if (ReferenceEquals(typeobj, typeobj_T)) dest = T.Impl(recv, args)` per
    /// target. Each arm is an ordinary direct call, so the call graph / recursion analysis sees precise
    /// edges (charter #5) and there is no shared conv-var / bridge (charter #1/#3 N/A). A covariant
    /// override stores its narrower result into the single static-typed dest — Udon heap slots are
    /// dynamically typed, so no conv-ret desync. CW3 ref/out parity with the devirt sibling
    /// (EmitStructInstanceCall): guard per impl (cycle edges are per-override) and copy back inside the
    /// executed arm — the arms are mutually exclusive, so the per-arm stores never race.</summary>
    CLeaf EmitVirtualChain(IInvocationOperation op, List<VDispatchTarget> targets)
    {
        // Pure analysis (throws only): the substituted symbol equals ResolveStructMember's result minus
        // its on-demand registration side effect, which stays inside the arms (registration order intact).
        foreach (var t in targets)
            GuardRefOutArguments(op.Arguments, SubstituteMethodTypeArgs(t.Impl));

        var recvSlot = _ctx.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
        EmitAssign(recvSlot, LoadInstanceRaw(op.Instance));

        var argRefs = new List<CLeaf>();
        var chainPrepared = MarshalArgumentsByOrdinal(op.Arguments, op.TargetMethod, argRefs, (val, arg) =>
        {
            var s = _ctx.Builder.AllocScratch(GetStorageType(arg.Value.Type));
            EmitAssign(s, val);
            return SlotRef(s);
        });

        var typeObjSlot = _ctx.Builder.AllocScratch(StorageTypes.String);
        EmitAssign(typeObjSlot, AggregateAbi.ReadSlot(_builder, SlotRef(recvSlot), 0, StorageTypes.String));

        bool isVoid = op.Type == null || op.Type.SpecialType == SpecialType.System_Void;
        int destSlot = isVoid ? -1 : _ctx.Builder.AllocScratch(GetStorageType(op.Type));

        // Phase-A armor: a null receiver or a laundered non-bundle value matches no arm. is/cast guards
        // that case to `false`; the chain must be equally loud — LogError + default, never silent.
        var matched = _ctx.Builder.AllocScratch(StorageTypes.Boolean);
        EmitAssign(matched, Const(false, StorageTypes.Boolean));

        foreach (var t in targets)
        {
            var eq = ExternCall("SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
                new List<CLeaf> { SlotRef(typeObjSlot), LoadField(t.TypeObjVar, StorageTypes.String) }, StorageTypes.Boolean);
            var callArgs = new List<CLeaf> { SlotRef(recvSlot) };
            callArgs.AddRange(argRefs);
            _builder.EmitIf(eq, _ =>
            {
                EmitAssign(matched, Const(true, StorageTypes.Boolean));
                var impl = ResolveStructMember(t.Impl);
                var call = EmitCallToMethod(impl, callArgs);
                if (isVoid) EmitExprStmt(call);
                else EmitAssign(destSlot, call);
                EmitRefOutCopyBack(op.Arguments, impl, 0, chainPrepared);
            }, null);
        }

        var noMatch = ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            new List<CLeaf> { SlotRef(matched) }, StorageTypes.Boolean);
        _builder.EmitIf(noMatch, _ =>
            EmitExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                new List<CLeaf> { Const(
                    $"USugar: NullReferenceException — virtual call '{op.TargetMethod.ContainingType.Name}.{op.TargetMethod.Name}' on a null or non-class receiver ({_classSymbol.Name}). Returning default.",
                    StorageTypes.String) }), null);

        return isVoid ? Const(null, StorageTypes.Object) : SlotRef(destSlot);
    }

    // AssertClosedVirtualDispatch lives in HandlerBase (CW1 lift: the accessor dispatch arms in
    // PreparePropertySet / CaptureLValue / the pattern lowering share the same open-family armor).

    /// <summary>Phase-A armor: the empty-target lowering — evaluate receiver and args for side-effect
    /// parity (CLR evaluates them before the NRE), then LogError + default (§2.6 null-invoke polarity).</summary>
    CLeaf EmitUnreachableVirtualCall(IInvocationOperation op, INamedTypeSymbol recvTy, IMethodSymbol target)
    {
        var recvSlot = _ctx.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
        EmitAssign(recvSlot, LoadInstanceRaw(op.Instance));
        foreach (var a in op.Arguments)
        {
            var s = _ctx.Builder.AllocScratch(GetStorageType(a.Value.Type));
            EmitAssign(s, VisitExpression(a.Value));
        }
        EmitExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
            new List<CLeaf> { Const(
                $"USugar: NullReferenceException — virtual call '{recvTy.Name}.{target.Name}' has no minted implementor, so the receiver must be null ({_classSymbol.Name}). Returning default.",
                StorageTypes.String) });
        bool isVoid = op.Type == null || op.Type.SpecialType == SpecialType.System_Void;
        if (isVoid) return Const(null, StorageTypes.Object);
        return SlotRef(_ctx.Builder.AllocScratch(GetStorageType(op.Type)));
    }

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
            && op.Instance?.Type is INamedTypeSymbol recvAgg && TypeClassifier.IsAggregateValue(recvAgg))
            recv = AggregateAbi.DeepClone(_builder, recv, recvAgg, _ctx.Aggregates.GetLayout);
        var args = new List<CLeaf> { recv };
        var structPrepared = MarshalArguments(op, args);
        var result = EmitCallToMethod(target, args);
        // Round-8 [R5]: this path used to drop the ref/out copy-back entirely (DiffFuzz: ref-arg
        // ref=136 vs VM 106, out-arg ref=10 vs 0). Param ids are ordinal-indexed (receiver separate).
        EmitRefOutCopyBack(op, target, 0, structPrepared);
        return result;
    }

    // ── Classification helpers ──
    // IsForeignStatic / IsBaseInstanceMethod: this handler's open-coded copies were deleted at C4
    // retirement — call the single-source statics on UasmEmitter (whose IsBaseInstanceMethod carries
    // the [Y10] MethodKind.LocalFunction exclusion the copy lacked).

    // IsResolvedConcreteNonBehaviour moved to HandlerBase (wave-9 round-4 [X4]/[X5]/[X9]: the
    // interface-receiver accessor gates in the assignment handlers share it).
}
