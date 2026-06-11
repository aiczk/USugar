using System;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 4 — tracked pins for the nine VM-proven findings (DiffFuzz refs cited per
/// test; real-VM value pins live in the local harness wave corpus fcd_wave9_fixround4.json,
/// 25/25 Match post-fix, 9 Mismatch + 9 loud crashes pre-fix):
///
/// [X1] Delegate .Equals() method call emitted a nonexistent
///      UnityEngineComponent.__Equals__SystemObject__SystemBoolean extern (loud crash on legal
///      C#; ref eq1=1). Now routed through the same (target, method) value comparison as `==`
///      (CompareDelegates, moved to HandlerBase — one knowledge source).
/// [X2] Capturing LAMBDA as recursion-cycle member: a captured local read AFTER the reentrant
///      dispatch inside the lambda body saw the inner activation's re-seeded flat slot
///      (DiffFuzz ref=60 vs VM 50). The hoisted node's dispatch-site spill set now includes its
///      READ-ONLY capture cells whose declaring function shares the node's SCC
///      (EmitContext.HoistedCaptureSpillCells; cells written by any hoisted node keep flat
///      sharing — same-environment mutation must stay visible).
/// [X3] Same root, capturing LOCAL FUNCTION flavor (direct-invoked; ref=60 vs 50).
/// [X4] Interface-typed receiver: user indexer read/write/compound/inc-dec fell through to
///      extern resolution (nonexistent IUdonEventReceiver.__get_Item/__set_Item → loud
///      UasmValidationException on legal C#). Now dispatched through the interface accessor
///      bridges like interface methods/properties.
/// [X5] Interface-typed receiver: property COMPOUND/inc-dec WRITE-BACK emitted a nonexistent
///      __set_P extern (the read leg and simple set already bridged). New EmitWriteBack arm.
/// [X9] Root cause of [X4]: IsVariableReceiverBehaviourIndexer tests
///      IsUdonSharpBehaviour(Property.ContainingType) — the INTERFACE for these sites — so the
///      r2-[W6] cross-indexer gate never fired; closed by TryGetInterfaceAccessorLayout.
/// [X6] Named index arguments on a THIS-receiver user indexer bound positionally (read, write,
///      compound; DiffFuzz ref c5=11/c7=0 vs usugar c5=0/c7=11). All this-path accessor arms now
///      evaluate textually and slot by Parameter.Ordinal (EvaluateIndexerArgs).
/// [X7] Same family on a user STRUCT indexer (ref rp=1/rq=7 vs usugar rp=2/rq=6).
/// [X8] Same family on a base[...] indexer access (the base-instance copy accessor rides the
///      same this-path setter arm; ref c5=11/c7=0 vs usugar c5=0/c7=11).
/// </summary>
public class Wave9Round4RegressionTests
{
    // ── [X1] delegate .Equals method ──

    [Fact]
    public void DelegateEqualsMethod_CompilesAsValueEquality()
    {
        // Pre-fix: loud 'missing extern: UnityEngineComponent.__Equals__SystemObject__SystemBoolean'.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9V26Min : UdonSharpBehaviour {
    public int eq1;
    void Start() {
        Func<int, int> a = M;
        Func<int, int> b = M;
        eq1 = a.Equals(b) ? 1 : 0;
    }
    public int M(int v) { return v + 1; }
}", "W9V26Min");
        Assert.DoesNotContain("__Equals__SystemObject__SystemBoolean", uasm);
        // The == value-equality machinery: method-element compare via string equality.
        Assert.Contains("SystemString.__op_Equality__SystemString_SystemString__SystemBoolean", uasm);
    }

    [Fact]
    public void DelegateEqualsMethod_NonDelegateArgument_RejectsLoudly()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9X1Bad : UdonSharpBehaviour {
    public int eq1;
    void Start() {
        Func<int, int> a = M;
        eq1 = a.Equals(5) ? 1 : 0;
    }
    public int M(int v) { return v + 1; }
}", "W9X1Bad"));
        Assert.NotNull(ex);
        Assert.Contains("non-delegate argument", ex.Message);
    }

    // ── [X2]/[X3] capture-cell spill at hoisted cycle members' dispatch sites ──

    const string X2LambdaSource = @"
