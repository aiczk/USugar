using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 3 — tracked pins for the four VM-proven findings (DiffFuzz refs cited per
/// test; real-VM value pins live in the local harness wave corpus fcd_wave9_fixround3.json,
/// 20/20 Match post-fix, 13 Mismatch pre-fix):
///
/// [W1] A recursion cycle closed through a base-instance copy's virtual call was invisible to the
///      static recursion graph: the derived override M calls base.M, whose body recurses via a
///      this-receiver M(m-1) that EMISSION resolves to the LEAF override while the graph recorded
///      only the static Base.M->Base.M self-edge — Derived.M was never a cycle member and its live
///      locals/params clobbered to the deepest frame (DiffFuzz ref=12 vs VM 14; param flavor
///      ref=10 vs 8).
/// [W2] Bundle flavor of the same root: fb = base.M stored to an own field and dispatched from the
///      override closes override ->fb-> base copy ->virtual leaf-> override; the synthetic dispatch
///      edge existed but the leaf back-edge did not, so neither frame spilled (ref=21 vs 17).
/// [W3] A recursion cycle through a virtual OVERRIDE (no base. call at all): the inherited base
///      body Walk calls/reads a virtual member whose static binding is the chain ROOT (no
///      back-edge) while emission dispatches the LEAF override that calls Walk back — method AND
///      property-getter flavors both ref=605 vs VM 305. Fixed by adding the emission-faithful
///      leaf-override edge (and IsInternalCallTo match) for this-receiver virtual call/accessor
///      sites; static edges stay (extra edges only over-spill, §8-3).
/// [W4] Named arguments on cross-program dispatch bound POSITIONALLY: EmitInterfaceCall,
///      EmitCrossClassCall, and the cross-behaviour tuple-deconstruction path consumed
///      op.Arguments in textual order while indexing ParamIds by textual position (DiffFuzz
///      ref=54 vs 45 own-typed receiver / interface receiver; deconstruct ref=5401 vs 4499).
///      Now evaluated textually but slotted at Parameter.Ordinal, pairs emitted in ordinal order
///      (CrossCallArgPairs) — the this-path and delegate-invoke paths were fixed in earlier rounds.
/// </summary>
public class Wave9Round3RegressionTests
{
    // ── [W1] override <-> base-copy recursion ──

    [Fact]
    public void OverrideBaseCopyRecursion_SpillsFrames()
    {
        // Pre-fix: no __recurStack anywhere — Derived.M was not a cycle member, and the copy's
        // call site emitted callee=Derived.M which never matched recursive[Base.M]={Base.M}.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R3B01_OverrideBaseCopyMin : W9R3B01Base {
    public int n; public int result;
    void Start() { result = M(n); }
    public override int M(int m) {
        if (m <= 0) return 2;
        int s = m * 3 + 1;
        int below = base.M(m - 1);
        return below + s % 8;
    }
}
public class W9R3B01Base : UdonSharpBehaviour {
    public virtual int M(int m) {
        if (m <= 0) return 5;
        return M(m - 1) + 1;
    }
}", "W9R3B01_OverrideBaseCopyMin");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void OverrideBaseCall_AcyclicBase_Control_DoesNotSpill()
    {
        // The base body never calls back — no cycle, the new leaf edges must not over-spill.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R3Ctl1 : W9R3Ctl1Base {
    public int n; public int result;
    void Start() { result = M(n); }
    public override int M(int m) {
        int s = m * 3 + 1;
        int below = base.M(m - 1);
        return below + s % 8;
    }
}
public class W9R3Ctl1Base : UdonSharpBehaviour {
    public virtual int M(int m) { return m * 2 + 5; }
}", "W9R3Ctl1");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [W2] fb = base.M bundle cycle ──

    [Fact]
    public void BaseMethodGroupBundleCycle_SpillsFrames()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R3B02_BaseMgBundleMin : W9R3B02Base {
    Func<int, int> fb;
    public int n; public int result;
    void Start() { fb = base.M; result = M(n); }
    public override int M(int m) {
        if (m <= 0) return 2;
        int s = m ^ 3;
        int below = fb(m - 1);
        return below + s % 9;
    }
}
public class W9R3B02Base : UdonSharpBehaviour {
    public virtual int M(int m) {
        if (m <= 0) return 3;
        int t = m * 4;
        int below = M(m - 1);
        return below + t % 6;
    }
}", "W9R3B02_BaseMgBundleMin");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void BaseMethodGroupBundle_AcyclicBase_Control_DoesNotSpill()
    {
        // fb = base.M where the base body never calls back: synthetic edge only, no cycle.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R3Ctl2 : W9R3Ctl2Base {
    Func<int, int> fb;
    public int n; public int result;
    void Start() { fb = base.M; result = M(n); }
    public override int M(int m) {
        if (m <= 0) return 2;
        int s = m ^ 3;
        int below = fb(m - 1);
        return below + s % 9;
    }
}
public class W9R3Ctl2Base : UdonSharpBehaviour {
    public virtual int M(int m) { return m * 2 + 1; }
}", "W9R3Ctl2");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [W3] virtual-override recursion hole ──

    [Fact]
    public void VirtualOverrideRecursionCycle_SpillsFrames()
    {
        // Walk (inherited base body) calls Step() whose static binding is the folded chain ROOT;
        // the leaf override calls Walk back. Pre-fix: no graph edge Walk->Step, no spill anywhere.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinVirtRecBase : UdonSharpBehaviour {
    public int cnt;
    public virtual int Step() { return 7; }
    public int Walk(int n) {
        if (n <= 0) { return 1; }
        int keep = n * 100;
        int sub = Step();
        return keep + sub;
    }
}
public class MinVirtRec : MinVirtRecBase {
    public int seed; public int result;
    public override int Step() {
        cnt = cnt - 1;
        if (cnt <= 0) { return 5; }
        return Walk(cnt);
    }
    void Start() { cnt = seed; result = Walk(seed); }
}", "MinVirtRec");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void VirtualGetterOverrideRecursionCycle_SpillsFrames()
    {
        // Property-getter flavor: the base body READS virtual P; the leaf getter calls Walk back.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9J05Base : UdonSharpBehaviour {
    public int cnt;
    public virtual int P { get { return 7; } }
    public int Walk(int n) {
        if (n <= 0) { return 1; }
        int keep = n * 100;
        int sub = P;
        return keep + sub;
    }
}
public class W9J05_VirtGetterRecursion : W9J05Base {
    public int seed; public int result;
    public override int P { get { cnt = cnt - 1; if (cnt <= 0) { return 5; } return Walk(cnt); } }
    void Start() { cnt = seed; result = Walk(seed); }
}", "W9J05_VirtGetterRecursion");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void VirtualOverrideCall_AcyclicLeaf_Control_DoesNotSpill()
    {
        // Leaf override never calls back — the new leaf edge exists but closes no cycle.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R3Ctl3Base : UdonSharpBehaviour {
    public virtual int Step() { return 7; }
    public int Walk(int n) {
        int keep = n * 100;
        int sub = Step();
        return keep + sub;
    }
}
public class W9R3Ctl3 : W9R3Ctl3Base {
    public int seed; public int result;
    public override int Step() { return 9; }
    void Start() { result = Walk(seed); }
}", "W9R3Ctl3");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [W4] named arguments on cross-program dispatch ──

    const string CrossClassTemplate = @"
using System;
using UdonSharp;
public class MinNamedOwnRecv : UdonSharpBehaviour {{
    public int k; public int result;
    public int Mix(int a, int b) {{ return a * 10 + b; }}
    void Start() {{
        MinNamedOwnRecv me = this;
        result = me.Mix({0});
    }}
}}";

    [Fact]
    public void CrossClassCall_NamedArgsReordered_EmitsSameUasmAsDeclarationOrder()
    {
        // Post-fix the reversed-named-args call is byte-identical to the declaration-order call
        // (textual evaluation, SetProgramVariable pairs in ordinal order). Pre-fix the values were
        // stored to the positionally-indexed param ids (DiffFuzz ref=54 vs VM 45).
        var reordered = TestHelper.CompileToUasm(string.Format(CrossClassTemplate, "b: k, a: 9"), "MinNamedOwnRecv");
        var declOrder = TestHelper.CompileToUasm(string.Format(CrossClassTemplate, "a: 9, b: k"), "MinNamedOwnRecv");
        Assert.Equal(declOrder, reordered);
    }

    const string IfaceTemplate = @"
using System;
using UdonSharp;
public interface IW9R3Mix {{ int Mix(int a, int b); }}
public class MinNamedIface : UdonSharpBehaviour, IW9R3Mix {{
    public int k; public int result;
    public int Mix(int a, int b) {{ return a * 10 + b; }}
    void Start() {{
        IW9R3Mix h = this;
        result = h.Mix({0});
    }}
}}";

    [Fact]
    public void InterfaceCall_NamedArgsReordered_EmitsSameUasmAsDeclarationOrder()
    {
        var reordered = TestHelper.CompileToUasm(string.Format(IfaceTemplate, "b: k, a: 9"), "MinNamedIface");
        var declOrder = TestHelper.CompileToUasm(string.Format(IfaceTemplate, "a: 9, b: k"), "MinNamedIface");
        Assert.Equal(declOrder, reordered);
    }

    const string DeconstructTemplate = @"
using System;
using UdonSharp;
public class W9R3NamedDecon : UdonSharpBehaviour {{
    public int k; public int result;
    public (int, int) Pair(int a, int b) {{ return (a * 10 + b, a - b); }}
    void Start() {{
        W9R3NamedDecon me = this;
        (int x, int y) = me.Pair({0});
        result = x * 100 + y;
    }}
}}";

    [Fact]
    public void CrossTupleDeconstruct_NamedArgsReordered_EmitsSameUasmAsDeclarationOrder()
    {
        var reordered = TestHelper.CompileToUasm(string.Format(DeconstructTemplate, "b: k, a: 9"), "W9R3NamedDecon");
        var declOrder = TestHelper.CompileToUasm(string.Format(DeconstructTemplate, "a: 9, b: k"), "W9R3NamedDecon");
        Assert.Equal(declOrder, reordered);
    }
}
