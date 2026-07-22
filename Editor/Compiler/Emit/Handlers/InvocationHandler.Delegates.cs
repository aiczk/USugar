using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
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
            && op.Arguments.Length == 1 && DelegateAbi.IsDelegateType(op.Instance.Type))
        {
            var eqArg = UnwrapConversions(op.Arguments[0].Value);
            if (!DelegateAbi.IsDelegateType(eqArg.Type))
                throw new System.NotSupportedException(
                    "Delegate .Equals(...) with a non-delegate argument is not supported. "
                    + "Compare two delegate values (or use ==).");
            if (!SymbolEqualityComparer.Default.Equals(op.Instance.Type, eqArg.Type))
            {
                VisitExpression(op.Instance);
                VisitExpression(eqArg);
                result = Const(false, StorageTypes.Boolean);
                return true;
            }
            result = DelegateAbi.CompareDelegates(_builder,
                VisitExpression(op.Instance), VisitExpression(eqArg), isNotEquals: false);
            return true;
        }

        // Static form: object.Equals(a, b) with delegate-typed operands (round-5 [X12]).
        if (target.IsStatic && target.ContainingType.SpecialType == SpecialType.System_Object
            && op.Arguments.Length == 2)
        {
            var lhs = UnwrapConversions(op.Arguments[0].Value);
            var rhs = UnwrapConversions(op.Arguments[1].Value);
            var lhsDlg = DelegateAbi.IsDelegateType(lhs.Type);
            var rhsDlg = DelegateAbi.IsDelegateType(rhs.Type);
            if (!lhsDlg && !rhsDlg) return false; // not a delegate comparison — existing extern path
            if (lhsDlg != rhsDlg)
                throw new System.NotSupportedException(
                    "object.Equals(...) mixing a delegate and a non-delegate operand is not supported. "
                    + "Compare two delegate values (or use ==).");
            if (!SymbolEqualityComparer.Default.Equals(lhs.Type, rhs.Type))
            {
                VisitExpression(lhs);
                VisitExpression(rhs);
                result = Const(false, StorageTypes.Boolean);
                return true;
            }
            result = DelegateAbi.CompareDelegates(_builder,
                VisitExpression(lhs), VisitExpression(rhs), isNotEquals: false);
            return true;
        }

        return false;
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
            _ctx.Storage.TryDeclareVar(convArgs[i], GetStorageType(invoke.Parameters[i].Type));
        StorageType? retType = null;
        if (!invoke.ReturnsVoid)
        {
            retType = GetStorageType(invoke.ReturnType);
            _ctx.Storage.TryDeclareVar(convRet, retType.Value);
        }
        // Stage 2 §5.1: every dispatch site unconditionally stages DelegateAbi.Env → __dlgc_{sig}__env, so
        // declare it on first use here (a capture-free target sends null; the bridge's null guard is
        // the backstop). Declared at the dispatch site only — never in a capture-free bridge (§1.3).
        _ctx.Storage.TryDeclareVar(convEnv, new StorageType(EnvEmit.EnvType));

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
    /// synthetic passes, mirroring DelegateBridgeEmitter's resolved type-parameter snapshot use.
    /// </summary>
    CLeaf EmitDelegateDispatchCore(CLeaf bundle, IMethodSymbol invoke, string[] convArgs, string convRet, string convEnv,
        StorageType? retType, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        CLeaf[] argExprs, bool isConditional, bool reentrant, string receiverDescription)
    {
        // retSlot pre-initialized to default(T): every guard-failure arm falls through with it (§2.6).
        int retSlot = -1;
        if (retType != null)
        {
            retSlot = _ctx.Builder.AllocScratch(retType.Value);
            EmitAssign(retSlot, DefaultConst(_builder, retType.Value));
        }

        // Guard-failure arm: LogError (NRE deviation, exact message per §2.6) — or silent for ?.Invoke.
        System.Action<CoreBuilder> failArm = null;
        if (!isConditional)
            failArm = _ => DelegateAbi.EmitNullInvokeLog(_builder, _classSymbol.Name, receiverDescription);
        System.Action<CoreBuilder> invalidBundleArm = null;
        if (!isConditional)
            invalidBundleArm = _ => DelegateAbi.EmitInvalidBundleLog(_builder, _classSymbol.Name, receiverDescription);

        void EmitGuardedDispatch()
        {
            var kindOk = DelegateAbi.IsTaggedBundle(_builder, bundle);
            _builder.EmitIf(kindOk, _ =>
            {
                // tgt is a SystemObject temp fed to externs directly — no Convert needed (P1/P5a).
                var tgt = DelegateAbi.ReadSlot(_builder, bundle, DelegateAbi.Target, "SystemObject");
                // target-null guard: unset element, or the in-game security filter nulling DelegateAbi.Target.
                var tOk = DelegateAbi.HasTarget(_builder, tgt);
                _builder.EmitIf(tOk, _ =>
                {
                    // Conv stores: the FINAL writes before dispatch (§3.3 clobber discipline).
                    for (int i = 0; i < argExprs.Length && i < convArgs.Length; i++)
                        if (argExprs[i] != null)
                            EmitStoreField(convArgs[i], argExprs[i]);

                    // Stage 2 §5.1: stage DelegateAbi.Env into the env conv global (unconditional, both arms —
                    // the SELF arm's bridge reads this field directly; the CROSS arm SPVs it below). A
                    // capture-free target sends null; the receiving bridge's null-env guard is the backstop.
                    EmitStoreField(convEnv, DelegateAbi.ReadSlot(_builder, bundle, DelegateAbi.Env, EnvEmit.EnvType));

                    var adr = DelegateAbi.ReadSlot(_builder, bundle, DelegateAbi.Addr, "SystemUInt32");
                    var mtd = DelegateAbi.ReadSlot(_builder, bundle, DelegateAbi.Method, "SystemString");
                    var thisType = GetStorageType(_classSymbol);
                    var thisRef = LoadField(_ctx.Storage.DeclareThisOnce(thisType), thisType);
                    // Self/cross is decided by TARGET IDENTITY only (P6) — addr≠0 merely qualifies the
                    // fast path (addr is meaningless across program boundaries; 0-addr JUMP_INDIRECT would
                    // silently jump to bytecode 0, P5d — addr is only ever read inside this guard).
                    var isSelf = ExternCall("UnityEngineObject.__op_Equality__UnityEngineObject_UnityEngineObject__SystemBoolean",
                        new List<CLeaf> { tgt, thisRef }, StorageTypes.Boolean);
                    var hasAddr = DelegateAbi.HasAddress(_builder, adr);
                    var selfFast = ExternCall("SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<CLeaf> { isSelf, hasAddr }, StorageTypes.Boolean);
                    _builder.EmitIf(selfFast,
                        _ =>
                        {
                            // SELF: JUMP_INDIRECT into the bridge __body (EmitCallIndirect verbatim, P5b).
                            EmitInternalVoid("__indirect", new List<CLeaf> { adr }, reentrant);
                            // Immediate conv-ret materialization (§3.3-4, fcd11/12 invariant).
                            if (retType != null)
                                EmitAssign(retSlot, LoadField(convRet, retType.Value));
                        },
                        _ =>
                        {
                            // CROSS — includes a foreign-minted bundle with target==this && addr==0, which
                            // correctly falls to a self-addressed SendCustomEvent.
                            // method-null guard: hand-rolled object[] bundles cast back to a delegate (§2.6).
                            var mOk = DelegateAbi.HasMethod(_builder, mtd);
                            _builder.EmitIf(mOk, _ =>
                            {
                                for (int i = 0; i < convArgs.Length; i++)
                                {
                                    var argType = ExternResolver.GetStorageType(
                                        new RuntimeType(invoke.Parameters[i].Type), typeParamMap);
                                    EmitExternVoid(
                                        ExternResolver.EventReceiverSetProgramVariable,
                                        new List<CLeaf> { tgt, Const(convArgs[i], StorageTypes.String), LoadField(convArgs[i], argType) });
                                }
                                // Stage 2 §5.1: forward the staged env to the receiver alongside the conv args
                                // (a missing-symbol SPV on a capture-free receiver is a proven silent no-op).
                                EmitExternVoid(
                                    ExternResolver.EventReceiverSetProgramVariable,
                                    new List<CLeaf> { tgt, Const(convEnv, StorageTypes.String), LoadField(convEnv, new StorageType(EnvEmit.EnvType)) });
                                EmitExternVoid(
                                    ExternResolver.EventReceiverSendCustomEvent,
                                    new List<CLeaf> { tgt, mtd }, reentrant);
                                if (retType != null)
                                    EmitAssign(retSlot, ExternCall(
                                        ExternResolver.EventReceiverGetProgramVariable,
                                        new List<CLeaf> { tgt, Const(convRet, StorageTypes.String) }, StorageTypes.Object));
                            }, failArm);
                        });
                }, failArm);
            }, invalidBundleArm);
        }

        if (isConditional)
        {
            // Bundle-null was already guarded by the conditional access (silent skip, args unevaluated).
            EmitGuardedDispatch();
        }
        else
        {
            var nb = DelegateAbi.CompareToNull(_builder, bundle, isNotEquals: true);
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
        StorageType? retType = invoke.ReturnsVoid
            ? null
            : ExternResolver.GetStorageType(new RuntimeType(invoke.ReturnType), typeParamMap);
        return EmitDelegateDispatchCore(bundle, invoke, convArgs, convRet, convEnv, retType, typeParamMap,
            argExprsByOrdinal, isConditional: false, reentrant: true, receiverDescription: "multicast fan-out");
    }

    /// <summary>Multicast design §1.4: exposes the existing element-equality leg (target+method+env,
    /// §2.5) for the synthetic combine/remove helpers' LastContiguousMatch search — reused verbatim,
    /// never re-derived, so the `-=` removal semantics can never drift from `==`'s element leg.</summary>
    internal CLeaf EmitDelegateElementEquals(CLeaf a, CLeaf b)
        => DelegateAbi.CompareDelegates(_builder, a, b, isNotEquals: false);

    /// <summary>default(T) constant for the dispatch retSlot pre-init (§2.6). Non-primitive Udon types
    /// (objects, arrays, bundles, SDK structs) approximate with null — only observable on the
    /// null-invoke deviation path, which is already a documented deviation (§8-8).</summary>
    CConst DefaultConst(string udonType) => DefaultConst(_builder, new StorageType(udonType));

    /// <summary>Shared default(T) const builder (dispatch retSlot pre-init + Stage 2 §5.1 bridge
    /// null-env arm). Static so UasmEmitter's bridge emission reuses the same mapping.</summary>
    internal static CConst DefaultConst(CoreBuilder b, StorageType type) => type.Name switch
    {
        "SystemBoolean" => b.Const(false, type),
        "SystemInt32" => b.Const(0, type),
        "SystemUInt32" => b.Const(0u, type),
        "SystemInt64" => b.Const(0L, type),
        "SystemUInt64" => b.Const(0UL, type),
        "SystemInt16" => b.Const((short)0, type),
        "SystemUInt16" => b.Const((ushort)0, type),
        "SystemSByte" => b.Const((sbyte)0, type),
        "SystemByte" => b.Const((byte)0, type),
        "SystemSingle" => b.Const(0f, type),
        "SystemDouble" => b.Const(0d, type),
        "SystemDecimal" => b.Const(0m, type),
        "SystemChar" => b.Const('\0', type),
        _ => b.Const(null, type),
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

}
