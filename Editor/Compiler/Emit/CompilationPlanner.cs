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
    readonly Func<IEnumerable<IOperation>> _fieldInits;
    readonly Func<IMethodSymbol, IOperation> _bodyOf;
    readonly Func<INamedTypeSymbol, IEnumerable<IOperation>> _classFieldInits;
    readonly Func<IEnumerable<IMethodSymbol>> _additionalCallableDefinitions;
    readonly INamedTypeSymbol _rootType;

    public CompilationPlanner(Compilation compilation, Func<IMethodSymbol[]> methods,
        Func<IMethodSymbol[], ReachableBodies> reach, Func<IEnumerable<IOperation>> fieldInits,
        Func<IMethodSymbol, IOperation> bodyOf,
        Func<INamedTypeSymbol, IEnumerable<IOperation>> classFieldInits,
        Func<IEnumerable<IMethodSymbol>> additionalCallableDefinitions,
        INamedTypeSymbol rootType)
    {
        _compilation = compilation;
        _methods = methods;
        _reach = reach;
        _fieldInits = fieldInits;
        _bodyOf = bodyOf;
        _classFieldInits = classFieldInits;
        _additionalCallableDefinitions = additionalCallableDefinitions;
        _rootType = rootType;
    }

    public ClassCompilePlan Build()
    {
        var plan = new ClassCompilePlanBuilder(
            _methods, _reach, _fieldInits, _additionalCallableDefinitions).Build();
        var specializationCensus = new GenericTypeSpecCensus(
            _compilation, _bodyOf, _classFieldInits, _rootType).Build(plan);
        var definitions = new HashSet<IMethodSymbol>(
            plan.Callables.Definitions, SymbolEqualityComparer.Default);
        var eagerlyRegistered = new HashSet<IMethodSymbol>(
            plan.Callables.ProgramMethods.Where(method => !method.IsGenericMethod)
                .Concat(plan.Callables.ForeignStatics)
                .Concat(plan.Callables.StructMethods)
                .Concat(plan.Callables.BaseInstanceMethods),
            SymbolEqualityComparer.Default);
        plan.Callables.AddSpecializationCandidates(specializationCensus.MethodSpecializations
            .Where(method => definitions.Contains(method.OriginalDefinition)
                && !eagerlyRegistered.Contains(method)));
        plan.Callables.SetClosureSpecializations(specializationCensus.ClosureSpecializations);
        // Keep portable non-generic classes seeded by reach: they may enter from another Udon program
        // without a local mint. The census contributes closed generic instantiations on top.
        plan.Reach.MintedClasses.RemoveWhere(ClassTypeObjectContext.ContainsTypeParameter);
        plan.Reach.MintedClasses.UnionWith(specializationCensus.MintedClasses);
        return plan;
    }
}
