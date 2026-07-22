using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Adds closed runtime-dispatch targets after type objects and VirtualDispatch are seeded,
/// but before callable registration and body emission begin.</summary>
internal sealed class RuntimeSpecializationPlanner
{
    readonly EmitContext _context;

    public RuntimeSpecializationPlanner(EmitContext context) => _context = context;

    public IMethodSymbol[] Build(ClassCompilePlan plan)
    {
        var result = new HashSet<IMethodSymbol>(
            plan.Callables.Specializations, SymbolEqualityComparer.Default);
        var definitions = new HashSet<IMethodSymbol>(
            plan.Callables.Definitions, SymbolEqualityComparer.Default);
        foreach (var root in plan.Reach.BodyByDef.Values.Concat(plan.FieldInitOps))
            foreach (var operation in root.DescendantsAndSelf())
                foreach (var site in CallableSites.FromOperation(operation))
                {
                    if (site.Receiver?.Type is not INamedTypeSymbol receiver) continue;
                    foreach (var target in _context.VirtualDispatch.Resolve(site, receiver).RuntimeTargets)
                    {
                        var method = target.Impl;
                        if (site.Kind is not (CallableSiteKind.EventAdd or CallableSiteKind.EventRemove)
                            && !method.IsGenericMethod && !method.ContainingType.IsGenericType)
                            continue;
                        if (!definitions.Contains(method.OriginalDefinition)) continue;
                        if (ClassTypeObjectContext.ContainsTypeParameter(method.ContainingType)
                            || method.IsGenericMethod && method.TypeArguments.Any(
                                ClassTypeObjectContext.ContainsTypeParameter)) continue;
                        if (method.AssociatedSymbol is IPropertySymbol property
                            && !UasmEmitter.IsComputedProperty(property)) continue;
                        result.Add(method);
                    }
                }
        return result.ToArray();
    }
}
