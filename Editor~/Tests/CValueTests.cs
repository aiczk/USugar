using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 1 of the Core IR migration: proves the unified CValue vocabulary losslessly
/// represents every HIR expression and every LIR operand/call (field-for-field round-trip).
/// This is the empirical sufficiency proof that lets handlers later emit CValue directly.
/// CValue is additive here — the live HIR→LIR→UASM pipeline is unchanged, so the snapshot
/// oracle stays byte-identical.
/// </summary>
public class CValueTests
{
    // ── leaves ──

    [Fact]
    public void HConst_RoundTrip_PreservesFields()
    {
        var o = new HConst(42, "SystemInt32");
        var r = (HConst)CValueBridge.ToHExpr(CValueBridge.FromHExpr(o));
        Assert.Equal(o.Value, r.Value);
        Assert.Equal(o.Type, r.Type);
    }

    [Fact]
    public void HSlotRef_RoundTrip_PreservesFields()
    {
        var o = new HSlotRef(7, "SystemSingle");
        var r = (HSlotRef)CValueBridge.ToHExpr(CValueBridge.FromHExpr(o));
        Assert.Equal(o.SlotId, r.SlotId);
        Assert.Equal(o.Type, r.Type);
    }

    [Fact]
    public void HFuncRef_RoundTrip_PreservesFields()
    {
        var o = new HFuncRef("MyMethod");
        var r = (HFuncRef)CValueBridge.ToHExpr(CValueBridge.FromHExpr(o));
        Assert.Equal(o.FuncName, r.FuncName);
        Assert.Equal(o.Type, r.Type);
    }

    [Fact]
    public void HFieldAddr_RoundTrip_PreservesFieldsAndAddrMode()
    {
        var o = new HFieldAddr("score", "SystemInt32");
        var c = (CFieldRef)CValueBridge.FromHExpr(o);
        Assert.Equal(CFieldMode.Addr, c.Mode);
        var r = (HFieldAddr)CValueBridge.ToHExpr(c);
        Assert.Equal(o.FieldName, r.FieldName);
        Assert.Equal(o.Type, r.Type);
    }

    [Fact]
    public void HLoadField_RoundTrip_PreservesFieldsAndLoadMode()
    {
        var o = new HLoadField("score", "SystemInt32");
        var c = (CFieldRef)CValueBridge.FromHExpr(o);
        Assert.Equal(CFieldMode.Load, c.Mode);
        var r = (HLoadField)CValueBridge.ToHExpr(c);
        Assert.Equal(o.FieldName, r.FieldName);
        Assert.Equal(o.Type, r.Type);
    }

    [Fact]
    public void LOperands_RoundTrip_PreservesFields()
    {
        var slot = new LSlotRef(3, "SystemInt32");
        var cst = new LConst("hi", "SystemString");
        var fld = new LFieldRef("target", "VRCUdonCommonInterfacesIUdonEventReceiver");
        var fn = new LFuncRef("Handler");

        var slotR = (LSlotRef)CValueBridge.ToLOperand(CValueBridge.FromLOperand(slot));
        Assert.Equal(slot.SlotId, slotR.SlotId);
        Assert.Equal(slot.Type, slotR.Type);

        var cstR = (LConst)CValueBridge.ToLOperand(CValueBridge.FromLOperand(cst));
        Assert.Equal(cst.Value, cstR.Value);
        Assert.Equal(cst.Type, cstR.Type);

        var fldR = (LFieldRef)CValueBridge.ToLOperand(CValueBridge.FromLOperand(fld));
        Assert.Equal(fld.FieldName, fldR.FieldName);
        Assert.Equal(fld.Type, fldR.Type);

        var fnR = (LFuncRef)CValueBridge.ToLOperand(CValueBridge.FromLOperand(fn));
        Assert.Equal(fn.FuncName, fnR.FuncName);
        Assert.Equal(fn.Type, fnR.Type);
    }

    [Fact]
    public void HirAndLir_SameLeaf_MapToEquivalentCValue()
    {
        var fromHir = CValueBridge.FromHExpr(new HSlotRef(5, "SystemInt32"));
        var fromLir = CValueBridge.FromLOperand(new LSlotRef(5, "SystemInt32"));
        Assert.Equal(fromHir.ToString(), fromLir.ToString());
    }

    [Fact]
    public void ToLOperand_LoadFieldRef_Throws_NoLirOperandForm()
    {
        var load = new CFieldRef("x", "SystemInt32", CFieldMode.Load);
        Assert.ThrowsAny<System.Exception>(() => CValueBridge.ToLOperand(load));
    }

    // ── value-producing call ops (increment 2) ──

    [Fact]
    public void HExternCall_NestedArgs_RoundTrips()
    {
        var inner = new HExternCall("Inner.__op__SystemInt32__SystemInt32",
            new List<HExpr> { new HConst(1, "SystemInt32") }, "SystemInt32");
        var outer = new HExternCall("Outer.__op__SystemInt32_SystemInt32__SystemBoolean",
            new List<HExpr> { inner, new HSlotRef(2, "SystemInt32") }, "SystemBoolean");
        var r = (HExternCall)CValueBridge.ToHExpr(CValueBridge.FromHExpr(outer));
        Assert.Equal(outer.Sig, r.Sig);
        Assert.Equal(outer.Type, r.Type);
        Assert.Equal(2, r.Args.Count);
        Assert.Equal("Inner.__op__SystemInt32__SystemInt32", ((HExternCall)r.Args[0]).Sig);
        Assert.Equal(2, ((HSlotRef)r.Args[1]).SlotId);
    }

