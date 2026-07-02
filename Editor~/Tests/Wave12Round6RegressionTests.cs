using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 6 — tracked pins for the switch-expression / method-call producer gaps in the
/// object-box variance-laundering reject (real-VM value pins live in the local harness corpus
/// fcd_wave12_envcross_r6.json):
///
/// [X1-X4] DivergingDelegateEvidence unwrapped ternary (IConditionalOperation) and null-coalesce
///      (ICoalesceOperation) arms but had NO case for ISwitchExpressionOperation, so a switch
///      expression sat as an opaque producer: a variant delegate boxed through a `seed switch {...}`
///      arm and cast back defeated the whole [V2]/round-4/round-5 gate family (VM-proven silent loss:
///      covariant Func ref=2 vs -1, contravariant Action ref=7 vs 5). The evidence walker now
///      recurses through every switch-expression arm value, so the gap closes on all four reach
///      channels the collectors already cover: a local write, a call-site ARGUMENT
///      (CollectParamEvidence's argument path), and a cross-dispatched setter's `value` parameter.
///
/// [X5] DivergingDelegateEvidence had no case AT ALL for IInvocationOperation as a producer: an
///      ordinary same-class method call's return value opaquely defeated the reject with zero
///      indirection through switch/ternary/generics (VM-proven ref=2 vs -1). The walker now traces
///      into a visible callee's return expressions; a return that yields a parameter is then mapped
///      back to the call-site arguments by the existing IParameterReferenceOperation branch, so an
///      identity-like helper no longer launders variance opaquely.
/// </summary>
public class Wave12Round6RegressionTests
{
    // ── [X1] switch-expression covariant Func laundering (local write) ──

    [Fact]
    public void SwitchExprBoxedCovariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6SwCo : UdonSharpBehaviour {
    public int seed; public int result;
    W12R6SwCo other;
    Func<object> bundle;
    string MakeTag() { return ""w"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = seed switch { > 0 => (object)narrow, _ => (object)narrow };
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R6SwCo"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
        Assert.Contains("Func<string>", ex.Message);
        Assert.Contains("Func<object>", ex.Message);
    }

    // ── [X2] switch-expression contravariant Action laundering ──

    [Fact]
    public void SwitchExprBoxedContravariantAction_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6SwContra : UdonSharpBehaviour {
    public int seed; public int result;
    W12R6SwContra other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = seed switch { > 0 => (object)wideAction, _ => (object)wideAction };
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""ab"");
    }
}", "W12R6SwContra"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("Action<object>", ex.Message);
        Assert.Contains("Action<string>", ex.Message);
    }

    // ── [X3] switch-expression reached through a call-site ARGUMENT ──

    [Fact]
    public void SwitchExprArgumentBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6SwArg : UdonSharpBehaviour {
    public int seed; public int result;
    W12R6SwArg other;
    Func<object> bundle;
    string MakeTag() { return ""p"" + seed; }
    Func<object> Identity(object boxed) { return (Func<object>)boxed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        Func<object> wide = Identity(seed switch { > 0 => (object)narrow, _ => (object)narrow });
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R6SwArg"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X4] switch-expression reached through a cross-dispatched setter's `value` ──

    [Fact]
    public void SwitchExprSetterValueBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6SwSet : UdonSharpBehaviour {
    public int seed; public int result;
    W12R6SwSet other;
    public Func<object> store;
    public object Boxed { set { store = (Func<object>)value; } }
    string MakeTag() { return ""s"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        other.Boxed = seed switch { > 0 => (object)narrow, _ => (object)narrow };
        object o = other.store();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R6SwSet"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X5] method-call return value as the object-boxed producer ──

    [Fact]
    public void MethodCallReturnBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6Call : UdonSharpBehaviour {
    public int seed; public int result;
    W12R6Call other;
    Func<object> bundle;
    string MakeTag() { return ""c"" + seed; }
    object Identity(object o) { return o; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = Identity(narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R6Call"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── controls: the pinned accepted boundary keeps flowing (no over-reject) ──

    [Fact]
    public void SwitchExprBoxedSameSigDelegate_Compiles()
    {
        // Both arms carry a SAME-sig (Func<object>) delegate, so the switch-expression evidence
        // agrees with the destination — the reference passthrough must stay.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6SwSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeA() { return ""a"" + seed; }
    object MakeB() { return ""b"" + seed; }
    void Start() {
        Func<object> a = MakeA;
        Func<object> b = MakeB;
        object boxed = seed switch { > 0 => (object)a, _ => (object)b };
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R6SwSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void MethodCallReturnSameSigDelegate_Compiles()
    {
        // The call returns its argument, and every class-visible argument feeding it is a same-sig
        // Func<object> — the invocation tracer finds no divergence, so the cast keeps flowing.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R6CallSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    object Identity(object o) { return o; }
    void Start() {
        Func<object> narrow = MakeObj;
        object boxed = Identity(narrow);
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R6CallSame");
        Assert.Contains("__dlg", uasm);
    }
}
