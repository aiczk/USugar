using Microsoft.CodeAnalysis;

/// <summary>Registration projections consumed by body emission after recursion analysis.</summary>
internal sealed class ProgramRegistration
{
    public readonly IMethodSymbol[] ForeignStatics;
    public readonly IMethodSymbol[] StructMethods;
    public readonly IMethodSymbol[] BaseInstanceMethods;

    public ProgramRegistration(IMethodSymbol[] foreignStatics, IMethodSymbol[] structMethods,
        IMethodSymbol[] baseInstanceMethods)
    {
        ForeignStatics = foreignStatics;
        StructMethods = structMethods;
        BaseInstanceMethods = baseInstanceMethods;
    }
}
