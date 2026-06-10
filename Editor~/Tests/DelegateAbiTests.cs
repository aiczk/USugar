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

    // ── Invocation/param laundering (M3-review blocker): identity callees must not launder
    //    capturing lambdas past the §2.8(b) guards ──

    [Fact]
    public void IdLaunderedCapturingLambda_ArrayStore_Throws()
    {
        // [A] reviewer repro: fs[i] = Id(()=>v) in a loop compiled clean and shipped VM sum=60
        // vs CLR 30 — the delegate-typed invocation RESULT with a capturing-lambda arg is
        // tainted-equivalent and the array store goes loud.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class IdLaunder : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; fs[i] = Id(() => v); } sum = fs[0]() + fs[1]() + fs[2](); }
    Func<int> Id(Func<int> x) { return x; }
}", "IdLaunder"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void IdLaunderedCapturingLambda_Return_Throws()
    {
        // [B] reviewer repro: Mk(a){return Id(x=>x+a);} across two activations gave VM 42 vs
        // CLR 32 — returning a tainted delegate-typed invocation result goes loud at Mk's return.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class MkLaunder : UdonSharpBehaviour {
    public int result;
    void Start() { var f1 = Mk(10); var f2 = Mk(20); result = f1(1) + f2(1); }
    Func<int, int> Mk(int a) { return Id(x => x + a); }
    Func<int, int> Id(Func<int, int> x) { return x; }
}", "MkLaunder"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void IdLaunderedResult_ViaLocalCopy_ArrayStore_Throws()
    {
        // F4 composition: the laundered result TAINTS the receiving local, so the copy's escaping
        // store stays loud.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class IdTaintCopy : UdonSharpBehaviour {
    public int r;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[1]; int v = 4; var t = Id(() => v); fs[0] = t; r = fs[0](); }
    Func<int> Id(Func<int> x) { return x; }
}", "IdTaintCopy"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void DelegateParam_StoredIntoArrayElement_Throws()
    {
        // [C] callee-side laundering: Keep(x){fs[k]=x;} called with ()=>v in a loop compiled clean
        // and shipped VM sum=60 vs CLR 30 — a delegate-typed PARAM read is tainted-equivalent at
        // escaping store targets (the callee cannot see what the caller passed).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class KeepParam : UdonSharpBehaviour {
    public int sum;
    int k;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; Keep(() => v); } sum = fs[0]() + fs[1]() + fs[2](); }
    void Keep(Func<int> x) { fs[k] = x; k = k + 1; }
}", "KeepParam"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void DelegateParam_StoredIntoDelegateField_Throws()
    {
        // [C] field variant: SetCb(x){cb=x;} launders past RecordLongLivedLambdaStore (which
        // records only direct lambda creations) — VM 54 vs CLR 42 across two activations.
        // Over-rejection of SetCb(M) method-group setters is accepted per design §8-3.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class SetCbParam : UdonSharpBehaviour {
    public int result;
    Func<int> cb;
    void Start() { Mk(5); var f1 = cb; Mk(9); result = f1() + cb(); }
    void Mk(int n) { SetCb(() => n * 3); }
    void SetCb(Func<int> x) { cb = x; }
}", "SetCbParam"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void MethodGroupThroughIdentityCallee_ArrayStore_Compiles()
    {
        // Identity callees stay legal: returning a delegate-typed param compiles, and a
        // method-group argument carries no captures — fs[0] = Id(M) is the supported flow
        // (real-VM verified in the local harness laundering probes).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class IdMg : UdonSharpBehaviour {
    public int result;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[1]; fs[0] = Id(M); result = fs[0](); }
    int M() { return 12; }
    Func<int> Id(Func<int> x) { return x; }
}", "IdMg");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void CapturingLambdaArg_NonDelegateResult_Compiles()
    {
        // fcd37 pin: an arg-position capturing lambda whose invocation result is NOT delegate-typed
        // is consumed by the callee — int Apply(...) stays legal.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ApplyCap : UdonSharpBehaviour {
    public int k;
    public int result;
    void Start() { int kk = k; result = Apply(x => x * kk, 5); }
    int Apply(Func<int, int> fn, int v) { return fn(v); }
}", "ApplyCap");
        Assert.NotNull(uasm);
    }

    // ── Capture-escape laundering, round 2: tuple/object/type-param erasure, out/ref params,
    //    member-read round-trips (all VM-reproduced silent-wrong pre-fix; see the local-harness
    //    FcdLaunderProbes2 for the real-VM contract probes) ──

    [Fact]
    public void TupleReturnEnvelope_CapturingLambda_Throws()
    {
        // [H1a] tuples erase delegate typing: (Func<int>,int) Mk(a){return (()=>a*3,a);} compiled
        // clean and shipped VM 68 vs CLR 56 — loud at Mk's return now.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleRet : UdonSharpBehaviour {
    public int result;
    void Start() { var (f1, n1) = Mk(5); var (f2, n2) = Mk(9); result = f1() + f2() + n1 + n2; }
    (Func<int>, int) Mk(int a) { return (() => a * 3, a); }
}", "TupleRet"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void TupleParamMemberRead_ArrayStore_Throws()
    {
        // [H1b] KeepT((Func<int>,int) t){fs[k]=t.Item1;} — t.Item1 is an IFieldReferenceOperation,
        // not a bare param read; VM 60 vs CLR 30 pre-fix. Param-rooted delegate member reads are
        // tainted-equivalent at escaping stores (and the caller's tuple ARG is guarded too).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleKeep : UdonSharpBehaviour {
    public int sum;
    int k;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; KeepT((() => v, 0)); } sum = fs[0]() + fs[1]() + fs[2](); }
    void KeepT((Func<int>, int) t) { fs[k] = t.Item1; k = k + 1; }
}", "TupleKeep"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void TupleLiteralLocalDecl_CapturingLambda_Throws()
    {
        // [H2] aggregate local declarations bypassed the scalar guard block entirely:
        // var t=((Func<int>)(()=>v),0);fs[i]=t.Item1; shipped VM 60 vs CLR 30. The aggregate
        // declaration path now guards the initializer and each tuple-literal element.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleDecl : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; var t = ((Func<int>)(() => v), 0); fs[i] = t.Item1; } sum = fs[0]() + fs[1]() + fs[2](); }
}", "TupleDecl"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void GenericObjectIdentity_Laundered_Throws()
    {
        // [H3a] T Id<T>(T x){return x;} at T=object laundered (VM 60 vs CLR 30): the capturing
        // lambda must not enter the object-resolved param — loud at the argument now.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class GenObjId : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; object o = Id<object>((Func<int>)(() => v)); fs[i] = (Func<int>)o; } sum = fs[0]() + fs[1]() + fs[2](); }
    T Id<T>(T x) { return x; }
}", "GenObjId"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void ObjectIdentity_Laundered_Throws()
    {
        // [H3b] object IdO(object x){return x;} laundered (VM 60 vs CLR 30) — loud at the
        // capturing-lambda argument to the object param.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ObjId : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; fs[i] = (Func<int>)IdO((Func<int>)(() => v)); } sum = fs[0]() + fs[1]() + fs[2](); }
    object IdO(object x) { return x; }
}", "ObjId"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void AutoPropertyRoundTrip_ArrayStore_Throws()
    {
        // [H4] P=()=>v;fs[i]=P; — ONE lambda with multiple live activations aliases its own flat
        // capture slot, below the aliasing detector's 2+ threshold (VM 60 vs CLR 30). Reads of a
        // capture-RECEIVING member are tainted-equivalent at escaping stores.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class PropTrip : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    Func<int> P { get; set; }
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; P = () => v; fs[i] = P; } sum = fs[0]() + fs[1]() + fs[2](); }
}", "PropTrip"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CapturingLambdaArg_ObjectParam_Throws()
    {
        // [H5a] KeepObj(object x){objs[k]=x;} laundered (VM 60 vs CLR 30). The object param is
        // sealed at the CALL SITE (the callee is type-blind; tainting every object param read
        // would reject ordinary object plumbing like the stock LocalFunctionTest).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class KeepObj : UdonSharpBehaviour {
    public int sum;
    int k;
    object[] objs;
    void Start() { objs = new object[3]; for (int i = 0; i < 3; i++) { int v = i * 10; Keep((Func<int>)(() => v)); } sum = ((Func<int>)objs[0])() + ((Func<int>)objs[1])() + ((Func<int>)objs[2])(); }
    void Keep(object x) { objs[k] = x; k = k + 1; }
}", "KeepObj"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void GenericDelegateInstantiation_ParamArrayStore_Throws()
    {
        // [H5b] Keep<T>(T[] a,int i,T x){a[i]=x;} at T=Func<int> laundered (VM 60 vs CLR 30):
        // the emitted body keeps Parameter.Type == T, so the guard resolves T through the
        // type-param map — the delegate instantiation rejects, Keep<int> keeps compiling.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class GenKeep : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; Keep<Func<int>>(fs, i, () => v); } sum = fs[0]() + fs[1]() + fs[2](); }
    void Keep<T>(T[] a, int i, T x) { a[i] = x; }
}", "GenKeep"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void OutParamCapturingLambdaWrite_Throws()
    {
        // [H6] out/ref delegate param writes escaped to the caller's local with no taint
        // (VM 60 vs CLR 30): a capturing lambda (or tainted value) stored into ANY parameter
        // is loud (out/ref hands it to the caller; by-value could launder via param-ref return).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class OutMk : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; Func<int> f; Mk(v, out f); fs[i] = f; } sum = fs[0]() + fs[1]() + fs[2](); }
    void Mk(int v, out Func<int> f) { f = () => v; }
}", "OutMk"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void RefParamCapturingLambdaWrite_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class RefMk : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; Func<int> f = null; Mk(v, ref f); fs[i] = f; } sum = fs[0]() + fs[1]() + fs[2](); }
    void Mk(int v, ref Func<int> f) { f = () => v; }
}", "RefMk"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CaptureReceivingFieldRead_ArrayStore_Throws()
    {
        // [H7a] cb=()=>v;fs[i]=cb; — the member store is legal (one live bundle is correct), but
        // copying the member out re-creates single-lambda multi-activation aliasing (VM 40 vs
        // CLR 30). The recipient set is PRE-SCANNED before emission, so the reject is independent
        // of method emission order.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FieldTrip : UdonSharpBehaviour {
    public int sum;
    Func<int> cb;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[2]; for (int i = 0; i < 2; i++) { int v = (i + 1) * 10; cb = () => v; fs[i] = cb; } sum = fs[0]() + fs[1](); }
}", "FieldTrip"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CaptureReceivingFieldRead_Return_Throws()
    {
        // [H7b] Get(){return cb;} fs[i]=Get(); — a zero-arg delegate-typed invocation result is
        // untainted by construction (the caller-side rule sees only arguments), so the RETURN of
        // a capture-receiving member goes loud instead (VM 40 vs CLR 30 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FieldGet : UdonSharpBehaviour {
    public int sum;
    Func<int> cb;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[2]; for (int i = 0; i < 2; i++) { int v = (i + 1) * 10; cb = () => v; fs[i] = Get(); } sum = fs[0]() + fs[1](); }
    Func<int> Get() { return cb; }
}", "FieldGet"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void StructEnvelopeParamMemberRead_ArrayStore_Throws()
    {
        // [H7c] s.f=()=>v;Keep(s); with Keep(RS5 s){fs[k]=s.f;} — the stored value is a member
        // read, not a param read (VM 60 vs CLR 30). Covered twice: RS5.f is a capture-receiving
        // member (pre-scan), and s.f is a param-rooted delegate member read.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct RS5 { public Func<int> f; }
public class StructEnv : UdonSharpBehaviour {
    public int sum;
    int k;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[3]; for (int i = 0; i < 3; i++) { int v = i * 10; RS5 s; s.f = () => v; Keep(s); } sum = fs[0]() + fs[1]() + fs[2](); }
    void Keep(RS5 s) { fs[k] = s.f; k = k + 1; }
}", "StructEnv"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CrossBehaviourCapturingLambdaFieldStore_Throws()
    {
        // The recipient pre-scan is per-class: a capturing lambda stored into ANOTHER behaviour's
        // member could not make that class's reads loud, so the cross-class direct store itself
        // is loud. Method-group cross-behaviour stores (fcd22) stay legal.
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CrossTarget : UdonSharpBehaviour {
    public Func<int> cb;
}
public class CrossStore : UdonSharpBehaviour {
    public CrossTarget other;
    void Start() { int v = 3; other.cb = () => v; }
}", "CrossStore"));
        Assert.Contains("capture", ex.Message);
    }

    // ── Round-2 precision pins: the legal flows the widened guards must NOT break ──

    [Fact]
    public void ObjectParamPlumbing_StoredIntoObjectArray_Compiles()
    {
        // The stock-UdonSharp LocalFunctionTest shape: object params are NOT tainted (their call
        // sites are sealed instead), so ordinary object plumbing keeps compiling.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ObjPlumb : UdonSharpBehaviour {
    public int result;
    object[] objs;
    void Start() { objs = new object[2]; Add(objs, (Func<int>)Five); result = ((Func<int>)objs[0])(); }
    void Add(object[] a, object b) { a[0] = b; }
    int Five() { return 5; }
}", "ObjPlumb");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericIntInstantiation_ParamArrayStore_Compiles()
    {
        // H5b precision: the SAME Keep<T> body at T=int stays legal — the guard resolves T
        // through the type-param map instead of blanket-rejecting generic params.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class GenKeepInt : UdonSharpBehaviour {
    public int sum;
    void Start() { int[] ks = new int[3]; for (int i = 0; i < 3; i++) { Keep<int>(ks, i, i * 10); } sum = ks[0] + ks[1] + ks[2]; }
    void Keep<T>(T[] a, int i, T x) { a[i] = x; }
}", "GenKeepInt");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void MethodGroupField_SwapCopyAndReturn_Compiles()
    {
        // H7 precision (fcd26 + Get shape): member-read taint is RECIPIENT-narrowed — fields that
        // only ever received method groups stay copyable, swappable, and returnable.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class MgSwap : UdonSharpBehaviour {
    public int result;
    Func<int, int> a;
    Func<int, int> b;
    void Start() { a = AddOne; b = Dec; Func<int, int> t = a; a = b; b = t; var g = Get(); result = a(10) * 100 + b(10) + g(1); }
    Func<int, int> Get() { return a; }
    int AddOne(int x) { return x + 1; }
    int Dec(int x) { return x - 1; }
}", "MgSwap");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void IntTupleReturnAndDeconstruct_Compiles()
    {
        // H1 precision: tuples without delegate-capable elements are untouched by the widenings.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class IntTuple : UdonSharpBehaviour {
    public int result;
    void Start() { var (x, y) = Mk(5); result = x + y; }
    (int, int) Mk(int a) { return (a * 3, a); }
}", "IntTuple");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void CaptureReceivingField_InvokeOnly_Compiles()
    {
        // H4/H7 precision: storing a capturing lambda into a member and INVOKING it stays legal
        // (one live bundle at a time is correct); only escaping READS of the member go loud.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CbInvoke : UdonSharpBehaviour {
    public int seed;
    public int result;
    Func<int> cb;
    void Start() { int v = seed + 5; cb = () => v; result = cb(); }
}", "CbInvoke");
        Assert.NotNull(uasm);
    }

    // ── Capture-escape laundering, round 3: containers (local functions, struct envelopes,
    //    object initializers, foreign members) laundering the bundle past type-gated guards ──

    [Fact]
    public void CapturingLocalFunction_MethodGroupArrayStore_Throws()
    {
        // [A] int Get(){return v;} fs[i]=Get; — a method-group conversion of a local function is
        // an IMethodReferenceOperation, never analyzed for captures (VM 40 vs CLR 30 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LfArr : UdonSharpBehaviour {
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[2]; for (int i = 0; i < 2; i++) { int v = (i + 1) * 10; int Get() { return v; } fs[i] = Get; } sum = fs[0]() + fs[1](); }
}", "LfArr"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CapturingLocalFunction_FieldRoundTrip_Throws()
    {
        // [A] cb=Get;fs[i]=cb; — the recipient pre-scan must treat the capturing local-function
        // method group as a capturing-lambda seed (VM 40 vs CLR 30 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LfTrip : UdonSharpBehaviour {
    public int sum;
    Func<int> cb;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[2]; for (int i = 0; i < 2; i++) { int v = (i + 1) * 10; int Get() { return v; } cb = Get; fs[i] = cb; } sum = fs[0]() + fs[1](); }
}", "LfTrip"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void CapturingLocalFunction_ZeroArgReturnLaunder_Throws()
    {
        // [A] Func<int> MkL(){…return Get;} fs[i]=MkL(); — a zero-argument invocation result is
        // untainted by construction (the caller-side rule sees only arguments), so the capturing
        // method-group RETURN goes loud at the source (VM 40 vs CLR 30 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LfMk : UdonSharpBehaviour {
    public int seed;
    public int sum;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[2]; fs[0] = MkL(); fs[1] = MkL(); sum = fs[0]() + fs[1](); }
    Func<int> MkL() { seed = seed + 10; int v = seed; int Get() { return v; } return Get; }
}", "LfMk"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void StructLocalCapturingSeed_WholeStructArrayStore_Throws()
    {
        // [B] s.f=()=>v;arr[i]=s; — the member seed must taint the CONTAINER local, or the struct
        // envelope ships the aliased bundle past every delegate-typed gate (VM 90 vs CLR 60).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct SEnv { public Func<int> f; }
public class SArr : UdonSharpBehaviour {
    public int sum;
    void Start() { SEnv[] arr = new SEnv[3]; for (int i = 0; i < 3; i++) { int v = (i + 1) * 10; SEnv s; s.f = () => v; arr[i] = s; } sum = arr[0].f() + arr[1].f() + arr[2].f(); }
}", "SArr"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void StructLocalCapturingSeed_Return_Throws()
    {
        // [B] SEnv2 Mk(int v){SEnv2 s;s.f=()=>v;return s;} — the tainted container local is loud
        // at the return, closing the call-boundary launder (VM 270 vs CLR 180 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct SEnv2 { public Func<int> f; }
public class SRet : UdonSharpBehaviour {
    public int sum;
    void Start() { SEnv2 a = Mk(30); SEnv2 b = Mk(60); SEnv2 c = Mk(90); sum = a.f() + b.f() + c.f(); }
    SEnv2 Mk(int v) { SEnv2 s; s.f = () => v; return s; }
}", "SRet"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void StructParamMemberCapturingSeed_Throws()
    {
        // [B] Seed(SEnv3 s,int v){s.f=()=>v;return s;} — a PARAM-rooted member seed launders out
        // via the legal param-ref return (the caller's arg is clean, so the invocation result is
        // untainted by construction); loud at the seed (VM 40 vs CLR 30 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct SEnv3 { public Func<int> f; }
public class SSeed : UdonSharpBehaviour {
    public int sum;
    void Start() { SEnv3 s0; s0.f = null; SEnv3 a = Seed(s0, 10); SEnv3 b = Seed(s0, 20); sum = a.f() + b.f(); }
    SEnv3 Seed(SEnv3 s, int v) { s.f = () => v; return s; }
}", "SSeed"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void ObjectInitializer_DelegateParamPack_Throws()
    {
        // [C] Mk(Func<int> x){var s=new OEnv{f=x};return s;} — both object-initializer arms used
        // to emit a raw member __Set with no guard and no taint (VM 90 vs CLR 60 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct OEnv { public Func<int> f; }
public class OPack : UdonSharpBehaviour {
    public int sum;
    void Start() { OEnv[] env = new OEnv[3]; for (int i = 0; i < 3; i++) { int v = (i + 1) * 10; env[i] = Mk(() => v); } sum = env[0].f() + env[1].f() + env[2].f(); }
    OEnv Mk(Func<int> x) { var s = new OEnv { f = x }; return s; }
}", "OPack"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void ObjectInitializer_DirectCapturingLambda_Throws()
    {
        // [C] var s=new OEnv2{f=()=>v};arr[i]=s; — the initializer-form seed bypassed the
        // assignment-form guard entirely (VM 90 vs CLR 60 pre-fix).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct OEnv2 { public Func<int> f; }
public class OInit : UdonSharpBehaviour {
    public int sum;
    void Start() { OEnv2[] arr = new OEnv2[3]; for (int i = 0; i < 3; i++) { int v = (i + 1) * 10; var s = new OEnv2 { f = () => v }; arr[i] = s; } sum = arr[0].f() + arr[1].f() + arr[2].f(); }
}", "OInit"));
        Assert.Contains("capture", ex.Message);
    }

    [Fact]
    public void ForeignDelegateFieldRead_ArrayStore_Throws()
    {
        // [D] B legally seeds its OWN field with a capturing lambda; A reads other.cb into a
        // delegate array — the recipient pre-scan is per-class, so the read is recipient-blind
        // and conservatism is the only sound option (compile-proven vector).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FTarget : UdonSharpBehaviour {
    public Func<int> cb;
    void Start() { int v = 3; cb = () => v; }
}
public class FReader : UdonSharpBehaviour {
    public FTarget other;
    public int r;
    Func<int>[] fs;
    void Start() { fs = new Func<int>[1]; fs[0] = other.cb; r = fs[0] != null ? 1 : 0; }
}", "FReader"));
        Assert.Contains("another behaviour", ex.Message);
    }

    [Fact]
    public void ForeignDelegateFieldRead_Return_Throws()
    {
        // [D] Func<int> Get(){return other.cb;} — the zero-arg return launder of a foreign member
        // (the caller-side rule cannot see it; the per-class pre-scan cannot either).
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FTarget2 : UdonSharpBehaviour {
    public Func<int> cb;
    void Start() { int v = 3; cb = () => v; }
}
public class FReturner : UdonSharpBehaviour {
    public FTarget2 other;
    public int r;
    void Start() { var f = Get(); r = f != null ? 1 : 0; }
    Func<int> Get() { return other.cb; }
}", "FReturner"));
        Assert.Contains("another behaviour", ex.Message);
    }

    // ── Round-3 precision pins: the legal flows the container rules must NOT break ──

    [Fact]
    public void CaptureFreeLocalFunction_MethodGroupStoreAndDispatch_Compiles()
    {
        // [A] precision: the widening is capture-driven, not a blanket local-function ban.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LfFree : UdonSharpBehaviour {
    public int result;
    Func<int, int>[] fs;
    void Start() { int Dbl(int x) { return x * 2; } fs = new Func<int, int>[1]; fs[0] = Dbl; result = fs[0](21); }
}", "LfFree");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void StructMemberCapturingSeed_SameScopeDispatch_Compiles()
    {
        // [B] precision: the container taint blocks ESCAPES only — single-activation member seed
        // plus same-scope dispatch stays legal (real-VM-pinned in the local harness).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct SDisp { public Func<int> f; }
public class SLegal : UdonSharpBehaviour {
    public int seed;
    public int result;
    void Start() { int v = seed + 5; SDisp s; s.f = () => v; result = s.f(); }
}", "SLegal");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void ObjectInitializer_MethodGroupSeed_Compiles()
    {
        // [C] precision: the per-member guard is value-driven — method-group seeds in object
        // initializers stay storable whole and dispatchable.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct OMg { public Func<int> f; }
public class OLegal : UdonSharpBehaviour {
    public int result;
    void Start() { OMg[] arr = new OMg[1]; var s = new OMg { f = Eight }; arr[0] = s; result = arr[0].f(); }
    public int Eight() { return 8; }
}", "OLegal");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void ForeignDelegateField_DirectInvoke_Compiles()
    {
        // [D] precision: the foreign-read reject is escape-position-narrowed — direct invocation
        // (other.cb()) stays legal.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FTarget3 : UdonSharpBehaviour {
    public Func<int> cb;
    void Start() { cb = Nine; }
    public int Nine() { return 9; }
}
public class FInvoker : UdonSharpBehaviour {
    public FTarget3 other;
    public int r;
    void Start() { r = other.cb(); }
}", "FInvoker");
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
