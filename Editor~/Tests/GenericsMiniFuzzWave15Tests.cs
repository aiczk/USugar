using System;
using Xunit;

namespace USugar.Tests;

// Generics mini-fuzz wave-15 batch — tracked compile/reject pins (values verified on the real VM by the
// gitignored harness). One class per finding; see docs/roadmap.md B51-B55.
public class GenericsMiniFuzzWave15Tests
{
    // ── B53: a nested generic local function's OWN type parameter must not pin the enclosing generic ──
    // TypeUsesMethodTypeParam treated ANY ITypeParameterSymbol as the enclosing method's, so Inner<U>'s
    // unrelated U falsely pinned M<T> to one instantiation. The pin must filter to the def's OWN params.

    [Fact]
    public void B53_UnrelatedNestedGenericTypeParam_DoesNotPin()
    {
        // M's T is never referenced; only Inner<U> uses its own U. Both M<int>/M<string> must compile.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B53P1 : UdonSharpBehaviour {
  public int r1, r2;
  int M<T>(int baseVal){
    int Inner<U>(){ U[] arr = new U[3]; return baseVal + arr.Length; }
    return Inner<int>();
  }
  void Start(){ r1 = M<int>(10); r2 = M<string>(20); }
}", "B53P1");
    }

    [Fact]
    public void B53_UnrelatedNestedGenericTypeParam_ThroughClosure_DoesNotPin()
    {
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B53P2 : UdonSharpBehaviour {
  public int r1, r2;
  int M<T>(int baseVal){
    Func<int> outer = () => {
      int Inner<U>(){ U[] arr = new U[3]; return baseVal + arr.Length; }
      return Inner<int>();
    };
    return outer();
  }
  void Start(){ r1 = M<int>(10); r2 = M<string>(20); }
}", "B53P2");
    }

    [Fact]
    public void B53_TDependentClosure_StillRejects()
    {
        // Control (b): a closure genuinely referencing M's own T must STILL pin (two instantiations reject).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B53Ctrl : UdonSharpBehaviour {
  public int r1, r2;
  int M<T>(int baseVal){
    Func<int> f = () => { T[] a = new T[1]; return baseVal + a.Length; };
    return f();
  }
  void Start(){ r1 = M<int>(10); r2 = M<string>(20); }
}", "B53Ctrl"));
        Assert.Contains("type parameters", ex.Message);
    }

    // ── B51: a nested generic local function must resolve the ENCLOSING generic's type parameters ──
    // The generic LF's OriginalDefinition body-walk freshens the enclosing T too, so it resolved as raw
    // 'T' (bogus TArray extern). The rekey now carries the enclosing owners' params under body-walk symbols.

    [Fact]
    public void B51_GenericLocalFunction_ResolvesEnclosingTypeParam()
    {
        // Single instantiation (passes the pin); Inner<W> references both enclosing T and its own W.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B51C1 : UdonSharpBehaviour {
  public int r1;
  int M<T>(int b){ int Inner<W>(){ T[] t = new T[1]; W[] w = new W[2]; return b + t.Length + w.Length; } return Inner<int>(); }
  void Start(){ r1 = M<int>(10); }
}", "B51C1");
    }

    [Fact]
    public void B51_GenericLocalFunction_MultiLevelAndMultiParamEnclosing()
    {
        // Two generic levels (M<T> -> Mid<S> -> Inner<W>): every enclosing owner's params must resolve.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B51V4 : UdonSharpBehaviour {
  public int r1;
  int M<T>(int b){
    int Mid<S>(){
      int Inner<W>(){ T[] t = new T[1]; S[] s = new S[2]; W[] w = new W[3]; return b + t.Length + s.Length + w.Length; }
      return Inner<int>();
    }
    return Mid<int>();
  }
  void Start(){ r1 = M<string>(10); }
}", "B51V4");
    }

    [Fact]
    public void B53_MultiSpecCapturingGenericLocalFunction_StillRejects()
    {
        // Control (c): a generic LF's OWN U in its OWN escaping closure across 2 of its OWN specs is the
        // roadmap M4 'unverified' scenario — a correct clean reject (not a false positive on the ENCLOSING).
        Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class B53MultiSpec : UdonSharpBehaviour {
  public int r1, r2;
  void Start(){
    int baseVal = 100;
    Func<int> Inner<U>(){ return () => baseVal + new U[2].Length; }
    var f1 = Inner<int>();
    var f2 = Inner<string>();
    r1 = f1(); r2 = f2();
  }
}", "B53MultiSpec"));
    }
}
