using System;

// ============================================================================
// TEMPORARY adapter: a flattened Core function -> LFunction, used to prove CoreFlatten produces
// the SAME flat output as HirToLir (compare LFunction.Dump()). Deleted once LIR is retired and
// the backend reads Core flat blocks directly (Phase 4).
// ============================================================================

public static class CoreFlattenBridge
{
    /// <summary>Convert a Flat-shape CFunction to an LFunction (structural, for comparison).</summary>
    public static LFunction ToLFunction(CFunction f)
    {
        if (f.Shape != Shape.Flat)
            throw new InvalidOperationException("ToLFunction requires a flattened (Shape=Flat) CFunction.");

        var lf = new LFunction(f.Name, f.ExportName) { ReturnType = f.ReturnType };
        foreach (var p in f.ParamFieldNames) lf.ParamFieldNames.Add(p);
        foreach (var r in f.ReturnSlots) lf.ReturnSlots.Add(r);
        foreach (var s in f.Slots) lf.Slots.Add(new SlotDecl(s.Id, s.Type, s.Class, s.FixedName));

        foreach (var cb in f.FlatBlocks)
        {
            var lb = new LBlock(cb.FlatId) { Hint = cb.Hint };
            foreach (var inst in cb.Stmts) lb.Insts.Add(ToLInst(inst, f));
            lb.Term = ToLTerm(cb.Terminator);
            lf.Blocks.Add(lb);
        }

        return lf;
    }

    static LInst ToLInst(CStmt s, CFunction f) => s switch
    {
        CAssign a => new LMove(a.DestSlot, CValueBridge.ToLOperand(a.Value), f.Slots[a.DestSlot].Type),
        CStoreField sf => new LStoreField(sf.FieldName, CValueBridge.ToLOperand(sf.Value)),
        CLoadField lf => new LLoadField(lf.DestSlot, lf.FieldName, lf.Type),
        CExprStmt es => CValueBridge.ToLCall(es.Expr),
        _ => throw new InvalidOperationException($"Not a flat instruction: {s?.GetType().Name}"),
    };

    static LTerminator ToLTerm(CTerminator t) => t switch
    {
        CJump j => new LJump(j.TargetBlockId),
        CBranch b => new LBranch(CValueBridge.ToLOperand(b.Cond), b.TrueBlockId, b.FalseBlockId),
        CRet r => new LReturn(r.Value == null ? null : CValueBridge.ToLOperand(r.Value)),
        _ => throw new InvalidOperationException($"Unknown CTerminator: {t?.GetType().Name}"),
    };
}
