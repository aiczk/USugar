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

    // ── [Q3] `in` params: declaration-side loud reject ──
    // The flat-heap call ABI copies parameters by value. Accepting `in` would lose both the readonly
    // alias (VM 1 vs CLR 5) and the defensive copy (VM 11 vs CLR 1).

    [Fact]
    public void InParam_OnClassMethod_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class InPar1 : UdonSharp.UdonSharpBehaviour {
    public int f; public int sum;
    void Start() { f = 1; M(in f); }
    public void M(in int x) { f = 5; sum = x; }
}", "InPar1"));
        Assert.Contains("'in' parameter", ex.Message);
    }

    [Fact]
    public void InParam_OnStructMethod_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public struct InS { public int v; public void M(in int x) { v = x; } }
public class InPar2 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { InS s = new InS(); s.M(in sum); sum = s.v; }
}", "InPar2"));
        Assert.Contains("'in' parameter", ex.Message);
    }

    [Fact]
    public void InParam_OnLocalFunction_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class InPar3 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int L(in int x) { return x + 1; } sum = L(in sum); }
}", "InPar3"));
        Assert.Contains("'in' parameter", ex.Message);
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

    // ── [Q5] ref/out arg rooted at a this-field the callee also touches ──
    // The copy-in/copy-back convention snapshots the field: the callee's param reads go stale
    // (VM 19 vs CLR 59) and its direct field writes are reverted by the copy-back (VM 1 vs CLR 5).

    [Fact]
    public void RefArgThisField_CalleeTouchesField_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class Alias1 : UdonSharp.UdonSharpBehaviour {
    public int f; public int sum; public int res;
    void Start() { f = 1; M(ref f); res = sum * 10 + f; }
    public void M(ref int x) { f = 5; sum = x; x = 9; }
}", "Alias1"));
        Assert.Contains("this-field", ex.Message);
    }

    [Fact]
    public void RefArgThisField_TransitiveTouch_Throws()
    {
        // The callee only touches f through a helper — the touch set is transitive.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class Alias2 : UdonSharp.UdonSharpBehaviour {
    public int f; public int res;
    void Start() { f = 1; M(ref f); res = f; }
    public void M(ref int x) { H(); x = x + 1; }
    void H() { f = 5; }
}", "Alias2"));
        Assert.Contains("this-field", ex.Message);
    }

    [Fact]
    public void RefArgThisField_ViaPropertyAccessor_Throws()
    {
        // The callee touches f only through a manual property getter — accessor edges close it.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class Alias3 : UdonSharp.UdonSharpBehaviour {
    public int f; public int res;
    int P { get { return f; } }
    void Start() { f = 1; M(ref f); res = f; }
    public void M(ref int x) { x = x + P; }
}", "Alias3"));
        Assert.Contains("this-field", ex.Message);
    }

    [Fact]
    public void RefArgThisField_StructMemberRoot_Throws()
    {
        // `ref s.v` roots at the this-field s; the callee touches s directly.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public struct AliasS { public int v; }
public class Alias4 : UdonSharp.UdonSharpBehaviour {
    public AliasS s; public int res;
    void Start() { M(ref s.v); res = s.v; }
    public void M(ref int x) { s.v = 5; x = x + 1; }
}", "Alias4"));
        Assert.Contains("this-field", ex.Message);
    }

    [Fact]
    public void RefArgThisField_CalleeUntouched_StillCompiles()
    {
        // The canonical supported convention (sweep_methods-pinned on the real VM): callees that
        // never touch the field keep the copy-back convention.
        var uasm = TestHelper.CompileToUasm(@"
public class Alias5 : UdonSharp.UdonSharpBehaviour {
    public int res; public int a; public int b;
    void Inc(ref int x) { x = x + 1; }
    void Sw(ref int x, ref int y) { int t = x; x = y; y = t; }
    void Start() { a = 7; Inc(ref a); Sw(ref a, ref b); res = a * 10 + b; }
}", "Alias5");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void RefArgLocal_CalleeTouchesField_StillCompiles()
    {
        // A LOCAL ref arg never aliases this-storage — field-touching callees stay legal.
        var uasm = TestHelper.CompileToUasm(@"
public class Alias6 : UdonSharp.UdonSharpBehaviour {
    public int f; public int res;
    void Start() { f = 1; int v = 2; M(ref v); res = f * 10 + v; }
    public void M(ref int x) { f = 5; x = 9; }
}", "Alias6");
        Assert.NotNull(uasm);
    }

    // ── Round-8 [R4]: two ref/out args of one call sharing a storage root ──

    [Fact]
    public void DoubleRefAlias_SameLocal_Throws()
    {
        // DiffFuzz: M(ref a, ref a) with x=x+1;y=y+3 → CLR 5 (true alias), VM 4 (last copy-back
        // wins) — independent param heap vars cannot represent the alias. Loud per §8-3.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class DblRef1 : UdonSharp.UdonSharpBehaviour {
    public int res;
    void M(ref int x, ref int y) { x = x + 1; y = y + 3; }
    void Start() { int a = 1; M(ref a, ref a); res = a; }
}", "DblRef1"));
        Assert.Contains("same storage", ex.Message);
    }

    [Fact]
    public void DoubleRefAlias_SameThisField_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class DblRef2 : UdonSharp.UdonSharpBehaviour {
    public int res; public int a;
    void M(ref int x, ref int y) { x = x + 1; y = y + 3; }
    void Start() { a = 1; M(ref a, ref a); res = a; }
}", "DblRef2"));
        Assert.Contains("same storage", ex.Message);
    }

    [Fact]
    public void DoubleRefAlias_DistinctRoots_StillCompiles()
    {
        // The pinned Swap convention (sweep_methods #12 = 83 on the real VM) — distinct storage
        // roots keep the copy-back convention.
        var uasm = TestHelper.CompileToUasm(@"
public class DblRef3 : UdonSharp.UdonSharpBehaviour {
    public int res;
    void Sw(ref int x, ref int y) { int t = x; x = y; y = t; }
    void Start() { int a = 3; int b = 8; Sw(ref a, ref b); res = a * 10 + b; }
}", "DblRef3");
        Assert.NotNull(uasm);
    }

    // ── Round-8 [R5]/[R6]: ref/out copy-back on struct-instance and foreign-static paths ──

    [Fact]
    public void StructMethod_OutArg_CopiesBack()
    {
        // The callee only WRITES x (param is a COPY destination in the body), so the one place the
        // param var appears as a COPY SOURCE is the caller-side copy-back — absent entirely pre-fix
        // (DiffFuzz: out-arg ref=10 vs VM 0; ref-arg flavor ref=136 vs VM 106).
        var uasm = TestHelper.CompileToUasm(@"
public struct CbS { public int v; public void Give(out int x) { x = 10; } }
public class CbStruct : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { CbS s = new CbS(); int t; s.Give(out t); sum = t; }
}", "CbStruct");
        Assert.Matches(@"PUSH, __\d+_x__param\s*\n\s*PUSH, [^\n]+\n\s*COPY", uasm);
    }

    [Fact]
    public void ForeignStatic_OutArg_CopiesBack()
    {
        // Same structural pin for the inlined foreign-static path (DiffFuzz: plain ref=6 vs VM 1,
        // generic monomorphized ref=9 vs VM 1).
        var uasm = TestHelper.CompileToUasm(@"
public static class CbH { public static void Give(out int x) { x = 10; } }
public class CbForeign : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int t; CbH.Give(out t); sum = t; }
}", "CbForeign");
        Assert.Matches(@"PUSH, __\d+_x__param\s*\n\s*PUSH, [^\n]+\n\s*COPY", uasm);
    }
}
