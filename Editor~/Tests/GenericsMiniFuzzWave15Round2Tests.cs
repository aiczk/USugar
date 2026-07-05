using System;
using Xunit;

namespace USugar.Tests;

// Generics mini-fuzz wave-15 round-2 batch — tracked compile/reject pins (values verified on the real
// VM by the gitignored harness). See docs/roadmap.md B56-B60.
public class GenericsMiniFuzzWave15Round2Tests
{
    // ── B56: a struct-hosted generic method must populate FirstGenericSpec so a nested LF referencing
    // the method's T resolves it (the struct arm returned before the generic-method registration). ──

    [Fact]
    public void B56_StructGenericMethod_NonGenericLF_ResolvesMethodTypeParam()
    {
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public struct R2Box3 {
  public int Run<T>(T tv){ int Inner(){ T[] ta = new T[1]; ta[0] = tv; return (int)(object)ta[0]; } return Inner(); }
}
public class R2E : UdonSharpBehaviour {
  public int seed; public int result;
  void Start(){ R2Box3 b = new R2Box3(); result = b.Run<int>(seed); }
}", "R2E");
    }

    [Fact]
    public void B56_StructGenericMethod_MultiParam_NonGenericLF_Resolves()
    {
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public struct R2Box2 {
  public int Run<T, U>(T tv, U u){ int Inner(){ T[] ta = new T[1]; ta[0] = tv; return (int)(object)ta[0]; } return Inner(); }
}
public class R2C : UdonSharpBehaviour {
  public int seed; public int result;
  void Start(){ R2Box2 b = new R2Box2(); result = b.Run<int, int>(seed, 9); }
}", "R2C");
    }

    [Fact]
    public void B56_StructGenericMethod_DualInstantiation_TDependentLF_StillRejects()
    {
        // Two instantiations of a struct generic method whose LF references T → shared hoist would run the
        // first spec's types → correct pin (same as the class case; the FirstGenericSpec record now exists).
        Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public struct R2BoxD {
  public int Run<T>(T tv){ Func<int> f = () => { T[] ta = new T[1]; ta[0] = tv; return ta.Length; }; return f(); }
}
public class R2Dual : UdonSharpBehaviour {
  public int r1, r2;
  void Start(){ R2BoxD b = new R2BoxD(); r1 = b.Run<int>(1); r2 = b.Run<string>(""x""); }
}", "R2Dual"));
    }
}
