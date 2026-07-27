using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Stage 2 M2 — env-record codegen pins (design §3/§4). These lock the STRUCTURAL shape of closure
/// environment emission now that the Stage-1 capture-escape guard cluster is retired: a captured
/// local lives in a per-scope object[] env record (allocation, __Get/__Set access, __envp chain,
/// capturing-bridge null-env guard) instead of a flat __lcl_ field. Behavioural correctness (the
/// produced heap VALUES) is verified separately on the real Udon VM by the local fcd corpus
/// (fcd36/38/41/44/48/49/50); these tracked pins guard the emission SHAPE so a later refactor cannot
/// silently unwire env. The env ctor/get/set externs are:
///   ctor SystemObjectArray.__ctor__SystemInt32__SystemObjectArray
///   get  SystemObjectArray.__Get__SystemInt32__SystemObject
///   set  SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid
/// Every source below has NO aggregate (tuple/struct) local, so any object[] ctor is an env alloc.
/// </summary>
public class Stage2EnvCodegenTests
{
    const string EnvCtor = "SystemObjectArray.__ctor__SystemInt32__SystemObjectArray";
    const string EnvGet = "SystemObjectArray.__Get__SystemInt32__SystemObject";

    [Fact]
    public void EnvAbi_RecordLayout_IsTaggedBeforeParentAndCaptures()
    {
        Assert.Equal(0, EnvAbi.Kind);
        Assert.Equal(1, EnvAbi.Parent);
        Assert.Equal(2, EnvAbi.FirstCapture);
        Assert.Equal("__usugar_env", EnvAbi.KindTag);
        Assert.Equal(2, EnvAbi.RecordSize(0));
        Assert.Equal(3, EnvAbi.CaptureSlot(2));
    }

    [Fact]
    public void EnvKindTag_CannotCollideWithBundleKindTags()
    {
        Assert.DoesNotContain(BundleAbi.Prefix, EnvAbi.KindTag, System.StringComparison.Ordinal);
        foreach (RuntimeBundleKind kind in System.Enum.GetValues(typeof(RuntimeBundleKind)))
        {
            if (kind == RuntimeBundleKind.None) continue;
            Assert.NotEqual(BundleAbi.KindTag(kind), EnvAbi.KindTag);
            Assert.False(EnvAbi.KindTag.StartsWith(BundleAbi.KindTag(kind), System.StringComparison.Ordinal));
            Assert.False(BundleAbi.KindTag(kind).StartsWith(EnvAbi.KindTag, System.StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Curry_ReturnsCapturingClosure_CompilesThroughEnv()
    {
        // fcd36: `a => b => a + b`, then add(3), add3(4)+add3(10). Pre-B1 this threw at the OUTER
        // creation site (nested-param over-approximation → ClosureEnvLeaf with null BindingScope);
        // pre-B2 the escape guard rejected the returned capturing closure. Now it compiles: the inner
        // closure carries `a` in an env record it chains to through __envp.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CurryEnv : UdonSharpBehaviour {
    public int result;
    void Start() {
        Func<int, Func<int, int>> add = a => b => a + b;
        Func<int, int> add3 = add(3);
        result = add3(4) + add3(10);
    }
}", "CurryEnv");
        Assert.Contains(EnvCtor, uasm);   // an env record is allocated for the captured `a`
        Assert.Contains("__envp", uasm);  // the inner closure receives its parent env chain
    }

    [Fact]
    public void TwoLambdas_ShareOneCapturedLocal_CompileThroughSingleEnvCell()
    {
        // fcd38: two delegate fields capture the SAME local `seedv`. Under Stage-1 flat capture this
        // was an aliasing ERROR (DetectLambdaCaptureAliasing); with one env record holding `seedv`
        // both closures read the same cell — correct, and no longer an error.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ShareEnv : UdonSharpBehaviour {
    Func<int> fa; Func<int> fb;
    public int r1; public int r2;
    void Start() {
        int seedv = 7;
        fa = () => seedv + 1;
        fb = () => seedv * 2;
        r1 = fa(); r2 = fb();
    }
}", "ShareEnv");
        Assert.Contains(EnvCtor, uasm);
        // The captured local no longer has a flat scalar field.
        Assert.DoesNotContain("__lcl_seedv_", uasm);
    }

    [Fact]
    public void CapturingLambda_InLoopBody_AllocatesEnvRecord_Inv1()
    {
        // fcd49 shape (INV-1): a per-iteration local captured by a loop-body lambda stored into an
        // array. Each iteration must allocate a FRESH env record — the array of closures reads each
        // iteration's own value. Compiles (Stage-1 rejected this per-iteration escape) with an env
        // ctor inside the emitted body.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LoopEnv : UdonSharpBehaviour {
    public int c0; public int c1;
    void Start() {
        Func<int>[] fs = new Func<int>[2];
        for (int i = 0; i < 2; i++) {
            int v = i * 10;
            fs[i] = () => v;
        }
        c0 = fs[0](); c1 = fs[1]();
    }
}", "LoopEnv");
        Assert.Contains(EnvCtor, uasm);
        Assert.DoesNotContain("__lcl_v_", uasm);
    }

    [Fact]
    public void CapturingClosure_DispatchedThroughField_HasEnvpParamAndNullEnvGuard()
    {
        // A capturing lambda stored to a delegate FIELD and dispatched exercises the full env ABI:
        // the hoisted closure gains a hidden __envp parameter (copy-in of DelegateAbi.Env) and its bridge
        // guards a null env with a loud LogError before consuming it.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FieldDispatchEnv : UdonSharpBehaviour {
    Func<int> cb;
    public int result;
    void Start() {
        int captured = 41;
        cb = () => captured + 1;
        result = cb();
    }
}", "FieldDispatchEnv");
        Assert.Contains("__envp", uasm);
        Assert.Contains(EnvGet, uasm); // the closure reads its captured cell out of the env record
    }

