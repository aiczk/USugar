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
/// - ValidateDelegateBinding rejects ref/out and variant method groups loudly; tuple-return delegates are
///   SUPPORTED (Stage 1.75 design 2026-07-04 §1 — see DelegateAbiTests.ValidateDelegateBinding_TupleReturn_Passes).
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

    static UdonTypeSystem Types(CSharpCompilation compilation)
        => new CompilationSession(
            compilation, TestHelper.RegistryFacts).Types;

    [Fact]
    public void BuildSigPart_FuncIntInt_UsesUdonTypeNames()
    {
        var c = AbiCompile(AbiSrc);
        Assert.Equal("SystemInt32__SystemInt32",
            DelegateAbi.BuildSigPart(
                DelegateOf(c, "F").DelegateInvokeMethod, Types(c)));
    }

    [Fact]
    public void BuildSigPart_Action_IsVoidVoid()
    {
        var c = AbiCompile(AbiSrc);
        Assert.Equal("Void__Void",
            DelegateAbi.BuildSigPart(
                DelegateOf(c, "A").DelegateInvokeMethod, Types(c)));
    }

    [Fact]
    public void BuildSigPart_DelegateOfDelegate_UsesSystemObjectArrayToken()
    {
        // The Stage-1 ABI token for a delegate-typed param is SystemObjectArray (bundle reference) —
        // builder identity: every consumer (caller convention, bridge, conv-var decl) shares this output.
        var c = AbiCompile(AbiSrc);
        Assert.Equal("SystemObjectArray__SystemInt32",
            DelegateAbi.BuildSigPart(
                DelegateOf(c, "G").DelegateInvokeMethod, Types(c)));
    }

    [Fact]
    public void BuildSigPart_DelegateInvokeAndExactTargetMethod_Agree()
    {
        // Caller derives the __dlgc_ name from the delegate type, the bridge from the target method —
        // for an exact (non-variant) binding the two derivations must be byte-identical.
        var c = AbiCompile(AbiSrc);
        var types = Types(c);
        Assert.Equal(
            DelegateAbi.BuildSigPart(
                DelegateOf(c, "F").DelegateInvokeMethod, types),
            DelegateAbi.BuildSigPart(MethodOf(c, "M"), types));
    }

    [Fact]
    public void BuildSigPartUsesTheSessionEnumAuthority()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
using VRC.SDKBase;
public delegate void FoldedEnumDelegate(UnityEngine.HideFlags value);
public delegate void RegisteredEnumDelegate(
    VRC_EventHandler.VrcBroadcastType value);
