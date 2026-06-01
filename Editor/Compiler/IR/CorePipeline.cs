// ============================================================================
// CorePipeline — bridge-mediated drop-in for HirToLir.Lower that routes flattening through the
// unified Core IR (HIR -> Core structured -> CoreFlatten -> Core flat -> LIR). First live-path
// step of the migration: CoreFlatten replaces HirToLir in production while handlers still build
// HIR and the backend still consumes LIR. Because CoreFlatten is byte-identical to HirToLir and
// the bridges are field-isomorphic, the produced LModule is unchanged (the snapshot oracle
// guards this). Deleted once handlers emit Core directly and LIR is retired (Phase 4).
// ============================================================================

public static class CorePipeline
{
    /// <summary>Flatten an HModule into an LModule via the Core IR (drop-in for HirToLir.Lower).</summary>
    public static LModule FlattenViaCoreIR(HModule hir)
    {
        var core = FromHModule(hir);
        foreach (var cf in core.Functions)
        {
            CoreFlatten.Lower(cf);
            FlatVerify.Verify(cf);
        }
        return ToLModule(core);
    }

    static CModule FromHModule(HModule hir)
    {
        var cm = new CModule { ClassName = hir.ClassName };
        foreach (var f in hir.Fields) cm.Fields.Add(f);
        foreach (var hf in hir.Functions)
        {
            var cf = new CFunction(hf.Name, hf.ExportName) { ReturnType = hf.ReturnType };
            foreach (var p in hf.ParamFieldNames) cf.ParamFieldNames.Add(p);
            foreach (var r in hf.ReturnSlots) cf.ReturnSlots.Add(r);
            foreach (var s in hf.Slots) cf.Slots.Add(new SlotDecl(s.Id, s.Type, s.Class, s.FixedName));
            cf.Body = (CBlock)CNodeBridge.FromHStmt(hf.Body);
            cm.Functions.Add(cf);
        }
        return cm;
    }

    static LModule ToLModule(CModule core)
    {
        var lm = new LModule { ClassName = core.ClassName };
        foreach (var f in core.Fields) lm.Fields.Add(f);
        foreach (var cf in core.Functions) lm.Functions.Add(CoreFlattenBridge.ToLFunction(cf));
        return lm;
    }
}
