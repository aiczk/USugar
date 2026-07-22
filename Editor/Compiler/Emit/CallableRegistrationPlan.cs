using Microsoft.CodeAnalysis;

/// <summary>The frozen callable set registered and emitted for one program. Reachability computes
/// the candidates; ClassCompilePlanBuilder applies registration-only gates exactly once.</summary>
internal sealed class CallableRegistrationPlan
{
    public readonly IMethodSymbol[] ForeignStatics;
    public readonly IMethodSymbol[] StructMethods;
    public readonly IMethodSymbol[] BaseInstanceMethods;

    public CallableRegistrationPlan(IMethodSymbol[] foreignStatics, IMethodSymbol[] structMethods,
        IMethodSymbol[] baseInstanceMethods)
    {
        ForeignStatics = foreignStatics;
        StructMethods = structMethods;
        BaseInstanceMethods = baseInstanceMethods;
    }
}
