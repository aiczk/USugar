using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// M1 pins for the first-class delegate ABI (design 2026-06-10):
/// - DelegateAbi.BuildSigPart is the single sig builder (SystemObjectArray token for delegate-typed params)
///   and must agree between the delegate-type side (caller) and the target-method side (bridge).
/// - ValidateDelegateBinding rejects ref/out, variant method groups, and tuple returns loudly.
/// - §2.8(b) capture-escape guard: capturing lambdas cannot be stored into arrays/objects or returned.
/// - Delegate casts are reference passthrough (no Convert extern — fcd25 audit).
/// </summary>
public class DelegateAbiTests
{
    // ── BuildSigPart unit tests ──

    static CSharpCompilation AbiCompile(string src) =>
        CSharpCompilation.Create("AbiTest",
            new[] { CSharpSyntaxTree.ParseText(src) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    const string AbiSrc = @"
public delegate void RefDel(ref int x);
public delegate (int, int) TupleDel();
public class C {
    public System.Func<int, int> F;
    public System.Action A;
    public System.Func<System.Func<int, int>, int> G;
    public System.Func<object> O;
    public RefDel R;
    public TupleDel T;
    public int M(int x) => x;
    public string GetStr() => ""s"";
}";

    static INamedTypeSymbol DelegateOf(CSharpCompilation c, string field) =>
        (INamedTypeSymbol)c.GetTypeByMetadataName("C").GetMembers(field).OfType<IFieldSymbol>().Single().Type;

    static IMethodSymbol MethodOf(CSharpCompilation c, string name) =>
        c.GetTypeByMetadataName("C").GetMembers(name).OfType<IMethodSymbol>().Single();

    [Fact]
    public void BuildSigPart_FuncIntInt_UsesUdonTypeNames()
    {
        var c = AbiCompile(AbiSrc);
        Assert.Equal("SystemInt32__SystemInt32",
            DelegateAbi.BuildSigPart(DelegateOf(c, "F").DelegateInvokeMethod));
    }

    [Fact]
    public void BuildSigPart_Action_IsVoidVoid()
    {
        var c = AbiCompile(AbiSrc);
        Assert.Equal("Void__Void",
            DelegateAbi.BuildSigPart(DelegateOf(c, "A").DelegateInvokeMethod));
    }

    [Fact]
    public void BuildSigPart_DelegateOfDelegate_UsesSystemObjectArrayToken()
    {
        // The Stage-1 ABI token for a delegate-typed param is SystemObjectArray (bundle reference) —
        // builder identity: every consumer (caller convention, bridge, conv-var decl) shares this output.
        var c = AbiCompile(AbiSrc);
        Assert.Equal("SystemObjectArray__SystemInt32",
            DelegateAbi.BuildSigPart(DelegateOf(c, "G").DelegateInvokeMethod));
    }

    [Fact]
    public void BuildSigPart_DelegateInvokeAndExactTargetMethod_Agree()
    {
        // Caller derives the __dlgc_ name from the delegate type, the bridge from the target method —
        // for an exact (non-variant) binding the two derivations must be byte-identical.
        var c = AbiCompile(AbiSrc);
        Assert.Equal(
            DelegateAbi.BuildSigPart(DelegateOf(c, "F").DelegateInvokeMethod),
            DelegateAbi.BuildSigPart(MethodOf(c, "M")));
    }

    // ── ValidateDelegateBinding unit tests ──

    [Fact]
    public void ValidateDelegateBinding_RefOutParams_Throws()
    {
        var c = AbiCompile(AbiSrc);
        var ex = Assert.Throws<NotSupportedException>(
            () => DelegateAbi.ValidateDelegateBinding(DelegateOf(c, "R"), null));
        Assert.Contains("ref/out", ex.Message);
    }

    [Fact]
    public void ValidateDelegateBinding_TupleReturn_Throws()
    {
        var c = AbiCompile(AbiSrc);
        var ex = Assert.Throws<NotSupportedException>(
            () => DelegateAbi.ValidateDelegateBinding(DelegateOf(c, "T"), null));
        Assert.Contains("Tuple-return", ex.Message);
    }

    [Fact]
    public void ValidateDelegateBinding_VariantMethodGroup_Throws()
    {
        // Func<object> bound to a string-returning method: legal C#, but the __dlgc_ names diverge.
        var c = AbiCompile(AbiSrc);
        var ex = Assert.Throws<NotSupportedException>(
            () => DelegateAbi.ValidateDelegateBinding(DelegateOf(c, "O"), MethodOf(c, "GetStr")));
        Assert.Contains("Variant method-group", ex.Message);
    }

    [Fact]
    public void ValidateDelegateBinding_ExactMethodGroup_Passes()
    {
        var c = AbiCompile(AbiSrc);
        DelegateAbi.ValidateDelegateBinding(DelegateOf(c, "F"), MethodOf(c, "M"));
    }

    // ── End-to-end reject pins ──

    [Fact]
    public void DelegateField_RefParamSignature_ThrowsNotSupported()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public delegate void RefDel(ref int x);
public class RefDlg : UdonSharpBehaviour {
    public RefDel d;
    void Start() { }
}", "RefDlg"));
        Assert.Contains("ref/out", ex.Message);
    }

    [Fact]
    public void VariantMethodGroupBinding_ThrowsNotSupported()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class VariantDlg : UdonSharpBehaviour {
    public Func<object> f;
    string GetStr() { return ""s""; }
    void Start() { f = GetStr; }
}", "VariantDlg"));
        Assert.Contains("Variant method-group", ex.Message);
    }

