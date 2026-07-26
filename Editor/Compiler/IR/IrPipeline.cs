using System;

/// <summary>
/// One-way Core IR pipeline:
/// mutable CFG -> verify -> allocate/spill -> verify/freeze -> UASM.
/// </summary>
public static class IrPipeline
{
    public static (
        VerifiedFlatModule FlatModule,
        CodeGenResult CodeGen) Run(
            FlatModule module,
            CoreBuilder builder)
    {
        if (module == null) throw new ArgumentNullException(nameof(module));
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (!ReferenceEquals(module, builder.Module))
            throw new ArgumentException(
                "The CFG builder does not own the supplied module.", nameof(builder));
        builder.Complete();
        FlatVerify.Verify(module);

        // Slot coalescing is the sole retained flat optimization. Measurements showed that the
        // removed CFG/value rewrites changed neither EXTERN count nor runtime cost.
        CoreFlatOptimizer.CoalesceSlots(module);

        // Recursion spill insertion allocates flat-only scratch slots after coalescing.
        CoreFlatOptimizer.InsertRecursionSpills(module);
        var verifiedModule = VerifiedFlatModule.VerifyAndFreeze(module);
        return (
            verifiedModule,
            CoreToUasm.Generate(verifiedModule));
    }
}
