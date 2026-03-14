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
    public static CodeGenResult GenerateUasmFromHir(HModule hirModule)
    {
        HirVerifier.Verify(hirModule);
        var lirModule = HirToLir.Lower(hirModule);
        return LirToUasm.Generate(lirModule);
    }
}
