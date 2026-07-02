using Xunit;

namespace USugar.Tests;

/// <summary>
/// Pre-wave-12 comprehension refactor (item 2): EmitContext.HasNonTailSelfCall (delegate-dispatch-site
/// tail classification, via IsNonTailDispatchSite) and UasmEmitter.HasNonTailCallTo (named-callee
/// recursion-spill tail classification) used to be two hand-copied walkers; both now delegate to the
/// single shared <c>TailCallAnalysis</c>. These are consolidated battery pins proving the unification
/// preserved each caller's exact tail-position answer across the shape families both walkers handle
/// (plain call, statement-form if/switch/labeled, compound/inc-dec property write, conditional-access
/// dispatch, return-position ternary) — a regression here means the shared walk drifted from what
/// either original did. Oracled against the actual pre-dedup two-classifier compiler via a differential
/// worktree (a real diff of emitted UASM, not hand-derived expectations — see the pre-wave-12 handoff
/// note for the method): a same-UASM diff across a dozen shapes including deeply nested/mixed ternaries
/// found zero divergence, so every assertion below is the VERIFIED pre-dedup answer.
///
/// Return-position ternary recursion (`return cond ? Rec(a) : Rec(b);`) turned out to be spill-free for
/// BOTH the named-callee and the dispatch-site classifier even before this dedup — TailCallAnalysis's
/// file header explains why (BuildRecursionInfo's per-CALLEE HasNonTailCallTo answer only gates whether
/// a callee is registered as a possible recursion edge; the actual per-SITE spill/reload decision
/// always re-runs through the ternary-precise dispatch-site classifier, for named call sites too). The
/// `_MixedNonTailBranch_Spills` pins are the case with a REAL non-tail leg, which does force a spill.
/// </summary>
public class PreWave12TailDedupTests
{
    // ── named-callee (UasmEmitter.HasNonTailCallTo) battery ──

    [Fact]
    public void NamedRecursion_PlainTailCall_NoSpill()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12Plain : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { result = M(n % 50 + 600); }
    public int M(int m) => m <= 0 ? 0 : M(m - 1);
}", "PW12Plain");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void NamedRecursion_SwitchArmTail_NoSpill()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12Switch : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(n % 3 + 600); result = trace; }
    public void M(int m) {
        trace = (trace + m) % 99991;
        switch (m & 1) {
            case 0: if (m > 0) M(m - 1); break;
            default: if (m > 0) M(m - 1); break;
        }
    }
}", "PW12Switch");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void NamedRecursion_LabeledTail_NoSpill()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12Labeled : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { result = M(n % 50 + 600); }
    public int M(int m) {
        if (m <= 0) return 0;
    TAIL:
        return M(m - 1);
    }
}", "PW12Labeled");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void NamedRecursion_NonTailPlainCall_Spills()
    {
        // Control: frame state is read AFTER the call — must spill.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12PlainCtl : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { result = M(n % 50 + 6); }
    public int M(int m) {
        if (m <= 0) return 0;
        return M(m - 1) + 1;
    }
}", "PW12PlainCtl");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void NamedRecursion_ReturnTernary_BothBranchesTail_NoSpill()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12Ternary : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { result = M(n % 50 + 600); }
    public int M(int m) => m <= 0 ? 0 : (m % 2 == 0 ? M(m - 1) : M(m - 1));
}", "PW12Ternary");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void NamedRecursion_ReturnTernary_MixedNonTailBranch_Spills()
    {
        // One branch is a bare tail call, the other adds after it — a genuine non-tail leg exists
        // (found by the generic non-tail descent regardless of ternary-branch awareness), so this
        // must spill under both the pre- and post-dedup classifier.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12TernaryMixed : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { result = M(n % 50 + 6); }
    public int M(int m) {
        if (m <= 0) return 0;
        return m % 2 == 0 ? M(m - 1) : M(m - 1) + 1;
    }
}", "PW12TernaryMixed");
        Assert.Contains("__recurStack", uasm);
    }

    // ── delegate-dispatch-site (EmitContext.IsNonTailDispatchSite) battery ──

    [Fact]
    public void DispatchSite_ReturnTernary_BothBranchesTail_NoSpill()
    {
        // Dispatch twin of NamedRecursion_ReturnTernary_BothBranchesTail_NoSpill: a capturing recursive
        // local function invoked through its delegate bundle instead of a named call.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12DispatchTernary : UdonSharpBehaviour {
    public int n; public int result;
    void Start() {
        System.Func<int, int> f = null;
        f = m => m <= 0 ? 0 : (m % 2 == 0 ? f(m - 1) : f(m - 1));
        result = f(n % 50 + 600);
    }
}", "PW12DispatchTernary");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void DispatchSite_NonTailCall_Spills()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12DispatchCtl : UdonSharpBehaviour {
    public int n; public int result;
    void Start() {
        System.Func<int, int> f = null;
        f = m => m <= 0 ? 0 : f(m - 1) + 1;
        result = f(n % 50 + 6);
    }
}", "PW12DispatchCtl");
        Assert.Contains("__recurStack", uasm);
    }

    // ── property-accessor arms (getter/setter disambiguation, named-callee only) ──

    [Fact]
    public void CompoundPropertyWrite_TailStatement_NoSpill()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12CompProp : UdonSharpBehaviour {
    int q;
    public int n; public int result;
    public int Q {
        get { return q; }
        set { q = value; if (q <= 0) return; Q -= 1; }
    }
    void Start() { Q = n % 2 + 600; result = q; }
}", "PW12CompProp");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    [Fact]
    public void IndexerCompoundWrite_TailStatement_NoSpill()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PW12IdxProp : UdonSharpBehaviour {
    int cell;
    public int n; public int result;
    public int this[int i] {
        get { return cell; }
        set { cell = value; if (cell <= 0) return; this[0] += -1; }
    }
    void Start() { this[0] = n % 2 + 600; result = cell; }
}", "PW12IdxProp");
        Assert.DoesNotContain("__recurStack", uasm);
    }
}
