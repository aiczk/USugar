using Xunit;

namespace USugar.Tests;

/// <summary>
/// Round-7 follow-up: ref/out/in PARAMETER semantics holes around the caller-side copy-back
/// convention (all VM-proven at caceb3d, see Library/fcd_stage1_handoff.md round-7 follow-up).
/// [Q2] Recursive frames share ONE heap var per parameter and ref/out params are deliberately not
///      spilled (self-threading must keep its mutations across the call — diff-fuzz wave-3 #3).
///      A recursion-cycle call passing a DIFFERENT lvalue clobbers the outer frame at copy-in
///      (VM 200 vs CLR 101) — loud reject; self-threading stays legal.
/// </summary>
public class RefParamSemanticsTests
{
    // ── [Q2] recursive-edge ref/out args ──

    [Fact]
    public void RecursiveRefArg_DifferentLvalue_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class RecRef1 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int a = 1; Rec(ref a, 1); sum = a; }
    public void Rec(ref int x, int d) {
        if (d == 0) { x = 100; return; }
        int local = 5; Rec(ref local, d - 1); x = x + local;
    }
}", "RecRef1"));
        Assert.Contains("recursive call", ex.Message);
    }

    [Fact]
    public void RecursiveRefArg_DifferentLvalue_Struct_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public struct RecS { public int v; }
public class RecRef2 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { RecS a = new RecS(); a.v = 1; Rec(ref a, 1); sum = a.v; }
    public void Rec(ref RecS x, int d) {
        if (d == 0) { x.v = 100; return; }
        RecS t = new RecS(); t.v = 5; Rec(ref t, d - 1); x.v = x.v + t.v;
    }
}", "RecRef2"));
        Assert.Contains("recursive call", ex.Message);
    }

    [Fact]
    public void RecursiveRefArg_SelfThreaded_StillCompiles()
    {
        // The pinned wave-3 convention: threading the method's OWN ref param stays legal
        // (RecRefRegressionTests value-pins 10 on the real VM; struct_ref_param sentinel tier).
        var uasm = TestHelper.CompileToUasm(@"
public class RecRef3 : UdonSharp.UdonSharpBehaviour {
    public int res;
    void Add(int n, ref int acc) { if (n <= 0) return; Add(n - 1, ref acc); acc += n; }
    void Start() { int a = 0; Add(5, ref a); res = a; }
}", "RecRef3");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void NonRecursiveRefArg_DifferentLvalues_StillCompile()
    {
        // Non-cycle calls keep the full copy-back convention for any lvalue shape.
        var uasm = TestHelper.CompileToUasm(@"
public class RecRef4 : UdonSharp.UdonSharpBehaviour {
    public int res;
    void Twice(ref int v) { v *= 2; }
    void Start() { int x = 5; Twice(ref x); int[] a = new int[1]; Twice(ref a[0]); res = x + a[0]; }
}", "RecRef4");
        Assert.NotNull(uasm);
    }
}
