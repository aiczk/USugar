using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Stage 2 M3 — reentrancy-hardening pins (design §5.4 EscapeSet widening + sig-filter, §5.5 bridge-
/// target armor, §6.2 captured-param spill skip, §6.3/§5.4 SCC/spill measurement gates). Behavioural
/// values are verified on the real Udon VM by the fcd corpus (fcd40/44/47/53/55/56); these tracked
/// pins lock the emission-SHAPE invariants a later refactor could silently break: the widening must
/// protect a foreign-wired self-callback, the sig-filter must not over-spill a cross-signature
/// dispatch, the skip arm must drop a captured param's spill (incl. through a generic spec's
/// OriginalDefinition re-keying), and the widening's cost must stay bounded to same-signature
/// dispatch patterns (not inflate ordinary recursion / capture rings).
/// </summary>
public class Stage2M3ReentryTests
{
    static int Count(string haystack, string needle)
    {
        int k = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { k++; i += needle.Length; }
        return k;
    }

    // ── §6.2 captured-param spill skip arm (E5 corollary: consumed-into-env params are dead) ──

    [Fact]
    public void CapturedParam_ConsumedIntoEnv_IsNotSpilled()
    {
        // M(p) captures p into a closure; p is consumed into the env record at MethodEntry and every
        // later read routes through env, so its param field is DEAD across the non-tail self-recursion.
        // The skip arm removes its spill save/restore: `__0_p__param` is touched only by the consume-
        // early read and the recursive-call argument pass (4 pushes), NOT by a __recurStack save/restore
        // pair (which HEAD emitted — 6 pushes). The env-carrying local `__lcl_f` still spills, so the
        // captured value survives the unwind through the env the bundle references (VM result unchanged).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3CapParam : UdonSharpBehaviour {
    public int seed; public int result; Func<int> held;
    void Start() { result = M(seed); }
    int M(int p) {
        Func<int> f = () => p;
        held = f;
        if (p <= 0) return 0;
        int below = M(p - 1);
        return f() + below;
    }
}", "M3CapParam");
        Assert.Contains("SystemObjectArray.__ctor", uasm);      // p lives in an env record
        // R-M2: M is an unbound private method — no speculative __dlg_M bridge is emitted, so its
        // conv-arg copy of __0_p__param is gone too (pre-R-M2: 4, incl. the bridge's copy; the skip arm
        // still removes the spill save/restore pair, which was the point — HEAD-with-spill was 6).
        Assert.Equal(3, Count(uasm, "PUSH, __0_p__param"));      // consume-early read + recursive-call arg pass only
    }

    [Fact]
    public void GenericSpec_CapturedParam_IsNotSpilled_ThroughOriginalDefinitionReKey()
    {
        // §6.2 mandatory: the skip arm matches CapturedSlots by DEFINITION symbol, but a generic spec's
        // param is a FRESH constructed IParameterSymbol that never compares equal to the definition's —
        // TryGetEnvBinding re-keys through ContainingSymbol.OriginalDefinition + ordinal (§2 rule 2).
        // Without that re-key the skip silently no-ops for the spec (the exact silent-miss §6.2 warns of).
        // Pin: G<int>'s captured param `__1_p__param` is touched 3 times (consume-early + arg pass),
        // NOT the 5 a spilled param needs. If the re-keying regresses this jumps back to 5.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3GenCapParam : UdonSharpBehaviour {
    public int seed; public int result; Func<int> held;
    void Start() { result = G<int>(seed); }
    int G<T>(int p) {
        Func<int> f = () => p;
        held = f;
        if (p <= 0) return 0;
        return f() + G<T>(p - 1);
    }
}", "M3GenCapParam");
        Assert.Equal(3, Count(uasm, "PUSH, __1_p__param"));      // spec's captured param not spilled (HEAD: 5)
    }

    // ── §5.4 EscapeSet widening: foreign-wired self-callback protection ──

    [Fact]
    public void ForeignWiredUnboundPrivate_HasNoBridge_AndIsNotWideningSpilled()
    {
        // R-M2 (design §2, lead ruling): N is an UNBOUND PRIVATE method that non-tail self-dispatches
        // through a public field `cb` never assigned in this class. Pre-R-M2 the wave-12 [V1] widening
        // protected it because N had a SPECULATIVE __dlg_N export a foreign program could name and wire
        // into cb (runtime bundle-wiring). R-M2 removes that export — the channel is CLOSED, so no bundle
        // (foreign or same-program) can ever name N, N is undispatchable, and its blanket spill correctly
        // goes with the removed door. This control pins the LOAD-BEARING fact (the absent door), not just
        // the absent spill. (N has no direct self-recursion, so it now needs no spill at all.)
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3WorkerGap : UdonSharpBehaviour {
    public Action cb; public int seed; public int acc; int nn;
    void N() {
        int cap = seed;
        Action logger = () => { acc = acc + cap; };
        logger();
        if (nn < 3) { nn = nn + 1; cb(); acc = acc + cap; }
    }
}", "M3WorkerGap");
        Assert.DoesNotContain(".export __dlg_N", uasm);         // channel closed: no speculative bridge to wire
        Assert.DoesNotContain("__recurStack", uasm);            // undispatchable + no direct recursion → no spill
    }

    [Fact]
    public void PublicForeignFieldDispatch_KeepsWidening_NarrowingIsPrivateScoped()
    {
        // Scope control (design §2 condition d): the SAME shape with a PUBLIC method N stays widening-
        // protected. A public method keeps its speculative __dlg_N export, so a foreign program CAN wire
        // cb to it (runtime bundle-wiring), the cb() dispatch self-connects N→N, and N's frame is spilled.
        // Proves the R-M2 narrowing is EXACTLY private-unbound-scoped, not a blanket removal.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3PubWired : UdonSharpBehaviour {
    public Action cb; public int seed; public int acc; int nn;
    public void N() { int cap = seed; if (nn < 3) { nn = nn + 1; cb(); acc = acc + cap; } }
}", "M3PubWired");
        Assert.Contains(".export __dlg_N", uasm);               // public → speculative bridge kept (wireable)
        Assert.Contains("__recurStack", uasm);                  // widening still protects the public dispatch
    }

    [Fact]
    public void BoundPrivateForeignFieldDispatch_StaysEscapedAndSpilled()
    {
        // Positive control (design §2 condition c): a private method that IS method-group bound gets a
        // PENDING bridge (on-demand, same-program), so its __dlg_N export exists and a bundle CAN name it.
        // It stays in the escape set (via the body-walk of the actual binding) and keeps its blanket spill.
        // Proves the protection that REMAINS for dispatchable private methods is intact.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3BoundPriv : UdonSharpBehaviour {
    public int seed; public int acc; int nn; Action cb;
    void Start() { cb = N; N(); }
    void N() { int cap = seed; if (nn < 3) { nn = nn + 1; cb(); acc = acc + cap; } }
}", "M3BoundPriv");
        Assert.Contains(".export __dlg_N", uasm);               // bound → pending bridge exists (dispatchable)
        Assert.Contains("__recurStack", uasm);                  // still escaped + spilled across the dispatch
    }

    [Fact]
    public void SigFilter_CrossSignatureDispatch_IsNotOverSpilled()
    {
        // The widening's sig-filter keeps only SAME-signature synthetic edges. M(int→void) dispatches a
        // Void→Void `bump` whose signature differs from M's own — a bundle of M's signature can never
        // flow to a Void→Void dispatch, so the dispatch must NOT be marked Reentrant. The direct self-
        // call still spills; the dispatch adds no extra spill. (If the sig-filter regressed to
        // signature-blind widening, `bump()` would be spuriously wrapped.)
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3CrossSig : UdonSharpBehaviour {
    public int n; public int acc; public int result;
    void Start() { M(n + 2); result = acc; }
    public void M(int m) {
        if (m <= 0) return;
        Action bump = () => { acc = acc + m; };
        bump();
        M(m - 1);
    }
}", "M3CrossSig");
        // The self-call spills a bounded frame; the cross-sig dispatch must add no spill wrap. A signature-
        // blind widening (M reaches its own SCC) would mark bump() and inflate this well past a plain
        // self-recursion. Pin the bound: only the direct-call frame spill is present.
        Assert.True(Count(uasm, "PUSH, __recurStack") <= 3,
            "cross-signature dispatch must not be wrapped by the widening (sig-filter over-spill)");
    }

    // ── §5.4 sig-filter ↔ variance widening (SigFilterCoupledToVarianceReject → widened form) ──

    [Fact]
    public void SigFilter_WidensUnderVariantAdapter_NotRejected()
    {
        // Stage 1.75 (design 2026-07-04 §2.2): variance is no longer rejected at creation — the §5.4
        // sig-filter now widens correctly: a variant target is escaped under its ADAPTER's protocol sig
        // (sig-S), not its own (ResolvedEdgeResolver.ResolveVariantEscapeSigs). Bare compile pin; the reentrancy
        // gate itself is pinned below (SigFilter_VariantAdapterTarget_GetsReentrantSpill).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3VariantMg : UdonSharpBehaviour {
    Action<string> a;
    void Start() { a = M; a(""x""); }
    void M(object o) { }
}", "M3VariantMg");
        Assert.Contains("__dlg_adapt_", uasm);
    }

    [Fact]
    public void SigFilter_VariantAdapterTarget_GetsReentrantSpill()
    {
        // The widened escape-set must key a variant target by its ADAPTER's sig-S, not its own —
        // otherwise a variant target that reaches back into the dispatch's own recursion cycle would
        // silently lose its reentrancy spill (the exact gap SigFilterCoupledToVarianceReject used to
        // guard against by rejecting variance outright). Here M (bound to Action<string> a, sig-S =
        // SystemString__Void, differing from M's own SystemObject__Void) calls Go — closing a real
        // cycle Go→Go / Go→a-dispatch→M→Go. If the widening keyed M by its OWN sig, SigsMatch would
        // never fire and the a(\"x\") dispatch inside Go would NOT be marked Reentrant (no
        // __recurStack spill wrap) — a silent frame-clobber hazard on real recursion.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3VariantReentrySpill : UdonSharpBehaviour {
    public int n; public int acc;
    Action<string> a;
    void Start() { a = M; Go(n); }
    void M(object o) { acc = acc + ((string)o).Length; if (acc < 100) Go(1); }
    void Go(int d) {
        if (d <= 0) return;
        a(""x"");
        Go(d - 1);
    }
}", "M3VariantReentrySpill");
        Assert.Contains("__recurStack", uasm);
    }

    // ── §5.5 bridge-target armor: dormant on valid programs ──

    [Fact]
    public void BridgeTargetArmor_DoesNotFire_OnValidCapturingBridgeProgram()
    {
        // VerifyBridgeTargetsAreNodes throws only when a capturing bridge target has no recursion-graph
        // node (a registration path that escaped the analysis). On a valid capturing-closure program it
        // is dormant — compilation succeeds and the closure gets its env bridge.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3ArmorOk : UdonSharpBehaviour {
    Func<int> held; public int seed; public int result;
    void Start() { int cap = seed; Func<int> f = () => cap; held = f; result = held(); }
}", "M3ArmorOk");
        Assert.Contains("__dlg", uasm);   // the capturing closure's bridge is emitted; armor did not throw
    }

    // ── §6.3 / §5.4 SCC/spill measurement gates (upper bounds — the widening's cost is measured) ──
    // Baselines captured at HEAD d9a61af and unchanged by the widening EXCEPT SameSigWorst (the widening's
    // target pattern). Ring7 (zero-capture 7-ring) and fcd56 (min-capture 6-ring) prove the widening does
    // NOT inflate ordinary recursion / capture rings; SameSigWorst proves the widening's cost stays bounded
    // and CONTAINED to same-signature dispatch. A regression that inflates any of these fails the gate.

    const string Ring7 = @"
