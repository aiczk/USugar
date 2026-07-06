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
    public readonly IMethodSymbol[] ForeignStatics;
    public readonly IMethodSymbol[] StructMethods;
    public readonly IMethodSymbol[] BaseInstanceMethods;
    public readonly List<IMethodSymbol> CaptureRoots;
    public readonly List<IOperation> FieldInitOps;

    public ClassCompilePlan(
        IMethodSymbol[] methods,
        ReachableBodies reach,
        IMethodSymbol[] foreignStatics,
        IMethodSymbol[] structMethods,
        IMethodSymbol[] baseInstanceMethods,
        List<IMethodSymbol> captureRoots,
        List<IOperation> fieldInitOps)
    {
        Methods = methods;
        Reach = reach;
        ForeignStatics = foreignStatics;
        StructMethods = structMethods;
        BaseInstanceMethods = baseInstanceMethods;
        CaptureRoots = captureRoots;
        FieldInitOps = fieldInitOps;
    }
}
