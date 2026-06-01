/// <summary>
/// HIR/LIR compilation pipeline.
///
/// Pipeline: Handlers → HirBuilder → HModule → HirVerifier → HirToLir → LModule → LirToUasm → UASM
/// </summary>
public static class IrPipeline
{
    /// <summary>
    /// Generate UASM from a Core IR module: CoreVerify → CoreOptimizer → CoreFlatten → LIR → UASM.
    /// Handlers build Core directly; HIR is no longer on the live path. The LIR backend is retained
    /// until Phase 4. Structured (HIR) dumps are omitted until the Core IR gains Dump().
    /// </summary>
    public static CodeGenResult GenerateUasmFromCore(CModule coreModule, bool dumpEnabled = false)
    {
        var className = coreModule.ClassName ?? "unknown";

        CoreVerify.Verify(coreModule);

        // Core structured optimization
        CoreOptimizer.ConstantFold(coreModule);
        CoreOptimizer.DeadCodeElimination(coreModule);
        CoreOptimizer.CopyPropagation(coreModule);

        // Flatten each function, then bridge the flat Core to LIR for the (retained) backend.
        var lirModule = CorePipeline.FlattenCoreToLir(coreModule);

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
            var dir = System.IO.Path.Combine("Library", "USugarCache", SanitizeName(className));
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
