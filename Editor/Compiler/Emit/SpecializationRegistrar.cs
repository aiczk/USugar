using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>Registers the planner's closed named-method specializations before any body is emitted.</summary>
internal sealed class SpecializationRegistrar : HandlerBase
{
    public SpecializationRegistrar(EmitContext context) : base(context) { }

    public void Register(IMethodSymbol specialization)
        => RegisterGenericSpecialization(specialization);

    public void Register(ClosureSpecializationCandidate candidate)
    {
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map = null;
        for (var i = candidate.OwnerSpecs.Length - 1; i >= 0; i--)
            map = TypeEnvironment.ForMethod(candidate.OwnerSpecs[i], map);
        map = TypeEnvironment.ForMethod(candidate.Method, map);
        using var genericScope = _ctx.Generics.EnterOverlayScope(map);
        using var methodScope = _ctx.Methods.EnterEmission(
            candidate.OwnerSpecs.Length > 0 ? candidate.OwnerSpecs[0] : null,
            null, null, candidate.OwnerSpecs);
        if (candidate.Method.IsGenericMethod)
            RegisterGenericSpecialization(candidate.Method);
        else
            RegisterLocalFunction(candidate.Method);
    }
}