using System;
using UdonSharp;
public class W9R4MinLambda : UdonSharpBehaviour {
    Func<int, int> d;
    public int n;
    public int result;
    void Start() { d = M; result = M(n); }
    int M(int m) {
        if (m <= 0) return 0;
        int keep = m * 10;
        Func<int, int> c = k => d(k) + keep;
        return c(m - 1) + keep;
    }
}";

    [Fact]
    public void LambdaCycleMember_SpillsReadOnlyCaptureCellAtDispatchSite()
    {
        // The captured local `keep` is declared by M, which shares the lambda's SCC (d = M; the
        // lambda dispatches d; M dispatches the lambda) — the lambda's marked dispatch arms must
        // save/restore keep's flat slot so the post-dispatch read sees the creating activation's
        // value (DiffFuzz ref=60 vs VM 50 pre-fix). Pre-fix the only references to keep were M's
        // own body + M's call-site spill; post-fix the lambda's two flagged arms each add a
        // spill LoadField + reload store of the cell. Exact occurrence count pinned (the [Q4]
        // exact-delta precedent); stash-verified lower pre-fix.
        var uasm = TestHelper.CompileToUasm(X2LambdaSource, "W9R4MinLambda");
        int keepRefs = Regex.Matches(uasm, @"__lcl_keep_\w+").Count;
        Assert.Equal(12, keepRefs); // pre-fix: 8 (no lambda-side spill/reload of the cell; stash-verified)
    }

    const string X3CapLfSource = @"
