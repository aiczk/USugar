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
}
