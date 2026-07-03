using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 7 — tracked pins for the remaining producer/writer gaps in the object-box
/// variance-laundering reject (DivergingDelegateEvidence / CollectLocalWrites). Real-VM value pins
/// live in the local harness corpus fcd_wave12_envcross_r7.json.
///
/// [X1] A LOCAL FUNCTION identity callee's body was never reached: MethodBody(localFn) resolves to
///      the ILocalFunctionOperation node, and CollectReturns bailed on that node before recursing,
///      so an identity-like local function laundered variance opaquely (unlike the r6 instance-method
///      identity). The invocation tracer now descends into a local function's .Body.
/// [X2] A get-only PROPERTY getter's returned producer was invisible — the walker only ever inspected
///      a property in the setter direction. The walker now traces a visible getter's returns.
/// [X3] A private FIELD holding the narrow delegate boxed to object was invisible. The walker now
///      traces class-family assignments to the field.
/// [X4] An ARRAY ELEMENT holding the narrow delegate boxed to object was invisible. The walker now
///      traces class-family element assignments to the same array symbol.
/// [X5] An inline `out var` argument declaration wrote the local through neither an initializer nor a
///      simple assignment, so its producer was invisible. CollectLocalWrites now follows the local
///      through an out/ref argument into the callee parameter's assignments.
/// [X6] A tuple-deconstruction declaration wrote the local through neither recognized write form.
///      CollectLocalWrites now matches deconstruction targets positionally to their value elements.
/// [X7] A ref-parameter reassignment of an already-declared local was a third invisible writer shape,
///      closed by the same out/ref argument tracing as [X5].
/// </summary>
public class Wave12Round7RegressionTests
{
    // ── [X1] local-function identity launders a covariant delegate ──

    [Fact]
    public void LocalFunctionIdentityBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7Lf : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7Lf other;
    Func<object> bundle;
    string MakeTag() { return ""l"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object Identity(object o) { return o; }
        object boxed = Identity(narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R7Lf"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X2] property getter launders a covariant delegate ──

    [Fact]
    public void PropertyGetterBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7Prop : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7Prop other;
    Func<object> bundle;
    Func<string> narrow;
    string MakeTag() { return ""g"" + seed; }
    object BoxedNarrow { get { return narrow; } }
    void Start() {
        other = this;
        narrow = MakeTag;
        object boxed = BoxedNarrow;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7Prop"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X3] field launders a covariant delegate ──

    [Fact]
    public void FieldBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7Field : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7Field other;
    Func<object> bundle;
    object _stash;
    string MakeTag() { return ""f"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        _stash = narrow;
        object boxed = _stash;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7Field"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X4] array element launders a covariant delegate ──

    [Fact]
    public void ArrayElementBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7Arr : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7Arr other;
    Func<object> bundle;
    string MakeTag() { return ""a"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        var arr = new object[1];
        arr[0] = narrow;
        object boxed = arr[0];
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7Arr"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X5] out-var declaration launders a covariant delegate ──

    [Fact]
    public void OutVarBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7OutVar : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7OutVar other;
    Func<object> bundle;
    string MakeTag() { return ""o"" + seed; }
    void GetBoxed(out object o) { o = (Func<string>)MakeTag; }
    void Start() {
        other = this;
        GetBoxed(out object boxed);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R7OutVar"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X6] tuple deconstruction launders a covariant delegate ──

    [Fact]
    public void DeconstructionBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7Decon : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7Decon other;
    Func<object> bundle;
    string MakeTag() { return ""d"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        (object boxed, int tag) = (narrow, seed);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = (o == null ? -1 : o.ToString().Length) + tag - seed;
    }
}", "W12R7Decon"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X7] ref-parameter reassignment launders a covariant delegate ──

    [Fact]
    public void RefParamBoxedVariantDelegate_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7RefParam : UdonSharpBehaviour {
    public int seed; public int result;
    W12R7RefParam other;
    Func<object> bundle;
    string MakeTag() { return ""r"" + seed; }
    void SetBoxed(ref object o) { o = (Func<string>)MakeTag; }
    void Start() {
        other = this;
        object boxed = null;
        SetBoxed(ref boxed);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R7RefParam"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── controls: same-signature producers keep the reference passthrough (no over-reject) ──

    [Fact]
    public void LocalFunctionIdentitySameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7LfSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> narrow = MakeObj;
        object Identity(object o) { return o; }
        object boxed = Identity(narrow);
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7LfSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void PropertyGetterSameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7PropSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    Func<object> wide;
    object MakeObj() { return ""m"" + seed; }
    object BoxedWide { get { return wide; } }
    void Start() {
        wide = MakeObj;
        object boxed = BoxedWide;
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7PropSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void FieldSameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7FieldSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object _stash;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        _stash = wide;
        object boxed = _stash;
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7FieldSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void ArrayElementSameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7ArrSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        var arr = new object[1];
        arr[0] = wide;
        object boxed = arr[0];
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R7ArrSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void OutVarSameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7OutVarSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void GetBoxed(out object o) { o = (Func<object>)MakeObj; }
    void Start() {
        GetBoxed(out object boxed);
        bundle = (Func<object>)boxed;
        object o2 = bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R7OutVarSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void DeconstructionSameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7DeconSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        (object boxed, int tag) = (wide, seed);
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = (o == null ? -1 : o.ToString().Length) + tag - seed;
    }
}", "W12R7DeconSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void RefParamSameSigDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R7RefParamSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void SetBoxed(ref object o) { o = (Func<object>)MakeObj; }
    void Start() {
        object boxed = null;
        SetBoxed(ref boxed);
        bundle = (Func<object>)boxed;
        object o2 = bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R7RefParamSame");
        Assert.Contains("__dlg", uasm);
    }
}
