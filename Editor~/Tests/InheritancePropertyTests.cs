using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Pins for virtual auto-property override dispatch (the round-5 [N1] codegen bug, which predates
/// fcd-stage1): a member read/written through a BASE-typed reference binds the BASE virtual symbol,
/// whose local registration is the never-exported base-instance copy (`__N_get_P`), so the cross
/// dispatch SendCustomEvent'd a name no program exports and read a stale return var (VM-verified:
/// base-typed read of an overridden auto-property returned default instead of the stored value).
/// The fix routes non-exported local registrations of behaviour members through the planner's
/// override-chain-ROOT layout, which every program in the class family exports.
/// </summary>
public class InheritancePropertyTests
{
    const string OverrideSrc = @"
public class N1Base : UdonSharp.UdonSharpBehaviour { public virtual int P { get; set; } }
public class N1Drv : N1Base {
    public int sum;
    public override int P { get; set; }
    void Start() { N1Base b = this; P = 7; sum = b.P * 10 + P; }
}";

    [Fact]
    public void VirtualAutoPropOverride_BaseTypedRead_DispatchesChainRootExport()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(OverrideSrc, "N1Drv");

        // The override accessor is exported under the chain-root (base layout) name…
        Assert.Contains(".export get_P", uasm);
        // …and the base-typed cross dispatch targets exactly that name and its layout return var
        // (pre-fix: the SendCustomEvent name was the base-instance copy's internal VarPrefix and
        // the GetProgramVariable read its never-written `__3_get_P__ret`).
        var stringConsts = consts.Where(c => c.UdonType == "SystemString").Select(c => (string)c.Value).ToArray();
        Assert.Contains("get_P", stringConsts);
        Assert.Contains("__0_get_P__ret", stringConsts);
        Assert.DoesNotContain(stringConsts, s => s != null && s.EndsWith("_get_P__ret") && s != "__0_get_P__ret");
    }

    [Fact]
    public void VirtualAutoPropNoOverride_BaseTypedRead_DispatchesInheritedExport()
    {
        // Control: WITHOUT an override the inherited accessors are exported under the base layout
        // names and the cross dispatch already resolved — must stay byte-stable.
        var src = @"
public class N1GBase : UdonSharp.UdonSharpBehaviour { public virtual int Q { get; set; } }
public class N1GDrv : N1GBase {
    public int sum;
    void Start() { N1GBase b = this; Q = 7; sum = b.Q * 10 + Q; }
}";
        var (uasm, consts) = TestHelper.CompileWithConsts(src, "N1GDrv");
        Assert.Contains(".export get_Q", uasm);
        var stringConsts = consts.Where(c => c.UdonType == "SystemString").Select(c => (string)c.Value).ToArray();
        Assert.Contains("get_Q", stringConsts);
        Assert.Contains("__0_get_Q__ret", stringConsts);
    }

    [Fact]
    public void VirtualAutoPropOverride_BaseTypedWrite_DispatchesChainRootExport()
    {
        // The write mirror: `b.P = 9` must SetProgramVariable the chain-root setter's layout param
        // and SendCustomEvent the chain-root setter export.
        var src = @"
public class N1WBase : UdonSharp.UdonSharpBehaviour { public virtual int P { get; set; } }
public class N1WDrv : N1WBase {
    public int sum;
    public override int P { get; set; }
    void Start() { N1WBase b = this; b.P = 9; sum = P; }
}";
        var (uasm, consts) = TestHelper.CompileWithConsts(src, "N1WDrv");
        Assert.Contains(".export __0_set_P", uasm);
        var stringConsts = consts.Where(c => c.UdonType == "SystemString").Select(c => (string)c.Value).ToArray();
        Assert.Contains("__0_set_P", stringConsts);
        Assert.Contains("__0_value__param", stringConsts);
    }

    // ── round-6 [J2]/[J3]: receiver identity on same-family instance calls ──

    [Fact]
    public void VirtualMethod_BaseTypedReceiver_DispatchesCrossProgram()
    {
        // [J2] `B b = this; b.Get();` used to direct-JUMP the never-exported base-instance copy
        // (base body ran: VM 3 vs CLR 5; abstract flavor read a stale 0). Post-fix the call is a
        // receiver-read SendCustomEvent dispatch by the chain-root export name, so the receiver's
        // own program runs its most-derived override.
        var src = @"
public class J2Base : UdonSharp.UdonSharpBehaviour { public virtual int Get() { return 3; } }
public class J2Drv : J2Base {
    public int sum;
    public override int Get() { return 5; }
    void Start() { J2Base b = this; sum = b.Get(); }
}";
        var (uasm, consts) = TestHelper.CompileWithConsts(src, "J2Drv");
        Assert.Contains("__SendCustomEvent__SystemString__SystemVoid", uasm);
        var stringConsts = consts.Where(c => c.UdonType == "SystemString").Select(c => (string)c.Value).ToArray();
        Assert.Contains("Get", stringConsts);
    }

    [Fact]
    public void SameClassCall_NonThisReceiver_ReadsReceiver()
    {
        // [J3] `other.GetN()` on a same-class field compiled to a direct internal JUMP that never
        // read `other` (a NULL receiver silently self-executed, VM 3 where the CLR throws).
        // Post-fix: cross-program dispatch through the receiver.
        var src = @"
public class J3Cls : UdonSharp.UdonSharpBehaviour {
    public J3Cls other;
    public int sum;
    int n;
    public int GetN() { return n; }
    void Start() { n = 3; sum = other.GetN(); }
}";
        var (uasm, consts) = TestHelper.CompileWithConsts(src, "J3Cls");
        Assert.Contains("__SendCustomEvent__SystemString__SystemVoid", uasm);
        var stringConsts = consts.Where(c => c.UdonType == "SystemString").Select(c => (string)c.Value).ToArray();
        Assert.Contains("GetN", stringConsts);
    }

    [Fact]
    public void SameClassCall_NonThisReceiver_PrivateTarget_Throws()
    {
        // [J3] a PRIVATE method has no exported entry point, so the receiver-correct cross dispatch
        // cannot reach it — loud, never a silent self-executing JUMP.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class J3Priv : UdonSharp.UdonSharpBehaviour {
    public J3Priv other;
    public int sum;
    int GetN() { return 3; }
    void Start() { sum = other.GetN(); }
}", "J3Priv"));
        Assert.Contains("non-this receiver", ex.Message);
    }

    // ── round-6 [J1]: `new`-shadowed storage collision ──

    [Fact]
    public void NewShadowedField_Throws()
    {
        // [J1] Base.cb and Derived.cb are distinct CLR storages; the name-keyed heap model would
        // collapse both onto one var (VM 40 vs CLR 2 for the delegate flavor, 55 vs 57 for ints).
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class J1Base : UdonSharp.UdonSharpBehaviour {
    protected int x;
    public void SetBase() { x = 5; }
    public int GetBase() { return x; }
}
public class J1Drv : J1Base {
    public int sum;
    protected new int x;
    void Start() { x = 7; SetBase(); sum = GetBase() * 10 + x; }
}", "J1Drv"));
        Assert.Contains("shadow", ex.Message);
    }

    [Fact]
    public void NewShadowedAutoProp_Throws()
    {
        // [J1] auto-prop flavor — also covers the type-conflicting shadow that used to HALT the VM
        // at runtime (HeapTypeMismatchException) instead of rejecting at compile time.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class J1PBase : UdonSharp.UdonSharpBehaviour { public int P { get; set; } }
public class J1PDrv : J1PBase {
    public int sum;
    new System.Func<int> P { get; set; }
    int M() { return 6; }
    void Start() { P = M; sum = P(); }
}", "J1PDrv"));
        Assert.Contains("shadow", ex.Message);
    }

    [Fact]
    public void NewShadowedManualProperty_Compiles()
    {
        // [J1 precision] MANUAL-accessor properties have no backing storage — the planner already
        // disambiguates their accessor exports (wave-7), so shadowing them stays legal.
        var uasm = TestHelper.CompileToUasm(@"
public class J1MBase : UdonSharp.UdonSharpBehaviour {
    protected int r;
    public int Amount { get { return r + 1; } }
}
public class J1MDrv : J1MBase {
    public int sum;
    public new int Amount { get { return r + 100; } }
    void Start() { r = 6; sum = Amount + base.Amount; }
}", "J1MDrv");
        Assert.NotNull(uasm);
    }

    // ── round-6 [J4]: per-declaration auto-prop backing ──

    [Fact]
    public void OverrideAutoProp_BaseDeclaration_HasOwnBackingVar()
    {
        // [J4] C# gives an override auto-prop its OWN backing field: `base.P = 5; P = 7;` are two
        // storages (CLR 57; the round-5 name-unification shipped 77). The base declaration's
        // backing is the per-declaration var, used only by its base-instance copy accessors.
        var src = @"
public class J4Base : UdonSharp.UdonSharpBehaviour { public virtual int P { get; set; } }
public class J4Drv : J4Base {
    public int sum;
    public override int P { get; set; }
    void Start() { base.P = 5; P = 7; sum = base.P * 10 + P; }
}";
        var uasm = TestHelper.CompileToUasm(src, "J4Drv");
        Assert.Contains("__basebk_J4Base_P", uasm);   // base declaration's own storage
        Assert.Contains("P: %SystemInt32", uasm);     // chain-leaf keeps the bare exported name
    }

    // ── round-7 [P1]: this-path virtual accessor dispatch from INHERITED base method bodies ──
    // A property reference inside an inherited base body statically binds the BASE accessor; the
    // this-path lookups must resolve the chain-leaf override (ResolveDispatchProperty) and the
    // base-instance-copy collector must register accessors for actual `base.` receivers ONLY.
    // Pre-fix the copy ran the base accessor body (manual/indexer, VM 3 vs CLR 5 — pre-existing
    // v2.x) or read the base declaration's __basebk storage (auto, VM 7 vs CLR 77 — a 917d99c
    // regression).

    /// <summary>Function-entry labels for an accessor, excluding its __dlg_ delegate bridges —
    /// a base-instance COPY would add a second non-bridge `__N_{accessor}:` label.</summary>
    static int CountFunctionLabels(string uasm, string accessor)
        => System.Text.RegularExpressions.Regex.Matches(
            uasm, $@"^\s*(?!__dlg_)\S*{accessor}:\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    [Fact]
    public void InheritedBaseBody_AutoPropRead_BindsLeafStorage()
    {
        // No base.P anywhere → no base-instance copy accessors, so the base declaration's
        // per-declaration __basebk storage is never touched (its declaration stays, dead) and the
        // inherited ReadP dispatches the ONE chain-leaf accessor over the bare exported storage.
        var uasm = TestHelper.CompileToUasm(@"
public class P1RBase : UdonSharp.UdonSharpBehaviour {
    public virtual int P { get; set; }
    public int ReadP() { return P; }
}
public class P1RDrv : P1RBase {
    public int sum;
    public override int P { get; set; }
    void Start() { P = 7; sum = ReadP() * 10 + P; }
}", "P1RDrv");
        Assert.DoesNotContain("PUSH, __basebk", uasm);
        Assert.Contains(".export get_P", uasm);
        Assert.Equal(1, CountFunctionLabels(uasm, "get_P"));
        Assert.Equal(1, CountFunctionLabels(uasm, "set_P"));
    }

    [Fact]
    public void InheritedBaseBody_AutoPropWrite_BindsLeafStorage()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class P1WBase : UdonSharp.UdonSharpBehaviour {
    public virtual int P { get; set; }
    public void SetP(int v) { P = v; }
}
public class P1WDrv : P1WBase {
    public int sum;
    public override int P { get; set; }
    void Start() { SetP(7); sum = P; }
}", "P1WDrv");
        Assert.DoesNotContain("PUSH, __basebk", uasm);
        Assert.Equal(1, CountFunctionLabels(uasm, "set_P"));
    }

    [Fact]
    public void InheritedBaseBody_ManualPropOverride_SingleLeafGetter()
    {
        // The overridden base getter must NOT be emitted as a base-instance copy (it would run the
        // BASE accessor body); exactly one get_P function remains — the exported leaf override.
        var uasm = TestHelper.CompileToUasm(@"
public class P1MBase : UdonSharp.UdonSharpBehaviour {
    public virtual int P { get { return 3; } }
    public int ReadP() { return P; }
}
public class P1MDrv : P1MBase {
    public int sum;
    public override int P { get { return 5; } }
    void Start() { sum = ReadP(); }
}", "P1MDrv");
        Assert.Equal(1, CountFunctionLabels(uasm, "get_P"));
    }

    [Fact]
    public void InheritedBaseBody_VirtualIndexerOverride_SingleLeafGetter()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class P1IBase : UdonSharp.UdonSharpBehaviour {
    public virtual int this[int i] { get { return 3; } }
    public int ReadAt() { return this[0]; }
}
public class P1IDrv : P1IBase {
    public int sum;
    public override int this[int i] { get { return 5; } }
    void Start() { sum = ReadAt(); }
}", "P1IDrv");
        Assert.Equal(1, CountFunctionLabels(uasm, "get_Item"));
    }

    [Fact]
    public void BaseDotP_FromInheritedBaseBody_KeepsBaseStorage()
    {
        // base.P inside a BASE body (the mid-level override's Mix) keeps binding the ROOT
        // declaration's per-declaration storage — the collector still registers actual `base.`
        // receivers (917d99c semantics; real-VM value 57 pinned in the round-7 harness probes).
        var uasm = TestHelper.CompileToUasm(@"
public class P1BBase : UdonSharp.UdonSharpBehaviour { public virtual int P { get; set; } }
public class P1BMid : P1BBase {
    public override int P { get; set; }
    public int Mix() { base.P = 5; P = 7; return base.P * 10 + P; }
}
public class P1BDrv : P1BMid {
    public int sum;
    void Start() { sum = Mix(); }
}", "P1BDrv");
        Assert.Contains("__basebk_P1BBase_P", uasm);
        Assert.Contains("P: %SystemInt32", uasm);
    }

    // ── round-6 [G4]: explicit interface implementation auto-property ──

    [Fact]
    public void ExplicitInterfaceAutoProp_ProperDiagnostic()
    {
        // The dotted backing name ('IExp.P') used to crash the ASSEMBLER (ParseException — an
        // ICE-class failure). It must be a proper compile-time diagnostic instead.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public interface IExp { int P { get; set; } }
public class ExpCls : UdonSharp.UdonSharpBehaviour, IExp {
    public int sum;
    int IExp.P { get; set; }
    void Start() { IExp h = this; h.P = 4; sum = h.P; }
}", "ExpCls"));
        Assert.Contains("Explicit interface implementation auto-property", ex.Message);
    }
}