using UdonSharp;
public class M3Ring7 : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { result = R0(7); }
    int R0(int n){ int a=seed+1,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R1(n-1); }
    int R1(int n){ int a=seed+2,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R2(n-1); }
    int R2(int n){ int a=seed+3,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R3(n-1); }
    int R3(int n){ int a=seed+4,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R4(n-1); }
    int R4(int n){ int a=seed+5,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R5(n-1); }
    int R5(int n){ int a=seed+6,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R6(n-1); }
    int R6(int n){ int a=seed+7,b=a+1,c=b+1,d=c+1,e=d+1,f=e+1; if(n<=0)return 0; return a+b+c+d+e+f+R0(n-1); }
}";

    const string Fcd56 = @"
using System;
using UdonSharp;
public class M3Fcd56 : UdonSharpBehaviour {
    public int seed; public int depth; public int result;
    void Start() { result = Ring0(depth); }
    int Ring0(int n) { int cap = seed + 100; Func<int> f = () => cap; if (n <= 0) return 0; return f() + Ring1(n - 1); }
    int Ring1(int n) { int cap = seed + 200; Func<int> f = () => cap; if (n <= 0) return 0; return f() + Ring2(n - 1); }
    int Ring2(int n) { int cap = seed + 300; Func<int> f = () => cap; if (n <= 0) return 0; return f() + Ring3(n - 1); }
    int Ring3(int n) { int cap = seed + 400; Func<int> f = () => cap; if (n <= 0) return 0; return f() + Ring4(n - 1); }
    int Ring4(int n) { int cap = seed + 500; Func<int> f = () => cap; if (n <= 0) return 0; return f() + Ring5(n - 1); }
    int Ring5(int n) { int cap = seed + 600; Func<int> f = () => cap; if (n <= 0) return 0; return f() + Ring0(n - 1); }
}";

    // Wave-12 [V1] re-shape: the ORIGINAL shape's fN() dispatches were all in tail position, so the
    // pinned spill traffic came from the LOCAL g() dispatches the blanket sig-match spuriously marked
    // Reentrant (g's provenance is exact — its lambda cannot re-enter DN). The fN() dispatches now
    // read c AFTER the dispatch (non-tail), so the pinned cost is the genuine widening surface: five
    // same-signature foreign-writable FIELD dispatches, each spilling its live frame.
    const string SameSigWorst = @"
