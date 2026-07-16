using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// WaveJoint round-3 regression pins (2026-07-17).
/// A11: an inline-cast enum concat operand (`s += (E)(intExpr)`, `"" + (E)(intExpr)`) printed the
/// underlying number — UnwrapConversions stripped the user's enum cast along with the compiler's
/// boxing conversion, so the IsUserEnum/class checks saw the cast's INPUT type. The concat surfaces
/// now use UnwrapConcatOperand, which strips only value-preserving conversions (identity / boxing /
/// reference) and stops at value conversions — fixing the mirror direction too (`"" + (int)e`
/// name-stringified where C# prints the number).
/// B03/B05/B06/B11: `base[...]` and non-virtual `this[...]` indexer access on an object[]-emulated
/// containing type (v1 class or struct) rode the behaviour-only same-program internal-call arms
/// (VisitIndexerGet / CaptureLValue / EmitWriteBack) and dropped the hidden receiver param0 — a
/// loud CInternalCall arity-skew reject on legal C#. Those arms now exclude object[]-emulated
/// containing types, which fall through to the receiver-as-param0 arms.
/// </summary>
public class WaveJointRound3RegressionTests
{
    // ── A11: inline enum-cast concat operand synthesizes the name string ──

    [Fact]
    public void CompoundInlineCastEnum_MintsEnumNameHelper()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum Wj3E1 { Alpha, Bravo, Charlie }
public class Wj3A : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ s = """"; s += (Wj3E1)(seed % 3); }
}", "Wj3A");
        Assert.Contains("__enumstr_", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Bravo"));
    }

    [Fact]
    public void BinaryInlineCastEnum_MintsEnumNameHelper()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum Wj3E2 { Alpha, Bravo, Charlie }
public class Wj3B : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ s = """" + (Wj3E2)(seed % 3); }
}", "Wj3B");
        Assert.Contains("__enumstr_", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Bravo"));
    }

    [Fact]
    public void ExplicitIntCastOfEnum_DoesNotNameStringify()
    {
        // Mirror direction: `"" + (int)e` prints the NUMBER in C# — the old full unwrap landed on the
        // enum local and synthesized the name table. No __enumstr_ helper may be minted here.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum Wj3E3 { Alpha, Bravo, Charlie }
public class Wj3C : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ Wj3E3 e = (Wj3E3)(seed % 3); s = """" + (int)e; s += (int)e; }
}", "Wj3C");
        Assert.DoesNotContain("__enumstr_", uasm);
        Assert.DoesNotContain(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Bravo"));
    }

    // ── B03/B05/B06/B11: this/base indexer on object[]-emulated receivers carries param0 ──
    // (CompileToUasm runs UasmValidator + the CInternalCall arity gate — pre-fix each of these threw
    // "passes N args but the target declares N+1 param fields".)

    [Fact]
    public void ThisIndexerRead_PlainV1ClassBody_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Wj3D1 { protected int[] a = new int[4]; public int this[int i] { get { return a[i]; } set { a[i] = value; } } public int Sum(){ return this[1] + this[2]; } }
public class Wj3D : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj3D1 x = new Wj3D1(); x[1] = seed; x[2] = 3; r = x.Sum(); }
}", "Wj3D");
        Assert.Contains("get_Item", uasm);
    }

    [Fact]
    public void BaseIndexerRead_InOverrideGetter_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Wj3E1B { protected int[] a = new int[4]; public virtual int this[int i] { get { return a[i]; } set { a[i] = value; } } }
public class Wj3E2B : Wj3E1B { public override int this[int i] { get { return base[i] + 100; } set { a[i] = value; } } }
public class Wj3E : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj3E1B x = new Wj3E2B(); x[1] = seed; r = x[1]; }
}", "Wj3E");
        Assert.Contains("get_Item", uasm);
    }

    [Fact]
    public void BaseIndexerCompound_InOverrideSetter_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Wj3F1 { protected int[] a = new int[4]; public virtual int this[int i] { get { return a[i]; } set { a[i] = value; } } }
public class Wj3F2 : Wj3F1 { public override int this[int i] { get { return a[i]; } set { base[i] += value * 2; } } }
public class Wj3F : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj3F1 x = new Wj3F2(); x[1] = seed; r = x[1]; }
}", "Wj3F");
        Assert.Contains("set_Item", uasm);
    }

    [Fact]
    public void ThisIndexerCompound_InsideStructBody_Compiles()
    {
        // The B48 twin on the compound path: CaptureLValue/EmitWriteBack's this-indexer arms had no
        // object[]-emulated exclusion at all, so a struct's `this[i] += d` skewed the same way.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Wj3GS { public int x; public int y; public int this[int i] { get { return i == 0 ? x : y; } set { if (i == 0) x = value; else y = value; } } public void Bump(int i, int d){ this[i] += d; } }
public class Wj3G : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj3GS s; s.x = seed; s.y = 2; s.Bump(0, 5); r = s.x * 100 + s.y; }
}", "Wj3G");
        Assert.Contains("get_Item", uasm);
    }

    // ── C07: const-folded decimal (negative literal / compile-time decimal arithmetic) ──
    // ParseConstValue had no SystemDecimal arm: the folded value fell to the integer default arm,
    // failed both parses, and silently became null → 0 on the heap (surfaced by closing the
    // harness's decimal-remainder extern gap — the ExternMissing category was masking it).

    [Fact]
    public void NegativeDecimalConstFold_MintsDecimalConstant()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class Wj3I : UdonSharpBehaviour {
    public int seed; public decimal d;
    void Start(){ d = -12.5m + seed; }
}", "Wj3I");
        Assert.Contains(consts, c => c.UdonType == "SystemDecimal" && Equals(c.Value, -12.5m));
    }

    [Fact]
    public void FoldedDecimalArithmetic_MintsDecimalConstant()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class Wj3J : UdonSharpBehaviour {
    public int seed; public decimal d;
    void Start(){ d = (2.5m + 1.25m) * 2m + seed; }
}", "Wj3J");
        Assert.Contains(consts, c => c.UdonType == "SystemDecimal" && Equals(c.Value, 7.5m));
    }

    [Fact]
    public void ThisComputedPropertyCompound_InsideV1ClassBody_Compiles()
    {
        // The case-306 family member: EmitWriteBack's this-PROPERTY setter arm called the setter with
        // the value only, so a v1-class `this.P += d` dropped the receiver param the same way.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Wj3H1 { public int v; public int P { get { return v + 1; } set { v = value; } } public void Bump(int d){ P += d; } }
public class Wj3H : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj3H1 x = new Wj3H1(); x.v = seed; x.Bump(3); r = x.v; }
}", "Wj3H");
        Assert.Contains("get_P", uasm);
    }
}