    // ── §2.8(b) capture-escape guard ──

    [Fact]
    public void CapturingLambda_StoredIntoArrayElement_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CapArr : UdonSharpBehaviour {
    public Func<int>[] fs;
    void Start() { int x = 1; fs = new Func<int>[1]; fs[0] = () => x; }
}", "CapArr"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CapturingLambda_InArrayInitializer_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CapArrInit : UdonSharpBehaviour {
    void Start() { int x = 1; var fs = new Func<int>[] { () => x }; }
}", "CapArrInit"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CapturingLambda_StoredIntoObjectField_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CapObj : UdonSharpBehaviour {
    public object box;
    void Start() { int x = 1; box = (Func<int>)(() => x); }
}", "CapObj"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CapturingLambda_Returned_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CapRet : UdonSharpBehaviour {
    Func<int> Make() { int x = 1; return () => x; }
    void Start() { var f = Make(); }
}", "CapRet"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void TaintedLocal_StoredIntoDelegateField_Throws()
    {
        // Flow-insensitive taint: a local initialized with a capturing lambda may not be stored
        // into a field (the long-lived store would bypass the array/object/return positions).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CapTaint : UdonSharpBehaviour {
    public Func<int> F;
    void Start() { int x = 1; Func<int> f = () => x; F = f; }
}", "CapTaint"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CaptureFreeLambda_StoredIntoArrayElement_Compiles()
    {
        // fcd23 pin: capture-free lambdas and method groups are unrestricted.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FreeArr : UdonSharpBehaviour {
    public Func<int, int>[] fs;
    void Start() { fs = new Func<int, int>[2]; fs[1] = x => x * 2; }
}", "FreeArr");
        Assert.Contains("fs: %SystemObjectArray", uasm);
    }

    [Fact]
    public void CapturingLambda_StoredIntoDelegateLocal_Compiles()
    {
        // Delegate LOCALS stay legal for capturing lambdas (fcd27/28 — observationally equivalent).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CapLocal : UdonSharpBehaviour {
    public int r;
    void Start() { int x = 1; Func<int> f = () => x; f = () => x + 1; }
}", "CapLocal");
        Assert.NotNull(uasm);
    }

    // ── M2-review fixes (F1-F5): field initializers, buried capturing lambdas, taint laundering,
    //    dispatch-site ref/out ──

    [Fact]
    public void DelegateFieldInitializer_Lambda_BuildsBundleAtStart()
    {
        // F1: the initializer must reach the bundle-creation path (VisitDelegateCreation), not be
        // silently dropped to default(T) via the conversion-stripped inner operation.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgInit : UdonSharpBehaviour {
    public Func<int> cb = () => 5;
    public int result;
    void Start() { result = cb(); }
}", "DlgInit");
        Assert.Contains("cb: %SystemObjectArray, null", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
        Assert.Contains("__dlg_", uasm);
    }

    [Fact]
    public void DelegateFieldInitializer_StaticMethodGroup_BuildsBundleAtStart()
    {
        // F1: the stripped shape for a method-group initializer is IMethodReferenceOperation.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgInitMg : UdonSharpBehaviour {
    public Func<int> cb = Get;
    public int result;
    static int Get() { return 6; }
    void Start() { result = cb(); }
}", "DlgInitMg");
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
        Assert.Contains("__dlg_", uasm);
    }

    [Fact]
    public void DelegateFieldInitializer_WithoutStart_SynthesizedStartEmitsBundle()
    {
        // F2: the synthesized _start (no user Start) must run the initializer BEFORE the pending
        // local-function/bridge drains — the hoisted initializer lambda used to land in never-drained
        // pending lists (CoreToUasm 'CFuncRef references unknown function' ICE).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgInitNoStart : UdonSharpBehaviour {
    public Func<int> cb = () => 7;
}", "DlgInitNoStart");
        Assert.Contains(".export _start", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
        Assert.Contains("__dlg_", uasm);
    }

    [Fact]
    public void CapturingLambda_InTernary_DelegateStore_Throws()
    {
        // F3 (the reviewer's ternary probe): a capturing lambda buried in a ternary RHS evades the
        // §2.8 recording and the taint set — must be a loud reject, not 0 diagnostics.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TernCap : UdonSharpBehaviour {
    public bool flag;
    public int r;
    void Start() { int x = 3; Func<int> f = flag ? () => x : () => x + 1; r = f(); }
}", "TernCap"));
        Assert.Contains("assign the lambda directly", ex.Message);
    }

    [Fact]
    public void CapturingLambda_InCoalesce_DelegateStore_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CoalCap : UdonSharpBehaviour {
    public int r;
    void Start() { int x = 2; Func<int> g = null; Func<int> f = g ?? (() => x); r = f(); }
}", "CoalCap"));
        Assert.Contains("assign the lambda directly", ex.Message);
    }

    [Fact]
    public void CapturingLambda_InSwitchExpressionArm_DelegateStore_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class SwitchCap : UdonSharpBehaviour {
    public int k;
    public int r;
    void Start() { int x = 1; Func<int> f = k switch { 0 => () => x, _ => () => x + 1 }; r = f(); }
}", "SwitchCap"));
        Assert.Contains("assign the lambda directly", ex.Message);
    }

    [Fact]
    public void CaptureFreeLambdas_InTernary_DelegateStore_Compiles()
    {
        // F3 companion: capture-free lambdas in composite shapes stay allowed — verified working on
        // the real VM (local harness M2FixProbes, both arms).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TernFree : UdonSharpBehaviour {
    public bool flag;
    public int r;
    void Start() { Func<int> f = flag ? () => 1 : () => 2; r = f(); }
}", "TernFree");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void TaintedLocal_LaunderedViaCopy_ArrayStore_Throws()
    {
        // F4 (the reviewer's probe): `var g = f;` must PROPAGATE the taint — the escaping array
        // store of the copy used to compile clean and ship wrong values on the real VM.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TaintCopy : UdonSharpBehaviour {
    public int r;
    void Start() { var fs = new Func<int>[1]; int x = 4; Func<int> f = () => x; var g = f; fs[0] = g; r = fs[0](); }
}", "TaintCopy"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void RefParamDelegate_DispatchSite_Throws()
    {
        // F5: a ref/out delegate VALUE received as a param never passes a creation site in this
        // class — the dispatch-site conv-var declaration must re-validate (§3.4-1).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public delegate void RefD2(ref int v);
public class RefDispatch : UdonSharpBehaviour {
    public int result;
    void Use(RefD2 d) { int v = 1; if (d != null) { d(ref v); } result = v; }
    void Start() { Use(null); }
}", "RefDispatch"));
        Assert.Contains("ref/out", ex.Message);
    }

    // ── Type map + cast audit ──

    [Fact]
    public void DelegateField_SingleSystemObjectArrayVar()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgVar : UdonSharpBehaviour {
    public Action callback;
    void Start() { callback = null; }
}", "DlgVar");
        Assert.Contains("callback: %SystemObjectArray", uasm);
        Assert.DoesNotContain("callback__target", uasm);
        Assert.DoesNotContain("SystemAction", uasm);
    }

    [Fact]
    public void DelegateArrayField_IsSystemObjectArray_NotArrayArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgArr : UdonSharpBehaviour {
    public Func<int, int>[] fs;
    void Start() { fs = new Func<int, int>[3]; }
}", "DlgArr");
        Assert.Contains("fs: %SystemObjectArray", uasm);
        Assert.DoesNotContain("SystemObjectArrayArray", uasm);
        Assert.DoesNotContain("SystemFunc", uasm);
    }

    [Fact]
    public void DelegateCast_FromObjectElement_IsReferencePassthrough()
    {
        // fcd25 audit: (Func<int>)box[0] must not emit any Convert extern — the bundle reference passes through.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgCast : UdonSharpBehaviour {
    public object[] box;
    public Func<int> f;
    void Start() { f = (Func<int>)box[0]; }
}", "DlgCast");
        Assert.DoesNotContain("SystemConvert", uasm);
    }
}
