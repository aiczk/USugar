using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 1 of the Core IR migration: proves the unified CValue vocabulary losslessly
/// represents every HIR leaf expression and every LIR operand (field-for-field round-trip).
/// This is the empirical sufficiency proof that lets handlers later emit CValue directly.
/// CValue is additive here — the live HIR→LIR→UASM pipeline is unchanged, so the snapshot
/// oracle stays byte-identical.
/// </summary>
public class CValueTests
{
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
    public void FromHExpr_CompoundExpression_Throws()
    {
        var call = new HExternCall("Sig", new List<HExpr>(), "SystemVoid");
        Assert.ThrowsAny<System.Exception>(() => CValueBridge.FromHExpr(call));
    }

    [Fact]
    public void ToLOperand_LoadFieldRef_Throws_NoLirOperandForm()
    {
        var load = new CFieldRef("x", "SystemInt32", CFieldMode.Load);
        Assert.ThrowsAny<System.Exception>(() => CValueBridge.ToLOperand(load));
    }
}
