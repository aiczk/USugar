using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 8 — tracked pins for the remaining producer/writer gaps in the object-box
/// variance-laundering reject (DivergingDelegateEvidence and its collectors). Real-VM value pins live
/// in the local harness corpus fcd_wave12_envcross_r8.json.
///
/// [X1]/[X6]  An array-creation-WITH-INITIALIZER element (`new object[]{ narrow }`) was invisible —
///            CollectArrayElementWrites only recognized indexer assignments. It now matches the array
///            local's declarator and adds each initializer element (reached directly, through a call-
///            site argument indirection [X3], and through a ternary of two initializer arrays [X6]).
/// [X4]/[X5]  A user `params object[]` call site (`Boxed(narrow)`) desugars to a synthesized array
///            creation passed as the params argument; its elements were invisible. The collector now
///            adds the creation elements of class-family call-site arguments bound to the params param.
/// [X7]-[X10] A null-conditional producer (`other?.BoxedNarrow`, `other?.GetBoxed()`) wrapped the real
///            member access in an IConditionalAccessOperation the walker never stripped. It now traces
///            the .WhenNotNull member (property getter / call / local-function chain).
/// [X11]-[X14] A `??=` writer (local/field/array-element) matched none of the recognized write forms.
///            The three collectors now add the null-coalescing-assignment RHS.
/// </summary>
public class Wave12Round8RegressionTests
{
    static void AssertLaunderReject(string src, string cls)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(src, cls));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X1] array-initializer-list element (covariant) ──
    [Fact]
    public void ArrayInitializerListElementCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Ail : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Ail other;
    Func<object> bundle;
    string MakeTag() { return ""il"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        var arr = new object[] { narrow };
        object boxed = arr[0];
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Ail");

    // ── [X2] array-initializer-list element (contravariant) ──
    [Fact]
    public void ArrayInitializerListElementContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8AilCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8AilCv other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        var arr = new object[] { wideAction };
        object boxed = arr[0];
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""cd"");
    }
}", "W12R8AilCv");

    // ── [X3] array-initializer element through a call-site argument indirection ──
    [Fact]
    public void ArrayInitializerElementArgumentIndirection_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Ind : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Ind other;
    Func<object> bundle;
    string MakeTag() { return ""ai"" + seed; }
    object Identity(object o) { return o; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        var arr = new object[] { narrow };
        object boxed = Identity(arr[0]);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R8Ind");

    // ── [X4] params object[] boxing (covariant) ──
    [Fact]
    public void ParamsArrayCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Pa : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Pa other;
    Func<object> bundle;
    string MakeTag() { return ""pa"" + seed; }
    object Boxed(params object[] xs) { return xs[0]; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = Boxed(narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Pa");

    // ── [X5] params object[] boxing (contravariant) ──
    [Fact]
    public void ParamsArrayContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8PaCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8PaCv other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    object Boxed(params object[] xs) { return xs[0]; }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = Boxed(wideAction);
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""ef"");
    }
}", "W12R8PaCv");

    // ── [X6] ternary over two array-initializer elements ──
    [Fact]
    public void TernaryOfArrayInitializerElements_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Tern : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Tern other;
    Func<object> bundle;
    string MakeTag() { return ""ti"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        Func<string> narrow2 = MakeTag;
        var arr = new object[] { narrow };
        var arr2 = new object[] { narrow2 };
        object boxed = seed > 0 ? arr[0] : arr2[0];
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Tern");

    // ── [X7] conditional-access property getter (covariant) ──
    [Fact]
    public void ConditionalAccessPropertyGetter_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Ca : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Ca other;
    public Func<object> bundle;
    public Func<string> narrow;
    string MakeTag() { return ""ca"" + seed; }
    public object BoxedNarrow { get { return narrow; } }
    void Start() {
        other = this;
        narrow = MakeTag;
        object boxed = other?.BoxedNarrow;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Ca");

    // ── [X8] conditional-access method call ──
    [Fact]
    public void ConditionalAccessMethodCall_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Cm : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Cm other;
    Func<object> bundle;
    string MakeTag() { return ""cm"" + seed; }
    public object GetBoxed() { Func<string> narrow = MakeTag; return narrow; }
    void Start() {
        other = this;
        object boxed = other?.GetBoxed();
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Cm");

    // ── [X9] conditional-access property getter (contravariant) ──
    [Fact]
    public void ConditionalAccessContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8CaCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8CaCv other;
    Action<string> bundle;
    Action<object> wideAction;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    public object BoxedAction { get { return wideAction; } }
    void Start() {
        other = this;
        wideAction = TakeObject;
        object boxed = other?.BoxedAction;
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""gh"");
    }
}", "W12R8CaCv");

    // ── [X10] conditional-access through a local-function identity chain ──
    [Fact]
    public void ConditionalAccessLocalFunction_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Cl : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Cl other;
    Func<object> bundle;
    string MakeTag() { return ""cl"" + seed; }
    public object WrapLocal(object o) { object Pass(object x) { return x; } return Pass(o); }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = other?.WrapLocal(narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R8Cl");

    // ── [X11] `??=` local write (covariant) ──
    [Fact]
    public void CoalesceAssignmentLocalCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Cel : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Cel other;
    Func<object> bundle;
    string MakeTag() { return ""ce"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = null;
        boxed ??= narrow;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Cel");

    // ── [X12] `??=` local write (contravariant) ──
    [Fact]
    public void CoalesceAssignmentLocalContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8CelCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8CelCv other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = null;
        boxed ??= wideAction;
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""ij"");
    }
}", "W12R8CelCv");

    // ── [X13] `??=` field write ──
    [Fact]
    public void CoalesceAssignmentField_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Cef : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Cef other;
    Func<object> bundle;
    object _stash;
    string MakeTag() { return ""fc"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        _stash ??= narrow;
        object boxed = _stash;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Cef");

    // ── [X14] `??=` array-element write ──
    [Fact]
    public void CoalesceAssignmentArrayElement_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R8Cea : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8Cea other;
    Func<object> bundle;
    string MakeTag() { return ""ac"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        var arr = new object[1];
        arr[0] ??= narrow;
        object boxed = arr[0];
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8Cea");

    // ── controls: same-signature producers keep the reference passthrough (no over-reject) ──

    [Fact]
    public void ArrayInitializerListElementSameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R8AilSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        var arr = new object[] { wide };
        object boxed = arr[0];
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8AilSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void ParamsArraySameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R8PaSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    object Boxed(params object[] xs) { return xs[0]; }
    void Start() {
        Func<object> wide = MakeObj;
        object boxed = Boxed(wide);
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8PaSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void ConditionalAccessSameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R8CaSame : UdonSharpBehaviour {
    public int seed; public int result;
    W12R8CaSame other;
    public Func<object> bundle;
    public Func<object> wide;
    object MakeObj() { return ""m"" + seed; }
    public object BoxedWide { get { return wide; } }
    void Start() {
        other = this;
        wide = MakeObj;
        object boxed = other?.BoxedWide;
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8CaSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void CoalesceAssignmentLocalSameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R8CelSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        object boxed = null;
        boxed ??= wide;
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R8CelSame");
        Assert.Contains("__dlg", uasm);
    }
}
