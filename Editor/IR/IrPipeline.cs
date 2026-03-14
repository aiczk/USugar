using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// IR optimization pipeline.
///
/// Pipeline: Handlers → IrBuilder → IrModule → Mem2Reg → Optimize → PhiElimination
///   → RegisterAllocator → CodeGen → UASM text
/// </summary>
public static class IrPipeline
{
    /// <summary>
    /// Run the full IR optimization pipeline on an IrModule.
    /// </summary>
    public static void Optimize(IrModule irModule, int optLevel = 5, Action<string, IrModule> onPass = null)
    {
        // Phase 1: Promote local fields to SSA
        Mem2Reg.Run(irModule);
        onPass?.Invoke("after-mem2reg", irModule);

        // Phase 2: Optimize each function
        foreach (var func in irModule.Functions)
        {
            IrOptimizer.SimplifyCFG(func);
            if (optLevel >= 1) IrOptimizer.ConstantFolding(func);
            IrOptimizer.CopyPropagation(func);
            IrOptimizer.DCE(func);
            if (optLevel >= 2) IrOptimizer.CopyPropagation(func);
            if (optLevel >= 3) IrOptimizer.GVN(func);
            IrOptimizer.SimplifyCFG(func);
            IrOptimizer.DCE(func);
        }
        onPass?.Invoke("after-optimize", irModule);

        // Phase 3: Lower out of SSA
        PhiElimination.Run(irModule);
        onPass?.Invoke("after-phi-elim", irModule);

        // Phase 4: Register allocation
        foreach (var func in irModule.Functions)
            RegisterAllocator.AllocateAndRewrite(func);
    }

    /// <summary>
    /// Generate UASM from an optimized IrModule.
    /// Returns CodeGenResult containing UASM text, heap size, and constants.
    /// </summary>
    public static CodeGenResult GenerateUasm(IrModule irModule)
    {
        return CodeGen.Generate(irModule);
    }

    /// <summary>
    /// Generate UASM from an HModule via the HIR → LIR → UASM pipeline.
    /// </summary>
    public static CodeGenResult GenerateUasmFromHir(HModule hirModule)
    {
        var lirModule = HirToLir.Lower(hirModule);
        return LirToUasm.Generate(lirModule);
    }
}
