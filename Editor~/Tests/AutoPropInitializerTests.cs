using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Round-8 [R2]: auto-property initializers (IPropertyInitializerOperation) were handled nowhere —
/// `public int P{get;set;}=5;` compiled clean and the backing var stayed 0 (DiffFuzz native-CLR
/// oracle ref=5; overridden-base flavor base.P*10+P ref=50 — C# runs the base declaration's
/// initializer into the base backing __basebk_*). These tests pin the structural side: constants
/// land as heap defaults on the right backing var (leaf = bare name, overridden base declaration =
/// __basebk), and non-const initializers run at _start (synthesized when no user Start exists).
/// Value truth is pinned on the real VM in the local harness (FcdLaunderProbes9).
/// </summary>
public class AutoPropInitializerTests
{
    [Fact]
    public void AutoPropInitializer_Const_BecomesHeapDefault()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
public class ApiConst : UdonSharp.UdonSharpBehaviour {
    public int sum;
    public int P { get; set; } = 5;
    void Start() { sum = P; }
}", "ApiConst");
        Assert.Contains(consts, c => c.Id == "P" && c.Value is int v && v == 5);
    }

    [Fact]
    public void AutoPropInitializer_OverriddenBaseDecl_TargetsBaseBacking()
    {
        // The base declaration's initializer runs into ITS backing (__basebk_*); the leaf
        // declaration has no initializer, so the bare name must NOT receive the 5.
        var (_, consts) = TestHelper.CompileWithConsts(@"
public class ApiBase : UdonSharp.UdonSharpBehaviour {
    public virtual int P { get; set; } = 5;
}
public class ApiDerived : ApiBase {
    public int sum;
    public override int P { get; set; }
    void Start() { sum = base.P * 10 + P; }
}", "ApiDerived");
        Assert.Contains(consts, c => c.Id.StartsWith("__basebk_") && c.Id.EndsWith("_P")
                                      && c.Value is int v && v == 5);
        Assert.DoesNotContain(consts, c => c.Id == "P");
    }

    [Fact]
    public void AutoPropInitializer_InheritedNoOverride_LandsOnBareName()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
public class ApiInhBase : UdonSharp.UdonSharpBehaviour {
    public int Q { get; set; } = 7;
}
public class ApiInh : ApiInhBase {
    public int sum;
    void Start() { sum = Q; }
}", "ApiInh");
        Assert.Contains(consts, c => c.Id == "Q" && c.Value is int v && v == 7);
    }

    [Fact]
    public void AutoPropInitializer_SimpleArray_BecomesHeapDefault()
    {
        // `new int[3]` is heap-evaluable (TryEvaluateFieldInitForHeap) — same path as fields.
        var (_, consts) = TestHelper.CompileWithConsts(@"
public class ApiArr : UdonSharp.UdonSharpBehaviour {
    public int[] A { get; set; } = new int[3];
}", "ApiArr");
        Assert.Contains(consts, c => c.Id == "A" && c.Value is int[] arr && arr.Length == 3);
    }

    [Fact]
    public void AutoPropInitializer_NonConst_RunsAtSynthesizedStart()
    {
        // No user Start and a non-heap-evaluable initializer (jagged array): the runtime
        // initializer must synthesize _start and store into J.
        var uasm = TestHelper.CompileToUasm(@"
public class ApiJag : UdonSharp.UdonSharpBehaviour {
    public int[][] J { get; set; } = new int[2][];
}", "ApiJag");
        Assert.Contains(".export _start", uasm);
        Assert.Contains("PUSH, J", uasm);
    }
}
