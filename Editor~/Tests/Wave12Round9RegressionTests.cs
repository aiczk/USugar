using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 9 — tracked pins for the remaining producer/writer gaps in the object-box
/// variance-laundering reject (DivergingDelegateEvidence and its collectors). Real-VM value pins live
/// in the local harness corpus fcd_wave12_envcross_r9.json.
///
/// [X1] A simple-assignment EXPRESSION used inline as a value (`object boxed = (_stash = narrow);`) —
///      the assigned RHS is the value that flows out; the top-of-walker switch had no producer case
///      for ISimpleAssignmentOperation. Storage-kind orthogonal (field/local/array-element).
/// [X2] An inline `??=`-as-value (`object boxed = (_stash ??= narrow);`) — ICoalesceAssignmentOperation
///      as a producer value; the switch handled `??` but not `??=` used inline.
/// [X3] A whole-array assignment with an inline initializer to a FIELD (`_arr = new object[]{narrow};`),
///      not a local declarator — CollectArrayElementWrites now matches the assignment target's root.
/// [X4] A PLAIN (non-params) `object[]` parameter passed an inline array creation — the r8 params-array
///      writer case's IsParams guard was relaxed to any array parameter.
/// [X5] A `??=` reassignment of a by-ref parameter inside the callee — CollectParamAssignments now adds
///      the null-coalescing RHS.
/// [X6] A `??=` write through a cross-dispatched property/indexer setter — CollectParamEvidence's
///      setterProp branch now adds the null-coalescing RHS.
/// [X7] A `foreach` LOOP CONTROL VARIABLE — CollectLocalWrites now sources the collection's elements.
/// </summary>
public class Wave12Round9RegressionTests
{
    static void AssertLaunderReject(string src, string cls)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(src, cls));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [X1] simple-assignment-expression-as-value: field (covariant) ──
    [Fact]
    public void SimpleAssignExprAsValueFieldCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9SaF : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9SaF other;
    Func<object> bundle;
    object _stash;
    string MakeTag() { return ""sv"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = (_stash = narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9SaF");

    // ── [X1] simple-assignment-expression-as-value: field (contravariant) ──
    [Fact]
    public void SimpleAssignExprAsValueFieldContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9SaFcv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9SaFcv other;
    Action<string> bundle;
    object _stash;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = (_stash = wideAction);
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""kl"");
    }
}", "W12R9SaFcv");

    // ── [X1] simple-assignment-expression-as-value: local target ──
    [Fact]
    public void SimpleAssignExprAsValueLocal_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9SaL : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9SaL other;
    Func<object> bundle;
    string MakeTag() { return ""sl"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object sink = null;
        object boxed = (sink = narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9SaL");

    // ── [X1] simple-assignment-expression-as-value: array-element target ──
    [Fact]
    public void SimpleAssignExprAsValueArrayElement_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9SaA : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9SaA other;
    Func<object> bundle;
    string MakeTag() { return ""sa"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        var arr = new object[1];
        object boxed = (arr[0] = narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9SaA");

    // ── [X2] `??=`-expression-as-value: field (covariant) ──
    [Fact]
    public void CoalesceAssignExprAsValueFieldCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9CaF : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9CaF other;
    Func<object> bundle;
    object _stash;
    string MakeTag() { return ""cv"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = (_stash ??= narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9CaF");

    // ── [X2] `??=`-expression-as-value: field (contravariant) ──
    [Fact]
    public void CoalesceAssignExprAsValueFieldContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9CaFcv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9CaFcv other;
    Action<string> bundle;
    object _stash;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = (_stash ??= wideAction);
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""mn"");
    }
}", "W12R9CaFcv");

    // ── [X2] `??=`-expression-as-value: local target ──
    [Fact]
    public void CoalesceAssignExprAsValueLocal_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9CaL : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9CaL other;
    Func<object> bundle;
    string MakeTag() { return ""cl"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object sink = null;
        object boxed = (sink ??= narrow);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9CaL");

    // ── [X3] field-targeted array-initializer list (covariant) ──
    [Fact]
    public void FieldTargetedArrayInitializerCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9Fa : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9Fa other;
    Func<object> bundle;
    object[] _arr;
    string MakeTag() { return ""fa"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        _arr = new object[] { narrow };
        object boxed = _arr[0];
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9Fa");

    // ── [X3] field-targeted array-initializer list (contravariant) ──
    [Fact]
    public void FieldTargetedArrayInitializerContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9FaCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9FaCv other;
    Action<string> bundle;
    object[] _arr;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        _arr = new object[] { wideAction };
        object boxed = _arr[0];
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""op"");
    }
}", "W12R9FaCv");

    // ── [X4] plain (non-params) array-parameter initializer list (covariant) ──
    [Fact]
    public void PlainArrayParameterInitializerCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9Pp : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9Pp other;
    Func<object> bundle;
    string MakeTag() { return ""pp"" + seed; }
    object Boxed(object[] xs) { return xs[0]; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = Boxed(new object[] { narrow });
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9Pp");

    // ── [X4] plain array-parameter initializer list (contravariant) ──
    [Fact]
    public void PlainArrayParameterInitializerContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9PpCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9PpCv other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    object Boxed(object[] xs) { return xs[0]; }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = Boxed(new object[] { wideAction });
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""qr"");
    }
}", "W12R9PpCv");

    // ── [X5] ref-parameter `??=` reassignment in the callee (covariant) ──
    [Fact]
    public void RefParamCoalesceReassignmentCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9Rp : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9Rp other;
    Func<object> bundle;
    string MakeTag() { return ""rp"" + seed; }
    object MakeNarrow() { Func<string> narrow = MakeTag; return narrow; }
    void SetBoxed(ref object o) { o ??= MakeNarrow(); }
    void Start() {
        other = this;
        object boxed = null;
        SetBoxed(ref boxed);
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o2 = other.bundle();
        result = o2 == null ? -1 : o2.ToString().Length;
    }
}", "W12R9Rp");

    // ── [X6] cross-dispatched property setter `??=` write ──
    [Fact]
    public void PropertySetterCoalesceAssignment_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9Ps : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9Ps other;
    Func<object> bundle;
    object _store;
    string MakeTag() { return ""ps"" + seed; }
    public object Store { get { return _store; } set { _store = value; } }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        other.Store ??= narrow;
        object boxed = other.Store;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9Ps");

    // ── [X6] cross-dispatched indexer setter `??=` write ──
    [Fact]
    public void IndexerSetterCoalesceAssignment_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9Ix : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9Ix other;
    Func<object> bundle;
    object _slot;
    string MakeTag() { return ""ix"" + seed; }
    public object this[int i] { get { return _slot; } set { _slot = value; } }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        other[0] ??= narrow;
        object boxed = other[0];
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9Ix");

    // ── [X7] foreach loop control variable (covariant) ──
    [Fact]
    public void ForeachLoopControlVariableCovariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9Fe : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9Fe other;
    Func<object> bundle;
    string MakeTag() { return ""fe"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        var arr = new Func<string>[] { narrow };
        Func<object> wide = null;
        foreach (object boxed in arr) {
            wide = (Func<object>)boxed;
        }
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9Fe");

    // ── [X7] foreach loop control variable (contravariant) ──
    [Fact]
    public void ForeachLoopControlVariableContravariant_Throws() => AssertLaunderReject(@"
using System;
using UdonSharp;
public class W12R9FeCv : UdonSharpBehaviour {
    public int seed; public int result;
    W12R9FeCv other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        var arr = new Action<object>[] { wideAction };
        Action<string> narrowBundle = null;
        foreach (object boxed in arr) {
            narrowBundle = (Action<string>)boxed;
        }
        other.bundle = narrowBundle;
        other.bundle(""st"");
    }
}", "W12R9FeCv");

    // ── controls: same-signature producers keep the reference passthrough (no over-reject) ──

    [Fact]
    public void SimpleAssignExprAsValueSameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R9SaSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object _stash;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        object boxed = (_stash = wide);
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9SaSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void CoalesceAssignExprAsValueSameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R9CaSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object _stash;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        object boxed = (_stash ??= wide);
        bundle = (Func<object>)boxed;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9CaSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void ForeachLoopControlVariableSameSig_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R9FeSame : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    object MakeObj() { return ""m"" + seed; }
    void Start() {
        Func<object> wide = MakeObj;
        var arr = new Func<object>[] { wide };
        Func<object> pick = null;
        foreach (object boxed in arr) {
            pick = (Func<object>)boxed;
        }
        bundle = pick;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R9FeSame");
        Assert.Contains("__dlg", uasm);
    }
}