    [Fact]
    public void HInternalCall_RoundTrips()
    {
        var o = new HInternalCall("Square", new List<HExpr> { new HSlotRef(0, "SystemInt32") }, "SystemInt32");
        var r = (HInternalCall)CValueBridge.ToHExpr(CValueBridge.FromHExpr(o));
        Assert.Equal(o.FuncName, r.FuncName);
        Assert.Equal(o.Type, r.Type);
        Assert.Single(r.Args);
    }

    [Fact]
    public void LCallExtern_WithDestSlot_RoundTrips()
    {
        var call = new LCallExtern(5, "Sig.__m__SystemInt32_SystemInt32__SystemInt32",
            new List<LOperand> { new LConst(3, "SystemInt32"), new LSlotRef(1, "SystemInt32") }, "SystemInt32");
        var r = (LCallExtern)CValueBridge.ToLCall(CValueBridge.FromLCall(call));
        Assert.Equal(call.DestSlot, r.DestSlot);
        Assert.Equal(call.Sig, r.Sig);
        Assert.Equal(call.RetType, r.RetType);
        Assert.Equal(2, r.Args.Count);
        Assert.Equal(3, ((LConst)r.Args[0]).Value);
    }

    [Fact]
    public void LCallInternal_VoidNoDest_RoundTrips()
    {
        var call = new LCallInternal(null, "DoThing", new List<LOperand>(), "SystemVoid");
        var r = (LCallInternal)CValueBridge.ToLCall(CValueBridge.FromLCall(call));
        Assert.Null(r.DestSlot);
        Assert.Equal(call.FuncName, r.FuncName);
        Assert.Equal(call.RetType, r.RetType);
    }

    [Fact]
    public void Call_DestSlot_DistinguishesFlatFromTreeRole()
    {
        // flat role (from LIR): DestSlot set
        var fromLir = (CExternCall)CValueBridge.FromLCall(
            new LCallExtern(7, "S", new List<LOperand>(), "SystemInt32"));
        Assert.Equal(7, fromLir.DestSlot);
        // tree role (from HIR): DestSlot null
        var fromHir = (CExternCall)CValueBridge.FromHExpr(
            new HExternCall("S", new List<HExpr>(), "SystemInt32"));
        Assert.Null(fromHir.DestSlot);
    }

    [Fact]
    public void ToLCall_NestedCallArg_Throws_FlatRoleRequiresLeafArgs()
    {
        // A tree-role extern call whose arg is itself a call cannot become a flat LIR call:
        // flat operands must be leaves (that materialization is HirToLir/Flatten's job).
        var tree = new CExternCall("Outer",
            new List<CValue> { new CExternCall("Inner", new List<CValue>(), "SystemInt32") },
            "SystemInt32");
        Assert.ThrowsAny<System.Exception>(() => CValueBridge.ToLCall(tree));
    }

    // ── structured-only Core value nodes: select + cross-behaviour ──

    [Fact]
    public void HSelect_RoundTrips()
    {
        var o = new HSelect(new HSlotRef(0, "SystemBoolean"),
            new HConst(1, "SystemInt32"), new HConst(2, "SystemInt32"), "SystemInt32");
        var c = (CSelect)CValueBridge.FromHExpr(o);
        var r = (HSelect)CValueBridge.ToHExpr(c);
        Assert.Equal(o.Type, r.Type);
        Assert.Equal(0, ((HSlotRef)r.Cond).SlotId);
        Assert.Equal(1, ((HConst)r.TrueVal).Value);
        Assert.Equal(2, ((HConst)r.FalseVal).Value);
    }

    [Fact]
    public void HCrossBehaviourCall_RoundTrips_PreservesParamsAndReturns()
    {
        var o = new HCrossBehaviourCall(
            new HLoadField("enemy", "VRCUdonCommonInterfacesIUdonEventReceiver"),
            "TakeDamage",
            new List<(string, HExpr)> { ("amount", new HConst(5, "SystemInt32")) },
            new List<ReturnSlot> { new ReturnSlot("__ret_0", "SystemInt32") },
            "SystemInt32");
        var c = (CCrossCall)CValueBridge.FromHExpr(o);
        Assert.Single(c.Params);
        Assert.Equal("amount", c.Params[0].ParamName);
        Assert.Single(c.Returns);

        var r = (HCrossBehaviourCall)CValueBridge.ToHExpr(c);
        Assert.Equal(o.EventName, r.EventName);
        Assert.Equal(o.Type, r.Type);
        Assert.Single(r.Params);
        Assert.Equal("amount", r.Params[0].ParamName);
        Assert.Equal(5, ((HConst)r.Params[0].Value).Value);
        Assert.Single(r.Returns);
        Assert.Equal("__ret_0", r.Returns[0].Id);
    }
}
