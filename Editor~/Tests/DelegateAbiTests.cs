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
