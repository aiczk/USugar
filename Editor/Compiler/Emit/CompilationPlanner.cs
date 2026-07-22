using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Builds the complete immutable-by-convention plan consumed by registration and body emission.</summary>
internal sealed class CompilationPlanner
{
    readonly Compilation _compilation;
    readonly Func<IMethodSymbol[]> _methods;
    readonly Func<IMethodSymbol[], ReachableBodies> _reach;
    readonly Func<IEnumerable<IOperation>> _fieldInits;
    readonly Func<IMethodSymbol, IOperation> _bodyOf;
    readonly Func<INamedTypeSymbol, IEnumerable<IOperation>> _classFieldInits;

    public CompilationPlanner(Compilation compilation, Func<IMethodSymbol[]> methods,
        Func<IMethodSymbol[], ReachableBodies> reach, Func<IEnumerable<IOperation>> fieldInits,
        Func<IMethodSymbol, IOperation> bodyOf,
        Func<INamedTypeSymbol, IEnumerable<IOperation>> classFieldInits)
    {
        _compilation = compilation;
        _methods = methods;
        _reach = reach;
        _fieldInits = fieldInits;
        _bodyOf = bodyOf;
        _classFieldInits = classFieldInits;
    }

    public ClassCompilePlan Build()
    {
        var plan = new ClassCompilePlanBuilder(_methods, _reach, _fieldInits).Build();
        var closedMints = new GenericTypeSpecCensus(_compilation, _bodyOf, _classFieldInits).Build(plan);
        // Keep portable non-generic classes seeded by reach: they may enter from another Udon program
        // without a local mint. The census contributes closed generic instantiations on top.
        plan.Reach.MintedClasses.RemoveWhere(ClassTypeObjectContext.ContainsTypeParameter);
        plan.Reach.MintedClasses.UnionWith(closedMints);
        return plan;
    }
}
