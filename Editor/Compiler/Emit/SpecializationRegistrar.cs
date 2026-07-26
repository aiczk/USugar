using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Materializes the planner's complete closed callable set.</summary>
internal sealed class SpecializationRegistrar
{
    readonly LoweringServices _lowering;

    public SpecializationRegistrar(LoweringServices lowering)
        => _lowering = lowering;

    public void Register(IMethodSymbol specialization)
        => _lowering.RegisterSpecializationForPlanning(specialization);

    public void Register(ClosureSpecializationCandidate candidate)
    {
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map = null;
        for (var i = candidate.OwnerSpecs.Length - 1; i >= 0; i--)
            map = TypeEnvironment.ForMethod(candidate.OwnerSpecs[i], map);
        map = TypeEnvironment.ForMethod(candidate.Method, map);
        using var genericScope =
            _lowering.State.EnterTypeParamOverlay(map);
        using var methodScope =
            _lowering.State.Methods.EnterCallableScope(
                candidate.OwnerSpecs.Length > 0
                    ? candidate.OwnerSpecs[0]
                    : null,
                null,
                null,
                candidate.OwnerSpecs);
        if (candidate.Method.IsGenericMethod)
            _lowering.RegisterSpecializationForPlanning(candidate.Method);
        else
            _lowering.RegisterClosureForPlanning(candidate.Method);
    }
}
