using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Builds the analysis plan. Runtime dispatch adds its final specialization projection after
/// type-object seeding; registration and body emission only consume the finalized result.</summary>
internal sealed class CompilationPlanner
{
    readonly Compilation _compilation;
    readonly Func<IMethodSymbol[]> _methods;
    readonly Func<IMethodSymbol[], ReachableBodies> _reach;
    readonly FieldDiscoveryPlan _fields;
    readonly Func<IMethodSymbol, IOperation> _bodyOf;
    readonly Func<INamedTypeSymbol, IEnumerable<IOperation>> _classFieldInits;
    readonly Func<IEnumerable<IMethodSymbol>> _additionalCallableDefinitions;
    readonly INamedTypeSymbol _rootType;

    public CompilationPlanner(Compilation compilation, Func<IMethodSymbol[]> methods,
        Func<IMethodSymbol[], ReachableBodies> reach, FieldDiscoveryPlan fields,
        Func<IMethodSymbol, IOperation> bodyOf,
        Func<INamedTypeSymbol, IEnumerable<IOperation>> classFieldInits,
        Func<IEnumerable<IMethodSymbol>> additionalCallableDefinitions,
        INamedTypeSymbol rootType)
    {
        _compilation = compilation;
        _methods = methods;
        _reach = reach;
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
        _bodyOf = bodyOf;
        _classFieldInits = classFieldInits;
        _additionalCallableDefinitions = additionalCallableDefinitions;
        _rootType = rootType;
    }

    public ProgramDiscovery Build()
    {
        var seed = new ProgramDiscoverySeedBuilder(
            _methods, _reach, () => _fields.InitializerOperations,
            _additionalCallableDefinitions).Build();
        var specializationCensus = new GenericTypeSpecCensus(
            _compilation, _bodyOf, _classFieldInits, _rootType).Build(seed);
        var definitions = new HashSet<IMethodSymbol>(
            seed.Definitions, SymbolEqualityComparer.Default);
        var eagerlyRegistered = new HashSet<IMethodSymbol>(
            seed.ProgramMethods.Where(method => !method.IsGenericMethod)
                .Concat(seed.ForeignStatics)
                .Concat(seed.StructMethods)
                .Concat(seed.BaseInstanceMethods),
            SymbolEqualityComparer.Default);
        var specializations = specializationCensus.MethodSpecializations
            .Where(method => definitions.Contains(method.OriginalDefinition)
                && !eagerlyRegistered.Contains(method))
            .ToArray();
        var callables = new CallableDefinitionPlan(
            seed.ProgramMethods,
            seed.ForeignStatics,
            seed.StructMethods,
            seed.BaseInstanceMethods,
            seed.Definitions,
            specializations,
            specializationCensus.ClosureSpecializations);
        // Keep portable non-generic classes seeded by reach: they may enter from another Udon program
        // without a local mint. The census contributes closed generic instantiations on top.
        var reach = seed.Reach.Freeze(specializationCensus.MintedClasses);
        return new ProgramDiscovery(
            callables, reach, seed.CaptureRoots, seed.FieldInitOps, _fields);
    }
}
