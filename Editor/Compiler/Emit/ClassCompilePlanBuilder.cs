using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Builds the per-class compile plan from the already-registered field initializer state and
/// UasmEmitter's reachability collectors.
/// </summary>
sealed class ClassCompilePlanBuilder
{
    readonly Func<IMethodSymbol[]> _computeMethods;
    readonly Func<IMethodSymbol[], ReachableBodies> _buildReachableBodies;
    readonly Func<IEnumerable<IOperation>> _fieldInitOps;

    public ClassCompilePlanBuilder(
        Func<IMethodSymbol[]> computeMethods,
        Func<IMethodSymbol[], ReachableBodies> buildReachableBodies,
        Func<IEnumerable<IOperation>> fieldInitOps)
    {
        _computeMethods = computeMethods;
        _buildReachableBodies = buildReachableBodies;
        _fieldInitOps = fieldInitOps;
    }

    public ClassCompilePlan Build()
    {
        var methods = _computeMethods();
        var reach = _buildReachableBodies(methods);
        var methodSet = new HashSet<IMethodSymbol>(methods, SymbolEqualityComparer.Default);
        var baseInstanceMethods = reach.BaseCopies.Where(bm => !methodSet.Contains(bm)).ToArray();
        var captureRoots = reach.BodyByDef.Keys.Where(m => m.DeclaringSyntaxReferences.Length > 0).ToList();
        // Design 2026-07-10 v3 SS2A: supplementary capture roots (generic foreign statics) join the
        // root set; their bodies ride reach.GenericForeignStaticBodies into the Build call.
        captureRoots.AddRange(reach.GenericForeignStaticBodies.Keys
            .Where(m => m.DeclaringSyntaxReferences.Length > 0 && !reach.BodyByDef.ContainsKey(m)));
        return new ClassCompilePlan(
            methods,
            reach,
            reach.ForeignStatics,
            reach.StructMembers,
            baseInstanceMethods,
            captureRoots,
            _fieldInitOps().ToList());
    }
}
