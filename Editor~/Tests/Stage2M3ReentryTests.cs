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
        Assert.Equal(4, Count(uasm, "PUSH, __0_p__param"));      // no spill save/restore of p (HEAD: 6)
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
    public void ForeignWiredSelfCallback_IsProtected_ByWidening()
    {
        // N captures a local (needs an env) and non-tail self-dispatches through a PUBLIC field `cb`
        // that is NEVER assigned in this class — a foreign program can wire it to N (fcd47 form). Pre-
        // widening N is not a delegate-creation target, so its dispatch is not Reentrant and its env-ref
        // is unprotected (HEAD: zero __recurStack). Widening makes N a bridge-bearing escape target of
        // its own signature, so the cb() dispatch self-connects N→N and spills its frame.
        // Wave-12 [V1] re-shape: the ORIGINAL shape's cb() sat in tail position (last statement) and its
        // __recurStack came from the LOCAL logger() dispatch, which the blanket sig-match spuriously
        // marked Reentrant — logger's provenance is exact (one creation, the lambda cannot re-enter N),
        // so the precise analysis rightly unmarks it. cb() now reads cap AFTER the dispatch (non-tail),
        // pinning the genuine §5.4 protection surface: a foreign-writable FIELD dispatch keeps the
        // widening and spills N's live frame.
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
        Assert.Contains("__recurStack", uasm);                  // N's frame is spilled across the dispatch
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

    // ── §5.4 sig-filter ↔ variance-reject coupling (SigFilterCoupledToVarianceReject) ──

    [Fact]
    public void SigFilter_IsCoupledTo_VarianceReject()
    {
        // The §5.4 sig-filter matches a dispatch's delegate-Invoke signature to a target-method signature
        // by STRING EQUALITY (UasmEmitter.SigsMatch). That is sound ONLY because a variant method-group
        // binding — whose Invoke signature differs from the target signature — is REJECTED at creation
        // (DelegateProtocol.ValidateDelegateBinding). If that reject is ever relaxed, a variant target
        // would be dispatchable under a signature the sig-filter would not match to it, silently dropping
        // its reentrancy protection. This pin fails the moment the variance reject stops firing, forcing a
        // revisit of the sig-filter. (Couples to UasmEmitter.cs escapeSig/SigsMatch/DispatchSigOrWildcard.)
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M3VariantMg : UdonSharpBehaviour {
    Action<string> a;
    void Start() { a = M; a(""x""); }
    void M(object o) { }
}", "M3VariantMg"));
        Assert.Contains("Variant method-group", ex.Message);
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
        Assert.True(Count(uasm, "__recurStack") <= 113, "Ring7 spill traffic must not inflate (widening cost bound)");
    }

    [Fact]
    public void MeasurementGate_Fcd56_MinCaptureRing_NotInflatedByWidening()
    {
        var uasm = TestHelper.CompileToUasm(Fcd56, "M3Fcd56", out _);
        Assert.True(Count(uasm, ": %") <= 460, "fcd56 heap-var count must not inflate (widening cost bound)");
        Assert.True(Count(uasm, "__recurStack") <= 37, "fcd56 spill traffic must not inflate (widening cost bound)");
    }

    [Fact]
    public void MeasurementGate_SameSigWorst_WideningCostBounded()
    {
        var uasm = TestHelper.CompileToUasm(SameSigWorst, "M3SameSigWorst", out _);
        Assert.Contains("__recurStack", uasm);   // the widening DOES protect the same-sig ring
        // Bounds re-measured at the wave-12 [V1] re-shape (non-tail field dispatches, see SameSigWorst
        // comment): 410 heap vars / 41 spill refs — five live frames spilled across their field
        // dispatches. The old 310/21 bound belonged to the tail-dispatch shape, whose only spill
        // traffic was the local g() over-spill the precise analysis retired.
        Assert.True(Count(uasm, ": %") <= 410, "same-sig widening heap-var cost must stay bounded");
        Assert.True(Count(uasm, "__recurStack") <= 41, "same-sig widening spill cost must stay bounded");
    }
}
