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
            plan.Callables.SpecializationCandidates, SymbolEqualityComparer.Default);
        var definitions = new HashSet<IMethodSymbol>(
            plan.Callables.Definitions, SymbolEqualityComparer.Default);
        var pending = new Queue<(IOperation Body,
            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> Map)>();
        foreach (var root in plan.Reach.BodyByDef.Values.Concat(plan.FieldInitOps))
            pending.Enqueue((root, null));

        var scannedSpecializations = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var specialization in result.ToArray())
            EnqueueSpecialization(specialization);

        while (pending.Count > 0)
        {
            var (root, map) = pending.Dequeue();
            foreach (var operation in root.DescendantsAndSelf())
                foreach (var site in CallableSites.FromOperation(operation))
                {
                    var receiver = TypeEnvironment.CloseType(
                        _context.Compilation, site.Receiver?.Type, map) as INamedTypeSymbol;
                    if (receiver == null) continue;
                    var targetMethod = TypeEnvironment.CloseMethod(
                        _context.Compilation, site.Target, map);
                    var closedSite = CallableSites.Synthetic(site.Kind, targetMethod, site.Receiver);
                    foreach (var target in _context.VirtualDispatch.Resolve(closedSite, receiver).RuntimeTargets)
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
                        if (result.Add(method)) EnqueueSpecialization(method);
                    }
                }
        }
        return result.ToArray();

        void EnqueueSpecialization(IMethodSymbol method)
        {
            if (!scannedSpecializations.Add(method)) return;
            if (!plan.Reach.BodyByDef.TryGetValue(method.OriginalDefinition, out var body)) return;
            pending.Enqueue((body, TypeEnvironment.ForMethod(method)));
        }
    }
}
