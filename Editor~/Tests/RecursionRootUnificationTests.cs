using Xunit;

namespace USugar.Tests;

// CA rewrite (M5b/M5c experiment): the recursion-graph root set (registered functions + the 4 compensation
// concats) must EQUAL the reach-derived root set for the compensation arms to be provably redundant. These
// probes measure it across the shapes each compensation arm targets.
public class RecursionRootUnificationTests
{
    [Theory]
    [InlineData("selfrec", @"using UdonSharp;
public class A : UdonSharpBehaviour { public int r;
  int Fib(int n)=>n<2?n:Fib(n-1)+Fib(n-2);
  void Start(){ r = Fib(6); } }")]
    [InlineData("ownGeneric", @"using UdonSharp;
public class A : UdonSharpBehaviour { public int r;
  T Id<T>(T x)=>x;
  void Start(){ r = Id<int>(5); } }")]
    [InlineData("genericForeignStatic", @"using UdonSharp;
public static class H { public static T Id<T>(T x)=>x; }
public class A : UdonSharpBehaviour { public int r;
  void Start(){ r = H.Id<int>(5); } }")]
    [InlineData("structMember", @"using UdonSharp;
public struct V { public int s; public int Compute()=>s*s; }
public class A : UdonSharpBehaviour { public int r;
  void Start(){ V v = default; v.s = 3; r = v.Compute(); } }")]
    [InlineData("baseInstance", @"using UdonSharp;
public class Bc : UdonSharpBehaviour { public int Helper()=>1; }
public class A : Bc { public int r; void Start(){ r = base.Helper(); } }")]
    [InlineData("recursiveStructOperator", @"using UdonSharp;
public struct N { public int v; public static N operator +(N a, N b)=>new N{v=a.v+b.v}; }
public class A : UdonSharpBehaviour { public int r;
  void Start(){ N a = new N{v=1}; N b = new N{v=2}; r = (a+b).v; } }")]
    // The exact VM-proven shapes each removed compensation arm protected:
    [InlineData("recursiveGeneric", @"using UdonSharp;
public class A : UdonSharpBehaviour { public int r;
  int Fact<T>(int n)=>n<=1?1:n*Fact<T>(n-1);
  void Start(){ r = Fact<int>(5); } }")]
    [InlineData("recursiveGenericLocalFunction", @"using UdonSharp;
public class A : UdonSharpBehaviour { public int r;
  void Start(){ int Lf<T>(int n)=>n<=0?0:Lf<T>(n-1)+n; r = Lf<int>(4); } }")]
    [InlineData("mutualGenericStructRecursion", @"using UdonSharp;
public struct APart<T> { public int Ping(int n)=>n<=0?0:new BPart<T>().Pong(n-1); }
public struct BPart<T> { public int Pong(int n)=>n<=0?0:new APart<T>().Ping(n-1)+n; }
public class A : UdonSharpBehaviour { public int r;
  void Start(){ APart<int> a = default; r = a.Ping(5); } }")]
    [InlineData("selfRecursiveInheritedGeneric", @"using UdonSharp;
public class GBase : UdonSharpBehaviour { public int Sum<T>(int n)=>n<=0?0:Sum<T>(n-1)+n; }
public class A : GBase { public int r; void Start(){ r = Sum<int>(5); } }")]
    public void RecursionRoots_EqualReachDerivedRoots(string _, string src)
    {
        var diffs = TestHelper.RecursionRootDiff(src, "A");
        Assert.True(diffs.Count == 0, "recursion root divergence:\n" + string.Join("\n", diffs));
    }
}
