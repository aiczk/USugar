using System;
using Xunit;

namespace USugar.Tests;

// Generics mini-fuzz wave-15 round-3 batch — tracked pins. See docs/roadmap.md (production gate + B61-B65).
public class GenericsMiniFuzzWave15Round3Tests
{
    // ── Item 0: production extern-validation gate — a bogus extern is a named diagnostic, not opaque ──

    [Fact]
    public void ProductionExternGate_RejectsBogusExtern()
    {
        ExternResolver.IsExternValid = ExternRegistry.IsValid;
        var bogus = ".code_start\n        EXTERN, \"SystemEnum.__Equals__SystemObject__SystemBoolean\"\n.code_end";
        var ex = Assert.Throws<NotSupportedException>(() => ExternResolver.AssertEmittedExternsValid(bogus));
        Assert.Contains("SystemEnum.__Equals", ex.Message);
    }

    [Fact]
    public void ProductionExternGate_AcceptsValidExtern()
    {
        ExternResolver.IsExternValid = ExternRegistry.IsValid;
        var valid = ".code_start\n        EXTERN, \"SystemInt32.__Equals__SystemObject__SystemBoolean\"\n.code_end";
        ExternResolver.AssertEmittedExternsValid(valid); // must not throw
    }

    // ── B61: enum→object boxing must not hijack the enum↔underlying arm (bogus SystemConvert.__ToObject__) ──

    [Theory]
    [InlineData("B61C", "L a = (L)(seed % 3); L b = (L)((seed+1) % 3); result = a.CompareTo(b);")]
    [InlineData("B61E", "L a = (L)(seed % 3); L b = (L)((seed+2) % 3); result = a.Equals(b) ? 1 : 0;")]
    [InlineData("B61B", "L e = (L)(seed % 3); object o = e; result = (o == null) ? 0 : ((int)(L)o);")]
    public void B61_EnumBoxingDoesNotMintBogusConvert(string cls, string body)
    {
        var uasm = TestHelper.CompileToUasm($@"
using System; using UdonSharp;
public enum L {{ A, B, C }}
public class {cls} : UdonSharpBehaviour {{ public int seed; public int result; void Start(){{ {body} }} }}", cls);
        Assert.DoesNotContain("SystemConvert.__ToObject__", uasm);
    }

    // ── B62: `as`-cast implemented via the is-machinery (distinguishable → test+null-out; collapse → reject) ──

    [Fact]
    public void B62_AsCast_DistinguishableTarget_Compiles()
    {
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B62A : UdonSharpBehaviour {
  public int seed; public int result;
  void Start(){ object o = seed; string s = o as string; result = (s == null) ? 1 : 0; }
}", "B62A");
    }

    [Fact]
    public void B62_AsCast_CollapseSetTarget_RejectsLikeIs()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B62D : UdonSharpBehaviour {
  public int result;
  void Start(){ object o = ""x""; Func<int> f = o as Func<int>; result = (f == null) ? 1 : 0; }
}", "B62D"));
        Assert.Contains("'as'", ex.Message);
    }

    // ── B63 (immediate-use-only): a collapse-set typeof (non-injective Udon tag) may exist ONLY as a direct
    //    argument to a component-query engine call, where it is consumed in place. Any other position — ==/!=,
    //    a local/field store, a user-method argument, a return — is a loud reject at the mint site, so the
    //    non-injective token can never be laundered into a later comparison. Honest (uniquely-tagged) types
    //    are fully unrestricted. ──

