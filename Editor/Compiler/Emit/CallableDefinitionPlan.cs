using System;
using System.Collections.Generic;
using System.Linq;
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
    readonly HashSet<IMethodSymbol> _specializationCandidates =
        new(SymbolEqualityComparer.Default);
    IMethodSymbol[] _specializations;
    ClosureSpecializationCandidate[] _closureSpecializations = Array.Empty<ClosureSpecializationCandidate>();
    internal IEnumerable<IMethodSymbol> SpecializationCandidates => _specializationCandidates;
    public IReadOnlyList<IMethodSymbol> Specializations => _specializations
        ?? throw new InvalidOperationException("Callable specialization plan has not been frozen.");
    public IReadOnlyList<ClosureSpecializationCandidate> ClosureSpecializations
        => _closureSpecializations;

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

    internal void AddSpecializationCandidates(IEnumerable<IMethodSymbol> methods)
    {
        if (_specializations != null)
            throw new InvalidOperationException("Callable specialization plan is already frozen.");
        _specializationCandidates.UnionWith(methods);
    }

    internal void FreezeSpecializations(IEnumerable<IMethodSymbol> methods)
    {
        if (_specializations != null)
            throw new InvalidOperationException("Callable specialization plan was frozen twice.");
        _specializationCandidates.UnionWith(methods);
        _specializations = _specializationCandidates.ToArray();
        _specializationCandidates.Clear();
    }

    internal void SetClosureSpecializations(IEnumerable<ClosureSpecializationCandidate> closures)
        => _closureSpecializations = closures?.ToArray()
            ?? Array.Empty<ClosureSpecializationCandidate>();
}
