using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 4 — tracked pins for the three findings (real-VM value pins live in the local
/// harness corpus _w12_r4_min.json):
///
/// [W1]/[W2] The round-2 [V2] variant delegate-VALUE conversion gate fired only when a side of the
///      conversion was itself a delegate type, so the same channel divergence laundered straight
///      through an aggregate-typed conversion node: array COVARIANCE (Func&lt;string&gt;[] →
///      Func&lt;object&gt;[], the conversion is on the ARRAY type) and the element-wise TUPLE conversion
///      ((Func&lt;string&gt;, int) → (Func&lt;object&gt;, int)) both compiled clean and silently dropped the
///      return across the dispatch (VM-proven ref=2 vs -1 on both shapes: the callee bridge writes
///      __dlgc_Void__SystemString__ret while the invoke reads __dlgc_Void__SystemObject__ret).
///      ExpressionHandler.ContainsVariantDelegateConversion now recurses through array elements and
///      tuple elements with the same sig-part-divergence criterion, and VisitConversion rejects an
///      array/tuple-typed conversion that hides a variant delegate the same loud way. Equal-sig
///      element flows and object-laundering (an `object` destination element is not a delegate)
///      are untouched.
///
/// [W3] Binding a method group off an INTERFACE-typed variable receiver (`cb = iface.Get`) threw a
///      raw InvalidOperationException ('No delegate bridge for ...') from
///      LayoutPlanner.GetDelegateBridgeLayout — an ICE, neither loud-by-design nor correct. It
///      cannot compile correctly today: bundle[1] is SendCustomEvent'd on the runtime receiver, so
///      it must name a __dlgc_-convention bridge export derivable from the interface member alone,
///      and implementers only export bridges named by their own implementing methods. Rejected
///      loudly in ResolveDelegateBridge (§8-3), pointing at the supported lambda wrapping
///      (`() => iface.Get()` — VM-proven Match in the corpus control).
/// </summary>
public class Wave12Round4RegressionTests
{
    // ── [W1] array covariance hiding a variant delegate conversion ──

    [Fact]
    public void ArrayCovarianceDelegateConversion_Throws()
    {
        // Verbatim minimized repro (VM-proven ref=2, usugar -1: return silently dropped).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4ArrCo : UdonSharpBehaviour {
    public int seed; public int result;
    string Tag() { return ""a"" + seed; }
    void Start() {
        Func<string>[] narrow = new Func<string>[1];
        narrow[0] = Tag;
        Func<object>[] wide = narrow;
        object o = wide[0]();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R4ArrCo"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("Func<string>[]", ex.Message);
        Assert.Contains("Func<object>[]", ex.Message);
    }

    // ── [W2] tuple conversion hiding a variant delegate element ──

    [Fact]
    public void TupleElementDelegateCovarianceConversion_Throws()
    {
        // Verbatim minimized repro (VM-proven ref=2, usugar -1).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4TupCo : UdonSharpBehaviour {
    public int seed; public int result;
    string Tag() { return ""t"" + seed; }
    void Start() {
        Func<string> d = Tag;
        (Func<string> f, int tag) narrow = (d, 5);
        (Func<object> f, int tag) wide = narrow;
        object o = wide.f();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R4TupCo"));
        Assert.Contains("Variant delegate conversion", ex.Message);
    }

    [Fact]
    public void TupleOfDelegateArrayCovariance_RecursesThroughBothLayers_Throws()
    {
        // Deep flavor: the variant delegate sits under tuple → array → delegate, so the check must
        // recurse through BOTH aggregate layers to see it. (The inverse nesting — array of tuples —
        // is not convertible in C# at all: value-tuple element conversions are not reference
        // conversions, so array covariance never applies to them.)
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4Deep : UdonSharpBehaviour {
    public int seed; public int result;
    string Tag() { return ""d"" + seed; }
    void Start() {
        Func<string>[] arr = new Func<string>[1];
        arr[0] = Tag;
        (Func<string>[] fs, int tag) narrow = (arr, 5);
        (Func<object>[] fs, int tag) wide = narrow;
        object o = wide.fs[0]();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R4Deep"));
        Assert.Contains("Variant delegate conversion", ex.Message);
    }

    // ── [W1]/[W2] controls: same-sig aggregate flows and object-laundering stay legal ──

    [Fact]
    public void SameDelegateTypeArrayFlow_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4ArrSame : UdonSharpBehaviour {
    public int seed; public int result;
    string Tag() { return ""c"" + seed; }
    void Start() {
        Func<string>[] arr = new Func<string>[1];
        arr[0] = Tag;
        Func<string>[] same = arr;
        string s = same[0]();
        result = s == null ? -1 : s.Length;
    }
}", "W12R4ArrSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void SameDelegateTypeTupleFlow_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4TupSame : UdonSharpBehaviour {
    public int seed; public int result;
    string Tag() { return ""s"" + seed; }
    void Start() {
        Func<string> d = Tag;
        (Func<string> f, int tag) t = (d, 5);
        string s = t.f();
        result = (s == null ? -1 : s.Length) + t.tag;
    }
}", "W12R4TupSame");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void DelegateArrayToObjectArray_Launder_Compiles()
    {
        // The documented accepted boundary, one aggregate layer up: an `object` destination element
        // is NOT a delegate, so the covariant Func<string>[] → object[] flow is statically invisible
        // variance-wise and must stay accepted (cast-back to the SAME delegate type, channels agree).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4ObjArr : UdonSharpBehaviour {
    public int seed; public int result;
    string Tag() { return ""o"" + seed; }
    void Start() {
        Func<string>[] arr = new Func<string>[1];
        arr[0] = Tag;
        object[] wide = arr;
        Func<string> back = (Func<string>)wide[0];
        string s = back();
        result = s == null ? -1 : s.Length;
    }
}", "W12R4ObjArr");
        Assert.Contains("__dlg", uasm);
    }

    // ── [W3] interface method group off a variable receiver: clean loud reject, not an ICE ──

    [Fact]
    public void InterfaceMethodGroup_VariableReceiver_RejectsLoudly()
    {
        // Pre-fix this threw a raw InvalidOperationException ('No delegate bridge for 'Get' on
        // 'W12R4IProv'') from the planner — this pin fails on the exception TYPE until the fix.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface W12R4IProv { int Get(); }
public class W12R4IfaceMg : UdonSharpBehaviour, W12R4IProv {
    public int seed; public int result;
    W12R4IProv iface;
    Func<int> cb;
    public int Get() { return seed * 3; }
    void Start() {
        iface = this;
        cb = iface.Get;
        result = cb();
    }
}", "W12R4IfaceMg"));
        Assert.Contains("interface member 'W12R4IProv.Get'", ex.Message);
        Assert.Contains("lambda", ex.Message);
    }

    // ── [W3] controls: class-receiver method groups and the lambda wrapping stay legal ──

    [Fact]
    public void ClassTypedVariableReceiverMethodGroup_Compiles()
    {
        // The planner-bridge path for a CONCRETE class receiver is untouched.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R4ClsMg : UdonSharpBehaviour {
    public int seed; public int result;
    W12R4ClsMg self;
    Func<int> cb;
    public int Get() { return seed * 3; }
    void Start() {
        self = this;
        cb = self.Get;
        result = cb();
    }
}", "W12R4ClsMg");
        Assert.Contains("__dlg_Get", uasm);
    }

    [Fact]
    public void LambdaWrappedInterfaceCall_Compiles()
    {
        // The reject message's suggested workaround (VM-proven Match in _w12_r4_min.json): the
        // lambda captures nothing (iface is a field) and dispatches through the interface-call
        // convention's canonical __iface_ entry point.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface W12R4IProv2 { int Get(); }
public class W12R4IfaceLam : UdonSharpBehaviour, W12R4IProv2 {
    public int seed; public int result;
    W12R4IProv2 iface;
    Func<int> cb;
    public int Get() { return seed * 3; }
    void Start() {
        iface = this;
        cb = () => iface.Get();
        result = cb();
    }
}", "W12R4IfaceLam");
        Assert.Contains("__iface_", uasm);
    }
}
