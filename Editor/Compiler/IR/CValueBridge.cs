using System;

// ============================================================================
// TEMPORARY field-isomorphic adapters between the legacy HIR/LIR leaf types and the
// unified CValue vocabulary (Phase 1 of the Core IR migration). They prove CValue can
// losslessly represent both sides; they are deleted once handlers emit CValue directly
// (Phase 3-4). Pure renames — no semantic transformation.
// ============================================================================

public static class CValueBridge
{
    // ── HIR leaf expression -> CValue ──
    public static CValue FromHExpr(HExpr e) => e switch
    {
        HConst c => new CConst(c.Value, c.Type),
        HSlotRef s => new CSlotRef(s.SlotId, s.Type),
        HFuncRef f => new CFuncRef(f.FuncName),
        HFieldAddr fa => new CFieldRef(fa.FieldName, fa.Type, CFieldMode.Addr),
        HLoadField lf => new CFieldRef(lf.FieldName, lf.Type, CFieldMode.Load),
        _ => throw new ArgumentException(
            $"Not a leaf HExpr (compound expressions are not CValues): {e?.GetType().Name}"),
    };

    // ── CValue -> HIR leaf expression ──
    public static HExpr ToHExpr(CValue v) => v switch
    {
        CConst c => new HConst(c.Value, c.Type),
        CSlotRef s => new HSlotRef(s.SlotId, s.Type),
        CFuncRef f => new HFuncRef(f.FuncName),
        CFieldRef fr when fr.Mode == CFieldMode.Addr => new HFieldAddr(fr.FieldName, fr.Type),
        CFieldRef fr => new HLoadField(fr.FieldName, fr.Type), // Mode.Load
        _ => throw new ArgumentException($"Unknown CValue: {v?.GetType().Name}"),
    };

    // ── LIR operand -> CValue ──
    public static CValue FromLOperand(LOperand o) => o switch
    {
        LConst c => new CConst(c.Value, c.Type),
        LSlotRef s => new CSlotRef(s.SlotId, s.Type),
        LFuncRef f => new CFuncRef(f.FuncName),
        LFieldRef fr => new CFieldRef(fr.FieldName, fr.Type, CFieldMode.Addr),
        _ => throw new ArgumentException($"Unknown LOperand: {o?.GetType().Name}"),
    };

    // ── CValue -> LIR operand ──
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
}
