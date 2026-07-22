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
    public readonly IMethodSymbol[] Methods;
    public readonly ReachableBodies Reach;
    public readonly CallableRegistrationPlan Registration;
    public readonly List<IMethodSymbol> CaptureRoots;
    public readonly List<IOperation> FieldInitOps;

    public ClassCompilePlan(
        IMethodSymbol[] methods,
        ReachableBodies reach,
        CallableRegistrationPlan registration,
        List<IMethodSymbol> captureRoots,
        List<IOperation> fieldInitOps)
    {
        Methods = methods;
        Reach = reach;
        Registration = registration;
        CaptureRoots = captureRoots;
        FieldInitOps = fieldInitOps;
    }
}
