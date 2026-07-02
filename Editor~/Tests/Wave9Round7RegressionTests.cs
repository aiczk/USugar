using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 7 — tracked pins for the VM-proven findings (DiffFuzz refs cited per test;
/// real-VM value pins live in the local harness round-7 corpora fcd_wave9_*_r7.json and the
/// minimized _w9r7_* / _w9p_r7_* / _w10*_r7_* files):
///
/// [Y1]  `_ = expr;` discard assignment rejected loudly ("Unsupported simple assignment target:
///       DiscardOperation") on legal C# — general gap, not delegate-specific (the RHS side effect
///       must run; CLR ref trace=3). Fixed: SimpleAssignmentHandler evaluates the RHS and drops
///       the value (a discard has no storage, so no escape channel opens).
/// [Y2]/[Y4]/[Y6]/[Y8]/[Y10] FIELD lvalues whose receiver carries legs (struct-ARRAY-element
///       receivers `arr[idx].v`, member chains) evaluated those legs at STORE time — AFTER the
///       RHS — on BOTH the simple-assignment arm (ref=701 vs 71) and every deconstruction arm
///       (ref=702 vs 72, 1402 vs 72, wrong-cell writes). Fixed: TryPrepareFieldSet (the field
///       twin of PreparePropertySet) evaluates legs in C# order; the simple-assignment arm keeps
///       the legacy value-first order ONLY when both sides are emission-order inert (pure
///       reads/operators — unobservable; pins struct_ref_param sentinel bytes).
/// [Y3]  A closure inside a GENERIC method capturing the generic method's PARAMETER ICEd on a
///       single legal instantiation ("Cannot resolve parameter 'n' … in method ''") — the capture
///       walk binds the DEFINITION's parameter symbol while the param heap vars live under the
///       monomorphized spec. Fixed: GetParamVarId resolves through FirstGenericSpec (exact —
///       capturing closures pin their generic to one instantiation per the round-5 [X6] reject).
/// [Y5]  `var (a,b) = P2&lt;T&gt;(x)` inside a generic body failed loud ("Method P2 not found in
///       layout") — the deconstruction same-class invocation arm looked up return slots with the
///       OPEN symbol. Fixed: the arm resolves the callee through SubstituteMethodTypeArgs.
/// [Y7]/[Y9] Deconstruction into a FIELD target with ANY non-this receiver (own-typed variable,
///       behaviour-array element, foreign behaviour field) threw "Unsupported l-value target:
///       FieldReferenceOperation" on legal C# while the simple-assignment twin worked. Fixed:
///       AssignToLValue's field arms route through the shared TryPrepareFieldSet path.
/// </summary>
public class Wave9Round7RegressionTests
{
    // ── [Y1] discard assignment ──

    [Fact]
    public void DiscardAssignment_MethodCallRhs_Compiles()
    {
        // W10MinDiscardInt (UsugarRejected pre-fix; CLR ref trace=3): the RHS side effect runs.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R7Discard1 : UdonSharpBehaviour {
    public int k; public int trace;
    void Start() { _ = Bump(k); }
    int Bump(int x) { trace = trace + x; return x; }
}", "W9R7Discard1");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void DiscardAssignment_DelegateDispatchRhs_Compiles()
    {
        // W10MinDiscardDispatch flavor: `_ = f(k)` through a delegate local.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R7Discard2 : UdonSharpBehaviour {
    public int k; public int trace;
    void Start() { Func<int, int> f = Bump; _ = f(k); }
    int Bump(int x) { trace = trace + x; return x; }
}", "W9R7Discard2");
        Assert.NotNull(uasm);
    }

    // ── [Y2] simple-assignment field-target legs evaluate before a non-inert RHS ──

    [Fact]
    public void SimpleAssign_StructArrayElementFieldTarget_IndexLegEvaluatesBeforeRhs()
    {
        // W9R7MinA2 (ref result=701 vs 71): C# evaluates the arr[…] leg BEFORE the RHS. The RHS
        // here is an extern invocation (Max — non-inert), so the C# order must be emitted.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R7BoxA { public int v; }
public class W9R7SimpleLeg : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        W9R7BoxA[] arr = new W9R7BoxA[2];
        arr[Math.Abs(seed) % 2].v = Math.Max(seed, 2);
        result = arr[0].v * 10 + arr[1].v;
    }
}", "W9R7SimpleLeg");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
    }

    // ── [Y4]/[Y6]/[Y8]/[Y10] deconstruction field-target legs evaluate before the RHS ──

    [Fact]
    public void DeconIntoStructArrayElementFieldTarget_TupleLiteralRhs_IndexLegEvaluatesBeforeRhs()
    {
        // W10R7MinFieldLegLit / W9R7Min1 (ref=702 vs 72 wrong cell): the field-leaf arm of
        // PrepareDeconstructionTargets — the index leg (Abs) precedes the RHS element (Max).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R7BoxB { public int v; }
public class W9R7DeconLegLit : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        W9R7BoxB[] arr = new W9R7BoxB[2];
        int t;
        (arr[Math.Abs(seed) % 2].v, t) = (Math.Max(seed, 2), 5);
        result = arr[0].v * 100 + arr[1].v * 10 + t;
    }
}", "W9R7DeconLegLit");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemMath.__Max__SystemInt32_SystemInt32__SystemInt32");
    }

    [Fact]
    public void DeconIntoStructArrayElementFieldTarget_CallRhs_IndexLegEvaluatesBeforeCallResultReads()
    {
        // W10R7MinFieldLegCall / W9P7Min_FieldLegCall (ref r0=0/r2=13 vs 12/0): the invocation-RHS
        // arm — the index leg (Abs) precedes EVERY SystemObjectArray.__Get (pre-fix the return
        // tuple reads AND the element-leg read all ran before Abs).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R7BoxC { public int v; }
public class W9R7DeconLegCall : UdonSharpBehaviour {
    public int seed; public int result;
    W9R7BoxC[] arr;
    (int, int) Pair() { return (seed + 5, 3); }
    void Start() {
        arr = new W9R7BoxC[2];
        int t;
        (arr[Math.Abs(seed) % 2].v, t) = Pair();
        result = arr[0].v * 10 + t;
    }
}", "W9R7DeconLegCall");
        AssertExternBefore(uasm,
            "SystemMath.__Abs__SystemInt32__SystemInt32",
            "SystemObjectArray.__Get__SystemInt32__SystemObject");
    }

    // ── [Y7]/[Y9] deconstruction into field targets through non-this receivers ──

    [Fact]
    public void DeconIntoVariableReceiverField_Compiles()
    {
        // W9P7Probe_VarRecvField (loud "Unsupported l-value target: FieldReferenceOperation"
        // pre-fix; legal C# — the simple-assignment twin b.pub = v always worked).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R7VarRecv : UdonSharpBehaviour {
    public int seed; public int pub; public int rest;
    (int, int) Pair(int k) { return (k + 7, k * 3); }
    void Start() {
        W9R7VarRecv b = this;
        int rr;
        (b.pub, rr) = Pair(seed);
        rest = rr;
    }
}", "W9R7VarRecv");
        Assert.Contains("__SetProgramVariable__", uasm);
    }

    [Fact]
    public void DeconIntoBehaviourArrayElementField_Compiles()
    {
        // W9P7Min_FieldLegBehArr flavor: the receiver is a behaviour-ARRAY element.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R7BehArr : UdonSharpBehaviour {
    public int seed; public int pub; public int rest;
    W9R7BehArr[] hs;
    (int, int) Pair(int k) { return (k + 7, k * 3); }
    void Start() {
        hs = new W9R7BehArr[2];
        hs[0] = this; hs[1] = this;
        int rr;
        (hs[seed % 2].pub, rr) = Pair(seed);
        rest = rr;
    }
}", "W9R7BehArr");
        Assert.Contains("__SetProgramVariable__", uasm);
    }

    [Fact]
    public void DeconIntoForeignField_TupleLiteralRhs_Compiles()
    {
        // PR1_PlainIntForeignFieldDeconLit: a foreign (cross-behaviour) plain-int field target.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R7Foreign : UdonSharpBehaviour {
    W9R7Foreign other;
    public int pi; public int n; public int r1;
    void Start() {
        other = this;
        int x = 0;
        (other.pi, x) = (n + 1, n + 2);
        r1 = pi * 100 + x;
    }
}", "W9R7Foreign");
        Assert.Contains("__SetProgramVariable__", uasm);
    }

    [Fact]
    public void DeconIntoThisField_StaysDirectStore()
    {
        // Control: a this-receiver field target keeps the direct heap store (TryPrepareFieldSet
        // returns null for behaviour this-fields — no SetProgramVariable detour).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R7ThisCtl : UdonSharpBehaviour {
    public int seed; public int pub; public int rest;
    (int, int) Pair(int k) { return (k + 7, k * 3); }
    void Start() {
        int rr;
        (this.pub, rr) = Pair(seed);
        rest = rr;
    }
}", "W9R7ThisCtl");
        Assert.DoesNotContain("__SetProgramVariable__", uasm);
    }

    // ── [Y3] generic-body closures capturing the generic method's parameter ──

    [Fact]
    public void GenericParamCapture_Lambda_SingleInstantiation_Compiles()
    {
        // W9R7MinB1 (ICE pre-fix: "Cannot resolve parameter 'n' (ordinal 1) in method ''").
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R7GenCap1 : UdonSharpBehaviour {
    public int seed; public int result;
    int Gen<T>(T s, int n) {
        Func<int> p = () => n + 1;
        return p();
    }
    void Start() { result = Gen(seed, seed % 5 + 2); }
}", "W9R7GenCap1");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericParamCapture_LocalFunction_SingleInstantiation_Compiles()
    {
        // W9R7MinB3 flavor (same ICE with method 'Lf').
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R7GenCap2 : UdonSharpBehaviour {
    public int seed; public int result;
    int Gen<T>(T s, int n) {
        int Lf() { return n + 1; }
        return Lf();
    }
    void Start() { result = Gen(seed, seed % 5 + 2); }
}", "W9R7GenCap2");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericParamCapture_TwoInstantiations_StaysLegal()
    {
        // Stage 2 §8.1 FLIP (was GenericParamCapture_..._StillRejectsLoudly): the closure captures the
        // int param `n`, NOT the generic's type parameter, so it is non-T-dependent — the retired
        // Capturing tier no longer rejects it. Both instantiations share one T-free hoist and each
        // activation's `n` lives in its own env record, so the specs no longer alias.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R7GenCap3 : UdonSharpBehaviour {
    public int seed; public int result;
    int Gen<T>(T s, int n) {
        Func<int> p = () => n + 1;
        return p();
    }
    void Start() { result = Gen(seed, 2) + Gen((long)seed, 3); }
}", "W9R7GenCap3");
        Assert.Contains("_Gen_SystemInt32", uasm);
        Assert.Contains("_Gen_SystemInt64", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
    }

    // ── [Y5] deconstruction of a same-class generic call with an open type arg ──

    [Fact]
    public void DeconDeclaration_SameClassGenericCall_OpenTypeArg_Compiles()
    {
        // MinGenDeconOpenT (loud "Method P2 not found in layout" pre-fix; CLR ref r1=56). The
        // closed-arg twin, the top-level control, and the non-decon direct call all worked.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R7GenDecon : UdonSharpBehaviour {
    public int r1;
    void Start() { r1 = Run<int>(4); }
    int Run<T>(int x) {
        var (a, b) = P2<T>(x);
        return a * 10 + b;
    }
    (int, int) P2<T>(int x) { return (x + 1, x + 2); }
}", "W9R7GenDecon");
        Assert.NotNull(uasm);
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