using System;
using UdonSharp;
public class W9R4MinCapLf : UdonSharpBehaviour {
    Func<int, int> d;
    public int n;
    public int result;
    void Start() { d = M; result = M(n); }
    int M(int m) {
        if (m <= 0) return 0;
        int keep = m * 10;
        int L(int k) { return d(k) + keep; }
        return L(m - 1) + keep;
    }
}";

    [Fact]
    public void CapturingLocalFunctionCycleMember_SpillsReadOnlyCaptureCellAtDispatchSite()
    {
        var uasm = TestHelper.CompileToUasm(X3CapLfSource, "W9R4MinCapLf");
        int keepRefs = Regex.Matches(uasm, @"__lcl_keep_\w+").Count;
        Assert.Equal(10, keepRefs); // pre-fix: 6 (no local-function-side spill/reload of the cell; stash-verified)
    }

    [Fact]
    public void RecursiveLambda_EnclosingMethodOutsideScc_CaptureCellStaysUnspilled()
    {
        // Control for the SCC gate: g captures Start's local zed, but Start is not a cycle member
        // (nothing dispatches back into it) — zed can never be re-seeded mid-dispatch, so it must
        // NOT join g's spill set (byte-stability for the existing recursive-lambda shapes; the
        // count is identical pre-fix, stash-verified).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R4Ctl : UdonSharpBehaviour {
    Func<int, int> g;
    public int n;
    public int result;
    void Start() {
        int zed = n * 10;
        g = k => k <= 0 ? zed : g(k - 1) + 1;
        result = g(n);
    }
}", "W9R4Ctl");
        int zedRefs = Regex.Matches(uasm, @"__lcl_zed_\w+").Count;
        Assert.Equal(3, zedRefs); // declaration store + lambda read; unchanged pre/post (no spill)
    }

    [Fact]
    public void LambdaCycleMember_WrittenCaptureCell_RejectsLoudly()
    {
        // Wave-9 round-5 [X3]/[X10]/[X11] re-pin (supersedes the round-4 flat-sharing pin): a
        // capture cell WRITTEN by a hoisted cycle member shares ONE flat slot across all live
        // activations, so the inner activation's writes bleed into the outer frame across live
        // re-entry (DiffFuzz ref=51 vs VM 21 / 126 vs 101 — the round-4 structural pin protected a
        // VM-proven-wrong behavior). Correct codegen needs a per-activation closure environment
        // (Stage-2), so the cycle+written combination is loud per design §8-3.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R4Written : UdonSharpBehaviour {
    Func<int, int> d;
    public int n;
    public int result;
    void Start() { d = M; result = M(n); }
    int M(int m) {
        if (m <= 0) return 0;
        int acc = m * 10;
        Func<int, int> c = k => { acc = acc + 1; return d(k) + acc; };
        return c(m - 1) + acc;
    }
}", "W9R4Written"));
        Assert.Contains("recursion cycle", ex.Message);
    }

    [Fact]
    public void NonCycleLambda_WrittenCaptureCell_KeepsFlatSharing()
    {
        // [X3] control: a written capture cell whose lambda is NOT in a recursion cycle keeps flat
        // sharing — the correct same-environment C# semantics when no live re-entry exists.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R5WrittenCtl : UdonSharpBehaviour {
    public int n;
    public int result;
    void Start() {
        int acc = n * 10;
        Func<int, int> c = k => { acc = acc + 1; return k + acc; };
        result = c(n) + acc;
    }
}", "W9R5WrittenCtl");
        Assert.Contains("__lcl_acc_", uasm);
    }

    // ── [X4]/[X5]/[X9] interface-receiver accessors ──

    [Fact]
    public void InterfaceReceiver_UserIndexerRead_DispatchesBridge()
    {
        // Pre-fix: UasmValidationException 'Unknown extern:
        // VRCUdonCommonInterfacesIUdonEventReceiver.__get_Item__SystemInt32__SystemInt32'.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IIdxM1 { int this[int i] { get; } }
public class X2Min1 : UdonSharpBehaviour, IIdxM1 {
    public int seed;
    public int result;
    public int this[int i] { get { return i * 10 + seed; } }
    void Start() {
        IIdxM1 h = this;
        result = h[seed];
    }
}", "X2Min1");
        Assert.DoesNotContain("__get_Item", uasm);
        Assert.Contains("__iface_IIdxM1_", uasm);
    }

    [Fact]
    public void InterfaceReceiver_UserIndexerWriteAndCompound_DispatchBridges()
    {
        // Write + compound + inc-dec legs (X4): all must route through the get/set bridges.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IM3 { int this[int i] { get; set; } }
public class MinIfaceIdxRw : UdonSharpBehaviour, IM3 {
    public int seed;
    public int result;
    private int[] data;
    public int this[int i] {
        get { return data[i] + 1; }
        set { data[i] = value; }
    }
    void Start() {
        data = new int[2];
        IM3 h = this;
        h[0] = seed;
        h[0] += 3;
        h[1] = seed;
        h[1]++;
        result = h[0] * 100 + h[1];
    }
}", "MinIfaceIdxRw");
        Assert.DoesNotContain("__get_Item", uasm);
        Assert.DoesNotContain("__set_Item", uasm);
        Assert.Contains("__iface_IM3_", uasm);
    }

    [Fact]
    public void InterfaceReceiver_PropertyCompoundWriteBack_DispatchesBridge()
    {
        // X5: the compound's GET leg already bridged; only the WRITE-BACK arm emitted the bogus
        // __set_P extern. Inc-dec rides the same write-back arm.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IM2 { int P { get; set; } }
public class MinIfacePropCompound : UdonSharpBehaviour, IM2 {
    public int seed;
    public int result;
    public int P { get; set; }
    void Start() {
        IM2 h = this;
        h.P = seed;
        h.P += 3;
        h.P++;
        result = h.P;
    }
}", "MinIfacePropCompound");
        Assert.DoesNotContain("__set_P", uasm);
        Assert.Contains("__iface_IM2_", uasm);
    }

    // ── [X6]/[X7]/[X8] named index arguments bind by parameter ordinal ──
    //
    // Pin shape: the named/reordered program must equal its declaration-order twin in UASM text
    // AND in the constant table. The text alone is NOT discriminating on every path: constant
    // heap slots are value-interned and named in first-use instruction order, so a shape with a
    // single accessor site and no literal reuse (the base[...] write) emits IDENTICAL text for
    // both orderings pre-fix — the row/col swap lives only in which VALUE lands in which
    // __const slot (applied out-of-band; CZ38 Mismatches on the real VM while the text matches).
    // Comparing the (Id, UdonType, Value) table closes that blindness; stash-verified red
    // pre-fix on all three twins.

    static string ConstTable(System.Collections.Generic.List<(string Id, string UdonType, object Value)> consts)
        => string.Join("\n", consts.ConvertAll(c => $"{c.Id}|{c.UdonType}|{c.Value}"));

    const string ThisIdxTemplate = @"
