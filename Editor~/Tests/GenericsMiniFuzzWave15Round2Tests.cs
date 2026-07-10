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

    // ── B57: a closure whose only "capture" is its own self-declared symbol (is-pattern var, out-var,
    // deconstruction, recursive-pattern var) must not be misclassified as capturing. ──

    [Theory]
    [InlineData("B57a", @"int Inner(object o){ if (o is int tv) return tv; return 0; } return Inner(baseVal);")]
    [InlineData("B57b", @"Func<object,int> f = o => (o is int tv) ? tv : 0; return f(baseVal);")]
    [InlineData("B57c", @"int Inner(object o){ if (o is int tv) return tv; return -1; } return Inner(baseVal) + Peek();")]
    [InlineData("B57d", @"int Inner(){ int r; if (int.TryParse(""7"", out int p)) r = p; else r = 0; return r; } return Inner();")]
    [InlineData("B57e", @"int Inner(){ (int a, int b) t = (baseVal, 2); var (x, y) = t; return x + y; } return Inner();")]
    public void B57_SelfDeclaredSymbolNotMisclassifiedAsCapture(string cls, string body)
    {
        TestHelper.CompileToUasm($@"
using System; using UdonSharp;
public class {cls} : UdonSharpBehaviour {{
  public int seed; public int result;
  int Peek() => 5;
  void Start(){{ result = M(seed); }}
  int M(int baseVal){{ {body} }}
}}", cls);
    }

    // ── B59: a concrete enum receiver's inherited Equals/GetHashCode/CompareTo resolves to the
    // underlying-primitive extern (SystemInt32), not the unregistered SystemEnum owner. ──

    [Theory]
    [InlineData("B59E", "result = e.Equals(EE.A) ? 1 : 0;")]
    [InlineData("B59H", "result = e.GetHashCode();")]
    [InlineData("B59C", "result = e.CompareTo(EE.B);")]
    public void B59_ConcreteEnumInheritedMember_ResolvesUnderlyingExtern(string cls, string body)
    {
        var uasm = TestHelper.CompileToUasm($@"
using System; using UdonSharp;
public enum EE {{ A, B, C }}
public class {cls} : UdonSharpBehaviour {{ public int seed; public int result; void Start(){{ EE e = (EE)(seed % 3); {body} }} }}", cls);
        Assert.DoesNotContain("SystemEnum.__", uasm);
    }

    // ── B60: a concrete user-struct receiver's inherited Equals/GetHashCode is a designed loud reject
    // (parity with the type-parameter-receiver case), not a bogus SystemValueType extern. ──

    [Theory]
    [InlineData("B60E", "a.Equals(b)")]
    [InlineData("B60H", "a.GetHashCode() == 0")]
    public void B60_ConcreteStructInheritedMember_DesignedReject(string cls, string expr)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm($@"
using System; using UdonSharp;
public struct S60 {{ public int x; }}
public class {cls} : UdonSharpBehaviour {{
  public int result;
  void Start(){{ S60 a = new S60{{x=1}}; S60 b = new S60{{x=1}}; result = ({expr}) ? 1 : 0; }}
}}", cls));
        Assert.Contains("user-defined struct", ex.Message);
        Assert.DoesNotContain("SystemValueType", ex.Message);
    }

    // ── ConstTypeToken funnel (audit): every System.Type-constant bake (o is T / typeof(T) /
    // GetComponent<T>) routes through one resolve-or-throw-loud choke point. ──

    [Fact]
    public void ConstTypeToken_TypeofGenericTypeParam_Resolves()
    {
        // typeof(T)/typeof(W) in a generic method and a nested generic LF must resolve, not bake null.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public class CTT : UdonSharpBehaviour {
  public int r1, r2, r3;
  int Tag<T>() => typeof(T).Name.Length;
  int M<T>(){ int Inner<W>(){ return typeof(W).Name.Length; } return Inner<int>(); }
  void Start(){ r1 = Tag<int>(); r2 = Tag<string>(); r3 = M<int>(); }
}", "CTT");
    }

    // ── Owner-resolution funnel (audit): the inherited-member extern owner resolves through one shared
    // choke point across every site. Control shapes that must keep resolving to the receiver's type. ──

    [Theory]
    [InlineData("FN1", "result = this.name;", "public string result;")]
    [InlineData("FN2", "result = name;", "public string result;")]
    [InlineData("FN3", "result = gameObject.name;", "public string result;")]
    [InlineData("FN4", "this.name = \"x\";", "")]                 // B55 setter door: property-SET write-back
    [InlineData("FN5", "gameObject.name = \"z\";", "")]           // inherited settable property on a concrete receiver
    public void OwnerFunnel_InheritedUnityMember_ResolvesReceiverType(string cls, string body, string field)
    {
        var uasm = TestHelper.CompileToUasm($@"
using UnityEngine; using UdonSharp;
public class {cls} : UdonSharpBehaviour {{ {field} void Start(){{ {body} }} }}", cls);
        Assert.DoesNotContain("SystemReflectionMemberInfo", uasm);
    }

    // ── B58: a foreign generic STATIC method (helper class or struct) as a delegate target is now
    // supported — its spec is inlined into this program, so it bridges through the same machinery. ──

    [Theory]
    [InlineData("B58a", @"public static class HGC { public static T Id<T>(T x) => x; }",
        @"Func<int,int> d = HGC.Id<int>; result = d(5);")]
    [InlineData("B58b", @"public struct HGS { public static T Id<T>(T x) => x; }",
        @"Func<int,int> d = HGS.Id<int>; result = d(5);")]
    [InlineData("B58m", @"public struct LBPt { public static int Sum<T>(int a, int b) => a + b; }",
        @"Func<int,int,int> d = null; d += LBPt.Sum<int>; result = d(3,4);")]
    public void B58_ForeignGenericStaticDelegate_Compiles(string cls, string helper, string body)
    {
        TestHelper.CompileToUasm($@"
using System; using UdonSharp;
{helper}
public class {cls} : UdonSharpBehaviour {{ public int result; void Start(){{ {body} }} }}", cls);
    }

    [Fact]
    public void B56_StructGenericMethod_DualInstantiation_TDependentLF_Compiles()
    {
        // FLIPPED 2026-07-10 (per-spec closure root fix): the T-dependent closure is duplicated per
        // spec, so dual instantiation is legal — VM oracle: harness PerSpec B70_GenericMethod probes.
        TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public struct R2BoxD {
  public int Run<T>(T tv){ Func<int> f = () => { T[] ta = new T[1]; ta[0] = tv; return ta.Length; }; return f(); }
}
public class R2Dual : UdonSharpBehaviour {
  public int r1, r2;
  void Start(){ R2BoxD b = new R2BoxD(); r1 = b.Run<int>(1); r2 = b.Run<string>(""x""); }
}", "R2Dual");
    }
}
