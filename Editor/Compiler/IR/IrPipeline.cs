/// <summary>
/// Core IR compilation pipeline. There is ONE IR (CModule: structured, then flat); HIR/LIR no longer exist,
/// and there is NO structured-optimization pass.
///
/// Pipeline: Handlers/CoreBuilder build the structured CModule →
///           CoreVerify →
///           CoreFlatten (structured → flat, in place) + FlatVerify →
///           CoalesceSlots (the sole optimizer — slot allocation) →
///           InsertRecursionSpills + FlatVerify →
///           CoreToUasm → UASM
/// </summary>
public static class IrPipeline
{
    /// <summary>
    /// Generate UASM from a Core IR module. Handlers build the structured Core directly; the module
    /// is verified and optimized in structured form, flattened in place by CoreFlatten (the one
    /// structured→flat gate, asserted by FlatVerify), then run through the flat optimizer and the
    /// Core code generator. HIR and LIR no longer exist on the live path.
    /// </summary>
    public static CodeGenResult GenerateUasmFromCore(CModule coreModule)
    {
        CoreVerify.Verify(coreModule);

        // Structured → flat (in place): CoreFlatten + FlatVerify post-condition.
        foreach (var cf in coreModule.Functions)
        {
            CoreFlatten.Lower(cf);
            FlatVerify.Verify(cf);
        }

        // Slot coalescing is the only optimization retained: measurement showed it delivers the entire
        // heap-variable reduction (~30–100% fewer vars → smaller serialized program), while the former
        // constant-fold / DCE / copy-propagation / CFG-simplify passes changed neither EXTERN count nor
        // runtime cost on real (field-driven) code — Udon is EXTERN-bound, so they earned their complexity
        // and correctness risk for nothing. Value-equivalence (opt vs no-opt heap results) was verified.
        CoreFlatOptimizer.CoalesceSlots(coreModule);

        // Recursion frame spill/reload: wraps each recursive call with a spill/reload of the frame fields +
        // the slots LIVE ACROSS the call (using post-coalesce liveness — the physical slot set is small, so the
        // software stack stays bounded). Runs after coalescing; re-verify the flat shape after the rewrite.
        CoreFlatOptimizer.InsertRecursionSpills(coreModule);
        foreach (var cf in coreModule.Functions)
            FlatVerify.Verify(cf);

        return CoreToUasm.Generate(coreModule);
    }
}
