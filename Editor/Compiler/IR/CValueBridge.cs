using System;

// ============================================================================
// TEMPORARY field-isomorphic adapters between the legacy HIR/LIR types and the unified
// CValue vocabulary (Phase 1 of the Core IR migration). They prove CValue can losslessly
// represent both sides; they are deleted once handlers emit CValue directly (Phase 3-4).
// Pure renames — no semantic transformation. HSelect/HCrossBehaviourCall are intentionally
// NOT handled here: they remain first-class Core nodes (CSelect/CCrossCall) added later.
// ============================================================================

public static class CValueBridge
{
    // ── HIR expression -> CValue (leaves + nested-arg calls) ──
    public static CValue FromHExpr(HExpr e) => e switch
    {
        HConst c => new CConst(c.Value, c.Type),
        HSlotRef s => new CSlotRef(s.SlotId, s.Type),
        HFuncRef f => new CFuncRef(f.FuncName),
        HFieldAddr fa => new CFieldRef(fa.FieldName, fa.Type, CFieldMode.Addr),
        HLoadField lf => new CFieldRef(lf.FieldName, lf.Type, CFieldMode.Load),
        HExternCall ec => new CExternCall(ec.Sig, ec.Args.ConvertAll(FromHExpr), ec.Type, null),
        HInternalCall ic => new CInternalCall(ic.FuncName, ic.Args.ConvertAll(FromHExpr), ic.Type, null),
        HSelect sel => new CSelect(FromHExpr(sel.Cond), FromHExpr(sel.TrueVal), FromHExpr(sel.FalseVal), sel.Type),
        HCrossBehaviourCall cc => new CCrossCall(FromHExpr(cc.Instance), cc.EventName,
            cc.Params.ConvertAll(p => (p.ParamName, FromHExpr(p.Value))), cc.Returns, cc.Type),
        _ => throw new ArgumentException($"Unknown HExpr: {e?.GetType().Name}"),
    };

    // ── CValue -> HIR expression ──
    public static HExpr ToHExpr(CValue v) => v switch
    {
        CConst c => new HConst(c.Value, c.Type),
        CSlotRef s => new HSlotRef(s.SlotId, s.Type),
        CFuncRef f => new HFuncRef(f.FuncName),
        CFieldRef fr when fr.Mode == CFieldMode.Addr => new HFieldAddr(fr.FieldName, fr.Type),
        CFieldRef fr => new HLoadField(fr.FieldName, fr.Type), // Mode.Load
        CExternCall ec => new HExternCall(ec.Sig, ec.Args.ConvertAll(ToHExpr), ec.Type),
        CInternalCall ic => new HInternalCall(ic.FuncName, ic.Args.ConvertAll(ToHExpr), ic.Type),
        CSelect sel => new HSelect(ToHExpr(sel.Cond), ToHExpr(sel.TrueVal), ToHExpr(sel.FalseVal), sel.Type),
        CCrossCall cc => new HCrossBehaviourCall(ToHExpr(cc.Instance), cc.EventName,
            cc.Params.ConvertAll(p => (p.ParamName, ToHExpr(p.Value))), cc.Returns, cc.Type),
        _ => throw new ArgumentException($"Unknown CValue: {v?.GetType().Name}"),
    };

    // ── LIR operand <-> CValue (4 leaves) ──
    public static CValue FromLOperand(LOperand o) => o switch
    {
        LConst c => new CConst(c.Value, c.Type),
        LSlotRef s => new CSlotRef(s.SlotId, s.Type),
        LFuncRef f => new CFuncRef(f.FuncName),
        LFieldRef fr => new CFieldRef(fr.FieldName, fr.Type, CFieldMode.Addr),
        _ => throw new ArgumentException($"Unknown LOperand: {o?.GetType().Name}"),
    };

    public static LOperand ToLOperand(CValue v) => v switch
    {
        CConst c => new LConst(c.Value, c.Type),
        CSlotRef s => new LSlotRef(s.SlotId, s.Type),
        CFuncRef f => new LFuncRef(f.FuncName),
        CFieldRef fr when fr.Mode == CFieldMode.Addr => new LFieldRef(fr.FieldName, fr.Type),
        CFieldRef _ => throw new InvalidOperationException(
            "CFieldRef(Load) has no LIR operand form — a field load is materialized via the LLoadField instruction, not an operand."),
        _ => throw new ArgumentException($"Unknown CValue: {v?.GetType().Name}"),
    };

    // ── LIR call instruction <-> CValue op (flat role: leaf args + DestSlot) ──
    public static CValue FromLCall(LInst inst) => inst switch
    {
        LCallExtern e => new CExternCall(e.Sig, e.Args.ConvertAll(FromLOperand), e.RetType, e.DestSlot),
        LCallInternal c => new CInternalCall(c.FuncName, c.Args.ConvertAll(FromLOperand), c.RetType, c.DestSlot),
        _ => throw new ArgumentException($"Not a call LInst: {inst?.GetType().Name}"),
    };

    public static LInst ToLCall(CValue v) => v switch
    {
        CExternCall e => new LCallExtern(e.DestSlot, e.Sig, e.Args.ConvertAll(ToLOperand), e.Type),
        CInternalCall c => new LCallInternal(c.DestSlot, c.FuncName, c.Args.ConvertAll(ToLOperand), c.Type),
        _ => throw new ArgumentException($"Not a call CValue: {v?.GetType().Name}"),
    };
}