    [Theory]
    // Direct compare, store-then-anything, pass-to-user-method, and enum-vs-underlying all reject at the mint.
    [InlineData("B63A", @"public class B63A : UdonSharpBehaviour { public int result; void Start(){ result = (typeof(B63A) == typeof(B63B2)) ? 1 : 0; } }
public class B63B2 : UdonSharpBehaviour { void Start(){} }")]
    [InlineData("B63E", @"public enum E63 { A, B } public class B63E : UdonSharpBehaviour { public int result; void Start(){ result = (typeof(E63) == typeof(int)) ? 1 : 0; } }")]
    [InlineData("B63S", @"public class B63S : UdonSharpBehaviour { public int result; void Start(){ System.Type t = typeof(B63S); result = t.Name.Length; } }")]
    [InlineData("B63P", @"public class B63P : UdonSharpBehaviour { public int result; int L(System.Type t) => 1; void Start(){ result = L(typeof(B63P)); } }")]
    public void B63_TypeofCollapseSet_NonImmediateUse_RejectsLoudly(string cls, string body)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm($@"
using System; using UdonSharp;
{body}", cls));
        Assert.Contains("typeof(", ex.Message);
    }

    [Fact]
    public void B63_HonestTypeofToken_StoreAndCompare_StillCompiles()
    {
        // Control: a distinguishable (unique-tag) typeof is fully unrestricted — store it, compare it, read
        // its Name — all legal. Only the non-injective collapse-set tokens are gated.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B63C : UdonSharpBehaviour {
  public int result;
  void Start(){ System.Type ti = typeof(int); int a = (ti == typeof(int)) ? 1 : 0; int b = (typeof(int[]) == typeof(string[])) ? 0 : 2; result = a + b + ti.Name.Length; }
}", "B63C");
    }

    [Fact]
    public void B63_CollapseSetTypeof_AsComponentQueryArgument_StillCompiles()
    {
        // The one legal position for a collapse-set typeof: a direct argument to a GetComponent-family engine
        // call, which consumes the token in place (it never becomes a comparable heap value). Mirrors the SDK
        // Compat GetComponent(typeof(UdonBehaviour)) / GetComponents(typeof(UdonBehaviour)) shapes.
        var uasm = TestHelper.CompileToUasm(@"
using System; using UnityEngine; using UdonSharp;
public class B63G : UdonSharpBehaviour {
  public int result;
  void Start(){ Component c = GetComponent(typeof(UdonSharpBehaviour)); Component[] cs = GetComponents(typeof(UdonSharpBehaviour)); result = (c == null ? 0 : 1) + cs.Length; }
}", "B63G");
        Assert.Contains("__GetComponent", uasm);
    }

    // ── B66: the B63 array-typeof exemption was `operand is not IArrayTypeSymbol` (unconditional), so
    //         typeof(S1[]) (aggregate element → SystemObjectArray) laundered-compared silently equal. The
    //         exemption is narrowed to arrays whose ELEMENT is runtime-distinguishable AND not itself an
    //         array — exactly object[] (the stock-conformant fold); aggregate/delegate/interface/nested-array
    //         elements reject like any collapse token. ──

    [Theory]
    // Aggregate-element and nested-array typeof stored then compared → collapse tag, must reject at the mint.
    [InlineData("B66S", @"public struct S1_66 { public int a; } public struct S2_66 { public int b; }
public class B66S : UdonSharpBehaviour { public int result; void Start(){ System.Type t1 = typeof(S1_66[]); System.Type t2 = typeof(S2_66[]); result = (t1 == t2) ? 1 : 0; } }")]
    [InlineData("B66N", @"public class B66N : UdonSharpBehaviour { public int result; void Start(){ System.Type t1 = typeof(int[][]); System.Type t2 = typeof(string[][]); result = (t1 == t2) ? 1 : 0; } }")]
    public void B66_NonDistinguishableElementArrayTypeof_RejectsLoudly(string cls, string body)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm($@"
using System; using UdonSharp;
{body}", cls));
        Assert.Contains("typeof(", ex.Message);
    }

    [Fact]
    public void B66_DistinguishableElementArrayTypeof_StillCompiles()
    {
        // Controls: int[]/long[] elements are distinguishable (their arrays have unique tags), and object[]
        // (element object) is the stock-conformant SystemObjectArray fold DefaultHeapValueTest pins — all
        // stay legal to store and compare.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B66D : UdonSharpBehaviour {
  public int result;
  void Start(){ System.Type t1 = typeof(int[]); System.Type t2 = typeof(long[]); System.Type t3 = typeof(object[]);
    result = (t1 == t2 ? 1 : 0) + (t3 == typeof(object[]) ? 2 : 0); }
}", "B66D");
    }

    // ── B67: Enum.ToString()/concat/interpolation synthesize the value→name lookup (non-Flags); a [Flags]
    //         enum's ToString rejects (Udon can't synthesize the comma-separated decomposition). VM value
    //         parity is pinned in the harness (Wave15Batch4RegressionTests). ──

    [Fact]
    public void B67_NonFlagsEnumToString_CompilesWithNameHelper()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public enum Suit67 { Clubs, Diamonds, Hearts, Spades }
public class B67N : UdonSharpBehaviour {
  public string r1; public string r2; public string r3;
  void Start(){ Suit67 s = Suit67.Hearts; r1 = s.ToString(); r2 = $""x={s}""; r3 = ""y=""+s; }
}", "B67N");
        // The synthesized value→name helper exists and its label is reached (all three sites route to it);
        // the member-name string constants live in the data table so are not asserted as inline text here —
        // their VALUES are pinned on the real VM (Wave15Batch4RegressionTests).
        Assert.Contains("__enumstr_Suit67:", uasm);
        Assert.Contains("__enumstr_Suit67__ret", uasm);
    }

    [Fact]
    public void B67_FlagsEnumToString_RejectsLoudly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
[Flags] public enum Perm67 { None = 0, A = 1, B = 2 }
public class B67F : UdonSharpBehaviour {
  public string result;
  void Start(){ Perm67 p = Perm67.A | Perm67.B; result = p.ToString(); }
}", "B67F"));
        Assert.Contains("[Flags]", ex.Message);
    }

    // ── B68/B69: a generic local function registers as a real function (JUMP) in a FOREIGN host — a static
    //         helper class method or a user struct instance method — not a bogus `{Host}.__Lf__{T}` extern.
    //         Two distinct instantiations of a generic LF are separate specs (no shared hoist), so a generic
    //         LF using its OWN type param must not trip the B64 closure-alias pin. VM values pinned in the
    //         harness (Wave15Batch4RegressionTests). ──

    [Theory]
    [InlineData("B68H", @"public static class H68T { public static int Run(int seed){ int Lf<T>(){ T[] a = new T[2]; return a.Length; } return Lf<int>() + Lf<string>() + seed; } }
public class B68H : UdonSharpBehaviour { public int seed; public int result; void Start(){ result = H68T.Run(seed); } }")]
    [InlineData("B69H", @"public struct S69T { public int Tag; public int Run(int seed){ int Lf<T>(){ T[] a = new T[2]; return a.Length; } return Lf<int>() + seed; } }
public class B69H : UdonSharpBehaviour { public int seed; public int result; void Start(){ S69T s = new S69T(); result = s.Run(seed); } }")]
    public void B68_69_GenericLocalFunction_ForeignHost_RegistersAsJump(string cls, string body)
    {
        var uasm = TestHelper.CompileToUasm($@"
using System; using UdonSharp;
{body}", cls);
        Assert.DoesNotContain("__Lf__", uasm);   // no bogus {Host}.__Lf__{T} extern
        Assert.Contains("_Lf_", uasm);            // the monomorphized Lf specialization function(s) exist
    }

    // ── B70: local functions inside generic structs. The enclosing struct's CLOSED type args now reach the
    //         LF body's type-param map (no bogus TArray), a recursive generic LF's open self-reference is no
    //         longer collected as a mapless second body, and a nested closure/LF that uses a type param SHARED
    //         across two instantiations still pins on aliasing. Structural armor: an identity binding (T→T)
    //         from an unclosed spec can never recurse GetUdonTypeName into a stack overflow. VM values pinned
    //         in the harness (LensAWave15R5Probe A14/A11). ──

    [Theory]
    // A14: generic struct GS<T> STATIC method hosting a generic LF<U> that uses BOTH the struct's T and its
    // own U — the closed T (GS<bool>) must resolve so `new T[]` emits the bool-array ctor, not TArray.
    [InlineData("B70A", @"public struct GS70A<T> { public static int Run(int seed){ int Lf<U>(){ T[] ta = new T[1]; U[] ua = new U[2]; return ta.Length + ua.Length; } return Lf<int>() + seed; } }
public class B70A : UdonSharpBehaviour { public int seed; public int result; void Start(){ result = GS70A<bool>.Run(seed); } }")]
    // A11: recursive generic LF in a struct INSTANCE method, single instantiation — the open self-reference
    // Lf<T>(n-1) must not be collected as a mapless second body (that emitted TArray for the recursion arm).
    [InlineData("B70R", @"public struct S70R { public int Tag; public int Lf<T>(int n){ if(n<=0){ T[] a = new T[1]; return a.Length - 1; } return Lf<T>(n-1) + 1; } public int Run(int seed){ return Lf<int>(seed); } }
public class B70R : UdonSharpBehaviour { public int seed; public int result; void Start(){ S70R s = new S70R(); result = s.Run(seed); } }")]
    public void B70_GenericStructLocalFunction_ResolvesClosedTypeArgs_Compiles(string cls, string body)
    {
        var uasm = TestHelper.CompileToUasm($@"
using System; using UdonSharp;
{body}", cls);
        Assert.DoesNotContain("TArray.__ctor", uasm);   // no bogus unmapped-T array ctor
    }

    [Fact]
    public void B70_TwoStructInstantiations_SharedLFUsingStructT_RejectsAliasing()
    {
        // A15: GS<int>.Run and GS<string>.Run share ONE hoisted LF that uses the struct's T; it is emitted
        // with the first spec's T (VM-proven divergent: default(T) via GD<int>/GD<string> returned 410 not
        // 310 — both ran with T=int). So a type-param that varies across the two struct instantiations pins
        // exactly like a method-instantiation alias (same class as B64). Loud reject, not silent-wrong.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public struct GS70S<T> { public static int Run(int seed){ int Lf<U>(){ object o = default(T); return (o == null ? 100 : 200); } return Lf<int>() + seed; } }
public class B70S : UdonSharpBehaviour { public int seedA; public int seedB; public int result;
  void Start(){ int a = GS70S<int>.Run(seedA); int b = GS70S<string>.Run(seedB); result = a + b; } }", "B70S"));
        Assert.Contains("type parameter", ex.Message);
    }

    // ── B72: enum→floating/decimal is a real conversion, not a re-typing — the emitter must produce the
    //         SystemConvert.__To{Double,Single,Decimal}__, not COPY the raw underlying int into the wider
    //         slot. (VM value pins in the harness; DiffFuzz numeric coercion masks the double/float mistype,
    //         so the structural extern presence is the real regression guard.) enum→object stays identity. ──

    [Theory]
    [InlineData("B72Dd", "double", "SystemConvert.__ToDouble__SystemInt32__SystemDouble")]
    [InlineData("B72Ff", "float", "SystemConvert.__ToSingle__SystemInt32__SystemSingle")]
    [InlineData("B72Mm", "decimal", "SystemConvert.__ToDecimal__SystemInt32__SystemDecimal")]
    public void B72_EnumToFloatingDecimal_EmitsConversion(string cls, string dstType, string expectedExtern)
    {
        var uasm = TestHelper.CompileToUasm($@"
using System; using UdonSharp;
public enum E72c {{ A, B = 5, C = 10 }}
public class {cls} : UdonSharpBehaviour {{ public int seed; public {dstType} result; void Start(){{ E72c e = (E72c)seed; result = ({dstType})e; }} }}", cls);
        Assert.Contains(expectedExtern, uasm);
    }

    [Fact]
    public void B72_EnumToObjectBoxing_StaysIdentity_NoBogusConvert()
    {
        // Control: enum→object boxing must NOT mint a SystemConvert.__ToObject__ (the B61 fix this preserves).
        var uasm = TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public enum E72b { A, B = 5 }
public class B72Bx : UdonSharpBehaviour { public int seed; public int result; void Start(){ E72b e = (E72b)seed; object o = e; result = (o == null) ? 0 : ((int)(E72b)o); } }", "B72Bx");
        Assert.DoesNotContain("SystemConvert.__ToObject__", uasm);
    }

    // ── B64: the closure-pin is per-parameter AND capture-aware. A second instantiation that only varies a
    //         type param NO closure uses is legal; a varying CLOSURE-USED param still pins on type; and a
    //         STATIC generic method whose captures alias across its inlined specializations pins on capture
    //         (instance-method captures de-alias via per-activation env records and stay legal). ──

    [Fact]
    public void B64_MultiInstantiation_VaryingParamUnusedByClosure_CaptureFree_Compiles()
    {
        // H.Run<T,U>'s closure uses T (new T[2]) but captures nothing, and both calls fix T=int (only U
        // varies). The shared hoist was emitted with T=int and holds no capture cell, so nothing aliases —
        // must compile (before B64 the pin fired on any type-param-using closure regardless of the varying
        // param, and rejected the second distinct instantiation).
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public static class H64A {
  public static int Run<T, U>(T t, U u) { System.Func<int> inner = () => { T[] arr = new T[2]; return arr.Length; }; return inner(); }
}
public class B64A : UdonSharpBehaviour {
  public int result;
  void Start(){ int a = H64A.Run<int, string>(3, ""x""); int b = H64A.Run<int, bool>(4, true); result = a + b; }
}", "B64A");
    }

    [Fact]
    public void B64_MultiInstantiation_VaryingClosureUsedParam_Rejects()
    {
        // H.Run<T,U>'s closure returns U (uses U). Two calls fix T=int and vary U (string/bool): the shared
        // hoist can only carry one U, so the second instantiation genuinely aliases — must still reject.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public static class H64B {
  public static U Run<T, U>(T t, U u) { System.Func<U> inner = () => u; return inner(); }
}
public class B64B : UdonSharpBehaviour {
  public int result;
  void Start(){ var a = H64B.Run<int, string>(3, ""x""); var b = H64B.Run<int, bool>(4, true); result = a.Length + (b ? 1 : 0); }
}", "B64B"));
        Assert.Contains("type parameter", ex.Message);
    }

    [Fact]
    public void B64_MultiInstantiation_StaticCapturingClosure_Rejects()
    {
        // Soundness: H64C.Run is a STATIC generic method whose closure captures the parameter `t`. Even
        // though the closure-used T is constant (int in both calls) and only U varies, a static method's
        // inlined specializations share one hoisted closure with no per-activation env record, so the
        // capture cell aliases across the two instantiations (VM-proven: it returns 3+3, not 3+4). Must
        // reject. (The analogous INSTANCE-method shape de-aliases and stays legal — M4NonTCap.)
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public static class H64C {
  public static T Run<T, U>(T t, U u) { System.Func<T> inner = () => t; return inner(); }
}
public class B64C : UdonSharpBehaviour {
  public int result;
  void Start(){ int a = H64C.Run<int, string>(3, ""x""); int b = H64C.Run<int, bool>(4, true); result = a + b; }
}", "B64C"));
        Assert.Contains("captures locals/parameters", ex.Message);
    }

    // ── B65: a type-parameter receiver (T : Behaviour) resolves the inherited-member extern owner through
    //         the ambient type-param map to the CONCRETE leaf (which has the extern), not the abstract base. ──

    [Fact]
    public void B65_TypeParamReceiver_InheritedMember_ResolvesToConcreteLeaf()
    {
        // Read<T>(T c) where T : Behaviour reads c.enabled. T is inferred as Camera at the call site, so the
        // GET must emit UnityEngineCamera.__get_enabled__ (Camera carries the inherited extern) — before B65
        // the type-param receiver on the getter path fell through to the abstract UnityEngineBehaviour owner,
        // whose __get_enabled__ extern does not exist (UasmValidationException: Unknown extern).
        var uasm = TestHelper.CompileToUasm(@"
using UnityEngine; using UdonSharp;
public class B65G : UdonSharpBehaviour {
  public Camera cam; public bool r;
  bool Read<T>(T c) where T : Behaviour { return c.enabled; }
  void Start(){ r = Read(cam); }
}", "B65G");
        Assert.Contains("UnityEngineCamera.__get_enabled__", uasm);
        Assert.DoesNotContain("UnityEngineBehaviour.__get_enabled__", uasm);
    }

    [Fact]
    public void B65_AbstractBaseTypedReceiver_NonGeneric_StaysLoudReject()
    {
        // Control: a receiver STATICALLY typed as the abstract base Behaviour (not a type param bound to a
        // leaf) has no concrete extern owner — Udon registers .enabled per concrete type, never under
        // UnityEngineBehaviour — so it must stay a loud reject, not silently resolve.
        Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UnityEngine; using UdonSharp;
public class B65N : UdonSharpBehaviour {
  public Behaviour b;
  void Start(){ b.enabled = false; }
}", "B65N"));
    }
}
