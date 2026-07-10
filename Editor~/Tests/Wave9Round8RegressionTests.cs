using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 8 — tracked pins for the VM-proven findings (DiffFuzz refs cited per test;
/// real-VM value pins live in the local harness round-8 corpora fcd_wave9_*_r8.json and the
/// minimized _w10b/_w10c8/_w10fp/_w9rec/_w9q _r8_min files):
///
/// [Y1]/[Y4] `da?.Invoke(m - 1);` as the LAST statement of a self-recursive void method was not
///       classified tail — neither tail classifier had a conditional-access arm, so every frame
///       spilled and legal recursion overflowed the 512-entry __recurStack at depth ~600 (VmFault).
/// [Y2]  A hoisted closure whose SIGNATURE/BODY uses the enclosing generic's type parameter ICEd
///       on a SINGLE legal instantiation ("Type mismatch in CReturn: expected 'SystemInt32', got
///       'T'") — registration substituted the signature under the spec's map, but body emission ran
///       map-less. Fixed: EmitMethod derives the closure's map from FirstGenericSpec (exact: a
///       T-dependent closure pins its generic to one instantiation — the [X6] reject, widened here
///       from capturing-only to type-param-referencing closures).
/// [Y3]  A re-chained ref in pure RETURN position (`return M(m - 1, ref w);`) rode the TCO arm
///       straight past GuardRefOutArguments — the param-rebind treated `ref w` as a value arg, so
///       every frame threaded one param cell and the outer copy-back read the innermost value
///       (VM-proven ref=9021 vs usugar 21021). Fixed twice over: TCO only fires when every ref/out
///       arg self-threads, and the [Q2] guard gates on the UNFILTERED cycle-edge map (IsCycleEdge)
///       so tail-position re-chains reject loudly like the round-6 statement form.
/// [Y5]/[Y6]/[Y13] A compound property write (`Q -= 1;`), inc/dec (`Q--;`), and the indexer twin
///       (`this[0] += -1;`) as the LAST statement of a setter-cycle member were classified
///       non-tail (the classifiers' property arms only knew simple assignment) — per-frame spill,
///       VmFault at depth ~600 on legal C#.
/// [Y7]/[Y8] Hoisted lambda and local-function BLOCK bodies get an IMPLICIT trailing
///       IReturnOperation from Roslyn — the block arms indexed the last "statement" as the tail
///       position, so the real last statement was never tail and deep dispatch/self-recursion
///       spilled every frame (VmFault at depth ~604).
/// [Y9]  CollectRecursionSpillFields read the PERSISTENT class-wide _localBindings, so a cycle
///       member spilled every previously-emitted method's locals too — per-frame __recurStack cost
///       scaled with cycle FUNCTION COUNT (7-ring x 6 locals faulted at ~21 frames). Fixed: spill
///       only locals whose ContainingSymbol is the current method (own frame).
/// [Y10] A closure capturing a BASE-declared generic method's PARAMETER ICEd ("Cannot resolve
///       parameter 'a' (ordinal 0) in method ''") — the base-instance copy registration never
///       seeded FirstGenericSpec, so the round-7 [Y3] spec-param arm had nothing to resolve
///       through.
/// [Y11] An open-type-arg call to an INHERITED generic callee inside a generic body failed loud
///       (decon: "Method P2 not found in layout for MB1Base"; direct: bogus
///       IUdonEventReceiver extern) — the phase-1 base-copy collector only registers CLOSED
///       call-site symbols. Fixed: when the enclosing spec's map closes the symbol at emit time,
///       register it as an on-demand generic specialization.
/// [Y12] Ref/out copy-back re-evaluated the lvalue's receiver/index legs AFTER the call
///       (`AddTo(ref arr[Idx()].v)` ran Idx() twice and the write landed in the cell chosen by
///       the SECOND evaluation; out and plain int[]-element flavors identical). Fixed:
///       TryPrepareRefOutArg evaluates the legs once at copy-in and the copy-back stores through
///       the SAME legs.
/// </summary>
public class Wave9Round8RegressionTests
{
    // ── [Y1]/[Y4] conditional-access dispatch as the tail statement ──

    [Fact]
    public void NullCondInvoke_TailStatement_SelfCycle_NoSpill()
    {
        // W10BMinQTail600 / MinNullCondTail (VmFault at depth ~600 pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8NullCondTail : UdonSharpBehaviour {
    Action<int> da;
    public int n; public int acc; public int result;
    void Start() { da = Rec; Rec(n % 2 + 600); result = acc; }
    public void Rec(int m) {
        acc = acc + 1;
        if (m <= 0) return;
        da?.Invoke(m - 1);
    }
}", "W9R8NullCondTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void NullCondInvoke_NonLastStatement_StillSpills()
    {
        // Control: frame state (m) is read AFTER the dispatch — must stay non-tail and spill.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8NullCondCtl : UdonSharpBehaviour {
    Action<int> da;
    public int n; public int acc; public int result;
    void Start() { da = Rec; Rec(n % 2 + 6); result = acc; }
    public void Rec(int m) {
        if (m <= 0) return;
        da?.Invoke(m - 1);
        acc = acc + m;
    }
}", "W9R8NullCondCtl");
        Assert.Contains("__recurStack", uasm);
    }

    // ── [Y5]/[Y6]/[Y13] compound / inc-dec property and indexer writes as the tail statement ──

    [Fact]
    public void CompoundPropertyWrite_TailStatement_SetterCycle_NoSpill()
    {
        // MinCompoundPropTail / W10FP8MinCompTail (VmFault at depth ~600 pre-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8CompTail : UdonSharpBehaviour {
    int q;
    public int n; public int result;
    public int Q {
        get { return q; }
        set { q = value; if (q <= 0) return; Q -= 1; }
    }
    void Start() { Q = n % 2 + 600; result = q; }
}", "W9R8CompTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void IncDecPropertyWrite_TailStatement_SetterCycle_NoSpill()
    {
        // MinIncDecPropTail: `Q--;` is an IIncrementOrDecrementOperation, not a compound assign.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8IncDecTail : UdonSharpBehaviour {
    int q;
    public int n; public int result;
    public int Q {
        get { return q; }
        set { q = value; if (q <= 0) return; Q--; }
    }
    void Start() { Q = n % 2 + 600; result = q; }
}", "W9R8IncDecTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void CompoundIndexerWrite_TailStatement_SetterCycle_NoSpill()
    {
        // W10FP8MinCompIdxTail: this-indexer twin of the compound setter cycle.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8IdxTail : UdonSharpBehaviour {
    int cell;
    public int n; public int result;
    public int this[int i] {
        get { return cell; }
        set { cell = value; if (cell <= 0) return; this[0] += -1; }
    }
    void Start() { this[0] = n % 2 + 600; result = cell; }
}", "W9R8IdxTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void CompoundPropertyWrite_NonLastStatement_StillSpills()
    {
        // Control: the setter reads frame state after the compound write — must spill.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8CompCtl : UdonSharpBehaviour {
    int q;
    public int n; public int result; public int trace;
    public int Q {
        get { return q; }
        set { int before = value; q = value; if (q <= 0) return; Q -= 1; trace = trace + before; }
    }
    void Start() { Q = n % 2 + 6; result = q; }
}", "W9R8CompCtl");
        Assert.Contains("__recurStack", uasm);
    }

    // ── [Y7]/[Y8] implicit trailing return in hoisted block bodies ──

    [Fact]
    public void HoistedLambdaBody_TailIfDispatch_NoSpill()
    {
        // MinLambdaBodyTail (VmFault at depth ~604 pre-fix): Roslyn appends an implicit value-less
        // IReturnOperation to the lambda BLOCK body; the block arms must skip it when indexing the
        // tail statement.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8LambdaTail : UdonSharpBehaviour {
    Action<int> d;
    public int n; public int acc; public int result;
    void Start() {
        d = m => { acc = acc + 1; if (m > 0) d(m - 1); };
        d(n % 2 + 600);
        result = acc;
    }
}", "W9R8LambdaTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void LocalFunction_TailIfSelfCall_NoSpill()
    {
        // MinLocalFuncTail: capture-free local function, direct self-call in tail-if position.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8LfTail : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() {
        void Rec(int m) { acc = acc + 1; if (m > 0) Rec(m - 1); }
        Rec(n % 2 + 600);
        result = acc;
    }
}", "W9R8LfTail");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void LocalFunction_NonLastSelfCall_StillSpills()
    {
        // Control: the local function reads its param after the self-call — must spill.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8LfCtl : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() {
        void Rec(int m) { if (m > 0) Rec(m - 1); acc = acc + m; }
        Rec(n % 2 + 6);
        result = acc;
    }
}", "W9R8LfCtl");
        Assert.Contains("__recurStack", uasm);
    }

    // ── [Y3] ref re-chain in pure return position ──

    [Fact]
    public void RefReturnRechain_RejectsLoud()
    {
        // MinRefReturnRechain (silent corruption pre-fix: ref=9021 vs usugar 21021).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8RefRechain : UdonSharpBehaviour {
    public int n; public int result;
    void Start() {
        int z0 = n % 5 + 3;
        int r = M(4, ref z0);
        result = z0 * 1000 + r % 1000;
    }
    public int M(int m, ref int z) {
        z = z + 2;
        if (m <= 0) return z;
        int w = z + 1;
        return M(m - 1, ref w);
    }
}", "W9R8RefRechain"));
        Assert.Contains("an lvalue other than the same parameter", ex.Message);
    }

    [Fact]
    public void RefReturnSelfThreaded_KeepsTco_NoSpill()
    {
        // Control: threading the method's OWN ref param stays legal and keeps the TCO param-rebind
        // (param-to-param is an identity rebind under the shared flat heap) — no spill, no reject.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8RefSelfThread : UdonSharpBehaviour {
    public int n; public int result;
    void Start() {
        int z0 = n % 5 + 3;
        int r = M(600, ref z0);
        result = z0 * 1000 + r % 1000;
    }
    public int M(int m, ref int z) {
        z = z + 2;
        if (m <= 0) return z;
        return M(m - 1, ref z);
    }
}", "W9R8RefSelfThread");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [Y9] spill set is the OWN frame only ──

    [Fact]
    public void RecursionSpill_CycleMemberSpillsOwnFrameOnly()
    {
        // ProbeRing7 tier (7-ring x 6 locals faulted at ~21 frames pre-fix): _localBindings is
        // persistent class-wide, so each later-emitted ring member spilled every earlier member's
        // locals too — per-frame __recurStack cost scaled with cycle FUNCTION COUNT. Pin the
        // __recurStack reference count of a 3-ring with 2 locals each: pre-fix 45 (A2 spills A1's
        // locals, A3 spills A1's and A2's — stash-measured), post-fix 25 (own frame only).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8Ring3 : UdonSharpBehaviour {
    Action<int> d;
    public int n; public int acc; public int result;
    void Start() { d = A1; A1(n % 2 + 2); result = acc; }
    public void A1(int m) {
        if (m <= 0) return;
        int a = m * 3 + 1; int b = m * 5 + 2;
        A2(m);
        acc = (acc + a + b) % 99991;
    }
    public void A2(int m) {
        if (m <= 0) return;
        int a = m * 3 + 1; int b = m * 5 + 2;
        A3(m);
        acc = (acc + a + b) % 99991;
    }
    public void A3(int m) {
        if (m <= 0) return;
        int a = m * 3 + 1; int b = m * 5 + 2;
        d(m - 1);
        acc = (acc + a + b) % 99991;
    }
}", "W9R8Ring3");
        Assert.Contains("__recurStack", uasm); // the cycle still spills its own frames
        int refs = CountOccurrences(uasm, "__recurStack");
        Assert.True(refs <= 30, $"expected own-frame-only spill sets (25 __recurStack refs), got {refs}");
    }

    // ── [Y2] closure over the enclosing generic's type parameter, single instantiation ──

    [Fact]
    public void GenericBody_TypeParamLocalFunction_SingleInstantiation_Compiles()
    {
        // W10C8Min12_DirectLfCtl (pre-fix ICE: "Type mismatch in CReturn: expected 'SystemInt32',
        // got 'T'") — the hoisted body emits under the instantiation's type-param map.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8GenLf : UdonSharpBehaviour {
    public int seed; public int resultI;
    T Id<T>(T v) {
        T Lf(T x) { return x; }
        return Lf(v);
    }
    void Start() { resultI = Id(seed * 3 + 1); }
}", "W9R8GenLf");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericBody_TypeParamLambda_SingleInstantiation_Compiles()
    {
        // W10C8Min12_SingleIntT: capture-free Func<T,T> lambda, one instantiation.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8GenLambda : UdonSharpBehaviour {
    public int seed; public int resultI;
    T Id<T>(T v) {
        Func<T, T> f = x => x;
        return f(v);
    }
    void Start() { resultI = Id(seed * 3 + 1); }
}", "W9R8GenLambda");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericBody_TypeParamLambda_TwoInstantiations_Compiles()
    {
        // FLIPPED 2026-07-10 (per-spec closure root fix): the lambda is duplicated per spec with its
        // own T map, so two instantiations are legal — VM oracle: harness PerSpec B70 probes.
        TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8GenTwoInst : UdonSharpBehaviour {
    public int seed; public int resultI; public long resultL;
    T Id<T>(T v) {
        Func<T, T> f = x => x;
        return f(v);
    }
    void Start() {
        resultI = Id(seed * 3 + 1);
        resultL = Id((long)seed * 7L + 2L);
    }
}", "W9R8GenTwoInst");
    }

    [Fact]
    public void GenericBody_TFreeLambda_TwoInstantiations_StaysLegal()
    {
        // W10C8Min12_NoTSigCtl control: a capture-free closure that never references T is shared
        // safely across instantiations — the widened reject must not catch it.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8GenTFree : UdonSharpBehaviour {
    public int seed; public int resultI; public int resultL;
    int Tag<T>(int v) {
        Func<int, int> f = x => x + 1;
        return f(v);
    }
    void Start() {
        resultI = Tag<int>(seed * 3 + 1);
        resultL = Tag<long>(seed * 5 + 2);
    }
}", "W9R8GenTFree");
        Assert.NotNull(uasm);
    }

    // ── [Y10] closure capturing a BASE-declared generic's parameter ──

    [Fact]
    public void BaseDeclaredGeneric_LambdaCapturesParam_Compiles()
    {
        // W9QMinA1_BaseGenParamCapture (pre-fix ICE: "Cannot resolve parameter 'a' (ordinal 0) in
        // method ''") — the base-copy registration now seeds FirstGenericSpec.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8BaseGenCapBase : UdonSharpBehaviour {
    public int Fold<T>(int a) {
        Func<int, int> f = x => x + a;
        return f(2) + f(3);
    }
}
public class W9R8BaseGenCap : W9R8BaseGenCapBase {
    public int seed; public int result;
    void Start() { result = Fold<int>(seed); }
}", "W9R8BaseGenCap");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void BaseDeclaredGeneric_LocalFunctionCapturesParam_Compiles()
    {
        // W9QMinA5_BaseGenCaplfFlavor: capturing LOCAL FUNCTION twin.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8BaseGenLfBase : UdonSharpBehaviour {
    public int Fold<T>(int a) {
        int Add(int x) { return x + a; }
        return Add(2) + Add(3);
    }
}
public class W9R8BaseGenLf : W9R8BaseGenLfBase {
    public int seed; public int result;
    void Start() { result = Fold<int>(seed); }
}", "W9R8BaseGenLf");
        Assert.NotNull(uasm);
    }

    // ── [Y11] open-type-arg call to an INHERITED generic callee ──

    [Fact]
    public void InheritedGenericCallee_OpenTypeArg_Deconstruction_Compiles()
    {
        // W9QMinB1_OpenTDeconInherited (pre-fix loud: "Method P2 not found in layout for
        // MB1Base").
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8InhGenBase : UdonSharpBehaviour {
    protected (int, int) P2<T>(int x) { return (x + 1, x + 2); }
}
public class W9R8InhGenDecon : W9R8InhGenBase {
    public int seed; public int r1;
    int Run<T>(int x) {
        var (a, b) = P2<T>(x);
        return a * 10 + b;
    }
    void Start() { r1 = Run<int>(seed); }
}", "W9R8InhGenDecon");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void InheritedGenericCallee_OpenTypeArg_DirectCall_Compiles()
    {
        // W9QMinB4_OpenTDirect (pre-fix: bogus VRCUdonCommonInterfacesIUdonEventReceiver.__P2__
        // extern — assembler/validator crash on legal C#).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R8InhGenDirBase : UdonSharpBehaviour {
    protected int P3<T>(int x) { return x + 3; }
}
public class W9R8InhGenDirect : W9R8InhGenDirBase {
    public int seed; public int r1;
    int Run<T>(int x) { return P3<T>(x) * 10; }
    void Start() { r1 = Run<int>(seed); }
}", "W9R8InhGenDirect");
        Assert.NotNull(uasm);
    }

    // ── [Y12] ref/out lvalue legs evaluate exactly once ──

    [Fact]
    public void RefArg_StructArrayElementMember_IndexLegEvaluatesOnce()
    {
        // W10FP8MinRefLeg (kk ref=1 vs 2, c0/c1 swapped cells pre-fix): the index leg (Abs) must
        // appear exactly once — the copy-back stores through the SAME legs.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct W9R8Box { public int v; }
public class W9R8RefLeg : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        W9R8Box[] arr = new W9R8Box[2];
        AddTo(ref arr[Math.Abs(seed) % 2].v);
        result = arr[0].v * 10 + arr[1].v;
    }
    void AddTo(ref int x) { x = x + 5; }
}", "W9R8RefLeg");
        AssertExternCount(uasm, "SystemMath.__Abs__SystemInt32__SystemInt32", 1);
    }

    [Fact]
    public void RefArg_PlainIntArrayElement_IndexLegEvaluatesOnce_CopyBackPresent()
    {
        // W10FP8MinRefIntElem: plain int[] element flavor — one Get at copy-in, one Set at
        // copy-back, both over the SAME index leg.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8RefIntElem : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        int[] a = new int[2];
        Bump(ref a[Math.Abs(seed) % 2]);
        result = a[0] * 10 + a[1];
    }
    void Bump(ref int x) { x = x + 5; }
}", "W9R8RefIntElem");
        AssertExternCount(uasm, "SystemMath.__Abs__SystemInt32__SystemInt32", 1);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void OutArg_PlainIntArrayElement_IndexLegEvaluatesOnce()
    {
        // W10FP8MinOutLeg twin: out flavor.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R8OutElem : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        int[] a = new int[2];
        Fill(out a[Math.Abs(seed) % 2]);
        result = a[0] * 10 + a[1];
    }
    void Fill(out int x) { x = 9; }
}", "W9R8OutElem");
        AssertExternCount(uasm, "SystemMath.__Abs__SystemInt32__SystemInt32", 1);
    }

    static void AssertExternCount(string uasm, string extern_, int expected)
    {
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int actual = CountOccurrences(code, extern_);
        Assert.True(actual == expected,
            $"expected '{extern_}' to appear {expected} time(s) in the code section, got {actual}");
    }

    static int CountOccurrences(string text, string token)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        return count;
    }
}