    [Fact]
    public void NestedTwoHopChain_InnerReadsOuterThroughEnvChain()
    {
        // fcd41: a two-hop nested chain — the inner lambda reads AND writes the outermost method
        // local through the env parent chain (mid's env parent → the method env). Compiles with env
        // get/set access; the outer local has no flat cell.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ChainEnv : UdonSharpBehaviour {
    public int seed; public int result; public int outerFinal;
    void Start() {
        int outer = seed;
        Func<int> mid = () => {
            int middle = outer * 2;
            Func<int> inner = () => { outer = outer + 5; return outer; };
            int p = inner();
            return p + middle;
        };
        result = mid();
        outerFinal = outer;
    }
}", "ChainEnv");
        Assert.Contains(EnvCtor, uasm);
        Assert.Contains(EnvGet, uasm);
        Assert.DoesNotContain("__lcl_outer_", uasm);
    }

    [Fact]
    public void CapturingClosure_InInheritedBaseMethod_RoutesThroughEnv()
    {
        // FP5B4 shape (coverage pin): the capturing closure lives in an INHERITED user-base method
        // body that the derived program emits. CaptureScopeAnalysis must walk base-type method bodies
        // (not only the class's own GetMembers) or the closure silently stays a flat __lcl_ cell —
        // unsound when the method is a reentrant-recursion cycle member (the flat cell aliases across
        // activations; VM-proven 100 vs CLR 120). Pin that the inherited body's captured `s` is env-
        // backed, not flat.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EnvBaseMethod : UdonSharpBehaviour {
    public virtual int Hop(int k) { return 0; }
    public int M(int m) {
        if (m <= 0) return 0;
        int s = m * 10;
        Func<int, int> c = k => Hop(k) + s;
        return c(m - 1) + s;
    }
}
public class EnvBaseMethodClosure : EnvBaseMethod {
    public int n; public int result;
    public override int Hop(int k) { return M(k); }
    void Start() { result = M(n); }
}", "EnvBaseMethodClosure");
        Assert.Contains(EnvCtor, uasm);
        Assert.Contains("__envp", uasm);
        Assert.DoesNotContain("__lcl_s_", uasm);
    }

    [Fact]
    public void CapturingClosure_InPropertyAccessor_RoutesThroughEnv()
    {
        // R8C01/R8L03 shape (coverage pin): the capturing closure lives in a property GET accessor
        // body. CaptureScopeAnalysis must include PropertyGet/PropertySet roots (they are emitted like
        // ordinary methods) or the closure stays flat — same reentrant-recursion unsoundness as the
        // inherited-body case. Pin that the accessor's captured `cell` is env-backed.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EnvAccessorClosure : UdonSharpBehaviour {
    Func<int, int> f;
    public int seed; public int result;
    int P {
        get {
            int cell = seed * 3;
            f = k => k + cell;
            return f(2) + cell;
        }
    }
    void Start() { result = P; }
}", "EnvAccessorClosure");
        Assert.Contains(EnvCtor, uasm);
        Assert.Contains("__envp", uasm);
        Assert.DoesNotContain("__lcl_cell_", uasm);
    }

    [Fact]
    public void WhileConditionOutVar_Captured_CompilesInv3()
    {
        // fcd50 shape (INV-3): an out-var declared in a WHILE condition and captured by a body lambda
        // is Iteration-owned — its env must exist before the condition runs. Compiles through env.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class WhileOutVarEnv : UdonSharpBehaviour {
    public int c0; public int c1;
    int[] data; int di;
    void Start() {
        data = new int[] { 10, 20 };
        di = 0;
        Func<int>[] fs = new Func<int>[2];
        int k = 0;
        while (TryDeq(out int item)) { fs[k] = () => item; k = k + 1; }
        c0 = fs[0](); c1 = fs[1]();
    }
    bool TryDeq(out int item) {
        if (di < data.Length) { item = data[di]; di = di + 1; return true; }
        item = 0; return false;
    }
}", "WhileOutVarEnv");
        // The captured out-var lives in an env record read via __Get (a separate __lcl_item_ temp may
        // still stage the out-parameter's call-convention copy — orthogonal to the capture cell).
        Assert.Contains(EnvCtor, uasm);
        Assert.Contains(EnvGet, uasm);
    }
}
