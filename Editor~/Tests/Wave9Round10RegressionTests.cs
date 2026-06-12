using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-10 fix round 10 — tracked pins for the VM-proven finding (real-VM value pins live in the
/// local harness round-10 corpora fcd_wave9_genspec_r10.json and _w10gs_r10_min.json):
///
/// [Z1] A self-recursive BASE-declared generic method referenced ONLY through a delegate method
///      group ([Y7]-r9 bridge path) never fed BuildRecursionInfo: the [Y7] arm registers the
///      constructed spec at EMIT time — after the recursion graph was built — so the spec body
///      had no graph node, no self-edge, and no spill machinery; every unwinding frame read the
///      innermost frame's local (VM-proven r ref=47 vs 17 at depth 5; un-minimized W10GS_34
///      ref=137 vs 29 at depth 9; open-T flavor inside a derived generic reds identically).
///      Adding ONE direct call leg rescued the shape — the direct-call collector eagerly registers
///      the closed copy BEFORE BuildRecursionInfo — proving the hole was MG-only references.
///      Fixed: CollectBaseInstanceCallsInOperation seeds the referenced DEFINITION into
///      _openGenericBaseDefs (the round-9 [Y5] on-demand-spec family) for this/implicit-receiver
///      generic method groups, closed and open type args alike; `base.`/variable receivers stay
///      uncollected (the [Y7] creation gate rejects them loudly).
/// </summary>
public class Wave9Round10RegressionTests
{
    // ── [Z1] inherited generic MG-only reference joins the recursion graph ──

    [Fact]
    public void InheritedGenericMethodGroupOnly_SelfRecursion_Spills()
    {
        // W10GSMin34 (VM-proven r ref=47 vs 17 pre-fix: no spill machinery at all).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R10GMinB : UdonSharpBehaviour {
    public int RecB<T>(int n) {
        if (n <= 0) { return 2; }
        int loc = n * 3;
        int below = RecB<T>(n - 1);
        return loc + below;
    }
}
public class W9R10Min34 : W9R10GMinB {
    public int b; public int r;
    void Start() {
        Func<int, int> g = RecB<int>;
        r = g(b + 1);
    }
}", "W9R10Min34");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void InheritedGenericMethodGroupOnly_OpenT_SelfRecursion_Spills()
    {
        // W10GSMin34Open (same red, ref=47 vs 17): the MG's type arg is the ENCLOSING derived
        // generic's T — the definition must seed the graph before any spec exists.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R10GMinB4 : UdonSharpBehaviour {
    public int RecB<T>(int n) {
        if (n <= 0) { return 2; }
        int loc = n * 3;
        int below = RecB<T>(n - 1);
        return loc + below;
    }
}
public class W9R10Min34Open : W9R10GMinB4 {
    public int b; public int r;
    void Start() { r = Run<int>(b + 1); }
    int Run<T>(int v) {
        Func<int, int> g = RecB<T>;
        return g(v);
    }
}", "W9R10Min34Open");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void InheritedGenericMethodGroupOnly_NonRecursiveBody_NoSpill()
    {
        // Control (W10GSMin34CtlNoRec, the r9 [Y7] pin family — Match pre- AND post-fix): the
        // seeded definition gains a trivially cycle-free graph node, so a NON-recursive base
        // generic MG-only shape must not grow spill machinery.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R10GMinB5 : UdonSharpBehaviour {
    public int IdB<T>(int n) { return n * 3 + 2; }
}
public class W9R10Min34NoRec : W9R10GMinB5 {
    public int b; public int r;
    void Start() {
        Func<int, int> g = IdB<int>;
        r = g(b + 1);
    }
}", "W9R10Min34NoRec");
        Assert.DoesNotContain("__recurStack", uasm);
    }
}
