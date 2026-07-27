using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Multicast delegate combine/remove + fan-out (design 2026-07-03 §1, A-M1). Structural/UASM-level
/// pins for the headless main suite — real VM VALUE verification (fan-out order/env/last-ret, combine/
/// remove list algebra vs BCL, null rules, single-collapse) lives in
/// Editor~/_local_harness/MulticastDelegateVmTests.cs (DiffFuzz CLR-vs-UVM oracle) since this project
/// has no VM. These tests confirm: the synthesis is additive (untouched by non-multicast classes), the
/// per-sig dedup, the reject surface (ref/out, tuple-return), and the D1 reference-equality deviation.
/// </summary>
public class MulticastDelegateTests
{
    // ── `+=`/`-=` lower to the combine/remove helpers + fan-out bridge (no reject) ──

    [Fact]
    public void PlusEquals_OnDelegateField_EmitsCombineAndFanout()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgField : UdonSharpBehaviour {
    public Action callback;
    void Start() { callback += () => { }; }
}", "DlgField");
        Assert.Contains("__dlg_combine_Void__Void", uasm);
        Assert.Contains("__dlg_fanout_Void__Void", uasm);
        Assert.DoesNotContain("__dlg_remove_Void__Void", uasm);
        // §1.3: single-cast VisitDelegateCreation is unchanged — the lambda's OWN bridge still mints
        // via the plain __dlg_ prefix, distinct from the sig-level fan-out/combine/remove names.
        Assert.Contains(".export __dlg_fanout_Void__Void", uasm);
    }

    [Fact]
    public void MinusEquals_OnDelegateField_EmitsRemoveAndFanout()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgFieldMinus : UdonSharpBehaviour {
    public Action h;
    public Action callback;
    void Start() { h = () => { }; callback -= h; }
}", "DlgFieldMinus");
        Assert.Contains("__dlg_remove_Void__Void", uasm);
        Assert.Contains("__dlg_fanout_Void__Void", uasm);
        Assert.DoesNotContain("__dlg_combine_Void__Void", uasm);
    }

    [Fact]
    public void PlusEquals_OnDelegateLocal_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgLocal : UdonSharpBehaviour {
    public int hits;
    void Start() {
        Action d = null;
        d += () => hits++;
        d += () => hits++;
        d();
    }
}", "DlgLocal");

    // ── additive-only: a class with no delegate `+=`/`-=` mints none of this synthesis (§6 gate) ──

    [Fact]
    public void NoCompoundAssignment_EmitsNoMulticastSynthetics()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgPlainInvoke : UdonSharpBehaviour {
    public Action callback;
    void Start() { callback?.Invoke(); }
}", "DlgPlainInvoke");
        Assert.DoesNotContain("__dlg_combine_", uasm);
        Assert.DoesNotContain("__dlg_remove_", uasm);
        Assert.DoesNotContain("__dlg_fanout_", uasm);
    }

    // ── per-sig dedup: two `+=` sites sharing a signature mint the helper/bridge exactly once ──

    [Fact]
    public void TwoPlusEqualsSitesSharingSig_MintOnlyOneHelperSet()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgTwoSites : UdonSharpBehaviour {
    public Action a;
    public Action b;
    void Start() {
        a += () => { };
        b += () => { };   // same sig (Action) — must reuse a's __dlg_combine_/__dlg_fanout_
    }
}", "DlgTwoSites");
        int Count(string needle)
        {
            int c = 0, i = 0;
            while ((i = uasm.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
            return c;
        }
        // Each function is declared exactly once: one label + one (for the fan-out) export directive.
        Assert.Equal(1, Count("____dlg_combine_Void__Void:"));
        Assert.Equal(1, Count(".export __dlg_fanout_Void__Void"));
        Assert.DoesNotContain("__dlg_remove_Void__Void", uasm);
    }

    [Fact]
    public void PlusAndMinusSharingSig_EmitBothHelpersAndOneFanout()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgBothOps : UdonSharpBehaviour {
    public Action callback;
    void Handler() { }
    void Start() { callback += Handler; callback -= Handler; }
}", "DlgBothOps");
        Assert.Contains("__dlg_combine_Void__Void", uasm);
        Assert.Contains("__dlg_remove_Void__Void", uasm);
        Assert.Contains(".export __dlg_fanout_Void__Void", uasm);
    }

    // ── D1 (§1.5/§4): whole-multicast `==` stays reference equality — it must NEVER reference the
    //    multicast machinery (fan-out/combine/remove); it is the SAME CompareDelegates env-slot
    //    reference-equality leg single-cast `==` already used before A-M1. ──

    [Fact]
    public void DelegateEquality_NeverReferencesMulticastMachinery()
    {
        // No `+=`/`-=` anywhere — if `==` needed any multicast synthesis, this file would contain it.
        // It doesn't: CompareDelegates (§2.5, reused verbatim) only ever does SystemObjectArray.__Get +
        // SystemObject.__op_Equality on Target/Method/Env — reference equality on the env slot, never a
        // structural list walk. This is D1 (§1.5/§4): whole-multicast `==` deliberately stays reference
        // equality, not upgraded to compare invocation lists.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgEq : UdonSharpBehaviour {
    public Action a;
    public Action b;
    public bool eq;
    void Start() { eq = (a == b); }
}", "DlgEq");
        Assert.DoesNotContain("__dlg_combine_", uasm);
        Assert.DoesNotContain("__dlg_remove_", uasm);
        Assert.DoesNotContain("__dlg_fanout_", uasm);
        Assert.Contains("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean", uasm);
    }

    // ── Reject surface inherited from delegate creation policy (§3.4-1, §R9) ──

    [Fact]
    public void PlusEquals_OnRefOutDelegate_EmitsFanoutAndCopyBack()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public delegate void RefDel(ref int x);
public class DlgRefOut : UdonSharpBehaviour {
    RefDel d;
    void Bump(ref int x) { x++; }
    void Start() { d += Bump; }
}", "DlgRefOut");
        Assert.Contains("__dlg_combine_", uasm);
        Assert.Contains("__dlg_fanout_", uasm);
    }

    // ── tuple-return delegate `+=` (Stage 1.75 design 2026-07-04 §1.2 bullet 6): SUPPORTED ──
    // The fan-out's last-ret is a type-blind COPY of the packed SystemObjectArray aggregate — no
    // multicast-side change needed (probe P1). Real VM values: Editor~/_local_harness DiffFuzz.

    [Fact]
    public void PlusEquals_OnTupleReturnDelegate_EmitsCombineAndFanout()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public delegate (int, int) PairDel();
public class DlgTupleRet : UdonSharpBehaviour {
    PairDel d;
    (int, int) Pair() => (1, 2);
    void Start() { d += Pair; }
}", "DlgTupleRet");
        Assert.Contains("__dlg_combine_Void__SystemObjectArray", uasm);
        Assert.Contains("__dlg_fanout_Void__SystemObjectArray", uasm);
    }
}
