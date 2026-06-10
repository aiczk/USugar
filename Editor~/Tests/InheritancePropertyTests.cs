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
}
