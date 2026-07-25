using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class CallableSitePlanTests
{
    [Fact]
    public void DirectRecursiveSite_UsesSharedTailDecision()
    {
        var site = Site();
        var plan = CallableSitePlan.Direct(null, site, recursiveEdge: true,
            Recursion(tailSpared: site));

        Assert.Equal(CallExecutionKind.Direct, plan.Kind);
        Assert.True(plan.RecursiveEdge);
        Assert.True(plan.TailSpared);
        Assert.False(plan.RequiresFrameSpill);
    }

    [Fact]
    public void DelegateSite_UsesSharedReentryDecision()
    {
        var site = Site();
        var plan = CallableSitePlan.Delegate(site, Recursion(reentrant: site));

        Assert.Equal(CallExecutionKind.DelegateDispatch, plan.Kind);
        Assert.True(plan.Reentrant);
        Assert.True(plan.RequiresFrameSpill);
    }

    [Fact]
    public void CrossRecursiveTailSite_IsNotMarkedReentrant()
    {
        var site = Site();
        var plan = CallableSitePlan.Cross(null, default, site, recursiveEdge: true,
            Recursion(tailSpared: site));

        Assert.Equal(CallExecutionKind.CrossDispatch, plan.Kind);
        Assert.True(plan.TailSpared);
        Assert.False(plan.Reentrant);
        Assert.False(plan.RequiresFrameSpill);
    }

    [Fact]
    public void CrossRecursiveNonTailSite_IsMarkedReentrant()
    {
        var plan = CallableSitePlan.Cross(null, default, Site(), recursiveEdge: true, Recursion());

        Assert.True(plan.Reentrant);
        Assert.True(plan.RequiresFrameSpill);
    }

    static SyntaxNode Site() => CSharpSyntaxTree.ParseText("M();").GetRoot().DescendantNodes().First();

    static RecursionInfo Recursion(SyntaxNode reentrant = null, SyntaxNode tailSpared = null)
    {
        return new RecursionInfo(
            new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default),
            new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default),
            new Dictionary<IMethodSymbol, HashSet<IFieldSymbol>>(SymbolEqualityComparer.Default),
            reentrant == null ? new HashSet<SyntaxNode>() : new HashSet<SyntaxNode> { reentrant },
            tailSpared == null ? new HashSet<SyntaxNode>() : new HashSet<SyntaxNode> { tailSpared },
            new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default));
    }
}