using System;
using UdonSharp;
public class N10F : UdonSharpBehaviour {{
    int[] grid;
    public int seed;
    public int c5; public int c7;
    public int this[int row, int col] {{
        get {{ return grid[row * 3 + col]; }}
        set {{ grid[row * 3 + col] = value; }}
    }}
    void Start() {{
        grid = new int[9];
        this[{0}] = seed + 5;
        c5 = c5 + this[{0}];
        this[{0}] += 2;
        c7 = grid[7];
    }}
}}";

    [Fact]
    public void ThisIndexer_NamedIndexArgs_EmitSameUasmAsDeclarationOrder()
    {
        // Read, write, AND compound legs in one shape: the named/reordered program must be
        // byte-identical to its declaration-order twin (textual evaluation, ordinal slotting —
        // the CrossCallArgPairs convention). Pre-fix the named form bound row=2,col=1
        // (DiffFuzz ref c5=11/c7=0 vs usugar c5=0/c7=11).
        var (reordered, reorderedConsts) = TestHelper.CompileWithConsts(string.Format(ThisIdxTemplate, "col: 2, row: 1"), "N10F");
        var (declOrder, declOrderConsts) = TestHelper.CompileWithConsts(string.Format(ThisIdxTemplate, "row: 1, col: 2"), "N10F");
        Assert.Equal(declOrder, reordered);
        Assert.Equal(ConstTable(declOrderConsts), ConstTable(reorderedConsts));
    }

    const string StructIdxTemplate = @"
using System;
using UdonSharp;
public struct SIdx {{
    public int p;
    public int q;
    public int this[int row, int col] {{
        get {{ return row * 10 + col + p; }}
        set {{ p = row; q = col + value; }}
    }}
}}
public class N10S1 : UdonSharpBehaviour {{
    public int seed;
    public int rp; public int rq;
    void Start() {{
        var s = new SIdx();
        s[{0}] = seed + 1;
        rp = s.p + s[{0}];
        s[{0}] += 2;
        rq = s.q;
    }}
}}";

    [Fact]
    public void StructIndexer_NamedIndexArgs_EmitSameUasmAsDeclarationOrder()
    {
        var (reordered, reorderedConsts) = TestHelper.CompileWithConsts(string.Format(StructIdxTemplate, "col: 2, row: 1"), "N10S1");
        var (declOrder, declOrderConsts) = TestHelper.CompileWithConsts(string.Format(StructIdxTemplate, "row: 1, col: 2"), "N10S1");
        Assert.Equal(declOrder, reordered);
        Assert.Equal(ConstTable(declOrderConsts), ConstTable(reorderedConsts));
    }

    const string BaseIdxTemplate = @"
using System;
using UdonSharp;
public class N10BWBase : UdonSharpBehaviour {{
    protected int[] grid;
    public virtual int this[int row, int col] {{
        get {{ return grid[row * 3 + col]; }}
        set {{ grid[row * 3 + col] = value; }}
    }}
}}
public class N10BW : N10BWBase {{
    public int seed;
    public int c5; public int c7;
    public override int this[int row, int col] {{
        get {{ return 0; }}
        set {{ }}
    }}
    void Start() {{
        grid = new int[9];
        base[{0}] = seed + 5;
        c5 = grid[5]; c7 = grid[7];
    }}
}}";

    [Fact]
    public void BaseIndexer_NamedIndexArgs_EmitSameUasmAsDeclarationOrder()
    {
        // base[...] keeps the static binding (base-instance copy accessor) and rides the same
        // this-path setter arm. This shape is the one that PROVES the const-table comparison is
        // load-bearing: with a single accessor site and no literal reuse the pre-fix TEXT is
        // already byte-identical between the orderings — only the const table (row slot fed the
        // value 2 instead of 1) is red pre-fix.
        var (reordered, reorderedConsts) = TestHelper.CompileWithConsts(string.Format(BaseIdxTemplate, "col: 2, row: 1"), "N10BW");
        var (declOrder, declOrderConsts) = TestHelper.CompileWithConsts(string.Format(BaseIdxTemplate, "row: 1, col: 2"), "N10BW");
        Assert.Equal(declOrder, reordered);
        Assert.Equal(ConstTable(declOrderConsts), ConstTable(reorderedConsts));
    }
}
