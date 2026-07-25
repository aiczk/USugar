using System;

/// <summary>The typed result of the Core IR pipeline.</summary>
public sealed class IrPipelineResult
{
    public readonly VerifiedFlatModule FlatModule;
    public readonly CodeGenResult CodeGen;

    public IrPipelineResult(VerifiedFlatModule flatModule, CodeGenResult codeGen)
    {
        FlatModule = flatModule ?? throw new ArgumentNullException(nameof(flatModule));
        CodeGen = codeGen;
    }
}

/// <summary>
/// One-way Core IR pipeline:
/// StructuredModule -> verify -> FlatModule -> verify -> allocate/spill -> verify -> UASM.
/// </summary>
public static class IrPipeline
{
    public static IrPipelineResult Run(StructuredModule structuredModule)
    {
        if (structuredModule == null) throw new ArgumentNullException(nameof(structuredModule));
        CoreVerify.Verify(structuredModule);

        // CoreFlatten creates a distinct graph. The verified structured graph is never repurposed.
        var flatModule = CoreFlatten.Lower(structuredModule);
        FlatVerify.Verify(flatModule);

        // Slot coalescing is the sole retained flat optimization. Measurements showed that the
        // removed CFG/value rewrites changed neither EXTERN count nor runtime cost.
        CoreFlatOptimizer.CoalesceSlots(flatModule);

        // Recursion spill insertion allocates flat-only scratch slots after coalescing.
        CoreFlatOptimizer.InsertRecursionSpills(flatModule);
        var verifiedModule = VerifiedFlatModule.VerifyAndFreeze(flatModule);
        return new IrPipelineResult(
            verifiedModule,
            CoreToUasm.Generate(verifiedModule));
    }
}
