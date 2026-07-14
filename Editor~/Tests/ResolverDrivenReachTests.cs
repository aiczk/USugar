using Xunit;

namespace USugar.Tests;

// CA rewrite (M5a): the resolver-driven reach worklist must reproduce BuildReachableBodies' facets at
// OriginalDefinition granularity. These probes measure the gap empirically before any cutover.
public class ResolverDrivenReachTests
{
    [Fact]
    public void NonGenericProgram_MatchesLegacyReach()
    {
        // Exercises every reach role: foreign-static (H.M), struct-member (V.Compute), base-instance
        // (base.Helper), and mint (new Dc → new E via field init). Non-generic ⇒ def == constructed.
        var src = @"using UdonSharp;
public static class H { public static int M(int x)=>x+1; }
public struct V { public int s; public int Compute()=>s*s; }
public class Bc : UdonSharpBehaviour { public int Helper()=>1; }
public class E { public int v; }
public class Dc { public E e = new E(); public int tag; }
public class A : Bc { public int seed; public int result; public V vv;
  void Start(){ result = H.M(seed) + vv.Compute() + base.Helper(); Dc d = new Dc(); } }";
        var diffs = TestHelper.ReachFacetDiff(src, "A");
        Assert.True(diffs.Count == 0, "reach facet divergence:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void GenericStructTwoSpecs_MatchesLegacyAtDefGranularity()
    {
        // Two specs of Box<T> (int, float): the legacy StructMembers carries both CONSTRUCTED specs, the
        // worklist carries the definition — equal once both project to OriginalDefinition (per-spec is the
        // trailing M6 layer, not a reach-set difference).
        var src = @"using UdonSharp;
public struct Box<T> { public T v; public T Get()=>v; }
public class A : UdonSharpBehaviour { public int ri; public float rf;
  void Start(){ Box<int> bi = default; ri = bi.Get(); Box<float> bf = default; rf = bf.Get(); } }";
        var diffs = TestHelper.ReachFacetDiff(src, "A");
        Assert.True(diffs.Count == 0, "reach facet divergence:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void GenericForeignStatic_MatchesLegacyReach()
    {
        // A generic foreign static (Helper.Id<int>) lands in the legacy SS2A supplementary set
        // (GenericForeignStaticBodies). The worklist's supplementary fixpoint now reproduces it — 0 divergence.
        var src = @"using UdonSharp;
public static class Helper { public static T Id<T>(T x)=>x; }
public class A : UdonSharpBehaviour { public int r;
  void Start(){ r = Helper.Id<int>(5); } }";
        var diffs = TestHelper.ReachFacetDiff(src, "A");
        Assert.True(diffs.Count == 0, "reach facet divergence:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void GenericForeignStaticReachingStructMember_MatchesLegacyReach()
    {
        // The SS2A body itself reaches a struct member — exercises the supp→main alternation (a struct member
        // reachable ONLY through a generic-foreign-static body must still register).
        var src = @"using UdonSharp;
public struct V { public int s; public int Compute()=>s*s; }
public static class Helper { public static int Run<T>(T x){ V v = default; return v.Compute(); } }
public class A : UdonSharpBehaviour { public int r;
  void Start(){ r = Helper.Run<int>(5); } }";
        var diffs = TestHelper.ReachFacetDiff(src, "A");
        Assert.True(diffs.Count == 0, "reach facet divergence:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void MixedFeatures_MatchesLegacyReach()
    {
        // class mint + generic struct + plain-class virtual dispatch (base-typed var) + self-recursion + array.
        var src = @"using UdonSharp;
public struct Box<T> { public T v; public T Get()=>v; }
public class VBase { public virtual int Kind()=>0; }
public class VDer : VBase { public override int Kind()=>1; }
public class A : UdonSharpBehaviour { public int seed; public int result;
  int Recur(int n)=>n<=0?0:Recur(n-1)+n;
  void Start(){
    Box<int> b = default;
    VBase v = new VDer();
    int[] arr = new int[2];
    result = b.Get() + Recur(3) + v.Kind() + arr[0] + seed;
  } }";
        var diffs = TestHelper.ReachFacetDiff(src, "A");
        Assert.True(diffs.Count == 0, "reach facet divergence:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void ClosureCapturingForeignStatic_MatchesLegacyReach()
    {
        // a lambda body reaches a foreign static — the capture-root / closure reach path.
        var src = @"using UdonSharp;
public static class Util { public static int Twice(int x)=>x+x; }
public class A : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ System.Func<int,int> f = x => Util.Twice(x) + seed; result = f(2); } }";
        var diffs = TestHelper.ReachFacetDiff(src, "A");
        Assert.True(diffs.Count == 0, "reach facet divergence:\n" + string.Join("\n", diffs));
    }
}
