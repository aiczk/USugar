using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Immutable callable universe consumed by registration and body lowering.
/// Planning must finish every specialization and closure projection before constructing this value.
/// </summary>
internal sealed class CallableDefinitionPlan
{
    public readonly IReadOnlyList<IMethodSymbol> ProgramMethods;
    public readonly IReadOnlyList<IMethodSymbol> ForeignStatics;
    public readonly IReadOnlyList<IMethodSymbol> StructMethods;
    public readonly IReadOnlyList<IMethodSymbol> BaseInstanceMethods;
    public readonly IReadOnlyList<IMethodSymbol> Definitions;
    public readonly IReadOnlyList<IMethodSymbol> Specializations;
    public readonly IReadOnlyDictionary<SpecializationKey, IMethodSymbol> SpecializationsByKey;
    public readonly IReadOnlyList<ClosureSpecializationCandidate> ClosureSpecializations;

    public CallableDefinitionPlan(IEnumerable<IMethodSymbol> programMethods,
        IEnumerable<IMethodSymbol> foreignStatics,
        IEnumerable<IMethodSymbol> structMethods,
        IEnumerable<IMethodSymbol> baseInstanceMethods,
        IEnumerable<IMethodSymbol> definitions,
        IEnumerable<IMethodSymbol> specializations,
        IEnumerable<ClosureSpecializationCandidate> closureSpecializations)
    {
        ProgramMethods = Copy(programMethods);
        ForeignStatics = Copy(foreignStatics);
        StructMethods = Copy(structMethods);
        BaseInstanceMethods = Copy(baseInstanceMethods);
        Definitions = Copy(definitions);
        Specializations = Copy(specializations);
        var keyed = new Dictionary<SpecializationKey, IMethodSymbol>();
        foreach (var specialization in Specializations)
        {
            var key = SpecializationKey.ForMethod(specialization);
            if (keyed.TryGetValue(key, out var existing)
                && !SymbolEqualityComparer.Default.Equals(existing, specialization))
                throw new InvalidOperationException(
                    $"Conflicting callable specializations for '{specialization.ToDisplayString()}'.");
            keyed[key] = specialization;
        }
        SpecializationsByKey =
            new ReadOnlyDictionary<SpecializationKey, IMethodSymbol>(keyed);
        ClosureSpecializations = Array.AsReadOnly(
            (closureSpecializations ?? Array.Empty<ClosureSpecializationCandidate>()).ToArray());
    }

    static IReadOnlyList<IMethodSymbol> Copy(IEnumerable<IMethodSymbol> methods)
        => Array.AsReadOnly((methods ?? Enumerable.Empty<IMethodSymbol>()).ToArray());
}
