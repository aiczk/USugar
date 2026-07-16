using System;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

// M4b (2026-07-16): ToString on v1 user-class receivers is a full virtual dispatch — the sealed-only
// TryGetUserToString restriction is lifted. Three surfaces share EmitClassToStringDispatch (the same
// v2b-2 machinery as method/accessor dispatch): the interpolation hole, the string-concat operand, and
// the direct .ToString() call. An arm with a user override direct-calls the most-derived impl; an arm
// without one prints the C#-parity runtime type-name CONSTANT (CLR Type.ToString format). Null parity:
// implicit surfaces yield "" silently (C# Format/Concat semantics); a direct null.ToString() is the
// established null-invoke deviation (LogError + ""). These pin the lowered STRUCTURE; runtime values
// are pinned by the local DiffFuzz harness (CLR-oracle probes incl. namespace-qualified names).
public class M4bToStringDispatchTests
{
    [Fact]
    public void NonSealedOverride_Interpolation_DispatchesBothImpls()
    {
        // Both minted classes override: the hole lowers to a typeobj chain of two direct calls —
        // never a raw-bundle Format arg, never the old sealed-only reject.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TiB { public int v; public override string ToString(){ return ""b"" + v; } }
public class TiD : TiB { public override string ToString(){ return ""d"" + v; } }
public class M4bInterp : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start(){ TiB t = seed > 0 ? (TiB)new TiD() : new TiB(); r = $""[{t}]"".Length; }
}", "M4bInterp");
        Assert.True(Regex.Matches(uasm, @"__\d+_ToString").Count >= 2,
            "both slot impls must be emitted and dispatched");
        Assert.Contains("__typeobj_TiB", uasm);
        Assert.Contains("__typeobj_TiD", uasm);
        Assert.Contains("SystemString.__Format__SystemString_SystemObject__SystemString", uasm);
    }

    [Fact]
    public void NonSealedOverride_Concat_DispatchesBothImpls()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TcB { public int v; public override string ToString(){ return ""b"" + v; } }
