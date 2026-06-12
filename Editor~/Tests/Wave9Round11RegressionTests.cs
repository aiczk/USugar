using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-11 fix round 11 — tracked pins for the two VM-proven findings (real-VM value pins live in
/// the local harness corpora fcd_wave9_lvaluegrammar_r11.json, _w11lg_r11_min.json and
/// _w11lg_r11_fixprobe.json):
///
/// [Z1] Compound assignment / inc-dec on a NON-indexer property of an aggregate (struct) receiver
///      re-evaluated the receiver legs at write-back: CaptureLValue had no caching arm for the
///      shape, so it fell to the default arm (ArrayVal stayed null) and EmitWriteBack's
///      aggregate-property arm re-ran the legs AFTER the RHS — wrong element + leg side effects
///      twice (VM-proven ss[Ix()].P += Mut(): trace ref=12 vs 121, result ref=803 vs 283; the
///      inc/dec flavor ref=11/2904 vs 1111/2306). Fixed: a CaptureLValue arm mirroring the
///      VisitPropertyReference read (layout slot / user getter, receiver legs ONCE) caches the raw
///      receiver, and EmitWriteBack's aggregate user-setter path consumes lv.ArrayVal.
///
/// [Z2] Nested deconstruction fed by a tuple-returning CALL component inside a tuple literal:
///      USugar snapshotted components in plain textual order, but Roslyn's lowering evaluates
///      components paired with non-deconstructed LEAF targets first (the `init` effects), then
///      components feeding a nested deconstruction (the `deconstructions` effects) — textual order
///      within each group, recursing through nested tuple LITERALS without leaving the init group
///      (VM-proven ((a,t),u) = (MPair(),V3()): ref trace=32 vs 23; leg-bearing flavor 132 vs 123;
///      the deep mixed flavor ((a,(b,c)),u) = ((V1(),MPair()),V3()) = 132, DiffFuzz-confirmed
///      post-fix in _w11lg_r11_fixprobe W11FP01).
/// </summary>
public class Wave9Round11RegressionTests
{
    // ── [Z1] aggregate-receiver property compound/inc-dec: receiver legs evaluate ONCE ──

    [Fact]
    public void CompoundProperty_StructArrayElementReceiver_LegsEvaluateOnce()
    {
        // W11LGMinA1 (VM-proven trace ref=12 vs 121): the index leg `k * 3` must emit exactly one
        // multiplication — pre-fix the write-back re-ran the receiver legs after the RHS (2 mults).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R11AS { public int pb; public int P { get { return pb; } set { pb = value; } } }
public class W9R11A : UdonSharpBehaviour {
    public int n; public int r; int k;
    W9R11AS[] ss;
    int Mut() { k = k + 1; return n + 3; }
    void Start() {
        ss = new W9R11AS[8];
        k = n;
        ss[k * 3].P += Mut();
        r = ss[0].pb + k;
    }
}", "W9R11A");
        Assert.Equal(1, CountInCode(uasm, "SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32"));
    }

    [Fact]
    public void IncDecProperty_StructArrayElementReceiver_LegsEvaluateOnce()
    {
        // W11LGMinA3 (VM-proven trace ref=11 vs 1111): inc/dec shares the CaptureLValue/EmitWriteBack
        // root — the leg runs once per statement.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R11BS { public int pb; public int P { get { return pb; } set { pb = value; } } }
public class W9R11B : UdonSharpBehaviour {
    public int n; public int r; int k;
    W9R11BS[] ss;
    void Start() {
        ss = new W9R11BS[8];
        k = n;
        ss[k * 3].P++;
        r = ss[0].pb + k;
    }
}", "W9R11B");
        Assert.Equal(1, CountInCode(uasm, "SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32"));
    }

    [Fact]
    public void CompoundAutoProperty_StructArrayElementReceiver_LegsEvaluateOnce()
    {
        // Auto-prop (layout-slot) flavor, oracle-confirmed in _w11lg_r11_fixprobe W11FP07: the
        // write-back's layout arm consumed lv.ArrayVal only when CaptureLValue cached it — the
        // missing arm meant the slot path re-ran the legs too.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R11CS { public int A { get; set; } }
public class W9R11C : UdonSharpBehaviour {
    public int n; public int r; int k;
    W9R11CS[] ss;
    void Start() {
        ss = new W9R11CS[8];
        k = n;
        ss[k * 3].A += n;
        r = ss[0].A + k;
    }
}", "W9R11C");
        Assert.Equal(1, CountInCode(uasm, "SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32"));
    }

    [Fact]
    public void SimpleSetProperty_StructArrayElementReceiver_SingleLeg_Control()
    {
        // Control (W11LGMinA2 Matched pre- AND post-fix): the simple-SET path rides
        // PreparePropertySet (untouched) and already evaluated the legs exactly once.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R11DS { public int pb; public int P { get { return pb; } set { pb = value; } } }
public class W9R11D : UdonSharpBehaviour {
    public int n; public int r; int k;
    W9R11DS[] ss;
    int Mut() { k = k + 1; return n + 3; }
    void Start() {
        ss = new W9R11DS[8];
        k = n;
        ss[k * 3].P = Mut();
        r = ss[0].pb + k;
    }
}", "W9R11D");
        Assert.Equal(1, CountInCode(uasm, "SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32"));
    }

    // ── [Z2] tuple-literal component order: leaf-paired before nested-paired ──

    [Fact]
    public void NestedDecon_CallComponent_LeafComponentEvaluatesFirst()
    {
        // W11LGMinB1 (VM-proven ref trace=32 vs 23): the leaf-paired V3 component (its argument
        // carries the Abs marker) must evaluate BEFORE the nested-paired MPair component (Max
        // marker), although MPair is textually first.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R11E : UdonSharpBehaviour {
    public int seed; public int r;
    (int, int) MPair(int x) { return (x + 5, 3); }
    int V3(int x) { return x + 1; }
    void Start() {
        int a; int t; int u;
        ((a, t), u) = (MPair(Math.Max(seed, 2)), V3(Math.Abs(seed)));
        r = a * 100 + t * 10 + u;
    }
}", "W9R11E");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
    }

    [Fact]
    public void DeepMixedNestedDecon_InitRecursesThroughLiterals()
    {
        // Deep mixed flavor (DiffFuzz-confirmed trace 132 in _w11lg_r11_fixprobe W11FP01): the init
        // group recurses through the nested LITERAL — V1 (Max) then u's component (Abs) — while the
        // nested CALL component (Min marker) lands in the deconstructions group, last.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R11F : UdonSharpBehaviour {
    public int seed; public int r;
    (int, int) MPair(int x) { return (x + 5, 3); }
    void Start() {
        int a; int b; int c; int u;
        ((a, (b, c)), u) = ((Math.Max(seed, 2), MPair(Math.Min(seed, 9))), Math.Abs(seed));
        r = a * 1000 + b * 100 + c * 10 + u;
    }
}", "W9R11F");
        AssertExternBefore(uasm,
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32",
            "SystemMath.__Abs__SystemInt32__SystemInt32");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Min__SystemInt32_SystemInt32__SystemInt32");
    }

    [Fact]
    public void NestedDecon_BothNestedComponents_TextualOrder_Control()
    {
        // Control (Matched pre- AND post-fix): two nested-paired call components both live in the
        // deconstructions group — textual order between them is preserved.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R11G : UdonSharpBehaviour {
    public int seed; public int r;
    (int, int) M1(int x) { return (x + 5, 3); }
    (int, int) M2(int x) { return (x + 7, 4); }
    void Start() {
        int a; int t; int b; int u;
        ((a, t), (b, u)) = (M1(Math.Max(seed, 2)), M2(Math.Abs(seed)));
        r = a * 1000 + t * 100 + b * 10 + u;
    }
}", "W9R11G");
        AssertExternBefore(uasm,
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32",
            "SystemMath.__Abs__SystemInt32__SystemInt32");
    }

    [Fact]
    public void NestedDecon_NestedLiteralComponent_TextualOrder_Control()
    {
        // Control (Matched pre- AND post-fix): a nested tuple-LITERAL component recurses inside the
        // init group, so all leaves stay textual (Max inside the nested literal before Abs).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R11H : UdonSharpBehaviour {
    public int seed; public int r;
    void Start() {
        int a; int t; int u;
        ((a, t), u) = ((Math.Max(seed, 2), 2), Math.Abs(seed));
        r = a * 100 + t * 10 + u;
    }
}", "W9R11H");
        AssertExternBefore(uasm,
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32",
            "SystemMath.__Abs__SystemInt32__SystemInt32");
    }

    // ── helpers ──

    static int CountInCode(string uasm, string needle)
    {
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int count = 0, pos = 0;
        while ((pos = code.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        return count;
    }

    static void AssertExternBefore(string uasm, string first, string second)
    {
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int firstPos = code.IndexOf(first, StringComparison.Ordinal);
        int secondPos = code.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstPos >= 0 && secondPos >= 0, "expected both externs in the code section");
        Assert.True(firstPos < secondPos,
            $"'{first}' must be emitted before '{second}' (C# evaluation order)");
    }
}
