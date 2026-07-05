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
}
