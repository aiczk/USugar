using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits the guarded self/cross-program delegate dispatch ladder.</summary>
public sealed class DelegateDispatchEmitter
{
    readonly EmitContext _ctx;
    readonly CoreBuilder _builder;

    public DelegateDispatchEmitter(EmitContext context)
    {
        _ctx = context;
        _builder = context.Builder;
    }

    CConst Const(object value, StorageType type) => _builder.Const(value, type);
    CSlotRef SlotRef(int slot) => _builder.SlotRef(slot);
    CSlotRef LoadField(string name, StorageType type) => _builder.LoadField(name, type);
    void EmitAssign(int slot, CValue value) => _builder.EmitAssign(slot, value);
    void EmitStoreField(string name, CLeaf value) => _builder.EmitStoreField(name, value);
    CSlotRef ExternCall(ExternSignature signature, List<CLeaf> args, StorageType returnType)
        => _builder.ExternCall(HandlerBase.ResolveExtern(signature), args, returnType);
    void EmitExternVoid(ExternSignature signature, List<CLeaf> args, bool reentrant = false)
        => _builder.EmitExternVoid(HandlerBase.ResolveExtern(signature), args, reentrant);
    void EmitInternalVoid(string name, List<CLeaf> args, bool reentrant = false)
        => _builder.EmitInternalVoid(name, args, reentrant);
    StorageType GetStorageType(ITypeSymbol type)
        => ExternResolver.GetStorageType(new RuntimeType(type), _ctx.Generics.TypeParamMap);

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
    public CLeaf Emit(CLeaf bundle, IMethodSymbol invoke, string[] convArgs, string convRet, string convEnv,
        StorageType? retType, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        CLeaf[] argExprs, bool isConditional, bool reentrant, string receiverDescription)
    {
        var localSignature = DelegateAbi.IsProgramLocalSignature(invoke);
        // retSlot pre-initialized to default(T): every guard-failure arm falls through with it (§2.6).
        int retSlot = -1;
        if (retType != null)
        {
            retSlot = _ctx.Builder.AllocScratch(retType.Value);
            EmitAssign(retSlot, InvocationHandler.DefaultConst(_builder, retType.Value));
        }

        // Guard-failure arm: LogError (NRE deviation, exact message per §2.6) — or silent for ?.Invoke.
        System.Action<CoreBuilder> failArm = null;
        if (!isConditional)
            failArm = _ => DelegateAbi.EmitNullInvokeLog(_builder, _ctx.ClassSymbol.Name, receiverDescription);
        System.Action<CoreBuilder> invalidBundleArm = null;
        if (!isConditional)
            invalidBundleArm = _ => DelegateAbi.EmitInvalidBundleLog(_builder, _ctx.ClassSymbol.Name, receiverDescription);

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
                    var thisType = GetStorageType(_ctx.ClassSymbol);
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
                            if (localSignature)
                            {
                                if (!isConditional)
                                    DelegateAbi.EmitInvalidBundleLog(_builder, _ctx.ClassSymbol.Name,
                                        receiverDescription + " (program-local signature)");
                                return;
                            }
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
}