public class TcD : TcB { public override string ToString(){ return ""d"" + v; } }
public class M4bConcat : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start(){ TcB t = seed > 0 ? (TcB)new TcD() : new TcB(); string s = ""x"" + t; r = s.Length; }
}", "M4bConcat");
        Assert.True(Regex.Matches(uasm, @"__\d+_ToString").Count >= 2);
        Assert.Contains("SystemString.__Concat__SystemObject_SystemObject__SystemString", uasm);
    }

    [Fact]
    public void DirectCall_BaseTypedNoOverride_MixedChain()
    {
        // Base declares NO override, derived does: `b.ToString()` on the base-typed receiver lowers to
        // a mixed chain — the derived arm direct-calls the override, the base arm assigns the runtime
        // type-name constant (CLR Object.ToString parity). This shape was the loud reject before M4b.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class TmB { public int v; }
public class TmD : TmB { public override string ToString(){ return ""d""; } }
public class M4bMixed : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start(){ TmB b = seed > 0 ? (TmB)new TmD() : new TmB(); r = b.ToString().Length; }
}", "M4bMixed");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "TmB"));   // type-name arm
        Assert.Matches(@"__\d+_ToString", uasm);                                                 // override arm
        Assert.Contains("__typeobj_TmB", uasm);
        Assert.Contains("__typeobj_TmD", uasm);
    }

    [Fact]
    public void NoOverride_Interpolation_EmitsTypeNameConstant_GlobalNamespace()
    {
        // No override anywhere: the singleton set devirtualizes to the bare type-name constant —
        // no ToString function, no extern ToString, and the no-namespace name has no dot.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class PlainG { public int v; }
public class M4bPlain : UdonSharpBehaviour {
    public int r;
    void Start(){ PlainG p = new PlainG(); r = $""{p}"".Length; }
}", "M4bPlain");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "PlainG"));
        Assert.DoesNotMatch(@"__\d+_ToString", uasm);
    }

    [Fact]
    public void NoOverride_TypeNameConstant_IsNamespaceQualified()
    {
        // CLR Object.ToString prints the FULL name — namespace-qualified when a namespace exists.
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
namespace My.Ns { public class PlainN { public int v; } }
public class M4bNsName : UdonSharpBehaviour {
    public int r;
    void Start(){ My.Ns.PlainN p = new My.Ns.PlainN(); r = $""{p}"".Length; }
}", "M4bNsName");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "My.Ns.PlainN"));
    }

    [Fact]
    public void NoOverride_ClosedGenericSpec_TypeNameUsesClrFormat()
    {
        // A closed generic spec minted at a concrete site prints the reflection-format name
        // (backtick arity + full argument names), matching CLR Type.ToString.
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class BoxG<T> { public T v; }
public class M4bGenName : UdonSharpBehaviour {
    public int r;
    void Start(){ BoxG<int> b = new BoxG<int>(); r = $""{b}"".Length; }
}", "M4bGenName");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "BoxG`1[System.Int32]"));
    }

    [Fact]
    public void InheritedOverride_DerivedArm_DispatchesBaseImpl()
    {
        // The derived class inherits the base override: its arm direct-calls the SAME impl (most-derived
        // walk), so no type-name constant is minted for either class.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class ThB { public int v; public override string ToString(){ return ""h"" + v; } }
public class ThD : ThB { }
public class M4bInherit : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start(){ ThB t = seed > 0 ? (ThB)new ThD() : new ThB(); r = $""{t}"".Length; }
}", "M4bInherit");
        Assert.Contains("__typeobj_ThB", uasm);
        Assert.Contains("__typeobj_ThD", uasm);
        Assert.Matches(@"__\d+_ToString", uasm);
        Assert.DoesNotContain(consts, c => c.UdonType == "SystemString" && (Equals(c.Value, "ThB") || Equals(c.Value, "ThD")));
    }

    [Fact]
    public void SealedOverride_Devirtualizes_NoChainArmor()
    {
        // The former M3 sealed fast path dissolves into the helper's devirt arm: a direct call with the
        // null guard but NO typeobj chain and NO no-match armor (its LogError const never appears).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public sealed class TsS { public int v; public override string ToString(){ return ""s"" + v; } }
public class M4bSealed : UdonSharpBehaviour {
    public int r;
    void Start(){ TsS t = new TsS(); r = $""{t}"".Length; }
}", "M4bSealed");
        Assert.Matches(@"__\d+_ToString", uasm);
        Assert.DoesNotContain(consts, c => c.Value is string m && m.Contains("matched no minted class"));
        Assert.DoesNotContain("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void ImplicitNull_IsSilent_DirectNull_IsLoudDeviation()
    {
        // C# parity: a null hole/operand yields "" with no error surface — the implicit lowering carries
        // no NRE LogError. A direct null.ToString() would NRE in C#: the deviation arm logs and yields "".
        var (_, interpConsts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public sealed class TnS { public int v; public override string ToString(){ return ""n""; } }
public class M4bNullSilent : UdonSharpBehaviour {
    public int r;
    void Start(){ TnS t = new TnS(); r = ($""{t}"" + t).Length; }
}", "M4bNullSilent");
        Assert.DoesNotContain(interpConsts, c => c.Value is string m && m.Contains("NullReferenceException"));

        var (_, directConsts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public sealed class TnD { public int v; public override string ToString(){ return ""n""; } }
public class M4bNullLoud : UdonSharpBehaviour {
    public int r;
    void Start(){ TnD t = new TnD(); r = t.ToString().Length; }
}", "M4bNullLoud");
        Assert.Contains(directConsts, c => c.Value is string m
            && m.Contains("NullReferenceException") && m.Contains("ToString()"));
    }

    [Fact]
    public void ChainForm_CarriesNoMatchArmor()
    {
        // A >=2 chain must stay loud on a laundered (non-null, no-typeobj-match) receiver.
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class TaB { public override string ToString(){ return ""a""; } }
public class TaD : TaB { public override string ToString(){ return ""b""; } }
public class M4bArmor : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start(){ TaB t = seed > 0 ? (TaB)new TaD() : new TaB(); r = $""{t}"".Length; }
}", "M4bArmor");
        Assert.Contains(consts, c => c.Value is string m && m.Contains("matched no minted class"));
    }

    [Fact]
    public void NewHidingToString_IsNotDispatched_PrintsTypeName()
    {
        // `new string ToString()` roots its OWN slot — the object.ToString stringify never calls it
        // (C#: Format calls the virtual slot), so the arm is the type-name constant.
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class HideC { public int v; public new string ToString(){ return ""hidden""; } }
public class M4bHide : UdonSharpBehaviour {
    public int r;
    void Start(){ HideC h = new HideC(); r = $""{h}"".Length; }
}", "M4bHide");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "HideC"));
    }

    [Fact]
    public void BaseToString_BoundToObject_PrintsRuntimeTypeName()
    {
        // base.ToString() binding DIRECTLY to object.ToString is non-virtual in C# yet still prints the
        // RUNTIME type name (Object.ToString reads GetType()) — the chain runs with override arms
        // disabled: the override's own body never re-enters through it (no infinite recursion), and the
        // DERIVED type-name constant is minted even though the derived class overrides.
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class TbB { public int v; }
public class TbD : TbB { public override string ToString(){ return base.ToString() + ""!""; } }
public class M4bBase : UdonSharpBehaviour {
    public int r;
    void Start(){ TbB t = new TbD(); r = $""{t}"".Length; }
}", "M4bBase");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "TbD"));
    }

    [Fact]
    public void OpenGenericFamily_StillRejectedByGuard()
    {
        // GuardOpenVirtualDispatch is shared with the method/accessor arms: a stringify whose family is
        // minted through an OPEN construction site has no spec-distinct runtime identity — loud.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GTs<T> { public T v; }
public class M4bOpenGen : UdonSharpBehaviour {
    public int r;
    int Make<T>() { GTs<T> g = new GTs<T>(); return $""{g}"".Length; }
    void Start(){ r = Make<int>(); }
}", "M4bOpenGen"));
        Assert.Contains("generic method", ex.Message);
    }

    [Fact]
    public void GetHashCode_And_GetType_StayRejected()
    {
        var ex1 = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GhC { public int v; }
public class M4bGh : UdonSharpBehaviour {
    public int r;
    void Start(){ GhC g = new GhC(); r = g.GetHashCode(); }
}", "M4bGh"));
        Assert.Contains("GetHashCode", ex1.Message);

        var ex2 = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtC { public int v; }
public class M4bGt : UdonSharpBehaviour {
    public int r;
    void Start(){ GtC g = new GtC(); r = g.GetType().Name.Length; }
}", "M4bGt"));
        Assert.Contains("GetType", ex2.Message);
    }

    // ── Compound-concat parity (B67/M4b twin found by the M4b DiffFuzz sweep): `s += x` is
    //    string.Concat one surface over — a user-enum operand must synthesize its name (was: silent
    //    number, CLR "xSpades" vs VM "x2") and a v1-class operand must dispatch ToString (was: loud
    //    erasure reject where the binary form dispatches). ──

    [Fact]
    public void CompoundConcat_UserEnum_SynthesizesName()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum CcSuit { Hearts, Spades }
public class M4bCcEnum : UdonSharpBehaviour {
    public CcSuit suit;
    public string s;
    void Start(){ s = ""x""; s += suit; }
}", "M4bCcEnum");
        // The B67 value→name helper is synthesized and called (name VALUES live in the data table,
        // pinned on the real VM) — pre-fix this path Concat'd the raw underlying number.
        Assert.Contains("__enumstr_TCcSuit", uasm);
        Assert.Contains("SystemString.__Concat__SystemObject_SystemObject__SystemString", uasm);
    }

    [Fact]
    public void CompoundConcat_ClassOperand_DispatchesToString()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CcB { public int v; public override string ToString(){ return ""b"" + v; } }
public class CcD : CcB { public override string ToString(){ return ""d"" + v; } }
public class M4bCcCls : UdonSharpBehaviour {
    public int seed;
    public string s;
    void Start(){ CcB c = seed > 0 ? (CcB)new CcD() : new CcB(); s = ""x""; s += c; }
}", "M4bCcCls");
        Assert.Contains("__typeobj_CcB", uasm);
        Assert.Contains("__typeobj_CcD", uasm);
        Assert.Contains("SystemString.__Concat__SystemObject_SystemObject__SystemString", uasm);
    }
}
