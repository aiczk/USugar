using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>Registers the planner's closed named-method specializations before any body is emitted.</summary>
internal sealed class SpecializationRegistrar
{
    readonly LoweringServices _lowering;
    public SpecializationRegistrar(LoweringServices lowering) => _lowering = lowering;

    public void Register(IMethodSymbol specialization)
        => _lowering.MaterializeGenericSpecialization(specialization);

    public void Register(ClosureSpecializationCandidate candidate)
    {
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map = null;
        for (var i = candidate.OwnerSpecs.Length - 1; i >= 0; i--)
            map = TypeEnvironment.ForMethod(candidate.OwnerSpecs[i], map);
        map = TypeEnvironment.ForMethod(candidate.Method, map);
        using var genericScope = _lowering.State.Generics.EnterOverlayScope(map);
        using var methodScope = _lowering.State.Methods.EnterEmission(
            candidate.OwnerSpecs.Length > 0 ? candidate.OwnerSpecs[0] : null,
            null, null, candidate.OwnerSpecs);
        if (candidate.Method.IsGenericMethod)
            _lowering.MaterializeGenericSpecialization(candidate.Method);
        else
            _lowering.RegisterLocalFunction(candidate.Method);
    }
}
