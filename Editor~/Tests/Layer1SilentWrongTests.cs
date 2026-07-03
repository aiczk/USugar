using Xunit;

namespace USugar.Tests;

/// <summary>
/// Layer-1 silent-wrong sweep: compile-clean code that produced a wrong value / wrong cross-client
/// behavior, made LOUD (doctrine: "LOUD or CORRECT — a silent wrong value is never acceptable; loud
/// over-rejection is acceptable"). Four holes, each pinned reject + a compiling control.
///  1. Multi-dimensional arrays (int[,]) silently dropped dimensions 2+ → loud reject at the single
///     type-lowering choke point (creation / element read / element write / field/param/local).
///  2. [NetworkCallable] delegate smuggled inside a struct/tuple param passed the delegate guard →
///     ContainsDelegateType now recurses aggregate fields.
///  3. `checked` was a silent no-op (overflow wraps where C# throws OverflowException) → loud reject
///     on IsChecked==true; `unchecked`/default wrapping (the C#-correct default) still compiles.
///  4. Non-exhaustive switch EXPRESSION silently returned default(T) → runtime LogError (matches the
///     §8-8 null-invoke deviation) then default(T).
/// </summary>
public class Layer1SilentWrongTests
{
    // ── 1. Multi-dimensional arrays ──

    [Fact]
    public void Multidim_Creation_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1M1 : UdonSharpBehaviour {
    public void M() { var a = new int[2,3]; }
}", "L1M1"));
        Assert.Contains("Multi-dimensional arrays", ex.Message);
    }

    [Fact]
    public void Multidim_ElementRead_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1M2 : UdonSharpBehaviour {
    public int[,] a;
    public int M() { return a[1,2]; }
}", "L1M2"));
        Assert.Contains("Multi-dimensional arrays", ex.Message);
    }

    [Fact]
    public void Multidim_ElementWrite_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1M3 : UdonSharpBehaviour {
    public int[,] a;
    public void M() { a[1,2] = 5; }
}", "L1M3"));
        Assert.Contains("Multi-dimensional arrays", ex.Message);
    }

    [Fact]
    public void Multidim_Field_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1M4 : UdonSharpBehaviour {
    public int[,] grid;
}", "L1M4"));
        Assert.Contains("Multi-dimensional arrays", ex.Message);
    }

    [Fact]
    public void Jagged_StillCompiles()
    {
        // Control: int[][] is the supported alternative — must keep compiling unchanged.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1M5 : UdonSharpBehaviour {
    public int M() { int[][] a = new int[2][]; a[0] = new int[3]; a[0][2] = 5; return a[0][2]; }
}", "L1M5");
        Assert.NotNull(uasm);
    }

    // ── 2. [NetworkCallable] delegate-in-struct/tuple smuggle ──

    [Fact]
    public void NetworkCallable_StructWithDelegateField_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public struct L1NS { public System.Func<int> cb; public int n; }
public class L1N1 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle(L1NS s) { }
}", "L1N1"));
        Assert.Contains("delegate-typed parameter 's'", ex.Message);
    }

    [Fact]
    public void NetworkCallable_TupleWithDelegate_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class L1N2 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle((int a, System.Func<int> f) t) { }
}", "L1N2"));
        Assert.Contains("delegate-typed parameter 't'", ex.Message);
    }

    [Fact]
    public void NetworkCallable_NestedStructWithDelegate_Rejects()
    {
        // Delegate two aggregate levels deep still reachable.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public struct L1Inner { public System.Action a; }
public struct L1Outer { public L1Inner inner; public int n; }
public class L1N3 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle(L1Outer o) { }
}", "L1N3"));
        Assert.Contains("delegate-typed parameter 'o'", ex.Message);
    }

    [Fact]
    public void NetworkCallable_PlainStruct_StillCompiles()
    {
        // Control: a struct carrying no delegate is fine — the guard must stay narrow.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public struct L1PS { public int n; public float f; }
public class L1N4 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle(L1PS s) { }
}", "L1N4");
        Assert.Contains(".export Handle", uasm);
    }

    // ── 3. checked context ──

    [Fact]
    public void CheckedExpr_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1C1 : UdonSharpBehaviour {
    public int a, b;
    public int M() { return checked(a + b); }
}", "L1C1"));
        Assert.Contains("'checked' context is not supported", ex.Message);
    }

    [Fact]
    public void CheckedBlock_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1C2 : UdonSharpBehaviour {
    public int a, b, c;
    public void M() { checked { c = a + b; } }
}", "L1C2"));
        Assert.Contains("'checked' context is not supported", ex.Message);
    }

    [Fact]
    public void CheckedCast_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1C3 : UdonSharpBehaviour {
    public int a;
    public byte M() { return checked((byte)a); }
}", "L1C3"));
        Assert.Contains("'checked' context is not supported", ex.Message);
    }

    [Fact]
    public void PlainArithmetic_StillCompiles()
    {
        // Control: ordinary (IsChecked==false) arithmetic is the default and must not reject.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1C4 : UdonSharpBehaviour {
    public int a, b, r;
    public void M() { r = a + b; }
}", "L1C4");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void Unchecked_StillCompiles()
    {
        // Control: explicit unchecked is wrapping = USugar's only behavior = C#-correct.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1C5 : UdonSharpBehaviour {
    public int a, b, r;
    public void M() { r = unchecked(a + b); }
}", "L1C5");
        Assert.NotNull(uasm);
    }

    // ── 4. Non-exhaustive switch expression ──

    [Fact]
    public void NonExhaustiveSwitchExpr_EmitsRuntimeLogError()
    {
        // No arm matches at runtime → LogError (loud), then the default(T) the slot was seeded with.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1S1 : UdonSharpBehaviour {
    public int x, r;
    public void M() { r = x switch { 1 => 10, 2 => 20 }; }
}", "L1S1");
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void SwitchExprWithDiscardArm_NoLogError()
    {
        // Control: a discard (_) arm is exhaustive at the language level — no fallthrough LogError.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class L1S2 : UdonSharpBehaviour {
    public int x, r;
    public void M() { r = x switch { 1 => 10, 2 => 20, _ => 0 }; }
}", "L1S2");
        Assert.DoesNotContain("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }
}
