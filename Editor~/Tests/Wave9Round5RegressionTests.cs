using System;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 5 — tracked pins for the thirteen VM-proven findings (DiffFuzz refs cited per
/// test; real-VM value pins live in the local harness round-5 wave corpora fcd_wave9_*_r5.json and
/// the minimized _w9*_min files):
///
/// [X1]  Escape set recorded the STATIC binding for this-receiver virtual method-group conversions
///       in inherited base bodies — leaf-override delegate-dispatch cycles never spilled (ref=66
///       vs 6). CollectEscapedDelegateTargets now adds the leaf override definition
///       (LeafMethodRefTarget, gated like LeafCallTarget).
/// [X2]  User indexer SET evaluated the RHS BEFORE the receiver/index args (C#: receiver → args →
///       value). The whole property/indexer SET path is factored into HandlerBase.EmitPropertySet
///       with the C# order; the value arrives through a Func factory.
/// [X3]/[X10]/[X11] A capture cell WRITTEN by a hoisted recursion-cycle member kept flat sharing
///       across live re-entry (ref=51 vs 21 / 126 vs 101) — now a loud reject (per-activation
///       closure environments are Stage-2). Re-pin lives in Wave9Round4RegressionTests
///       (LambdaCycleMember_WrittenCaptureCell_RejectsLoudly + non-cycle control).
/// [X4]  Recursion spill temps were allocated AFTER CoalesceSlots and never merged — ~500 heap
///       symbols pushed past the SDK assembler's 512-entry UdonHeap (VmFault). InsertRecursionSpills
///       now interval-coalesces the fresh temps among themselves above a 64-temp threshold.
/// [X5]  using/Dispose (or any struct/foreign-static call) inside a BASE-INSTANCE COPY body ICEd
///       ("No CFunction registered for method 'Dispose'") — the collectors are now seeded with the
///       base copies' bodies too (registration order unchanged).
/// [X6]  Two distinct instantiations of a generic whose body contains a CAPTURING closure shared
///       one hoisted closure (capture cells seeded last-spec-wins; ref r1=8 vs 3) — loud reject;
///       single instantiations and capture-free closures stay legal.
/// [X7]  The aggregate local declaration reuse guard made the SECOND generic spec skip both
///       DeclareLocal and the object[] ctor — every spec-2 activation aliased spec-1's array
///       (ref r2=183 vs 126). The aggregate path now redeclares + reallocates like the scalar path.
/// [X8]  delegate .Equals with a delegate-typed PARAM argument loud-rejected: the erasing-channel
///       argument guard ran before the Equals arm. The delegate-Equals arms now run first.
/// [X9]  `T cur = v; …; return cur;` in a generic delegate-param fold loud-rejected legal C# —
///       the [J5] param-copy taint is now a WEAK tier (sealed at stores/arguments, legal at
///       returns), propagated through the copy-edge fixpoint with strong-dominates promotion.
/// [X12] Static object.Equals(f, g) on delegate bundles compared object[] REFERENCES (ref eq=1
///       vs 0) — now routed through the same (target, method) value comparison as `==`.
/// [X13] Deconstruction into ANY property/indexer lvalue rejected ("Unsupported l-value target:
///       PropertyReferenceOperation") — AssignToLValue now routes property targets through
///       EmitPropertySet.
/// </summary>
public class Wave9Round5RegressionTests
{
    // ── [X1] escape-set leaf override ──

    [Fact]
    public void EscapeSet_InheritedBaseBody_VirtualMethodGroupLeafOverride_JoinsCycleAndSpills()
    {
        // `hop = Step;` sits in the BASE body; the bridge resolves to the chain-root export whose
        // body is the LEAF override (W9WMin01.Step), which dispatches hop non-tail — a delegate
        // cycle (DiffFuzz ref=66 vs 6). Pre-fix the escape set held only the never-registered base
        // definition, so no synthetic edge existed and no spill machinery was emitted at all.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9WMin01Base : UdonSharpBehaviour {
    public Func<int, int> hop;
    public int Seed() { hop = Step; return hop(3); }
    public virtual int Step(int n) { return n; }
}
public class W9WMin01 : W9WMin01Base {
    public int k;
    public int r1;
    public override int Step(int n) {
        if (n <= 0) return k;
        int inner = hop(n - 1);
        return inner + n * 10;
    }
    void Start() { r1 = Seed(); }
}", "W9WMin01");
        Assert.Contains("__recurStack", uasm); // pre-fix: absent (no cycle detected; stash-verified)
    }

    // ── [X2] property/indexer SET evaluation order ──

    [Fact]
    public void UserIndexerSet_IndexArgsEvaluateBeforeValue()
    {
        // C# evaluates receiver → index args → value. Pre-fix the RHS extern (Max) was emitted
        // before the index-arg extern (Abs). Each extern appears exactly once in the code section.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R5EvalOrder : UdonSharpBehaviour {
    int[] grid = new int[3];
    public int seed;
    public int result;
    int this[int i] { get { return grid[i]; } set { grid[i] = value; } }
    void Start() {
        this[Math.Abs(seed) % 3] = Math.Max(seed, 2);
        result = grid[0] + grid[1] * 10 + grid[2] * 100;
    }
}", "W9R5EvalOrder");
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int idxPos = code.IndexOf("SystemMath.__Abs__SystemInt32__SystemInt32", StringComparison.Ordinal);
        int valPos = code.IndexOf("SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32", StringComparison.Ordinal);
        Assert.True(idxPos >= 0 && valPos >= 0, "expected both externs in the code section");
        Assert.True(idxPos < valPos, "index args must evaluate before the RHS value (C# order)");
    }
    // ── [X4] spill-temp coalescing under heap pressure ──

    [Fact]
    public void RecursionSpills_ManyLiveValues_SpillTempsCoalesce()
    {
        // The a14-bisect cliff shape (getter with 11 live locals + long, reentrant dispatch):
        // pre-fix ~500 data-section symbols (+24 extern strings > the SDK's 512-entry UdonHeap →
        // VmFault on load). Post-fix the fresh spill temps coalesce among themselves.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R5B14M5 : UdonSharpBehaviour {
    Func<int, int> d;
    int lvl;
    public int n;
    public int result;
    void Start() { lvl = 0; d = M; result = G; }
    public int G {
        get {
            int k = lvl;
            if (k <= 0) return 1;
            lvl = k - 1;
            int a = k + 1;
            int b = k * 2;
            int c = k * 3 + 1;
            int e = k % 5;
            int f2 = k % 7 + 2;
            int g2 = k * 4;
            int h2 = k - 3;
            int i2 = k * 5 + 2;
            int j2 = k % 11;
            int l2 = k * 6;
            long w = k * 13L;
            int r = d(k - 1);
            return (r + a + b + c + e + f2 + g2 + h2 + i2 + j2 + l2 + (int)(w % 17L)) % 99991;
        }
    }
    public int M(int m) {
        if (m <= 0) return 2;
        return (G + m % 3) % 99991;
    }
}", "W9R5B14M5");
        var data = uasm.Substring(0, uasm.IndexOf(".data_end", StringComparison.Ordinal));
        int symbols = Regex.Matches(data, @"^\s+\S+: %", RegexOptions.Multiline).Count;
        // Pre-fix: ~500 (stash-verified); the SDK heap holds 512 entries including extern strings.
        Assert.True(symbols < 400, $"expected coalesced spill temps to keep the heap small, got {symbols} symbols");
    }

    // ── [X5] using/Dispose inside a base-instance copy body ──

    [Fact]
    public void UsingDispose_InsideBaseInstanceCopyBody_Compiles()
    {
        // Pre-fix: "No CFunction registered for method 'Dispose'" ICE — the base copy's body was
        // never scanned by CollectStructMethods/CollectForeignStaticMethods.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R5Min03_UsingBaseCopyNoRec : W9R5Min03Base {
    public int n;
    public int result;
    void Start() { result = M(n % 3 + 1); }
    public override int M(int m) { return base.M(m) + 1; }
}
public class W9R5Min03Base : UdonSharpBehaviour {
    public int mark;
    public virtual int M(int m) {
        var res = new W9R5MinRes03(0);
        res.host = this;
        using (var u = res) { mark = mark + 2; }
        return mark + m;
    }
}
public struct W9R5MinRes03 : IDisposable {
    public W9R5Min03Base host;
    public int pad;
    public W9R5MinRes03(int p) { host = null; pad = p; }
    public void Dispose() { if (host != null) { host.mark = host.mark + 3; } }
}", "W9R5Min03_UsingBaseCopyNoRec");
        Assert.Contains("_Dispose", uasm);
    }

    // ── [X6] two-instantiation generic with a capturing closure ──

    [Fact]
    public void TwoInstantiation_GenericWithCapturingLambda_StaysLegal()
    {
        // Stage 2 §8.1 FLIP (was [X6] TwoInstantiation_...RejectsLoudly): the former Capturing tier is
        // retired. The lambda is a NON-type-param-dependent capture (`bias` is int, no T in its
        // signature or body), so it shares one T-free hoist and its capture lives in a per-activation
        // env record — the two instantiations no longer alias (VM-proven: fcd45a ri=9 AND rl=9 both
        // correct). Both specs compile and no flat `__lcl_bias_` cell exists (capture is in the env).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinA3 : UdonSharpBehaviour {
    public int k;
    public int r1;
    public int r2;
    void Start() {
        r1 = Gen<int>(k + 1);
        r2 = Gen<long>(k + 2);
    }
    int Gen<T>(int v) {
        int bias = v + k;
        Func<int, int> f = x => x + bias;
        return f(v);
    }
}", "MinA3");
        Assert.Contains("__1_Gen_SystemInt32", uasm);
        Assert.Contains("__2_Gen_SystemInt64", uasm);
        Assert.DoesNotContain("__lcl_bias_", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
    }

    [Fact]
    public void TwoInstantiation_GenericWithCaptureFreeLambda_StaysLegal()
    {
        // [X6] control: a capture-free closure has no capture cells to share — both specs
        // dispatch the same hoisted body correctly.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinA3Ctl : UdonSharpBehaviour {
    public int k;
    public int r1;
    public int r2;
    void Start() {
        r1 = Gen<int>(k + 1);
        r2 = Gen<long>(k + 2);
    }
    int Gen<T>(int v) {
        Func<int, int> f = x => x + 1;
        return f(v);
    }
}", "MinA3Ctl");
        Assert.Contains("__1_Gen_SystemInt32", uasm);
        Assert.Contains("__2_Gen_SystemInt64", uasm);
    }

    [Fact]
    public void SingleInstantiation_GenericWithCapturingLambda_StaysLegal()
    {
        // [X6] control (Stage-2 flip): a SINGLE instantiation of a generic whose body has a
        // capturing closure stays legal — ClosurePin.Capturing only rejects a SECOND distinct
        // instantiation. The captured `bias` now lives in a per-scope env record (Stage 2), so it
        // has no flat `__lcl_bias_` cell and an env-record ctor is emitted instead.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinA1Ctl : UdonSharpBehaviour {
    public int k;
    public int r1;
    void Start() { r1 = Gen<int>(k + 1); }
    int Gen<T>(int v) {
        int bias = v + k;
        Func<int, int> f = x => x + bias;
        return f(v);
    }
}", "MinA1Ctl");
        Assert.DoesNotContain("__lcl_bias_", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
    }

    // ── [X7] per-spec aggregate local allocation ──

    [Fact]
    public void TwoInstantiation_GenericStructLocal_AllocatesPerSpec()
    {
        // MinC3: pre-fix the second spec's prologue had NO SystemObjectArray ctor (the reuse guard
        // skipped DeclareLocal + allocation), so every spec-2 recursion frame aliased spec-1's one
        // array (DiffFuzz r2=183 vs 126). Post-fix each spec allocates its own frame array.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinC3 : UdonSharpBehaviour {
    public int k;
    public int n;
    public int r1;
    public int r2;
    void Start() {
        r1 = Walk<int>(n);
        r2 = Walk<long>(n + 1);
    }
    int Walk<T>(int m) {
        FrameC3 s = new FrameC3();
        s.v = m + k;
        if (m <= 0) return s.v;
        int below = Walk<T>(m - 1);
        return s.v + below * 2;
    }
}
public struct FrameC3 { public int v; }", "MinC3");
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int ctors = Regex.Matches(code, Regex.Escape("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray")).Count;
        Assert.Equal(2, ctors); // pre-fix: 1 (spec 2 reused spec 1's array; stash-verified)
    }

    // ── [X8] delegate .Equals with a delegate-typed param argument ──

    [Fact]
    public void DelegateEquals_DelegateParamArgument_Compiles()
    {
        // MinD1: pre-fix GuardCaptureEscapeArguments rejected the call before the Equals arm ran
        // (Equals' System.Object parameter is erasing-typed). The operands are consumed by the
        // value comparison, never laundered — legal.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinD1 : UdonSharpBehaviour {
    public int k;
    public int result;
    void Start() { result = Check(AddK, AddK) * 10 + Check(AddK, Dbl) + k; }
    int Check(Func<int, int> a, Func<int, int> b) { return a.Equals(b) ? 1 : 0; }
    public int AddK(int x) { return x + k; }
    public int Dbl(int x) { return x * 2; }
}", "MinD1");
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    [Fact]
    public void DelegateEquals_NonDelegateArgument_StaysLoud()
    {
        // [X8] control: the round-4 loud surface is unchanged — only the guard ORDER moved.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R5EqLoud : UdonSharpBehaviour {
    Func<int, int> f;
    public int result;
    public int Inc(int x) { return x + 1; }
    void Start() { f = Inc; result = f.Equals(3) ? 1 : 0; }
}", "W9R5EqLoud"));
        Assert.Contains("non-delegate argument", ex.Message);
    }

    // ── [X9] weak param-copy taint tier ──

    [Fact]
    public void GenericFold_ParamCopyLocalReturn_StaysLegal()
    {
        // MinF1's Fold: `T cur = v; … return cur;` — the [J5] strong taint loud-rejected this
        // legal generic fold idiom at the RETURN guard. The param-copy tier keeps the local sealed
        // at stores/arguments but legal at returns.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinF1Fold : UdonSharpBehaviour {
    public int k;
    public int result;
    public long result2;
    void Start() {
        Func<int, int> f = AddK;
        result = Fold(f, k + 1, 2);
        result2 = Fold(MulL, k * 10L, 3);
    }
    T Fold<T>(Func<T, T> fn, T v, int times) {
        T cur = v;
        for (int j = 0; j < times; j++) { cur = fn(cur); }
        return cur;
    }
    public int AddK(int x) { return x + k; }
    public long MulL(long x) { return x * 2 + k; }
}", "MinF1Fold");
        Assert.Contains("__lcl_cur_", uasm);
    }
    // ── [X12] static object.Equals on delegate bundles ──

    [Fact]
    public void StaticObjectEquals_DelegateBundles_UsesValueEquality()
    {
        // DB01: pre-fix this fell through to the SystemObject.__Equals extern, comparing the two
        // bundle object[] REFERENCES (DiffFuzz ref eqSame=1 vs 0). Now it goes through the same
        // (target, method) comparison as `==`.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class DB01Min : UdonSharpBehaviour {
    Func<int, int> f;
    Func<int, int> g;
    public int n;
    public int eqSame;
    public int Inc(int x) { return x + n; }
    void Start() {
        f = Inc;
        g = Inc;
        eqSame = Equals(f, g) ? 1 : 0;
    }
}", "DB01Min");
        Assert.DoesNotContain("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean", uasm);
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    [Fact]
    public void StaticObjectEquals_NonDelegateOperands_KeepExternPath()
    {
        // [X12] control: object.Equals on non-delegate operands keeps the extern comparison.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R5ObjEqCtl : UdonSharpBehaviour {
    public object a;
    public object b;
    public int result;
    void Start() { result = Equals(a, b) ? 1 : 0; }
}", "W9R5ObjEqCtl");
        Assert.Contains("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean", uasm);
    }

    // ── [X13] deconstruction into property/indexer lvalues ──

    [Fact]
    public void Deconstruction_IntoInterfaceIndexerLValue_Compiles()
    {
        // DB03: pre-fix every property/indexer deconstruction target hit AssignToLValue's default
        // reject ("Unsupported l-value target: PropertyReferenceOperation") on legal C#.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IB3 { int this[int i] { get; set; } }
public class DB03Min : UdonSharpBehaviour, IB3 {
    DB03Min other;
    int[] d;
    public int n;
    public int total;
    public int this[int i] { get { return d[i]; } set { d[i] = value; } }
    void Start() {
        other = this;
        d = new int[4];
        IB3 h = this;
        int b;
        (h[1], b) = other.Pair(q: n, p: n + 5);
        total = d[1] * 100 + b;
    }
    public (int, int) Pair(int p, int q) { return (p * 10 + q, p - q); }
}", "DB03Min");
        Assert.Contains("__iface_IB3_", uasm);
    }

    [Fact]
    public void Deconstruction_IntoAutoPropertyAndThisIndexer_Compiles()
    {
        // [X13] this-receiver flavors: auto-property + this-indexer targets ride the same
        // EmitPropertySet arm set as simple assignment.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R5DeconProp : UdonSharpBehaviour {
    int[] grid = new int[3];
    public int n;
    public int result;
    public int P { get; set; }
    int this[int i] { get { return grid[i]; } set { grid[i] = value; } }
    void Start() {
        (P, this[1]) = (n + 1, n + 2);
        result = P * 10 + grid[1];
    }
}", "W9R5DeconProp");
        Assert.Contains("P: %SystemInt32", uasm);   // auto-prop backing store written directly
        Assert.Contains("_set_Item", uasm);         // this-indexer routed to the internal setter
    }
}
