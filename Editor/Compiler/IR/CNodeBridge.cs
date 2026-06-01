using System;

// ============================================================================
// TEMPORARY structured-statement adapters between HStmt and the Core CStmt vocabulary
// (Phase 2 of the Core IR migration). Structured role only — flat blocks/terminators are
// produced by CoreFlatten, not here. Pure renames; deleted once handlers emit CStmt directly
// (Phase 3-4).
// ============================================================================

public static class CNodeBridge
{
    public static CStmt FromHStmt(HStmt s) => s switch
    {
        HBlock b => new CBlock(b.Stmts.ConvertAll(FromHStmt)),
        HAssign a => new CAssign(a.DestSlot, CValueBridge.FromHExpr(a.Value)),
        HStoreField sf => new CStoreField(sf.FieldName, CValueBridge.FromHExpr(sf.Value)),
        HIf i => new CIf(CValueBridge.FromHExpr(i.Cond), CBlk(i.Then), CBlk(i.Else)),
        HWhile w => new CWhile(CValueBridge.FromHExpr(w.Cond), CBlk(w.Body), w.IsDoWhile, CBlk(w.CondBlock)),
        HFor f => new CFor(CBlk(f.Init), f.Cond == null ? null : CValueBridge.FromHExpr(f.Cond),
            CBlk(f.Update), CBlk(f.Body), CBlk(f.CondBlock)),
        HBreak _ => new CBreak(),
        HContinue _ => new CContinue(),
        HGoto g => new CGoto(g.Label),
        HLabelStmt l => new CLabel(l.Label),
        HReturn r => new CReturn(r.Value == null ? null : CValueBridge.FromHExpr(r.Value)),
        HExprStmt e => new CExprStmt(CValueBridge.FromHExpr(e.Expr)),
        _ => throw new ArgumentException($"Unknown HStmt: {s?.GetType().Name}"),
    };

    static CBlock CBlk(HBlock b) => (CBlock)FromHStmt(b);

    public static HStmt ToHStmt(CStmt s) => s switch
    {
        CBlock b => new HBlock(b.Stmts.ConvertAll(ToHStmt)),
        CAssign a => new HAssign(a.DestSlot, CValueBridge.ToHExpr(a.Value)),
        CStoreField sf => new HStoreField(sf.FieldName, CValueBridge.ToHExpr(sf.Value)),
        CIf i => new HIf(CValueBridge.ToHExpr(i.Cond), HBlk(i.Then), HBlk(i.Else)),
        CWhile w => new HWhile(CValueBridge.ToHExpr(w.Cond), HBlk(w.Body), w.IsDoWhile, HBlk(w.CondBlock)),
        CFor f => new HFor(HBlk(f.Init), f.Cond == null ? null : CValueBridge.ToHExpr(f.Cond),
            HBlk(f.Update), HBlk(f.Body), HBlk(f.CondBlock)),
        CBreak _ => new HBreak(),
        CContinue _ => new HContinue(),
        CGoto g => new HGoto(g.Label),
        CLabel l => new HLabelStmt(l.Label),
        CReturn r => new HReturn(r.Value == null ? null : CValueBridge.ToHExpr(r.Value)),
        CExprStmt e => new HExprStmt(CValueBridge.ToHExpr(e.Expr)),
        _ => throw new ArgumentException($"Unknown CStmt: {s?.GetType().Name}"),
    };

    static HBlock HBlk(CBlock b) => (HBlock)ToHStmt(b);
}
