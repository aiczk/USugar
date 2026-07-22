using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Per-class plan built after field registration and before body emission.
/// It carries the method universe and the single reachable-body fixpoint shared by registration,
/// recursion analysis, and closure analysis.
/// </summary>
sealed class ClassCompilePlan
{
    public readonly CallableDefinitionPlan Callables;
    public readonly ReachableBodies Reach;
    public readonly List<IMethodSymbol> CaptureRoots;
    public readonly List<IOperation> FieldInitOps;

    public ClassCompilePlan(
        CallableDefinitionPlan callables,
        ReachableBodies reach,
        List<IMethodSymbol> captureRoots,
        List<IOperation> fieldInitOps)
    {
        Callables = callables;
        Reach = reach;
        CaptureRoots = captureRoots;
        FieldInitOps = fieldInitOps;
    }
}
