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

    // ── C4 (M5d): direct-fact CallEdge classifier gates. Formerly old-vs-new differentials against the
    // emitter's classifier copy — that copy was deleted when the core relocated into the resolver, so a
    // diff would compare the classifier against itself. The expected sets below are the EXACT sets both
    // sides yielded at the last green differential run (captured 2026-07-16, pre-relocation), pinned as
    // fixed facts. Each probe builds+emits the emitter first so VirtualDispatch is seeded (the CallEdge
    // arm requires the seeded context and fails loud pre-seed). ──

    [Fact]
    public void VirtualOverrideSet_YieldsStaticAndOverrideTargets()
    {
        // Base-typed variable virtual call → the v2b-2 closed-world override set.
        var src = @"using UdonSharp;
public class Shape { public virtual int Area()=>0; }
public class Sq : Shape { public int side; public override int Area()=>side*side; }
public class A : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Shape s = new Sq(); result = s.Area() + seed; } }";
        var targets = TestHelper.ResolveInvocationCallEdges(src, "A", "Start", "Area");
        Assert.Equal(new[] { "Shape.Area", "Sq.Area" }, SortedNames(targets)); // fixed set (captured)
        Assert.Contains(targets, m => m.Name == "Area" && m.ContainingType.Name == "Sq");   // virtual override arm
        Assert.Contains(targets, m => m.Name == "Area" && m.ContainingType.Name == "Shape"); // static target
    }

    [Fact]
    public void InterfaceCrossDispatch_YieldsLocalImplTarget()
    {
        // `this` as an interface-typed variable → cross-dispatch lands back on this program's impl.
        var src = @"using UdonSharp;
public interface IFoo { int Bar(); }
public class A : UdonSharpBehaviour, IFoo { public int seed; public int result;
  public int Bar()=>seed+1;
  void Start(){ IFoo f = this; result = f.Bar(); } }";
        var targets = TestHelper.ResolveInvocationCallEdges(src, "A", "Start", "Bar");
        Assert.Equal(new[] { "A.Bar", "IFoo.Bar" }, SortedNames(targets)); // fixed set (captured)
        Assert.Contains(targets, m => m.Name == "Bar" && m.ContainingType.Name == "A"); // cross-dispatch impl arm
    }

    [Fact]
    public void PlainInternalCall_NoOverYield()
    {
        // A non-virtual same-behaviour call resolves to exactly one target — no spurious over-yield.
        var src = @"using UdonSharp;
public class A : UdonSharpBehaviour { public int seed; public int result;
  int Helper(int x) => x + 1;
  void Start(){ result = Helper(seed); } }";
        var targets = TestHelper.ResolveInvocationCallEdges(src, "A", "Start", "Helper");
        Assert.Equal(new[] { "A.Helper" }, SortedNames(targets)); // fixed set (captured)
        Assert.Single(targets);
        Assert.Contains(targets, m => m.Name == "Helper");
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

    [Fact]
    public void ClassMint_YieldsInstantiationReach()
    {
        // `new Dc()` reaches: Dc's field-initializer foreign static, Bc's explicit base ctor (implicit
        // chain), and Dc's virtual override — bodies that live off the walked op tree.
        var src = @"using UdonSharp;
public static class Sh { public static int G()=>7; }
public class Bc { public Bc(){} public virtual int Val()=>1; }
public class Dc : Bc { public int f = Sh.G(); public override int Val()=>f; }
public class A : UdonSharpBehaviour { public int result;
  void Start(){ Dc d = new Dc(); result = d.Val(); } }";
        var targets = TestHelper.ResolveEdgesForFirstOp(src, "A", o => o is IObjectCreationOperation);
        Assert.Contains(targets, t => t.Method.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor
            && t.Method.ContainingType.Name == "Bc" && t.Role == TargetRole.ReachStructMember);   // base ctor
        Assert.Contains(targets, t => t.Method.Name == "Val" && t.Method.ContainingType.Name == "Dc"
            && t.Role == TargetRole.ReachStructMember);                                            // virtual impl
        Assert.Contains(targets, t => t.Method.Name == "G" && t.Method.ContainingType.Name == "Sh"
            && t.Role == TargetRole.ReachForeignStatic);                                           // field-init reach
    }

    [Fact]
    public void ResolveMintedTypes_YieldsDirectAndFieldInitNested()
    {
        // `new Dc()` mints Dc directly, and E transitively through Dc's field initializer (off-body).
        var src = @"using UdonSharp;
public class E { public int v; }
public class Dc { public E e = new E(); public int tag; }
public class A : UdonSharpBehaviour { void Start(){ Dc d = new Dc(); } }";
        var minted = TestHelper.ResolveMintedTypesForFirstNew(src, "A");
        Assert.Contains(minted, t => t.Name == "Dc");
        Assert.Contains(minted, t => t.Name == "E");   // transitive via field initializer
    }

    static string[] SortedNames(System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.IMethodSymbol> ms)
        => ms.Select(m => m.ContainingType.Name + "." + m.Name)
            .OrderBy(n => n, System.StringComparer.Ordinal).ToArray();
}