using System;
using UdonSharp;
public class M3SameSigWorst : UdonSharpBehaviour {
    public System.Action f0; public System.Action f1; public System.Action f2; public System.Action f3; public System.Action f4;
    public int acc; int nn;
    void D0(){ int c=acc+0; System.Action g=()=>{acc=acc+c;}; g(); if(nn<3){nn=nn+1; f0(); acc=acc+c;} }
    void D1(){ int c=acc+1; System.Action g=()=>{acc=acc+c;}; g(); if(nn<3){nn=nn+1; f1(); acc=acc+c;} }
    void D2(){ int c=acc+2; System.Action g=()=>{acc=acc+c;}; g(); if(nn<3){nn=nn+1; f2(); acc=acc+c;} }
    void D3(){ int c=acc+3; System.Action g=()=>{acc=acc+c;}; g(); if(nn<3){nn=nn+1; f3(); acc=acc+c;} }
    void D4(){ int c=acc+4; System.Action g=()=>{acc=acc+c;}; g(); if(nn<3){nn=nn+1; f4(); acc=acc+c;} }
}";

    [Fact]
    public void MeasurementGate_Ring7_ZeroCapture_NotInflatedByWidening()
    {
        var uasm = TestHelper.CompileToUasm(Ring7, "M3Ring7", out _);
        Assert.True(Count(uasm, ": %") <= 168, "Ring7 heap-var count must not inflate (widening cost bound)");
        Assert.True(Count(uasm, "__recurStack") <= 115, "Ring7 spill traffic must not inflate (widening cost bound)");
    }

    [Fact]
    public void MeasurementGate_Fcd56_MinCaptureRing_NotInflatedByWidening()
    {
        var uasm = TestHelper.CompileToUasm(Fcd56, "M3Fcd56", out _);
        Assert.True(Count(uasm, ": %") <= 460, "fcd56 heap-var count must not inflate (widening cost bound)");
        Assert.True(Count(uasm, "__recurStack") <= 39, "fcd56 spill traffic must not inflate (widening cost bound)");
    }

    [Fact]
    public void SameSigPrivateRing_Undispatchable_NotWideningProtected()
    {
        // R-M2 (design §2): D0..D4 are all UNBOUND PRIVATE, forming a same-signature ring only through the
        // public fields f0..f4 (each Dk non-tail dispatches fk()). Pre-R-M2 the widening spilled all five
        // frames because each Dk had a speculative __dlg_Dk export a foreign program could wire into any fj.
        // R-M2 removes those exports, so NO fj can ever name a Dk — the ring is undispatchable and the
        // widening correctly no longer protects it (no Dk has direct recursion, so no spill remains). This
        // is the same-sig-worst measurement gate re-pinned to the closed door rather than the removed spill.
        var uasm = TestHelper.CompileToUasm(SameSigWorst, "M3SameSigWorst", out _);
        Assert.DoesNotContain(".export __dlg_D", uasm);   // no speculative bridge for any private Dk
        Assert.DoesNotContain("__recurStack", uasm);      // undispatchable ring → no widening spill at all
    }
}
