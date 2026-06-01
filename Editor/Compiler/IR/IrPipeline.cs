/// <summary>
/// Core IR compilation pipeline.
///
/// Pipeline: Handlers → CoreBuilder → CModule → CoreVerify → CoreOptimizer (structured) →
///           CoreFlatten (structured → flat) → FlatVerify → CoreFlatOptimizer → CoreToUasm → UASM
/// </summary>
public static class IrPipeline
{
    /// <summary>
    /// Generate UASM from a Core IR module. Handlers build the structured Core directly; the module
    /// is verified and optimized in structured form, flattened in place by CoreFlatten (the one
    /// structured→flat gate, asserted by FlatVerify), then run through the flat optimizer and the
    /// Core code generator. HIR and LIR no longer exist on the live path.
    /// </summary>
    public static CodeGenResult GenerateUasmFromCore(CModule coreModule, bool dumpEnabled = false)
    {
        var className = coreModule.ClassName ?? "unknown";

        CoreVerify.Verify(coreModule);

        // Structured optimization
        CoreOptimizer.ConstantFold(coreModule);
        CoreOptimizer.DeadCodeElimination(coreModule);
        CoreOptimizer.CopyPropagation(coreModule);

        // Structured → flat (in place): CoreFlatten + FlatVerify post-condition.
        foreach (var cf in coreModule.Functions)
        {
            CoreFlatten.Lower(cf);
            FlatVerify.Verify(cf);
        }

        // Flat optimization (identical pass order to the former LIR backend).
        CoreFlatOptimizer.SimplifyCFG(coreModule);
        CoreFlatOptimizer.CopyPropagation(coreModule);
        CoreFlatOptimizer.DeadCodeElimination(coreModule);
        CoreFlatOptimizer.SimplifyCFG(coreModule); // cleanup after DCE
        CoreFlatOptimizer.CoalesceSlots(coreModule);

        var result = CoreToUasm.Generate(coreModule);

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
