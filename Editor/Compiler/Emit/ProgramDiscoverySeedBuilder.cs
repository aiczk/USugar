using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Materializes the source-level inputs used by the closed-world planner. This mutable seed never
/// crosses the planning/lowering boundary; <see cref="CompilationPlanner"/> publishes a
/// <see cref="ProgramDiscovery"/> instead.
/// </summary>
sealed class ProgramDiscoverySeedBuilder
{
    readonly Func<IMethodSymbol[]> _computeMethods;
    readonly Func<IMethodSymbol[], ReachableBodies> _buildReachableBodies;
    readonly Func<IEnumerable<IOperation>> _fieldInitOps;
    readonly Func<IEnumerable<IMethodSymbol>> _additionalCallableDefinitions;

    public ProgramDiscoverySeedBuilder(
        Func<IMethodSymbol[]> computeMethods,
        Func<IMethodSymbol[], ReachableBodies> buildReachableBodies,
        Func<IEnumerable<IOperation>> fieldInitOps,
        Func<IEnumerable<IMethodSymbol>> additionalCallableDefinitions)
    {
        _computeMethods = computeMethods;
        _buildReachableBodies = buildReachableBodies;
        _fieldInitOps = fieldInitOps;
        _additionalCallableDefinitions = additionalCallableDefinitions;
    }

    public ProgramDiscoverySeed Build()
    {
        var methods = _computeMethods();
        var reach = _buildReachableBodies(methods);
        var methodSet = new HashSet<IMethodSymbol>(methods, SymbolEqualityComparer.Default);
        var baseInstanceMethods = reach.BaseCopies.Where(bm => !methodSet.Contains(bm)).ToArray();
        // Local functions register at their declaration/forward-reference site. Keeping them in the
        // eager foreign-static projection used to create a dead duplicate StructuredFunction.
        var foreignStatics = reach.ForeignStatics
            .Where(fm => fm.MethodKind != MethodKind.LocalFunction).ToArray();
        var definitions = methods.Concat(foreignStatics).Concat(reach.StructMembers)
            .Concat(baseInstanceMethods).Concat(_additionalCallableDefinitions())
            .Concat(reach.BodyByDef.Keys)
            .Concat(reach.GenericForeignStaticBodies.Keys)
            .Concat(reach.StructMemberDefs)
            .Select(method => method.OriginalDefinition)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
        var captureRoots = reach.BodyByDef.Keys.Where(m => m.DeclaringSyntaxReferences.Length > 0).ToList();
        // Design 2026-07-10 v3 SS2A: supplementary capture roots (generic foreign statics) join the
        // root set; their bodies ride reach.GenericForeignStaticBodies into the Build call.
        captureRoots.AddRange(reach.GenericForeignStaticBodies.Keys
            .Where(m => m.DeclaringSyntaxReferences.Length > 0 && !reach.BodyByDef.ContainsKey(m)));
        return new ProgramDiscoverySeed(methods, foreignStatics, reach.StructMembers, baseInstanceMethods,
            definitions, reach, captureRoots, _fieldInitOps());
    }
}

/// <summary>Transient builder product. It exists only while closed-world censuses are running.</summary>
sealed class ProgramDiscoverySeed
{
    public readonly IMethodSymbol[] ProgramMethods;
    public readonly IMethodSymbol[] ForeignStatics;
    public readonly IMethodSymbol[] StructMethods;
    public readonly IMethodSymbol[] BaseInstanceMethods;
    public readonly IMethodSymbol[] Definitions;
    public readonly ReachableBodies Reach;
    public readonly IReadOnlyList<IMethodSymbol> CaptureRoots;
    public readonly IReadOnlyList<IOperation> FieldInitOps;

    public ProgramDiscoverySeed(IEnumerable<IMethodSymbol> programMethods,
        IEnumerable<IMethodSymbol> foreignStatics,
        IEnumerable<IMethodSymbol> structMethods,
        IEnumerable<IMethodSymbol> baseInstanceMethods,
        IEnumerable<IMethodSymbol> definitions,
        ReachableBodies reach,
        IEnumerable<IMethodSymbol> captureRoots,
        IEnumerable<IOperation> fieldInitOps)
    {
        ProgramMethods = programMethods.ToArray();
        ForeignStatics = foreignStatics.ToArray();
        StructMethods = structMethods.ToArray();
        BaseInstanceMethods = baseInstanceMethods.ToArray();
        Definitions = definitions.ToArray();
        Reach = reach;
        CaptureRoots = Array.AsReadOnly(captureRoots.ToArray());
        FieldInitOps = Array.AsReadOnly(fieldInitOps.ToArray());
    }
}
