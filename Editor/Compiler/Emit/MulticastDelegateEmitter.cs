using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Emits multicast delegate combine, remove, and fan-out synthetic functions.</summary>
public sealed class MulticastDelegateEmitter
{
    readonly LoweringState _ctx;
    readonly CoreBuilder _builder;
    readonly StructuredModule _module;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _delegateConvention;
    readonly INamedTypeSymbol _classSymbol;
    readonly SyntheticDemandPlan _demands;

    internal MulticastDelegateEmitter(LoweringState context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention, SyntheticDemandPlan demands)
    {
        _ctx = context;
        _builder = context.Builder;
        _module = context.Module;
        _bridge = bridge;
        _delegateConvention = convention;
        _classSymbol = context.ClassSymbol;
        _demands = demands;
    }

    static readonly UdonAbiKey MulticastArrGet = UdonAbi.ArrayGet("SystemObjectArray", "SystemObject");
    static readonly UdonAbiKey MulticastArrSet = UdonAbi.ArraySet("SystemObjectArray", "SystemObject");
    static readonly UdonAbiKey MulticastArrCtor = UdonAbi.ArrayConstructor("SystemObjectArray");

    /// <summary>Element-by-element object[] blit (§1.4/§9 open item 1, A-M0 finding): the registry lists
    /// `SystemArray.__Copy` by name, but the real Udon VM assembler this harness targets does not resolve
    /// it (ExternMissing on-device) — the design's own documented fallback ("使えなければ手 loop") applies.
    /// Same semantics as Array.Copy(src, srcStart, dst, dstStart, len), one extra loop per copy.</summary>
    void EmitMulticastArrayBlit(int srcSlot, CLeaf srcStart, int dstSlot, CLeaf dstStart, int lenSlot)
    {
        var kSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitFor(
            _ => _builder.EmitAssign(kSlot, _bridge.ConstInt(0)),
            () => _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.Int32LessThan,
                new CLeaf[] { _builder.SlotRef(kSlot), _builder.SlotRef(lenSlot) }),
            _ => _builder.EmitAssign(kSlot, _bridge.CallExtern(StorageTypes.Int32,
                UdonAbi.Int32Add,
                new CLeaf[] { _builder.SlotRef(kSlot), _bridge.ConstInt(1) })),
            _ =>
            {
                var srcIdx = _bridge.CallExtern(StorageTypes.Int32, UdonAbi.Int32Add,
                    new CLeaf[] { srcStart, _builder.SlotRef(kSlot) });
                var dstIdx = _bridge.CallExtern(StorageTypes.Int32, UdonAbi.Int32Add,
                    new CLeaf[] { dstStart, _builder.SlotRef(kSlot) });
                var elem = _bridge.CallExtern(StorageTypes.Object, MulticastArrGet, new CLeaf[] { _builder.SlotRef(srcSlot), srcIdx });
                _bridge.CallExternVoid(MulticastArrSet, new CLeaf[] { _builder.SlotRef(dstSlot), dstIdx, elem });
            });
    }

    public void EmitPending()
    {
        foreach (var (sigPart, plan) in _demands.MulticastSignatures)
        {
            if ((plan.Operations & MulticastOperations.Combine) != 0)
                EmitMulticastCombineHelper(sigPart, plan.Invoke);
            if ((plan.Operations & MulticastOperations.Remove) != 0)
                EmitMulticastRemoveHelper(sigPart, plan.Invoke);
            EmitMulticastFanoutBridge(sigPart, plan.Invoke, plan.TypeParamMap);
        }
    }

    /// <summary>Mint a fresh multicast bundle (§1.1/§1.4): a tagged delegate ABI bundle with
    /// Target=this, Method=this sig's fan-out export name, Addr=that bridge's funcaddr
    /// (back-patched CFuncRef, §1.3 addr discipline), Env=the given invocation list.</summary>
    CLeaf EmitMulticastMintBundle(string sigPart, CLeaf listLeaf)
    {
        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);
        var mSlot = _ctx.Builder.AllocScratch(StorageTypes.ObjectArray);
        var thisType = _ctx.Session.Types.GetUdonTypeName(_classSymbol);
        return DelegateAbi.EmitBundleMintToSlot(_builder, mSlot,
            () => _bridge.Load(_ctx.Storage.DeclareThisOnce(new StorageType(thisType)), new StorageType(thisType)),
            _builder.Const(fanoutName, StorageTypes.String),
            _builder.FuncRef(fanoutName),
            listLeaf);
    }

    /// <summary>Multicast combine/remove shared operand normalization (§1.4): a multicast operand
    /// unwraps to its invocation list (DelegateAbi.Env); a single-cast operand wraps as its own 1-element
    /// list. The multicast test is a compile-time constant string compare against DelegateAbi.Method — the only
    /// test allowed to distinguish a multicast bundle from a single-cast one (the __dlg_fanout_ prefix
    /// is reserved and can never collide with a real user method/bridge export name).</summary>
    void EmitMulticastFlattenOperand(CLeaf operand, string fanoutName, out int listSlot, out int lenSlot)
    {
        var lSlot = _ctx.Builder.AllocScratch(StorageTypes.ObjectArray);
        var nSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);

        void EmitSingleCastWrap(CoreBuilder _)
        {
            _builder.EmitAssign(lSlot, _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrCtor,
                new CLeaf[] { _bridge.ConstInt(1) }));
            _bridge.CallExternVoid(MulticastArrSet, new CLeaf[] { _builder.SlotRef(lSlot), _bridge.ConstInt(0), operand });
            _builder.EmitAssign(nSlot, _bridge.ConstInt(1));
        }

        // CW10 (2026-07-15): the Method read is a bare slot-2 __Get — on an untagged foreign object[]
        // laundered into a delegate slot it faulted exactly where dispatch survives via IsTaggedBundle.
        // Same tag gate, same polarity: an untagged operand is a single-cast element (its own dispatch
        // LogErrors downstream). CW9/CW21 leg: a fan-out-named bundle whose Env is null (foreign
        // corruption) must not fault the length read — it too falls back to the single-cast wrap.
        var tagOk = DelegateAbi.IsTaggedBundle(_builder, operand);
        _builder.EmitIf(tagOk,
            _ =>
            {
                var tag = DelegateAbi.ReadSlot(_builder, operand, DelegateAbi.Method, "SystemString");
                var isMulticast = _bridge.CallExtern(StorageTypes.Boolean,
                    UdonAbi.StringEquality,
                    new CLeaf[] { tag, _builder.Const(fanoutName, StorageTypes.String) });
                _builder.EmitIf(isMulticast,
                    _ =>
                    {
                        _builder.EmitAssign(lSlot, DelegateAbi.ReadSlot(_builder, operand, DelegateAbi.Env, "SystemObjectArray"));
                        var listOk = _bridge.CallExtern(StorageTypes.Boolean,
                            UdonAbi.ObjectInequality,
                            new CLeaf[] { _builder.SlotRef(lSlot), _builder.Const(null, StorageTypes.Object) });
                        _builder.EmitIf(listOk,
                            _ => _builder.EmitAssign(nSlot, _bridge.CallExtern(StorageTypes.Int32, UdonAbi.SystemArrayLength,
                                new CLeaf[] { _builder.SlotRef(lSlot) })),
                            EmitSingleCastWrap);
                    },
                    EmitSingleCastWrap);
            },
            EmitSingleCastWrap);
        listSlot = lSlot;
        lenSlot = nSlot;
    }

    /// <summary>__dlg_combine_{sig}(x, y) (§1.4): null legs, else flatten both operands and concatenate
    /// (two non-null operands always yield |cat| >= 2 → always mints a multicast).</summary>
    void EmitMulticastCombineHelper(string sigPart, IMethodSymbol signatureMethod)
    {
        var helperName = DelegateAbi.MulticastCombineName(sigPart);
        var xId = $"{helperName}__x"; var yId = $"{helperName}__y"; var retId = NameAllocator.RetKey(helperName);
        _ctx.Storage.TryDeclareVar(xId, StorageTypes.ObjectArray);
        _ctx.Storage.TryDeclareVar(yId, StorageTypes.ObjectArray);
        _ctx.Storage.TryDeclareVar(retId, StorageTypes.ObjectArray);

        var func = _module.AddFunction(helperName);
        _ctx.Methods.AddSyntheticCallable(helperName, func, signatureMethod, null,
            MethodContext.CallableKind.Synthetic, new[] { xId, yId },
            new[] { new ReturnSlot(retId, StorageTypes.ObjectArray) });
        func.ParamFieldNames.Add(xId);
        func.ParamFieldNames.Add(yId);
        func.ReturnType = StorageTypes.ObjectArray;
        func.ReturnSlots.Add(new ReturnSlot(retId, StorageTypes.ObjectArray));

        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(func);

        var xLeaf = _bridge.Load(xId, StorageTypes.ObjectArray);
        var yLeaf = _bridge.Load(yId, StorageTypes.ObjectArray);

        var xNull = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.ObjectEquality,
            new CLeaf[] { xLeaf, _builder.Const(null, StorageTypes.Object) });
        _builder.EmitIf(xNull, _ => _builder.EmitReturn(yLeaf)); // null + y = y

        var yNull = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.ObjectEquality,
            new CLeaf[] { yLeaf, _builder.Const(null, StorageTypes.Object) });
        _builder.EmitIf(yNull, _ => _builder.EmitReturn(xLeaf)); // x + null = x

        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);
        EmitMulticastFlattenOperand(xLeaf, fanoutName, out var lxSlot, out var lenLxSlot);
        EmitMulticastFlattenOperand(yLeaf, fanoutName, out var lySlot, out var lenLySlot);

        var catLenSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitAssign(catLenSlot, _bridge.CallExtern(StorageTypes.Int32,
            UdonAbi.Int32Add,
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(lenLySlot) }));

        var catSlot = _ctx.Builder.AllocScratch(StorageTypes.ObjectArray);
        _builder.EmitAssign(catSlot, _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrCtor,
            new CLeaf[] { _builder.SlotRef(catLenSlot) }));
        EmitMulticastArrayBlit(lxSlot, _bridge.ConstInt(0), catSlot, _bridge.ConstInt(0), lenLxSlot);
        EmitMulticastArrayBlit(lySlot, _bridge.ConstInt(0), catSlot, _builder.SlotRef(lenLxSlot), lenLySlot);

        _builder.EmitReturn(EmitMulticastMintBundle(sigPart, _builder.SlotRef(catSlot)));

        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }

    /// <summary>__dlg_remove_{sig}(x, y) (§1.4): null legs, else flatten both operands and delete the
    /// LAST contiguous run of lx that elementwise-matches ly (element equality reuses the existing
    /// CompareDelegates leg via InvocationHandler.EmitDelegateElementEquals — never re-derived, §1.4).
    /// No match → x unchanged. Full removal → null. Single survivor → the bare bundle (not re-wrapped).</summary>
    void EmitMulticastRemoveHelper(string sigPart, IMethodSymbol signatureMethod)
    {
        var helperName = DelegateAbi.MulticastRemoveName(sigPart);
        var xId = $"{helperName}__x"; var yId = $"{helperName}__y"; var retId = NameAllocator.RetKey(helperName);
        _ctx.Storage.TryDeclareVar(xId, StorageTypes.ObjectArray);
        _ctx.Storage.TryDeclareVar(yId, StorageTypes.ObjectArray);
        _ctx.Storage.TryDeclareVar(retId, StorageTypes.ObjectArray);

        var func = _module.AddFunction(helperName);
        _ctx.Methods.AddSyntheticCallable(helperName, func, signatureMethod, null,
            MethodContext.CallableKind.Synthetic, new[] { xId, yId },
            new[] { new ReturnSlot(retId, StorageTypes.ObjectArray) });
        func.ParamFieldNames.Add(xId);
        func.ParamFieldNames.Add(yId);
        func.ReturnType = StorageTypes.ObjectArray;
        func.ReturnSlots.Add(new ReturnSlot(retId, StorageTypes.ObjectArray));

        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(func);

        var xLeaf = _bridge.Load(xId, StorageTypes.ObjectArray);
        var yLeaf = _bridge.Load(yId, StorageTypes.ObjectArray);

        var xNull = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.ObjectEquality,
            new CLeaf[] { xLeaf, _builder.Const(null, StorageTypes.Object) });
        _builder.EmitIf(xNull, _ => _builder.EmitReturn(_builder.Const(null, StorageTypes.ObjectArray))); // null - y = null

        var yNull = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.ObjectEquality,
            new CLeaf[] { yLeaf, _builder.Const(null, StorageTypes.Object) });
        _builder.EmitIf(yNull, _ => _builder.EmitReturn(xLeaf)); // x - null = x

        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);
        EmitMulticastFlattenOperand(xLeaf, fanoutName, out var lxSlot, out var lenLxSlot);
        EmitMulticastFlattenOperand(yLeaf, fanoutName, out var lySlot, out var lenLySlot);

        var elementEquals = new InvocationHandler(new LoweringServices(_ctx));

        // LastContiguousMatch: search the candidate start index DOWNWARD from (lenLx-lenLy) to 0 — the
        // first full match found this way is the RIGHTMOST (= last) one, per Delegate.Remove semantics.
        var startSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitAssign(startSlot, _bridge.CallExtern(StorageTypes.Int32,
            UdonAbi.Int32Subtract,
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(lenLySlot) }));
        var foundSlot = _ctx.Builder.AllocScratch(StorageTypes.Boolean);
        _builder.EmitAssign(foundSlot, _builder.Const(false, StorageTypes.Boolean));
        var matchIdxSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitAssign(matchIdxSlot, _bridge.ConstInt(-1));

        _builder.EmitWhile(() =>
            {
                var notFound = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.BooleanNot,
                    new CLeaf[] { _builder.SlotRef(foundSlot) });
                var startOk = _bridge.CallExtern(StorageTypes.Boolean,
                    UdonAbiKey.Method("SystemInt32", "op_GreaterThanOrEqual", new[] { "SystemInt32", "SystemInt32" }, "SystemBoolean"),
                    new CLeaf[] { _builder.SlotRef(startSlot), _bridge.ConstInt(0) });
                return _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.BooleanLogicalAnd,
                    new CLeaf[] { notFound, startOk });
            },
            _ =>
            {
                var allMatchSlot = _ctx.Builder.AllocScratch(StorageTypes.Boolean);
                _builder.EmitAssign(allMatchSlot, _builder.Const(true, StorageTypes.Boolean));
                var kSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
                _builder.EmitAssign(kSlot, _bridge.ConstInt(0));

                _builder.EmitWhile(() =>
                    {
                        var kOk = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.Int32LessThan,
                            new CLeaf[] { _builder.SlotRef(kSlot), _builder.SlotRef(lenLySlot) });
                        return _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.BooleanLogicalAnd,
                            new CLeaf[] { _builder.SlotRef(allMatchSlot), kOk });
                    },
                    _ =>
                    {
                        var lxIdx = _bridge.CallExtern(StorageTypes.Int32, UdonAbi.Int32Add,
                            new CLeaf[] { _builder.SlotRef(startSlot), _builder.SlotRef(kSlot) });
                        var lxElem = _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrGet,
                            new CLeaf[] { _builder.SlotRef(lxSlot), lxIdx });
                        var lyElem = _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrGet,
                            new CLeaf[] { _builder.SlotRef(lySlot), _builder.SlotRef(kSlot) });
                        var eq = elementEquals.EmitDelegateElementEquals(lxElem, lyElem);
                        var notEq = _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.BooleanNot,
                            new CLeaf[] { eq });
                        _builder.EmitIf(notEq, _ => _builder.EmitAssign(allMatchSlot, _builder.Const(false, StorageTypes.Boolean)));
                        _builder.EmitAssign(kSlot, _bridge.CallExtern(StorageTypes.Int32,
                            UdonAbi.Int32Add,
                            new CLeaf[] { _builder.SlotRef(kSlot), _bridge.ConstInt(1) }));
                    });

                _builder.EmitIf(_builder.SlotRef(allMatchSlot),
                    _ =>
                    {
                        _builder.EmitAssign(foundSlot, _builder.Const(true, StorageTypes.Boolean));
                        _builder.EmitAssign(matchIdxSlot, _builder.SlotRef(startSlot));
                    },
                    _ => _builder.EmitAssign(startSlot, _bridge.CallExtern(StorageTypes.Int32,
                        UdonAbi.Int32Subtract,
                        new CLeaf[] { _builder.SlotRef(startSlot), _bridge.ConstInt(1) })));
            });

        _builder.EmitIf(_builder.SlotRef(foundSlot), null, _ => _builder.EmitReturn(xLeaf)); // no match → x unchanged

        var rLenSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitAssign(rLenSlot, _bridge.CallExtern(StorageTypes.Int32,
            UdonAbi.Int32Subtract,
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(lenLySlot) }));

        var rLenIsZero = _bridge.CallExtern(StorageTypes.Boolean, UdonAbiKey.Method("SystemInt32", "op_Equality", new[] { "SystemInt32", "SystemInt32" }, "SystemBoolean"),
            new CLeaf[] { _builder.SlotRef(rLenSlot), _bridge.ConstInt(0) });
        _builder.EmitIf(rLenIsZero, _ => _builder.EmitReturn(_builder.Const(null, StorageTypes.ObjectArray))); // full removal → null

        var rSlot = _ctx.Builder.AllocScratch(StorageTypes.ObjectArray);
        _builder.EmitAssign(rSlot, _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrCtor, new CLeaf[] { _builder.SlotRef(rLenSlot) }));
        EmitMulticastArrayBlit(lxSlot, _bridge.ConstInt(0), rSlot, _bridge.ConstInt(0), matchIdxSlot);
        var tailStartSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitAssign(tailStartSlot, _bridge.CallExtern(StorageTypes.Int32, UdonAbi.Int32Add,
            new CLeaf[] { _builder.SlotRef(matchIdxSlot), _builder.SlotRef(lenLySlot) }));
        var tailLenSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitAssign(tailLenSlot, _bridge.CallExtern(StorageTypes.Int32, UdonAbi.Int32Subtract,
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(tailStartSlot) }));
        EmitMulticastArrayBlit(lxSlot, _builder.SlotRef(tailStartSlot), rSlot, _builder.SlotRef(matchIdxSlot), tailLenSlot);

        var rLenIsOne = _bridge.CallExtern(StorageTypes.Boolean, UdonAbiKey.Method("SystemInt32", "op_Equality", new[] { "SystemInt32", "SystemInt32" }, "SystemBoolean"),
            new CLeaf[] { _builder.SlotRef(rLenSlot), _bridge.ConstInt(1) });
        _builder.EmitIf(rLenIsOne, _ => _builder.EmitReturn( // single collapse → bare bundle, not re-wrapped
            _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrGet, new CLeaf[] { _builder.SlotRef(rSlot), _bridge.ConstInt(0) })));

        _builder.EmitReturn(EmitMulticastMintBundle(sigPart, _builder.SlotRef(rSlot)));

        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }

    /// <summary>__dlg_fanout_{sig} (§1.2): bridge protocol entry (conv args/env arrive via the standard
    /// __dlgc_{sig}__* convention fields, same as any delegate bridge), snapshots them into fan-out
    /// locals, then loops the invocation list dispatching each element through the EXISTING unified
    /// dispatch (InvocationHandler.EmitFanoutElementDispatch) — args are re-staged from the snapshot
    /// each iteration so a prior element's cross-dispatch clobber never leaks into the next. Last
    /// element's return value wins (§1.5, matches C# Invoke semantics); empty/null list → default.
    /// §1.6/A-M3: the element-dispatch call site is UNCONDITIONALLY Reentrant-flagged — see the
    /// reentrant-decision comment below.</summary>
    void EmitMulticastFanoutBridge(string sigPart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);

        // §1.6/A-M3 reentrancy decision: UNCONDITIONALLY reentrant. fan-out(sigPart) is always its own
        // sig-S escape target (any sig-S bundle's [1]/[2] can point at it, by construction), so a
        // sig-matched escape-set membership check only ever narrows the false case to a multicast
        // composed ENTIRELY from foreign-received bundles (no local delegate-creation of this sig) —
        // an under-approximation hole, not a real safety margin: a cross element dispatch can still
        // SendCustomEvent into a foreign handler that redispatches this sig and SCEs back into this
        // fan-out mid-iteration. Unconditional closes that hole pre-emptively and needs no snapshot of
        // BuildRecursionInfo's escape set (which cannot be computed before this synthetic function
        // exists anyway — see the design doc §1.6 note). Strictly conservative, matching the §8-3
        // "extra edges only ever over-spill" direction used throughout BuildRecursionInfo.
        // MarkReentrantDispatch (the normal path for a Reentrant-flagged site) declares __recurStack/
        // __recurSp on first use; this path bypasses that helper entirely (no IMethodSymbol to key
        // AccumulateRecursionSpillFields off), so declare the software stack here directly. No named
        // spill fields are added for the fan-out itself — every one of its locals (i/n/list/args-
        // snapshot/ret) is a plain scratch slot, spilled by InsertRecursionSpillsFunc's generic
        // post-coalesce liveness pass, not by the named-field mechanism.
        _ctx.Storage.EnsureRecursionStack();

        var retType = _delegateConvention.Declare(sigPart, invoke, typeParamMap, out var argTypes);
        _ctx.Storage.TryDeclareVar(DelegateAbi.ConvEnvName(sigPart), new StorageType(EnvEmit.EnvType));

        var fanoutFunc = _module.AddFunction(fanoutName, fanoutName);
        var fanoutParamIds = Enumerable.Range(0, invoke.Parameters.Length)
            .Select(index => DelegateAbi.ConvArgName(sigPart, index)).ToArray();
        var fanoutReturns = retType.HasValue
            ? new[] { new ReturnSlot(DelegateAbi.ConvRetName(sigPart), retType.Value) }
            : System.Array.Empty<ReturnSlot>();
        _ctx.Methods.AddSyntheticCallable(fanoutName, fanoutFunc, invoke, null,
            MethodContext.CallableKind.Bridge, fanoutParamIds, fanoutReturns);
        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(fanoutFunc);

        var listSlot = _ctx.Builder.AllocScratch(new StorageType(EnvEmit.EnvType));
        _builder.EmitAssign(listSlot, _bridge.Load(DelegateAbi.ConvEnvName(sigPart), new StorageType(EnvEmit.EnvType)));

        var argSlots = new int[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
        {
            argSlots[i] = _ctx.Builder.AllocScratch(argTypes[i]);
            _builder.EmitAssign(argSlots[i], _bridge.Load(DelegateAbi.ConvArgName(sigPart, i), argTypes[i]));
        }

        int retSlot = -1;
        if (retType != null)
        {
            retSlot = _ctx.Builder.AllocScratch(retType.Value);
            _builder.EmitAssign(retSlot, InvocationHandler.DefaultConst(_builder, retType.Value));
        }

        var dispatch = new InvocationHandler(new LoweringServices(_ctx));

        // CW9/CW21 (2026-07-15): this is an EXPORTED entry — a direct SendCustomEvent (foreign
        // program or by-name) reaches it with the env global at its initial null, and the doc
        // comment's "null list → default" promise had only its empty half implemented: get_Length
        // faulted where every sibling bridge LogErrors + returns default. Mirror the closure
        // bridge's env-null arm; retSlot is already default-initialized, so the conv-ret store
        // below the guard IS the default return.
        var listOk = _bridge.CallExtern(StorageTypes.Boolean,
            UdonAbi.ObjectInequality,
            new CLeaf[] { _builder.SlotRef(listSlot), _builder.Const(null, StorageTypes.Object) });
        _builder.EmitIf(listOk,
            _ =>
            {
                var nSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
                _builder.EmitAssign(nSlot, _bridge.CallExtern(StorageTypes.Int32, UdonAbi.SystemArrayLength,
                    new CLeaf[] { _builder.SlotRef(listSlot) }));

                var iSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
                _builder.EmitFor(
                    _ => _builder.EmitAssign(iSlot, _bridge.ConstInt(0)),
                    () => _bridge.CallExtern(StorageTypes.Boolean, UdonAbi.Int32LessThan,
                        new CLeaf[] { _builder.SlotRef(iSlot), _builder.SlotRef(nSlot) }),
                    _ => _builder.EmitAssign(iSlot, _bridge.CallExtern(StorageTypes.Int32,
                        UdonAbi.Int32Add,
                        new CLeaf[] { _builder.SlotRef(iSlot), _bridge.ConstInt(1) })),
                    _ =>
                    {
                        var elemSlot = _ctx.Builder.AllocScratch(StorageTypes.ObjectArray);
                        _builder.EmitAssign(elemSlot, _bridge.CallExtern(StorageTypes.ObjectArray, MulticastArrGet,
                            new CLeaf[] { _builder.SlotRef(listSlot), _builder.SlotRef(iSlot) }));

                        var argLeaves = new CLeaf[argSlots.Length];
                        for (int k = 0; k < argSlots.Length; k++) argLeaves[k] = _builder.SlotRef(argSlots[k]);

                        var elemRet = dispatch.EmitFanoutElementDispatch(_builder.SlotRef(elemSlot), invoke, typeParamMap, argLeaves);
                        if (retSlot >= 0 && elemRet != null)
                            _builder.EmitAssign(retSlot, elemRet);
                    });
            },
            _ => _bridge.CallExternVoid(UdonAbi.DebugLogError,
                new[] { (CLeaf)_builder.Const(
                    $"USugar: missing invocation list — multicast fan-out invoked with a null list ({fanoutName})",
                    StorageTypes.String) }));

        if (retSlot >= 0)
            _bridge.Store(DelegateAbi.ConvRetName(sigPart), _builder.SlotRef(retSlot));

        _builder.EmitReturn();
        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }
}
