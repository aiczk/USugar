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

    // ── B63: typeof(A)==typeof(B) on two DISTINCT C# types that fold onto one Udon tag is rejected at the
    //         comparison site; a bare typeof token (GetComponent(typeof(...)), .Name) stays legal.

    [Theory]
    [InlineData("B63A", @"public class B63A : UdonSharpBehaviour { public int result; void Start(){ result = (typeof(B63A) == typeof(B63B2)) ? 1 : 0; } }
public class B63B2 : UdonSharpBehaviour { void Start(){} }")]
    [InlineData("B63E", @"public enum E63 { A, B } public class B63E : UdonSharpBehaviour { public int result; void Start(){ result = (typeof(E63) == typeof(int)) ? 1 : 0; } }")]
    public void B63_TypeofCollapseSet_EqualityRejectsLoudly(string cls, string body)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm($@"
using System; using UdonSharp;
{body}", cls));
        Assert.Contains("typeof(", ex.Message);
    }

    [Fact]
    public void B63_TypeofDistinguishable_EqualityStillCompiles()
    {
        // Control: primitive/array typeof stays honest (distinguishable) and compiles.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B63C : UdonSharpBehaviour {
  public int result;
  void Start(){ int a = (typeof(int) == typeof(int)) ? 1 : 0; int b = (typeof(int[]) == typeof(string[])) ? 0 : 2; result = a + b + typeof(int).Name.Length; }
}", "B63C");
    }

    [Fact]
    public void B63_BareCollapseSetTypeofToken_StillCompiles()
    {
        // Control: a collapse-set typeof used only as a TOKEN (never == another type) is legal — the token
        // resolves through the receiver extern; only the ==/!= comparison is unsound. Mirrors the Compat
        // GetComponent(typeof(UdonBehaviour)) shape.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B63T : UdonSharpBehaviour {
  public int result;
  void Start(){ System.Type t = typeof(B63T); result = t.Name.Length; }
}", "B63T");
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
}
