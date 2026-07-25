using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 6 — tracked pins for the VM-proven findings (DiffFuzz refs cited per test;
/// real-VM value pins live in the local harness round-6 corpora fcd_wave9_*_r6.json and the
/// minimized _w9r6_*_min / _w9n_r6_min files):
///
/// [X1]  STATEMENT-form tail calls (a void call as the LAST statement executed before the implicit
///       return — direct named call, delegate dispatch, or property-setter write) were classified
///       NON-tail by both tail classifiers (LoweringState.HasNonTailSelfCall recognized only
///       IReturnOperation tails, and so did the duplicated UasmEmitter.HasNonTailCallTo), so every
///       frame spilled and deep legal recursion overflowed the 512-entry __recurStack
///       (compile-clean VmFault at depth &gt;512 self / &gt;~256 mutual; ref completes on the CLR).
///       Both walkers now thread a tail flag through method bodies, blocks (last statement),
///       statement-form if/else branches, and expression statements; `Callee(args);` and
///       `P = v;` / `this[i] = v;` tail statements are spared like `return Callee(args);`.
///       Calls carrying ref/out arguments are NOT spared — the copy-back reads the param heap
///       vars after the call, and the classification gates the [Q2] re-chained-ref reject.
/// [X2]-[X6] Deconstruction into property/indexer ([X2]-[X5]) and ARRAY-ELEMENT ([X6]) lvalues
///       evaluated the target's receiver/index legs at STORE time — AFTER the RHS — while C#
///       evaluates every target's component expressions left-to-right BEFORE the RHS (wrong-cell
///       writes when the legs read state the RHS mutates: ref=806 vs 86; leg-order traces
///       ref=12 vs 21, 352 vs 532, 752 vs 572). EmitPropertySet is split into PreparePropertySet
///       (legs now) + a deferred store; DeconstructionAssignmentHandler prepares every target's
///       legs (nested tuples included) before the RHS on ALL its arms and feeds the cached
///       stores through AssignToLValue.
/// </summary>
public class Wave9Round6RegressionTests
{
    // ── [X1] statement-form tail calls ──

    [Fact]
    public void SelfRecursion_DirectCallAsLastStatement_DoesNotSpill()
    {
        // W9R6MinST_SelfStmtTail (VmFault at 600 pre-fix; DiffFuzz ref result=10305): the void
        // self-call as the LAST statement reads nothing of its frame afterwards — tail, no spill.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6StmtTail : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(600 + n % 6); result = (trace + 11) % 99991; }
    public void M(int m) {
        if (m <= 0) { trace = (trace * 3 + 7) % 99991; return; }
        trace = (trace * 5 + m % 11) % 99991;
        M(m - 1);
    }
}", "W9R6StmtTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void SelfRecursion_DelegateDispatchAsLastStatement_DoesNotSpill()
    {
        // W9R6MinST_SelfDlgTail flavor: the per-site Reentrant marking (IsNonTailDispatchSite)
        // must spare the statement-form tail dispatch like the return-form one (§4.4).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R6DlgTail : UdonSharpBehaviour {
    Action<int> da;
    public int n; public int trace; public int result;
    void Start() { da = M; M(600 + n % 6); result = (trace + 11) % 99991; }
    public void M(int m) {
        if (m <= 0) { trace = (trace * 3 + 7) % 99991; return; }
        trace = (trace * 5 + m % 11) % 99991;
        da(m - 1);
    }
}", "W9R6DlgTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void SetterCycle_SetterWriteAndCallAsLastStatements_DoesNotSpill()
    {
        // W9R6MinST_NoDelegate (VmFault at 330 pre-fix): Step's last statement is the setter
        // write `P = k;` (accessor-edge tail), the setter's last statement is the direct call
        // Step(value - 1) — an all-tail mutual cycle needs no spill machinery at all.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6SetterTail : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { P = 330 + n % 6; result = (trace + 11) % 99991; }
    public int P {
        set {
            if (value <= 0) { trace = (trace * 3 + 7) % 99991; return; }
            trace = (trace * 5 + value % 11) % 99991;
            Step(value - 1);
        }
    }
    public void Step(int k) { P = k; }
}", "W9R6SetterTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void SelfRecursion_StatementCallInTailIfBranch_DoesNotSpill()
    {
        // The if/else branches of a tail-position statement if stay tail (the twin of the
        // expression-form conditional rule).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6IfTail : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(40 + n % 6); result = trace % 99991; }
    public void M(int m) {
        trace = (trace * 5 + m % 11) % 99991;
        if (m > 0) M(m - 1);
    }
}", "W9R6IfTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void SelfRecursion_CallNotLastStatement_StillSpills()
    {
        // Over-sparing control: a statement-form self-call followed by ANOTHER statement reads
        // frame state after the call — must stay classified non-tail and spill.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6NonLastCtl : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(5 + n % 6); result = trace % 99991; }
    public void M(int m) {
        if (m <= 0) return;
        M(m - 1);
        trace = (trace + m) % 99991;
    }
}", "W9R6NonLastCtl");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void SelfRecursion_CallAsLastStatementOfLoopBody_StillSpills()
    {
        // A loop body's last statement is NOT tail (the back-edge re-reads the condition's frame
        // state after the call) — the tail flag must not propagate through loops.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6LoopCtl : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(4 + n % 3); result = trace % 99991; }
    public void M(int m) {
        while (m > 0) {
            m = m - 1;
            M(m);
        }
        trace = trace + 1;
    }
}", "W9R6LoopCtl");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void SelfRecursion_TailStatementCallWithSelfThreadedRef_StaysNonTail()
    {
        // A statement-form tail call carrying ref/out arguments is NOT spared: the by-ordinal
        // copy-back reads the param heap vars AFTER the call. Self-threaded refs stay legal
        // (the supported convention) but the edge keeps the spill machinery.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6RefTailCtl : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { int a = n; M(5, ref a); result = a; }
    public void M(int m, ref int x) {
        if (m <= 0) return;
        x = x + m;
        M(m - 1, ref x);
    }
}", "W9R6RefTailCtl");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void SelfRecursion_TailStatementCallWithRechainedRef_StillRejectsLoudly()
    {
        // [Q2] interaction guard: the re-chained-ref reject keys on IsRecursiveEdge, which the
        // tail classification feeds. A statement-form tail call passing a DIFFERENT lvalue must
        // stay on the recursive edge and keep rejecting (sparing it would silently corrupt).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R6RefRechain : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { int a = n; M(5, ref a); result = a; }
    public void M(int m, ref int x) {
        if (m <= 0) return;
        int z = m;
        M(m - 1, ref z);
    }
}", "W9R6RefRechain"));
        Assert.Contains("lvalue other than the same parameter", ex.Message);
    }

    // ── [X2]-[X6] deconstruction target legs evaluate before the RHS ──

    [Fact]
    public void DeconIntoIndexer_TupleLiteralRhs_IndexLegEvaluatesBeforeRhs()
    {
        // W9R6MinDeconIdxLit (ref trace=12 vs 21): C# evaluates the target's index leg (Abs)
        // BEFORE the RHS element (Max). Each extern appears exactly once in the code section.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R6DeconLit : UdonSharpBehaviour {
    int[] store;
    public int seed; public int r1;
    public int this[int i] { get { return store[i]; } set { store[i] = value; } }
    void Start() {
        store = new int[3];
        int b;
        (this[Math.Abs(seed) % 3], b) = (Math.Max(seed, 2), 3);
        r1 = store[0] * 10 + b;
    }
}", "W9R6DeconLit");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
    }

    [Fact]
    public void DeconIntoIndexer_MethodCallRhs_IndexLegEvaluatesBeforeCallResultReads()
    {
        // W9R6MinDeconIdxCall flavor (ref trace=12 vs 21): the non-tuple-literal arm prepares
        // the legs before invoking the tuple-returning callee — the index extern (Abs) must
        // precede the first return-tuple element read (SystemObjectArray.__Get).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R6DeconCall : UdonSharpBehaviour {
    int[] store;
    public int seed; public int r1;
    public int this[int i] { get { return store[i]; } set { store[i] = value; } }
    (int, int) Pair() { return (seed + 5, 3); }
    void Start() {
        store = new int[3];
        int b;
        (this[Math.Abs(seed) % 3], b) = Pair();
        r1 = store[0] * 10 + b;
    }
}", "W9R6DeconCall");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemObjectArray.__Get__SystemInt32__SystemObject");
    }

    [Fact]
    public void DeconIntoArrayElement_IndexLegEvaluatesBeforeRhs()
    {
        // W9R6Min6 ([X6], ref result=806 vs 86): the pre-existing array-element arm shares the
        // same family — its array/index legs must also evaluate before the RHS.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R6DeconArr : UdonSharpBehaviour {
    public int seed; public int r1;
    void Start() {
        int[] a = new int[4];
        int b;
        (a[Math.Abs(seed) % 4], b) = (Math.Max(seed, 2), 6);
        r1 = a[1] * 100 + a[2] * 10 + b;
    }
}", "W9R6DeconArr");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
    }

    [Fact]
    public void DeconIntoNestedTupleTarget_IndexLegEvaluatesBeforeRhs()
    {
        // FP6B4 nested flavor (log ref=456723 vs 567423): the leg pre-pass walks NESTED target
        // tuples too — the inner indexer target's leg (Abs) still precedes the RHS (Max).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R6DeconNested : UdonSharpBehaviour {
    int[] store;
    public int seed; public int r1;
    public int this[int i] { get { return store[i]; } set { store[i] = value; } }
    void Start() {
        store = new int[3];
        int b; int c;
        ((this[Math.Abs(seed) % 3], b), c) = ((Math.Max(seed, 1), 2), 3);
        r1 = store[0] * 100 + b * 10 + c;
    }
}", "W9R6DeconNested");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
    }

    [Fact]
    public void SimpleIndexerSet_ReceiverArgsValueOrder_Unchanged()
    {
        // Control (round-5 [X2] order pin twin): the single-assignment path through the split
        // PreparePropertySet + deferred store keeps receiver → index args → value.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R6SimpleSetCtl : UdonSharpBehaviour {
    int[] grid;
    public int seed; public int result;
    int this[int i] { get { return grid[i]; } set { grid[i] = value; } }
    void Start() {
        grid = new int[3];
        this[Math.Abs(seed) % 3] = Math.Max(seed, 2);
        result = grid[0] + grid[1] * 10 + grid[2] * 100;
    }
}", "W9R6SimpleSetCtl");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
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
