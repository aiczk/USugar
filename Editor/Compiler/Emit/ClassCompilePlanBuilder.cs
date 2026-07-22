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
    readonly Func<IEnumerable<IMethodSymbol>> _additionalCallableDefinitions;

    public ClassCompilePlanBuilder(
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

    public ClassCompilePlan Build()
    {
        var methods = _computeMethods();
        var reach = _buildReachableBodies(methods);
        var methodSet = new HashSet<IMethodSymbol>(methods, SymbolEqualityComparer.Default);
        var baseInstanceMethods = reach.BaseCopies.Where(bm => !methodSet.Contains(bm)).ToArray();
        // Local functions register at their declaration/forward-reference site. Keeping them in the
        // eager foreign-static projection used to create a dead duplicate CFunction.
        var foreignStatics = reach.ForeignStatics
            .Where(fm => fm.MethodKind != MethodKind.LocalFunction).ToArray();
        var definitions = methods.Concat(foreignStatics).Concat(reach.StructMembers)
            .Concat(baseInstanceMethods).Concat(_additionalCallableDefinitions())
            .Concat(reach.BodyByDef.Keys)
            .Concat(reach.GenericForeignStaticBodies.Keys)
            .Concat(reach.StructMemberDefs)
            .Select(method => method.OriginalDefinition)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
        var callables = new CallableDefinitionPlan(
            methods, foreignStatics, reach.StructMembers, baseInstanceMethods, definitions);
        var captureRoots = reach.BodyByDef.Keys.Where(m => m.DeclaringSyntaxReferences.Length > 0).ToList();
        // Design 2026-07-10 v3 SS2A: supplementary capture roots (generic foreign statics) join the
        // root set; their bodies ride reach.GenericForeignStaticBodies into the Build call.
        captureRoots.AddRange(reach.GenericForeignStaticBodies.Keys
            .Where(m => m.DeclaringSyntaxReferences.Length > 0 && !reach.BodyByDef.ContainsKey(m)));
        return new ClassCompilePlan(
            callables,
            reach,
            captureRoots,
            _fieldInitOps().ToList());
    }
}
