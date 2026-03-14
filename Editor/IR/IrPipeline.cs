/// <summary>
/// HIR/LIR compilation pipeline.
///
/// Pipeline: Handlers → HirBuilder → HModule → HirVerifier → HirToLir → LModule → LirToUasm → UASM
/// </summary>
public static class IrPipeline
{
    /// <summary>
    /// Generate UASM from an HModule via the HIR → LIR → UASM pipeline.
    /// </summary>
    /// <param name="hirModule">The HIR module to compile.</param>
    /// <param name="dumpEnabled">When true, write IR dump files to Temp/USugar/{className}/.</param>
    public static CodeGenResult GenerateUasmFromHir(HModule hirModule, bool dumpEnabled = false)
    {
        var className = hirModule.ClassName ?? "unknown";

        if (dumpEnabled)
            DumpToFile(className, "1_hir.txt", hirModule.Dump());

        HirVerifier.Verify(hirModule);

        // HIR optimization
        HirOptimizer.ConstantFold(hirModule);
        HirOptimizer.DeadCodeElimination(hirModule);
        HirOptimizer.CopyPropagation(hirModule);

        if (dumpEnabled)
            DumpToFile(className, "1b_hir_optimized.txt", hirModule.Dump());

        var lirModule = HirToLir.Lower(hirModule);

        if (dumpEnabled)
            DumpToFile(className, "2_lir.txt", lirModule.Dump());

        LirOptimizer.SimplifyCFG(lirModule);
        LirOptimizer.CopyPropagation(lirModule);
        LirOptimizer.DeadCodeElimination(lirModule);
        LirOptimizer.SimplifyCFG(lirModule); // cleanup after DCE
        LirOptimizer.CoalesceSlots(lirModule);

        if (dumpEnabled)
            DumpToFile(className, "2b_lir_optimized.txt", lirModule.Dump());

        var result = LirToUasm.Generate(lirModule);

        if (dumpEnabled)
        {
            DumpToFile(className, "3_uasm.txt", result.Uasm);
            if (result.AnnotatedUasm != null)
                DumpToFile(className, "3_uasm_annotated.txt", result.AnnotatedUasm);
        }

        return result;
    }

    static void DumpToFile(string className, string fileName, string content)
    {
        try
        {
            var dir = System.IO.Path.Combine("Temp", "USugar", SanitizeName(className));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, fileName), content);
        }
        catch { /* ignore IO errors during dump */ }
    }

    static string SanitizeName(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
