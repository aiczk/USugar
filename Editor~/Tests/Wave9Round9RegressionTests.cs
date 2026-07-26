using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 9 — tracked pins for the VM-proven findings (real-VM value pins live in the
/// local harness round-9 corpora fcd_wave9_*_r9.json and the minimized _w10cap/_w9rec/_w10g/
/// _w10i/_w10cf/_w10fp _r9_min/probe files):
///
/// [Y1]/[Y2]/[Y13]/[Y17] A self-call as a switch ARM's last statement (before the implicit break)
///       was not classified tail by either classifier (no ISwitchOperation arm) — per-frame spill,
///       VmFault at depth ~600 on legal C#. Arms cannot fall through, so an arm's trailing
///       statement is exactly as tail as the switch itself.
/// [Y14] A LABELED last statement (`TAIL: Step(k-1);`) hid the tail call behind the
///       ILabeledOperation wrapper — same per-frame spill fault.
/// [Y3]  ONE non-tail self-call site put the callee in RecursiveCalleeNames and EVERY site of that
///       callee spilled (per-callee gating) — mixed tail/non-tail recursion overflowed the stack
///       at depth while the dispatch arm (per-site marking) survived the identical shape. Fixed:
///       BuildRecursionInfo records tail direct sites syntax-keyed (TailSparedDirectCallSites) and
///       EmitCallToMethod flags those instructions TailSpared so InsertRecursionSpills skips them.
/// [Y4]  Dispatch sites were marked Reentrant whenever the containing function was on a synthetic
///       SCC — even when NO escaped function can reach back into that SCC, so the spill/reload
///       around a harmless lambda dispatch discarded the lambda's write to a captured cell
///       (VM-proven ref=6 vs 1 at depth 1). Fixed: the marking is gated on escape-reachability
///       (some escaped function must reach the SCC for re-entry to exist).
/// [Y5]  Non-tail self-recursion in a BASE-declared generic called open-T never fed
///       BuildRecursionInfo (the on-demand spec family has no eager registration) — no graph node,
///       no self-edge, no spills; live locals clobbered per frame (VM-proven rA ref=234 vs 63).
/// [Y6]  using/Dispose inside a base generic open-T body ICEd ("No FlatFunction registered for
///       method 'Dispose'") — the open definition never seeded the struct/foreign collectors.
/// [Y7]  A delegate from an INHERITED generic method group with concrete type args rejected loudly
///       on legal C# — the creation gate only accepted the compiled class as the declaring type.
///       (Restoration pin lives in Wave9Round2RegressionTests.GenericMethodGroup_InheritedFromBase_Compiles.)
/// [Y8]  A capture-free local function with OWN type params ICEd on a single legal instantiation
///       ("expected 'SystemInt32', got 'T2'"): registering the DEFINITION declared raw-T heap vars,
///       and an LF's TYPE PARAMETER symbols do not compare equal across operation-tree walks (the
///       LF method symbol does), so the call-site-keyed spec map missed every body-walk reference.
///       Fixed: generic LFs register per call site like generic methods, and EmitMethod re-keys
///       the spec map with the body symbol's own type parameters.
/// [Y9]  A non-generic local function called BEFORE its declaration statement died loudly
///       ("Method not found in layout") — registration only happened at the declaration statement.
/// [Y10] A T-signature local function inside a BASE-declared generic registered as an eager
///       phase-1 base-instance COPY (IsBaseInstanceMethod had no MethodKind filter) with raw 'T'
///       param/return heap vars — CReturn ICE on a single legal instantiation.
/// [Y11]/[Y15] A capturing lambda stored through a VARIABLE receiver (own-class local `v = this`,
///       base-typed local, behaviour field `other.cb`, tuple-decon legs alike) compiled clean —
///       the receiver's runtime identity defeats the per-class read-loudness pre-scan. Loud now;
///       `this`-receiver stores stay the legal surface.
/// [Y12] Ref/out copy-back through a BEHAVIOUR-array-element FIELD lvalue (hs[Pick()].pub)
///       re-evaluated the receiver legs after the call (Pick() ran twice, the write landed in the
///       cell chosen by the SECOND evaluation). Fixed: a TryPrepareRefOutArg behaviour-field arm
///       caches the receiver once (Get/SetProgramVariable through the same reference).
/// [Y16] Ref/out copy-in read the VALUE at argument-evaluation position instead of call time —
///       C# passes the LOCATION and the callee reads it at call time, so a LATER argument's write
///       (F(ref arr[0], Wr(arr))) was invisible (VM-proven c0 ref=55 vs 6; plain element,
///       struct-array-element field leaf, and behaviour this-field flavors, one root). Fixed: the
///       value read defers past later effectful arguments; side-effect-free shapes byte-identical.
/// Also: an override threading its OWN ref param into the base copy (`base.M(n, ref z)`) tripped
///       the [Q2] re-chain reject although it is the same linear copy-in/copy-back identity chain
///       as self-threading — the guard now accepts the caller's own param at the same ordinal and
///       RefKind on cross-method cycle edges.
/// </summary>
public class Wave9Round9RegressionTests
{
    static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ── [Y1]/[Y2]/[Y13]/[Y17] switch-arm tail self-call ──

    [Fact]
    public void SwitchArmTailSelfCall_NoSpill()
    {
        // MinSwitchTail600 / W10Cap9MinSwitchTail600 (VmFault at depth ~600 pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9SwitchTail : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(n % 3 + 600); result = trace; }
    public void M(int m) {
        trace = (trace + m) % 99991;
        switch (m & 1) {
            case 0:
                if (m > 0) M(m - 1);
                break;
            default:
                if (m > 0) M(m - 1);
                break;
        }
    }
}", "W9R9SwitchTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void SwitchNotLastStatement_StillSpills()
    {
        // Control: frame state is read AFTER the switch — every arm call stays non-tail.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9SwitchCtl : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(n % 3 + 6); result = trace; }
    public void M(int m) {
        switch (m & 1) {
            default:
                if (m > 0) M(m - 1);
                break;
        }
        trace = (trace + m) % 99991;
    }
}", "W9R9SwitchCtl");
        Assert.Contains("__recurStack", uasm);
    }

    // ── [Y14] labeled tail statement ──

    [Fact]
    public void LabeledTailSelfCall_NoSpill()
    {
        // W10CF9MinLabeledTail (VmFault at depth ~600 pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9LabeledTail : UdonSharpBehaviour {
    public int n; public int acc; public int total;
    void Start() { Step(n + 600); total = acc; }
    public void Step(int k) {
        if (k <= 0) { acc = acc + 3; return; }
        acc = (acc + k % 7 + 1) % 9973;
        if (k % 4 == 0) goto TAIL;
        acc = (acc + 1) % 9973;
    TAIL:
        Step(k - 1);
    }
}", "W9R9LabeledTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [Y3] mixed tail/non-tail self-recursion: per-SITE sparing ──

    [Fact]
    public void MixedTailNonTail_TailSiteSpared()
    {
        // MinMixedTail600: one branch's call is tail, the other reads frame state after its call.
        // Pre-fix the per-callee gate spilled BOTH sites; the tail site is now TailSpared, so the
        // mixed shape carries strictly fewer __recurStack touches than the both-non-tail twin.
        var mixed = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9Mixed : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() { M(n % 3 + 600); result = acc; }
    public void M(int m) {
        if (m > 80) { M(m - 1); }
        else if (m > 0) { int a = (m * 7 + 3) % 101; M(m - 1); acc = (acc * 2 + a + m) % 99991; }
    }
}", "W9R9Mixed");
        var bothNonTail = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9Mixed2 : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() { M(n % 3 + 600); result = acc; }
    public void M(int m) {
        if (m > 80) { M(m - 1); acc = acc + 1; }
        else if (m > 0) { int a = (m * 7 + 3) % 101; M(m - 1); acc = (acc * 2 + a + m) % 99991; }
    }
}", "W9R9Mixed2");
        Assert.Contains("__recurStack", mixed);     // the non-tail site still spills
        Assert.True(Count(mixed, "__recurStack") < Count(bothNonTail, "__recurStack"),
            "tail site must not contribute spill traffic");
    }

    // ── [Y4] dispatch-site Reentrant marking gated on escape-reachability ──

    [Fact]
    public void NonReenterableDispatch_NoSpillAroundDispatch()
    {
        // ProbeWriteCellDepth1 / MinWriteCellRecursive: bump() cannot re-enter M's SCC (the lambda
        // never calls back), so the dispatch site must not be wrapped — pre-fix the reload over
        // the lambda's captured-cell write discarded it (VM ref=6 vs 1). The self-call still
        // spills, so the REENTERABLE twin (lambda calls M) carries strictly more spill traffic.
        var nonReenterable = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R9Dispatch : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() { M(n + 2); result = acc; }
    public void M(int m) {
        if (m <= 0) return;
        int z = m * 2;
        Action bump = () => { acc = acc + m; };
        bump();
        M(m - 1);
        acc = (acc * 3 + z) % 99991;
    }
}", "W9R9Dispatch");
        var reenterable = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R9Dispatch2 : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() { M(n + 2); result = acc; }
    public void M(int m) {
        if (m <= 0) return;
        int z = m * 2;
        Action bump = () => { acc = acc + m; if (m > 90) M(m - 9); };
        bump();
        M(m - 1);
        acc = (acc * 3 + z) % 99991;
    }
}", "W9R9Dispatch2");
        Assert.Contains("__recurStack", nonReenterable); // the direct self-call still spills
        Assert.True(Count(nonReenterable, "__recurStack") < Count(reenterable, "__recurStack"),
            "a dispatch that cannot re-enter the SCC must not be wrapped");
    }

    [Fact]
    public void WrittenCaptureCell_NonCycleLambda_StaysLegal()
    {
        // MinWriteCellRecursive's surface: a captured-cell WRITE inside a lambda that does NOT
        // share its declarer's SCC stays legal (the [X3] reject only owns true cycle members);
        // the dispatch no longer spills/reloads over the write (value pin: VM 116 vs 58 pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R9WriteCell : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() { M(n + 2); result = acc; }
    public void M(int m) {
        if (m <= 0) return;
        int z = 0;
        Action bump = () => { z = z + m; };
        bump();
        M(m - 1);
        acc = (acc * 3 + z + m) % 99991;
    }
}", "W9R9WriteCell");
        Assert.Contains("__recurStack", uasm); // the direct self-recursion still spills
    }

    // ── [Y5] inherited generic open-T self-recursion joins the recursion graph ──

    [Fact]
    public void InheritedGenericOpenT_SelfRecursion_Spills()
    {
        // MinInhGenSelfRec (VM-proven rA ref=234 vs 63 pre-fix: no spills at all).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9InhRecBase : UdonSharpBehaviour {
    public int NTRecB<T>(int n) { int keep = n * 3 + 1; if (n <= 0) { return 1; } int sub = NTRecB<T>(n - 1); return sub * 2 + keep; }
}
public class W9R9InhRec : W9R9InhRecBase {
    public int b; public int rA;
    void Start() { rA = Run<int>(b); }
    int Run<T>(int v) { return NTRecB<T>(v); }
}", "W9R9InhRec");
        Assert.Contains("__recurStack", uasm);
    }

    // ── [Y6] using/Dispose inside a base generic open-T body ──

    [Fact]
    public void UsingDispose_InBaseGenericOpenT_Compiles()
    {
        // MinUsingInhGenOpenT (ICE "No FlatFunction registered for method 'Dispose'" pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R9Res : IDisposable {
    public int n;
    public void Dispose() { n = 0; }
}
public class W9R9UsingBase : UdonSharpBehaviour {
    public int total;
    public void WorkB<T>(int v) {
        using (W9R9Res r = new W9R9Res()) { total = total + v * 2; }
        total = total + 1;
    }
}
public class W9R9Using : W9R9UsingBase {
    public int b; public int result;
    void Start() { Run<int>(); result = total; }
    void Run<T>() { WorkB<T>(b); }
}", "W9R9Using");
        Assert.Contains("Dispose", uasm);
    }

    // ── [Y8] generic local function with own type params ──

    [Fact]
    public void GenericLocalFunction_SingleInstantiation_Compiles()
    {
        // MinGenLfSingle (ICE "expected 'SystemInt32', got 'T2'" pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9GenLf : UdonSharpBehaviour {
    public int b; public int iOut;
    void Start() {
        iOut = Lf<int>(b + 1) * 10 + Lf<int>(b);
        T2 Lf<T2>(T2 v) { return v; }
    }
}", "W9R9GenLf");
        Assert.Contains("_Lf_SystemInt32", uasm); // the monomorphized spec body
    }

    [Fact]
    public void GenericLocalFunction_InsideGenericMethod_Compiles()
    {
        // MinGenLfInGeneric: the spec map (own T2) merges the enclosing generic's map (T).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9GenLfNest : UdonSharpBehaviour {
    public int b; public int iOut;
    void Start() { iOut = Run<int>(b); }
    int Run<T>(int v) {
        T2 Lf<T2>(T2 x) { return x; }
        return Lf<int>(v + 2);
    }
}", "W9R9GenLfNest");
        Assert.Contains("_Lf_SystemInt32", uasm);
    }

    // ── [Y9] forward-referenced local function ──

    [Fact]
    public void LocalFunction_CalledBeforeDeclaration_Compiles()
    {
        // MinLfAfter ("Method Lf not found in layout" pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9LfFwd : UdonSharpBehaviour {
    public int b; public int iOut;
    void Start() {
        iOut = Lf(b + 1);
        int Lf(int v) { return v + 1; }
    }
}", "W9R9LfFwd");
        Assert.Contains("_Lf", uasm);
    }

    // ── [Y10] T-signature local function inside a base-declared generic ──

    [Fact]
    public void TSignatureLocalFunction_InBaseGeneric_Compiles()
    {
        // MinT6_BaseTRetLfDirect (ICE "expected 'SystemInt32', got 'T'" pre-fix: the LF was
        // eagerly registered as a base-instance copy with raw 'T' heap vars).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9TLfBase : UdonSharpBehaviour {
    public T Pick<T>(T a, T b, int sel) {
        T Choose(T x, T y, int s) {
            if (s > 0) { return x; }
            return y;
        }
        return Choose(a, b, sel);
    }
}
public class W9R9TLf : W9R9TLfBase {
    public int seed; public int outv;
    void Start() { outv = Pick<int>(seed + 50, seed - 7, seed); }
}", "W9R9TLf");
        Assert.Contains("_Choose", uasm);
    }

    // ── [Y11]/[Y15] capturing store through a variable receiver ──

    const string CaptureStoreFragment = "capture local variables";
    [Fact]
    public void CapturingLambda_DirectOwnFieldStore_StaysLegal()
    {
        // MinE3_DirectStoreCtl: the `this`-receiver direct store is the legal surface.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R9DirectCtl : UdonSharpBehaviour {
    public int seed; public int result;
    Func<int, int> hook;
    void Start() {
        int k = seed + 1;
        hook = x => x + k;
        result = hook(seed);
    }
}", "W9R9DirectCtl");
        Assert.Contains("_lambda", uasm);
    }

    [Fact]
    public void CapturingLambda_OwnFieldTupleDecon_StaysLegal()
    {
        // W10CF9ProbeDirectTupleOwn (P4): implicit-this decon target leg stays legal.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R9OwnTup : UdonSharpBehaviour {
    public Func<int, int> cb;
    public int n; public int t; public int total;
    void Start() {
        int lv = n + 1;
        int tt = 0;
        (cb, tt) = (x => x + lv, 5);
        t = tt;
        total = cb(3) + t;
    }
}", "W9R9OwnTup");
        Assert.Contains("_lambda", uasm);
    }

    // ── override → base-copy ref param threading ──

    [Fact]
    public void OverrideThreadsOwnRefParamIntoBaseCopy_Compiles()
    {
        // MinR1_RefStmtThreadTwin: `base.M(n, ref z)` threads the override's OWN ref param —
        // the same linear copy-in/copy-back identity chain as self-threading (rejected pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9RefThreadBase : UdonSharpBehaviour {
    public int trace;
    public virtual void M(int n, ref int z) {
        trace = trace + 1;
        z = z + n;
        if (n > 0) { M(n - 1, ref z); }
    }
}
public class W9R9RefThread : W9R9RefThreadBase {
    public int seed; public int result;
    public override void M(int n, ref int z) {
        z = z + 1000;
        base.M(n, ref z);
    }
    void Start() {
        int z = seed;
        M(20, ref z);
        result = z;
    }
}", "W9R9RefThread");
        Assert.Contains("__recurStack", uasm); // the cycle still spills its non-ref state
    }

    [Fact]
    public void OverrideThreadsOwnRefParam_ReturnForm_BaseCopyCycle_Compiles()
    {
        // W10I08/W10I08B: the RETURN-form twin. The widened guard lets it compile (rejected
        // pre-round-9); the TCO gate now resolves virtual dispatch, so the base COPY's
        // `return M(n - 1, ref acc)` calls the LEAF override instead of goto-ing its own entry
        // (the short-circuit was VM-proven outv 363 vs CLR 528 at depth 30 — the value pin
        // lives in the inherit_r9 corpus; the leaf's own self-TCO shapes stay byte-identical).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9RefThreadRetBase : UdonSharpBehaviour {
    public virtual int M(int n, ref int acc) {
        acc = acc + 1;
        if (n <= 0) { return acc; }
        return M(n - 1, ref acc);
    }
}
public class W9R9RefThreadRet : W9R9RefThreadRetBase {
    public int seed; public int outv;
    public override int M(int n, ref int acc) {
        acc = acc + 2;
        if (n <= 0) { return acc; }
        return base.M(n - 1, ref acc);
    }
    void Start() {
        int z = seed;
        outv = M(30, ref z) * 10 + z;
    }
}", "W9R9RefThreadRet");
        Assert.NotNull(uasm);
    }

    // ── [Y12] behaviour-array-element field ref/out: receiver legs evaluate once ──

    [Fact]
    public void RefArg_BehaviourArrayElementField_LegsEvaluateOnce()
    {
        // W10CF9MinRefLeg: hs[Pick()].pub — pre-fix the copy-back re-ran the array Get (and
        // Pick()), so the element read extern appeared twice.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9RefLeg : UdonSharpBehaviour {
    W9R9RefLeg[] hs;
    int cur;
    public int pub; public int n; public int trace; public int total;
    void Start() {
        hs = new W9R9RefLeg[2];
        hs[0] = this;
        hs[1] = this;
        pub = n + 3;
        AddTo(ref hs[Pick()].pub, n + 4);
        total = pub * 1000 + trace;
    }
    public int Pick() { trace = trace * 10 + (cur % 2 + 1); int r = cur % 2; cur = cur + 1; return r; }
    public void AddTo(ref int p, int d) { trace = trace * 10 + 9; p = p + d; }
}", "W9R9RefLeg");
        Assert.Equal(1, Count(uasm, "UnityEngineComponentArray.__Get__"));
        Assert.Contains("__GetProgramVariable__", uasm); // read through the cached receiver
        Assert.Contains("__SetProgramVariable__", uasm); // copy-back through the SAME receiver
    }

    // ── [Y16] ref/out value read defers past later effectful arguments ──

    [Fact]
    public void RefArg_ValueRead_DefersPastLaterEffectfulArg()
    {
        // W10FP9MinRefStaleElem: F(ref arr[0], Wr(arr)) — the element read must be emitted AFTER
        // the later argument's call (C# reads the location at call time; VM-proven c0 ref=55 vs 6).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9RefStale : UdonSharpBehaviour {
    public int seed; public int c0;
    int Wr(int[] a) { a[0] = 50; return 5; }
    void F(ref int a, int w) { a = a + w; }
    void Start() {
        int[] arr = new int[1];
        arr[0] = seed + 1;
        F(ref arr[0], Wr(arr));
        c0 = arr[0];
    }
}", "W9R9RefStale");
        // Wr's body stores 50 into a[0] via SystemInt32Array.__Set; the FIRST element Get in
        // _start's CODE belongs to the deferred ref read and must follow the copy-in of Wr's
        // argument. Scope both probes past the "_start:" label — param vars are also DECLARED
        // in the .data section ("__N_a__param: %..."), which precedes all code and would make
        // a whole-text IndexOf compare vacuously true.
        int startLabel = uasm.IndexOf("_start:", StringComparison.Ordinal);
        Assert.True(startLabel >= 0, "_start label missing");
        string startCode = uasm.Substring(startLabel);
        int firstGet = startCode.IndexOf("SystemInt32Array.__Get__", StringComparison.Ordinal);
        int wrParamStore = startCode.IndexOf("a__param", StringComparison.Ordinal);
        Assert.True(firstGet >= 0 && wrParamStore >= 0, "expected externs missing");
        Assert.True(firstGet > wrParamStore,
            "ref/out value read must be deferred past the later effectful argument");
    }

    [Fact]
    public void RefArg_NoLaterEffectfulArg_KeepsImmediateRead()
    {
        // Control (W10FP9MinRefStaleCtl flavor): a pure later argument keeps the read at the
        // argument position — the Get precedes the param stores, byte-identical to round 8.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R9RefStaleCtl : UdonSharpBehaviour {
    public int seed; public int c0;
    void F(ref int a, int w) { a = a + w; }
    void Start() {
        int[] arr = new int[1];
        arr[0] = seed + 1;
        F(ref arr[0], seed + 9);
        c0 = arr[0];
    }
}", "W9R9RefStaleCtl");
        Assert.Contains("SystemInt32Array.__Get__", uasm);
    }
}
