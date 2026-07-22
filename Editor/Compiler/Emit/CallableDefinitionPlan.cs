using Microsoft.CodeAnalysis;

/// <summary>The complete callable definition universe and eager registration projections. The
/// specialization projection is finalized after runtime dispatch is seeded and before registration.</summary>
internal sealed class CallableDefinitionPlan
{
    public readonly IMethodSymbol[] ProgramMethods;
    public readonly IMethodSymbol[] ForeignStatics;
    public readonly IMethodSymbol[] StructMethods;
    public readonly IMethodSymbol[] BaseInstanceMethods;
    public readonly IMethodSymbol[] Definitions;
    public IMethodSymbol[] Specializations = System.Array.Empty<IMethodSymbol>();

    public CallableDefinitionPlan(IMethodSymbol[] programMethods, IMethodSymbol[] foreignStatics,
        IMethodSymbol[] structMethods, IMethodSymbol[] baseInstanceMethods,
        IMethodSymbol[] definitions)
    {
        ProgramMethods = programMethods;
        ForeignStatics = foreignStatics;
        StructMethods = structMethods;
        BaseInstanceMethods = baseInstanceMethods;
        Definitions = definitions;
    }
}