public class DelegateEnumCarrier : UdonSharpBehaviour
{
    public FoldedEnumDelegate folded;
    public RegisteredEnumDelegate registered;
}", "DelegateEnumCarrier", out var carrier);
        var fields = carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(field => field.Name);
        var types = new CompilationSession(
            compilation, TestHelper.RegistryFacts).Types;

        Assert.Equal("SystemInt32__Void",
            DelegateAbi.BuildSigPart(
                ((INamedTypeSymbol)fields["folded"].Type)
                .DelegateInvokeMethod, types));
        Assert.Equal(
            "VRCSDKBaseVRC_EventHandlerVrcBroadcastType__Void",
            DelegateAbi.BuildSigPart(
                ((INamedTypeSymbol)fields["registered"].Type)
                .DelegateInvokeMethod, types));
    }

    // ── ValidateDelegateBinding unit tests ──

    [Fact]
    public void ValidateDelegateBinding_RefOutParams_Throws()
    {
        var c = AbiCompile(AbiSrc);
        var ex = Assert.Throws<NotSupportedException>(
            () => DelegateAbi.ValidateDelegateBinding(
                DelegateOf(c, "R"), null, Types(c)));
        Assert.Contains("ref/out", ex.Message);
    }

    [Fact]
    public void ValidateDelegateBinding_TupleReturn_Passes()
    {
        // Stage 1.75 (design 2026-07-04 §1): a tuple return is already a single SystemObjectArray
        // aggregate slot (same representation as a user-struct return) — no adapter needed, so binding
        // a tuple-return delegate type is not rejected.
        var c = AbiCompile(AbiSrc);
        DelegateAbi.ValidateDelegateBinding(
            DelegateOf(c, "T"), null, Types(c));
    }

    [Fact]
    public void ValidateDelegateBinding_VariantMethodGroup_Throws()
    {
        // Func<object> bound to a string-returning method: legal C#, but the __dlgc_ names diverge.
        var c = AbiCompile(AbiSrc);
        var ex = Assert.Throws<NotSupportedException>(
            () => DelegateAbi.ValidateDelegateBinding(
                DelegateOf(c, "O"), MethodOf(c, "GetStr"), Types(c)));
        Assert.Contains("Variant method-group", ex.Message);
    }

    [Fact]
    public void ValidateDelegateBinding_ExactMethodGroup_Passes()
    {
        var c = AbiCompile(AbiSrc);
        DelegateAbi.ValidateDelegateBinding(
            DelegateOf(c, "F"), MethodOf(c, "M"), Types(c));
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
    public void VariantMethodGroupBinding_MintsSigAdapter_Compiles()
    {
        // Stage 1.75 (design 2026-07-04 §2.2, B-1): a same-program covariant-return method-group
        // binding mints a sig adapter under the delegate's OWN sig (Void__SystemObject, since Func<object>
        // erases to SystemObject) instead of rejecting.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class VariantDlg : UdonSharpBehaviour {
    public Func<object> f;
    string GetStr() { return ""s""; }
    void Start() { f = GetStr; }
}", "VariantDlg");
        Assert.Contains("__dlg_adapt_", uasm);
    }

    [Fact]
    public void VariantMethodGroupBinding_ContravariantParam_Invoke_Compiles()
    {
        // B-1, contravariant parameter direction: the delegate declares the NARROWER type (string, what
        // callers pass); the method accepts the WIDER type (object) it's convertible to.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class VariantDlgParam : UdonSharpBehaviour {
    public int result;
    void TakesObject(object o) { result = ((string)o).Length; }
    void Start() {
        Action<string> a = TakesObject;
        a(""hey"");
    }
}", "VariantDlgParam");
        Assert.Contains("__dlg_adapt_", uasm);
    }

    [Fact]
    public void VariantMethodGroupBinding_CapturingLocalFunction_ForwardsEnv_Compiles()
    {
        // B-1 capturing-LF flavor (design §2.2): a captured local function bound as a variant
        // method-group target forwards __envp untouched through the adapter.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class VariantDlgCaptureLf : UdonSharpBehaviour {
    public int cap;
    public object result;
    void Start() {
        int c = cap;
        string Lf(object o) { return o + ""|"" + c; }
        Func<object, object> f = Lf;
        result = f(""x"");
    }
}", "VariantDlgCaptureLf");
        Assert.Contains("__dlg_adapt_", uasm);
        Assert.Contains("__envp", uasm);
    }

    [Fact]
    public void VariantMethodGroupBinding_ThirdPartyTarget_MintsWrapper_Compiles()
    {
        // Stage 1.75 §2.2's hinge: a variant method-group binding to a THIRD-PARTY target cannot mint
        // an adapter (no program to plant it in) — it wraps the exact-sig third-party bundle instead
        // (§2.2's confirmed composition rule, B-2).
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class VariantThirdPartyProvider : UdonSharpBehaviour {
    public string GetStr() { return ""s""; }
}", @"
using UdonSharp;
using System;
public class VariantThirdPartyCaller : UdonSharpBehaviour {
    public VariantThirdPartyProvider provider;
    public object result;
    void Start() {
        Func<object> f = provider.GetStr;
        result = f();
    }
}" }, "VariantThirdPartyCaller");
        Assert.Contains("__dlg_wrap_", uasm);
        Assert.DoesNotContain("__dlg_adapt_", uasm);
    }

    [Fact]
    public void VariantMethodGroupBinding_SameTargetSig_DedupsToOneAdapter()
    {
        // Design §8-3 emission-count measurement (T-M2 gate, multicast §8-1 methodology): adapters are
        // bounded per-(target, sig-S), not per usage site. Two bindings of the SAME target (M1) to the
        // SAME sig-S dedup to ONE adapter; a different target (M2) to the same sig-S gets its own.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class VarianceEmissionCount : UdonSharpBehaviour {
    public object r1, r2, r3;
    string M1() { return ""a""; }
    string M2() { return ""b""; }
    void Start() {
        Func<object> f1 = M1;
        Func<object> f2 = M1;
        Func<object> f3 = M2;
        r1 = f1(); r2 = f2(); r3 = f3();
    }
}", "VarianceEmissionCount");
        var adapterExports = System.Text.RegularExpressions.Regex.Matches(uasm, @"\.export __dlg_adapt_\S+").Count;
        Assert.Equal(2, adapterExports);   // M1 (shared by f1/f2) + M2 — not 3
    }

    [Fact]
    public void WrapperEquality_ComparesTwoVariantConversions_Compiles()
    {
        // D1' (design §4): comparing two SEPARATELY method-group-bound delegates, both variant-converted
        // to the same wider type, structurally compiles (delegate compare extern emitted). The VALUE
        // divergence from C# (D1' — env is compared by BUNDLE REFERENCE, so two separately-minted
        // same-method bundles wrapped for comparison are unequal even though C#'s Method+Target equality
        // says they should match) is VM-verified in Editor~/_local_harness/VarianceVmTests.cs
        // (DiffCategory.Mismatch, documented deviation — same reasoning class as multicast's D1).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class D1WrapperEq : UdonSharpBehaviour {
    public bool same;
    string MakeTag() { return ""s""; }
    void Start() {
        Func<string> d1 = MakeTag;
        Func<string> d2 = MakeTag;
        Func<object> w1 = d1;
        Func<object> w2 = d2;
        same = w1 == w2;
    }
}", "D1WrapperEq");
        Assert.Contains("__dlg_wrap_", uasm);
        Assert.Contains("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean", uasm);
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
        Assert.Matches(@"__dlgc_[^:\r\n]+__env: %SystemObjectArray", uasm);
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
    // ── Round-2 precision pins: the legal flows the widened guards must NOT break ──

    [Fact]
    public void ObjectParamPlumbing_StoredIntoObjectArray_Throws()
    {
        // The stock-UdonSharp LocalFunctionTest shape stores a delegate into an object[] and casts
        // it back: `(Func<int>)objs[0]` reads an object element whose runtime delegate signature is
        // not statically visible, so the wave-12c bounded cast check rejects it (accepted
        // over-rejection, design §8-3 — route the delegate typed, not through object[]).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ObjPlumb : UdonSharpBehaviour {
    public int result;
    object[] objs;
    void Start() { objs = new object[2]; Add(objs, (Func<int>)Five); result = ((Func<int>)objs[0])(); }
    void Add(object[] a, object b) { a[0] = b; }
    int Five() { return 5; }
}", "ObjPlumb"));
        Assert.Contains("carries no statically visible signature", ex.Message);
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
    // ── Round-4 precision pins: the legal flows the new rules must NOT break ──

    [Fact]
    public void MethodGroupArrayElementMemberSeed_Compiles()
    {
        // [K1]/[K4] precision: the element-root reject is only reached on a DIRECT capturing
        // store — method-group element seeds stay legal (real-VM-pinned in the local harness).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct MgEnv { public Func<int> f; }
public class MgElem : UdonSharpBehaviour {
    public int result;
    void Start() { MgEnv[] arr = new MgEnv[1]; arr[0].f = Eight; result = arr[0].f(); }
    public int Eight() { return 8; }
}", "MgElem");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void CaptureFreeLocalFunctionChain_MethodGroup_Compiles()
    {
        // [K2] precision: the transitivity is capture-driven, not call-driven — a capture-free
        // chain stays legal as a method group.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LfChain : UdonSharpBehaviour {
    public int result;
    Func<int, int>[] fs;
    void Start() { int Inner(int x) { return x * 2; } int Outer(int x) { return Inner(x); } fs = new Func<int, int>[1]; fs[0] = Outer; result = fs[0](21); }
}", "LfChain");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void LocalFunctionCapturingOnlyCallerLocals_MethodGroup_Compiles()
    {
        // [K2] precision (the inside-filter): a callee capturing only the CALLER's own locals
        // runs entirely in the caller's activation — fs[0]=Outer stays legal and correct
        // (real-VM-pinned in the local harness).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LfSelf : UdonSharpBehaviour {
    public int seed;
    public int result;
    Func<int>[] fs;
    void Start() { int Outer() { int w = seed + 5; int I() { return w; } return I(); } fs = new Func<int>[1]; fs[0] = Outer; result = fs[0](); }
}", "LfSelf");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void MethodGroupSeededLocal_IntoArray_Compiles()
    {
        // [K3] precision: the pre-scan taints capturing seeds only — a local seeded with a
        // method group stays freely storable.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class MgLocal : UdonSharpBehaviour {
    public int result;
    Func<int>[] fs;
    void Start() { Func<int> f = Four; fs = new Func<int>[1]; fs[0] = f; result = fs[0](); }
    public int Four() { return 4; }
}", "MgLocal");
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
    public void DelegateCast_FromObjectElement_Throws()
    {
        // fcd25 previously allowed (Func<int>)box[0] as a reference passthrough. The wave-12c bounded
        // cast check rejects it: an object[] element's runtime delegate signature is not statically
        // visible at the cast, so a variant boxed delegate would silently diverge the channels.
        // Accepted over-rejection (design §8-3) — keep the value typed as Func<int> instead of object[].
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DlgCast : UdonSharpBehaviour {
    public object[] box;
    public Func<int> f;
    void Start() { f = (Func<int>)box[0]; }
}", "DlgCast"));
        Assert.Contains("carries no statically visible signature", ex.Message);
    }
    [Fact]
    public void MethodGroupAggregateDeconstruction_IntoLocals_Compiles()
    {
        // [N3 precision] deconstruction of a method-group-only aggregate into LOCALS stays legal —
        // the guard is taint-gated, not shape-gated.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DeconOk : UdonSharpBehaviour {
    public int sum;
    int M() { return 4; }
    void Start() { (Func<int>, int) t = (M, 3); (Func<int> g, int x) = t; sum = g() + x; }
}", "DeconOk");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void MemberReadCopy_MethodGroupSeed_Compiles()
    {
        // [N4 precision] the member-read copy edge carries no taint from an unseeded container —
        // the N4 shape with METHOD GROUPS stays legal end-to-end.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct COk { public Func<int> f; }
public class CopyOk : UdonSharpBehaviour {
    public int sum;
    int M() { return 1; }
    int M2() { return 2; }
    void Start() { var fs = new Func<int>[2]; Func<int> g = M; COk s = default; for (int i = 0; i < 2; i++) { fs[i] = g; s.f = M2; g = s.f; } sum = fs[0]() + fs[1](); }
}", "CopyOk");
        Assert.NotNull(uasm);
    }
    [Fact]
    public void ParamCopy_DispatchOnly_Compiles()
    {
        // [J5 precision] tainting a param copy must not break DISPATCH (reads at escape positions
        // are guarded; invocation reads are unguarded by design).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class J5Ok : UdonSharpBehaviour {
    public int sum;
    void Helper(Func<int> p) { Func<int> g = p; sum += g(); }
    void Start() { Helper(() => 21); Helper(() => 21); }
}", "J5Ok");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void StructConstrainedGenericParamCopy_Compiles()
    {
        // [J5 precision] a value-type-constrained T can never carry a delegate bundle, so the
        // pre-scan param-copy arm must not taint `T result = x;` (tracked pin shape: the int
        // instantiation returns the local legally).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class J5Gen : UdonSharpBehaviour {
    public int sum;
    T Dup<T>(T x) where T : struct { T result = x; return result; }
    void Start() { sum = Dup<int>(21); }
}", "J5Gen");
        Assert.NotNull(uasm);
    }

    // ── Tuple-return delegate (Stage 1.75 design 2026-07-04 §1): SUPPORTED ──
    // Structural/UASM-level pins for the headless main suite (mirrors MulticastDelegateTests' split);
    // real VM values live in Editor~/_local_harness (DiffFuzz CLR oracle + 2-program cross-behaviour).

    [Fact]
    public void TupleReturnDelegate_FieldAndLocalBinding_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleDlgBind : UdonSharpBehaviour {
    public Func<int, int, (int, int)> field;
    (int, int) Callee(int p, int q) => (p, q);
    void Start() {
        field = Callee;
        Func<int, int, (int, int)> local = Callee;
        field = local;
    }
}", "TupleDlgBind");
        Assert.Contains("SystemObjectArray", uasm);
        Assert.Contains(".export __dlg_", uasm);
    }

    [Fact]
    public void TupleReturnDelegate_Invoke_ElementAccess_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleDlgElem : UdonSharpBehaviour {
    public int x;
    public int y;
    (int, int) Callee(int p, int q) => (p * 10 + 1, q * 10 + 2);
    void Start() {
        Func<int, int, (int, int)> f = Callee;
        (int, int) r = f(3, 4);
        x = r.Item1; y = r.Item2;
    }
}", "TupleDlgElem");
        Assert.Contains("__dlgc_SystemInt32_SystemInt32__SystemObjectArray__ret", uasm);
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    [Fact]
    public void TupleReturnDelegate_Invoke_Deconstruct_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleDlgDeco : UdonSharpBehaviour {
    public int x;
    public int y;
    (int, int) Callee(int p, int q) => (p * 10 + 1, q * 10 + 2);
    void Start() {
        Func<int, int, (int, int)> f = Callee;
        var (a, b) = f(3, 4);
        x = a; y = b;
    }
}", "TupleDlgDeco");
        Assert.Contains("__dlgc_SystemInt32_SystemInt32__SystemObjectArray__ret", uasm);
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    [Fact]
    public void TupleReturnDelegate_CapturingLambda_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TupleDlgCapture : UdonSharpBehaviour {
    public int x;
    public int y;
    void Start() {
        int c = 7;
        Func<int, (int, int)> f = p => (p + c, p - c);
        (int, int) r = f(10);
        x = r.Item1; y = r.Item2;
    }
}", "TupleDlgCapture");
        Assert.Contains("SystemObjectArray", uasm);
    }

    [Fact]
    public void TupleReturnDelegate_CrossBehaviourDispatch_Compiles()
    {
        // A method-group bound to a FOREIGN behaviour's tuple-returning method (thirdParty target):
        // dispatch takes the CROSS arm (SetProgramVariable/SendCustomEvent/GetProgramVariable), never
        // JUMP_INDIRECT. The provider's OWN __dlg_ bridge (planned unconditionally, like any other
        // method — design 2026-07-04 §1.2, T-M0 finding) is what makes this resolvable without the
        // provider class participating in the caller's compile.
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class TupleDlgProvider : UdonSharpBehaviour {
    public (int, int) Pair(int p, int q) => (p, q);
}", @"
using UdonSharp;
using System;
public class TupleDlgCaller : UdonSharpBehaviour {
    public TupleDlgProvider provider;
    public int x;
    public int y;
    void Start() {
        Func<int, int, (int, int)> f = provider.Pair;
        (int, int) r = f(3, 4);
        x = r.Item1; y = r.Item2;
    }
}" }, "TupleDlgCaller");
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", uasm);
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject", uasm);
    }
}
