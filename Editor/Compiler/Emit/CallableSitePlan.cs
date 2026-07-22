using Microsoft.CodeAnalysis;

/// <summary>The normalized call-site facts consumed by emission. Direct calls, delegate dispatch,
/// and cross-program dispatch differ in transport, but recursion protection must use the same
/// vocabulary so syntax-keyed tail/re-entry decisions cannot drift between their emitters.</summary>
public enum CallExecutionKind
{
    Direct,
    DelegateDispatch,
    CrossDispatch,
}

public readonly struct CallableSitePlan
{
    public readonly CallExecutionKind Kind;
    public readonly IMethodSymbol StaticTarget;
    public readonly IMethodSymbol LocalTarget;
    public readonly bool RecursiveEdge;
    public readonly bool Reentrant;
    public readonly bool TailSpared;

    public bool RequiresFrameSpill => (RecursiveEdge && !TailSpared) || Reentrant;

    CallableSitePlan(CallExecutionKind kind, IMethodSymbol staticTarget, IMethodSymbol localTarget,
        bool recursiveEdge, bool reentrant, bool tailSpared)
    {
        Kind = kind;
        StaticTarget = staticTarget;
        LocalTarget = localTarget;
        RecursiveEdge = recursiveEdge;
        Reentrant = reentrant;
        TailSpared = tailSpared;
    }

    public static CallableSitePlan Direct(IMethodSymbol target, SyntaxNode site,
        bool recursiveEdge, RecursionInfo recursion)
    {
        var tailSpared = recursiveEdge && IsTailSpared(site, recursion);
        return new CallableSitePlan(CallExecutionKind.Direct, target, target,
            recursiveEdge, reentrant: false, tailSpared);
    }

    public static CallableSitePlan Delegate(SyntaxNode site, RecursionInfo recursion)
    {
        var reentrant = site != null && recursion?.ReentrantDispatchSites != null
            && recursion.ReentrantDispatchSites.Contains(site);
        return new CallableSitePlan(CallExecutionKind.DelegateDispatch, null, null,
            recursiveEdge: false, reentrant, tailSpared: false);
    }

    public static CallableSitePlan Cross(IMethodSymbol staticTarget, CrossDispatchPlan landing,
        SyntaxNode site, bool recursiveEdge, RecursionInfo recursion)
    {
        var tailSpared = recursiveEdge && IsTailSpared(site, recursion);
        return new CallableSitePlan(CallExecutionKind.CrossDispatch, staticTarget, landing.LocalTarget,
            recursiveEdge, reentrant: recursiveEdge && !tailSpared, tailSpared);
    }

    static bool IsTailSpared(SyntaxNode site, RecursionInfo recursion)
        => site != null && recursion?.TailSparedDirectCallSites != null
           && recursion.TailSparedDirectCallSites.Contains(site);
}
