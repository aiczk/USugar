using System.Linq;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class ResolvedEdgeResolverTests
{
    [Fact]
    public void DirectCall_YieldsOneCallEdge()
    {
        var src = @"using UdonSharp;
public class A : UdonSharpBehaviour { public int seed; public int result;
  int Helper(int x) => x + 1;
  void Start(){ result = Helper(seed); } }";
        var targets = TestHelper.ResolveEdgesForFirstCall(src, "A", callInMethod: "Start", calleeName: "Helper");
        Assert.Contains(targets, t => t.Method.Name == "Helper" && t.Role == TargetRole.CallEdge);
    }

    // ── Task 2: invocation-arm differential gate. Each probe builds+emits the emitter (so VirtualDispatch
    // is seeded), then asserts the resolver's CallEdge set equals the live EnumerateInternalCallTargets,
    // AND that the probe's arm actually fired (non-vacuous). ──

    [Fact]
    public void VirtualOverrideSet_MatchesOldClassifier()
    {
        // Base-typed variable virtual call → the v2b-2 closed-world override set.
        var src = @"using UdonSharp;
public class Shape { public virtual int Area()=>0; }
public class Sq : Shape { public int side; public override int Area()=>side*side; }
public class A : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Shape s = new Sq(); result = s.Area() + seed; } }";
        var (old, @new) = TestHelper.CompareInvocationCallEdges(src, "A", "Start", "Area");
        Assert.True(old.SetEquals(@new), $"old={Names(old)} new={Names(@new)}");
        Assert.Contains(@new, m => m.Name == "Area" && m.ContainingType.Name == "Sq");   // virtual override arm
        Assert.Contains(@new, m => m.Name == "Area" && m.ContainingType.Name == "Shape"); // static target
    }

    [Fact]
    public void InterfaceCrossDispatch_MatchesOldClassifier()
    {
        // `this` as an interface-typed variable → cross-dispatch lands back on this program's impl.
        var src = @"using UdonSharp;
public interface IFoo { int Bar(); }
public class A : UdonSharpBehaviour, IFoo { public int seed; public int result;
  public int Bar()=>seed+1;
  void Start(){ IFoo f = this; result = f.Bar(); } }";
        var (old, @new) = TestHelper.CompareInvocationCallEdges(src, "A", "Start", "Bar");
        Assert.True(old.SetEquals(@new), $"old={Names(old)} new={Names(@new)}");
        Assert.Contains(@new, m => m.Name == "Bar" && m.ContainingType.Name == "A"); // cross-dispatch impl arm
    }

    [Fact]
    public void PlainInternalCall_NoOverYield()
    {
        // A non-virtual same-behaviour call resolves to exactly one target — no spurious over-yield.
        var src = @"using UdonSharp;
public class A : UdonSharpBehaviour { public int seed; public int result;
  int Helper(int x) => x + 1;
  void Start(){ result = Helper(seed); } }";
        var (old, @new) = TestHelper.CompareInvocationCallEdges(src, "A", "Start", "Helper");
        Assert.True(old.SetEquals(@new), $"old={Names(old)} new={Names(@new)}");
        Assert.Single(@new);
        Assert.Contains(@new, m => m.Name == "Helper");
    }

    // ── reach-arm shape gates: each probe resolves the shape's op and asserts the target lands under the
    // expected role. (The reach arms are per-op cores whose predicates delegate to the emitter.) ──

    [Fact]
    public void StructMethodCall_YieldsStructMemberReach()
    {
        var src = @"using UdonSharp;
public struct V { public int side; public int Compute()=>side*side; }
public class A : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ V v = default; v.side = seed; result = v.Compute(); } }";
        var targets = TestHelper.ResolveEdgesForFirstOp(src, "A",
            o => o is IInvocationOperation inv && inv.TargetMethod.Name == "Compute");
        Assert.Contains(targets, t => t.Method.Name == "Compute"
            && t.Method.ContainingType.Name == "V" && t.Role == TargetRole.ReachStructMember);
    }

    [Fact]
    public void ForeignStaticCall_YieldsForeignStaticReach()
    {
        var src = @"using UdonSharp;
public static class H { public static int M(int x)=>x+1; }
public class A : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ result = H.M(seed); } }";
        var targets = TestHelper.ResolveEdgesForFirstOp(src, "A",
            o => o is IInvocationOperation inv && inv.TargetMethod.Name == "M");
        Assert.Contains(targets, t => t.Method.Name == "M"
            && t.Method.ContainingType.Name == "H" && t.Role == TargetRole.ReachForeignStatic);
    }

    [Fact]
    public void BaseInstanceCall_YieldsBaseInstanceReach()
    {
        var src = @"using UdonSharp;
public class BaseB : UdonSharpBehaviour { public int Helper()=>1; }
public class A : BaseB { public int result;
  void Start(){ result = base.Helper(); } }";
        var targets = TestHelper.ResolveEdgesForFirstOp(src, "A",
            o => o is IInvocationOperation inv && inv.TargetMethod.Name == "Helper");
        Assert.Contains(targets, t => t.Method.Name == "Helper"
            && t.Method.ContainingType.Name == "BaseB" && t.Role == TargetRole.ReachBaseInstance);
    }

    static string Names(System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.IMethodSymbol> ms)
        => "{" + string.Join(", ", ms.Select(m => m.ContainingType.Name + "." + m.Name)) + "}";
}
