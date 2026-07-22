using Xunit;

namespace USugar.Tests;

// new T() (kind-level census gap closed 2026-07-11): monomorphization substitutes T to a concrete
// type, so `new T()` mints as the concrete class/struct/primitive; member access on a T-typed
// receiver resolves through the type-param map. VM value gates live in the harness.
public class NewTypeParameterTests
{
    [Fact]
    public void NewT_Struct_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public struct SP2 { public int x; }
public static class F1 { public static int M<T>() where T : struct { T v = new T(); return 0; } }
public class NtP1 : UdonSharpBehaviour { public int result; void Start(){ result = F1.M<SP2>(); } }", "NtP1");

    // FLIPPED 2026-07-14 (Phase-A closed-world armor): a user-class `new T()` whose concrete type is never
    // constructed directly is invisible to the Phase-1 minted census — the mint left bundle[0] null and every
    // The substitution-threaded mint census closes `new T()` to the call-site class specialization, so
    // bundle[0] receives the same typeobj as a direct concrete construction.
    [Fact]
    public void NewT_ClassNeverMintedDirectly_CompilesWithTypeObj()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
public class Cel { public int v; }
public static class F2 { public static int M<T>(int n) where T : Cel, new() { T c = new T(); c.v = n; return c.v; } }
public class NtP2 : UdonSharpBehaviour { public int result; void Start(){ result = F2.M<Cel>(7); } }", "NtP2");
        Assert.Contains("__typeobj_Cel", uasm);
    }

    [Fact]
    public void NewT_ClassAlsoMintedDirectly_MemberAccess_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class Cel { public int v; }
public static class F2 { public static int M<T>(int n) where T : Cel, new() { T c = new T(); c.v = n; return c.v; } }
public class NtP2b : UdonSharpBehaviour { public int result; void Start(){ Cel k = new Cel(); k.v = 1; result = F2.M<Cel>(7) + k.v; } }", "NtP2b");

    [Fact]
    public void TypeParamTypedMember_Access_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class Cel2 { public int v; }
public static class F3 { public static int Set<T>(T c, int n) where T : Cel2 { c.v = n; return c.v; } }
public class NtP3 : UdonSharpBehaviour { public int result; void Start(){ Cel2 x = new Cel2(); result = F3.Set<Cel2>(x, 5); } }", "NtP3");
}
