using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 1 — tracked pins for four VM-proven bugs (DiffFuzz refs cited per test;
/// real-VM value pins live in the local harness):
///
/// [W1] A capturing lambda whose capture set contains a PER-ITERATION loop local (declared inside
///      a loop body / foreach variable) must not outlive the iteration: the flat capture model has
///      one heap slot that later iterations re-seed, and with a SINGLE lambda site the 2+-site
///      aliasing detector can never fire (VM-proven compile-clean ref=6 vs usugar=16). Member
///      stores, stores into locals declared outside the loop, copy launders, and laundered
///      invocation results reject loudly; inside-loop locals, non-loop captures, and the shared
///      for-initializer variable stay legal.
/// [W2] Capturing local-function method groups are the same closure: the per-iteration guard sees
///      them, and their (transitive) capture sets now register in the aliasing detector
///      (AllLambdaCaptures was lambda-keyed only — two caplf fields sharing a capture shipped
///      compile-clean wrong values where the identical two-lambda shape was diagnosed).
/// [W3] `base.M` method-group conversion binds the BASE implementation (C# ldftn non-virtual):
///      the bundle bridges the base-instance copy via a pending bridge instead of the planner's
///      chain-root export (= the most-derived override; VM-proven 6 where C# gives 103).
/// [W4] The planner/emitter inherit walks skip a base declaration overridden by an INHERITED
///      override: pre-fix the overridden ROOT was laid out as a second collision-renamed function
///      over stale storage, and a root-typed receiver dispatch bound it (read 0/stale, write lost,
///      method ran the base body; VM-proven 0/0/1 vs 7/9/7).
/// </summary>
public class Wave9RegressionTests
{
    // ── [W1] per-iteration capture escapes reject ──

    [Fact]
    public void PerIterationCapture_FieldStoreInLoop_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T1 : UdonSharpBehaviour {
    Func<int> fa; public int k; public int r1;
    void Start() {
        for (int i = 0; i < 2; i++) { int v = i * 10 + k; if (i == 0) { fa = () => v; } }
        r1 = fa();
    }
}", "W9T1"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    [Fact]
    public void PerIterationCapture_OuterLocalStore_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T2 : UdonSharpBehaviour {
    public int k; public int r1;
    void Start() {
        Func<int> f = null;
        for (int i = 0; i < 2; i++) { int v = i * 10 + k; if (i == 0) { f = () => v; } }
        r1 = f();
    }
}", "W9T2"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    [Fact]
    public void PerIterationCapture_CopyLaunder_Rejects()
    {
        // The fragility rides the pre-scan copy edges — order-independent like the capture taint.
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T3 : UdonSharpBehaviour {
    public int k; public int r1;
    void Start() {
        Func<int> g = null;
        for (int i = 0; i < 2; i++) { int v = i * 10 + k; Func<int> f = () => v; if (i == 0) { g = f; } }
        r1 = g();
    }
}", "W9T3"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    [Fact]
    public void PerIterationCapture_InvocationResultLaunder_Rejects()
    {
        // `g = Id(() => v)` carries the per-iteration fragility into the result local.
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T4 : UdonSharpBehaviour {
    public int k; public int r1;
    Func<int> Id(Func<int> x) { return x; }
    void Start() {
        Func<int> g = null;
        for (int i = 0; i < 2; i++) { int v = i * 10 + k; if (i == 0) { g = Id(() => v); } }
        r1 = g();
    }
}", "W9T4"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    [Fact]
    public void PerIterationCapture_ForeachVariable_FieldStore_Rejects()
    {
        // The foreach iteration variable is per-iteration in C# 5+.
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T5 : UdonSharpBehaviour {
    Func<int> fa; public int r1;
    void Start() {
        var arr = new int[] { 1, 2 };
        foreach (var x in arr) { if (x == 1) { fa = () => x; } }
        r1 = fa();
    }
}", "W9T5"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    [Fact]
    public void PerIterationCapture_WhileBodyLocal_FieldStore_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T6 : UdonSharpBehaviour {
    Func<int> fa; public int k; public int r1;
    void Start() {
        int i = 0;
        while (i < 2) { int v = i * 10 + k; if (i == 0) { fa = () => v; } i++; }
        r1 = fa();
    }
}", "W9T6"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    // ── [W1] legal flows stay legal ──

    [Fact]
    public void PerIterationCapture_InsideLoopLocal_StoreAndInvoke_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T7 : UdonSharpBehaviour {
    public int k; public int r1;
    void Start() {
        int total = 0;
        for (int i = 0; i < 3; i++) { int v = i * 10 + k; Func<int> f = () => v; total += f(); }
        r1 = total;
    }
}", "W9T7");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void NonLoopCapture_FieldStoreInsideLoop_Compiles()
    {
        // The captured local lives OUTSIDE the loop: one slot, one seed — single-activation legal.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T8 : UdonSharpBehaviour {
    Func<int> fa; public int k; public int r1;
    void Start() {
        int v = k * 2;
        for (int i = 0; i < 2; i++) { if (i == 0) { fa = () => v; } }
        r1 = fa();
    }
}", "W9T8");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void ForInitializerVariableCapture_FieldStore_Compiles()
    {
        // C# shares the for-initializer variable across iterations — the flat slot matches
        // (DiffFuzz Match pinned on the real VM in the harness).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T9 : UdonSharpBehaviour {
    Func<int> fa; public int k; public int r1;
    void Start() {
        for (int i = 0; i < 2; i++) { if (i == 0) { fa = () => i + k; } }
        r1 = fa();
    }
}", "W9T9");
        Assert.NotNull(uasm);
    }

    // ── [W2] caplf method groups: per-iteration guard + aliasing detector parity ──

    [Fact]
    public void PerIterationCapture_CapLfMethodGroup_FieldStore_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T10 : UdonSharpBehaviour {
    Func<int> fa; public int k; public int r1;
    void Start() {
        for (int i = 0; i < 2; i++) { int v = i * 10 + k; int Get() { return v; } if (i == 0) { fa = Get; } }
        r1 = fa();
    }
}", "W9T10"));
        Assert.NotNull(ex);
        Assert.Contains("per-iteration", ex.ToString());
    }

    [Fact]
    public void CapLfMethodGroup_TwoFieldStores_SharedCapture_RaisesAliasError()
    {
        // Non-loop local shared by two caplf method-group field stores: same diagnostic as the
        // identical two-lambda shape (pre-fix: zero diagnostics — the dictionary was lambda-keyed).
        TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T11 : UdonSharpBehaviour {
    Func<int> fa; Func<int> fb; public int k;
    void Start() {
        int v = k * 3;
        int Get() { return v; }
        fa = Get;
        fb = Get;
    }
}", "W9T11", out var emitter);
        var aliasingErrors = emitter.Diagnostics
            .Where(d => d.Severity == "Error" && d.Message.Contains("shared"))
            .ToArray();
        Assert.Single(aliasingErrors);
        Assert.Contains("'v'", aliasingErrors[0].Message);
    }

    [Fact]
    public void CapLfMethodGroup_AndLambda_SharedCapture_RaisesAliasError()
    {
        TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T12 : UdonSharpBehaviour {
    Func<int> fa; Func<int> fb; public int k;
    void Start() {
        int v = k * 3;
        int Get() { return v; }
        fa = Get;
        fb = () => v + 1;
    }
}", "W9T12", out var emitter);
        Assert.Single(emitter.Diagnostics.Where(d => d.Severity == "Error" && d.Message.Contains("shared")));
    }

    [Fact]
    public void CapLfMethodGroup_SingleFieldStore_NoAliasError()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9T13 : UdonSharpBehaviour {
    Func<int> fa; public int k; public int r1;
    void Start() {
        int v = k * 3;
        int Get() { return v; }
        fa = Get;
        r1 = fa();
    }
}", "W9T13", out var emitter);
        Assert.NotNull(uasm);
        Assert.DoesNotContain(emitter.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("shared"));
    }

    // ── [W3] base.M method group binds the base implementation ──

    [Fact]
    public void BaseMethodGroup_WithOverride_BridgesBaseInstanceCopy()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using System;
using UdonSharp;
public class W9T14B : UdonSharpBehaviour { public virtual int M(int x) { return x + 100; } }
public class W9T14 : W9T14B {
    public int seed; public int result;
    public override int M(int x) { return x * 2; }
    void Start() { Func<int, int> f = base.M; result = f(seed); }
}" }, "W9T14");
        // TWO __dlg_*_M bridges: the planner bridge for the exported override (__dlg___0_M) and
        // the pending bridge for the never-exported base-instance copy (__dlg___2_M) — the bundle
        // binds the copy, so base.M survives as a NON-virtual binding of the base body.
        var bridges = Regex.Matches(uasm, @"^\s*\.export (__dlg___\d+_M)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Equal(2, bridges.Length);
    }

    [Fact]
    public void BaseMethodGroup_NoOverride_KeepsPlannerBridge()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using System;
using UdonSharp;
public class W9T15B : UdonSharpBehaviour { public virtual int M(int x) { return x + 100; } }
public class W9T15 : W9T15B {
    public int seed; public int result;
    void Start() { Func<int, int> f = base.M; result = f(seed); }
}" }, "W9T15");
        // No override anywhere: base.M IS the one inherited implementation — exactly the planner
        // bridge, no base-instance copy, byte-identical to the pre-fix output.
        var bridges = Regex.Matches(uasm, @"^\s*\.export (__dlg___\d+_M)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.Single(bridges);
    }

    // ── [W4] inherited override owns the chain's virtual slot ──

    [Fact]
    public void InheritedMidOverride_AutoProp_SingleAccessorExport()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class W9T16A : UdonSharpBehaviour { public virtual int P { get; set; } }
public class W9T16B : W9T16A { public override int P { get; set; } }
public class W9T16 : W9T16B {
    public int result;
    void Start() { P = 7; W9T16A a = this; result = a.P; }
}" }, "W9T16");
        // Exactly ONE exported getter (the inherited MID override over the bare 'P' storage).
        // Pre-fix the overridden ROOT declaration was ALSO inherited as a collision-renamed
        // accessor over the dead __basebk storage, and the root-typed dispatch bound it.
        Assert.Single(Regex.Matches(uasm, @"^\s*\.export get_P\s*$", RegexOptions.Multiline));
        Assert.DoesNotContain("PUSH, __basebk", uasm);
    }

    [Fact]
    public void InheritedMidOverride_Method_SingleExport()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class W9T17A : UdonSharpBehaviour { public virtual int G() { return 1; } }
public class W9T17B : W9T17A { public override int G() { return 7; } }
public class W9T17 : W9T17B {
    public int result;
    void Start() { W9T17A a = this; result = a.G(); }
}" }, "W9T17");
        // ONE exported G (the inherited override body); the overridden root body is not emitted.
        Assert.Single(Regex.Matches(uasm, @"^\s*\.export G\s*$", RegexOptions.Multiline));
        Assert.Empty(Regex.Matches(uasm, @"^\s*\.export __\d+_G\s*$", RegexOptions.Multiline));
    }

    [Fact]
    public void NewShadowedInheritedMethod_StillReallocatesExport()
    {
        // Guard the collision path the [W4] fix narrowed: a `new`-shadowed inherited METHOD has
        // no override relation, so the base member still inherits under a re-allocated export.
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class W9T18A : UdonSharpBehaviour { public int H() { return 1; } }
public class W9T18 : W9T18A {
    public int result;
    public new int H() { return 7; }
    void Start() { result = H(); }
}" }, "W9T18");
        Assert.Single(Regex.Matches(uasm, @"^\s*\.export H\s*$", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(uasm, @"^\s*\.export __\d+_H\s*$", RegexOptions.Multiline));
    }
}
