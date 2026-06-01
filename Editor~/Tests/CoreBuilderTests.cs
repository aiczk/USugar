using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 3 foundation: CoreBuilder is the Core-emitting builder that handlers will target,
/// a 1:1 mirror of HirBuilder. These tests prove the same sequence of builder calls produces
/// the same structure (compared via HStmt.Dump after bridging Core->HIR) and the same constant
/// deduplication. CoreBuilder is additive here — handlers still use HirBuilder, so the oracle
/// stays byte-identical.
/// </summary>
public class CoreBuilderTests
{
    static HFunction CoreToHir(CFunction cf)
    {
        var hf = new HFunction(cf.Name, cf.ExportName) { ReturnType = cf.ReturnType };
        foreach (var s in cf.Slots) hf.Slots.Add(new SlotDecl(s.Id, s.Type, s.Class, s.FixedName));
        var body = (HBlock)CNodeBridge.ToHStmt(cf.Body);
        foreach (var st in body.Stmts) hf.Body.Stmts.Add(st);
        return hf;
    }

    [Fact]
    public void CoreBuilder_BuildsSameStructureAsHirBuilder()
    {
        // HirBuilder path
        var hb = new HirBuilder(new HModule());
        var hf = hb.BeginFunction("f", "_f");
        var hs = hb.AllocFrame("SystemInt32");
        hb.EmitAssign(hs, hb.Const(1, "SystemInt32"));
        hb.EmitIf(hb.SlotRef(hs),
            tb => tb.EmitStoreField("x", tb.Const(2, "SystemInt32")),
            eb => eb.EmitReturn());
        hb.EmitStoreField("y", hb.ExternCall("Op.__add__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { hb.SlotRef(hs), hb.Const(3, "SystemInt32") }, "SystemInt32"));
        hb.EmitWhile(hb.LoadField("flag", "SystemBoolean"), wb => wb.EmitBreak());

        // CoreBuilder path — identical call sequence, parallel API
        var cb = new CoreBuilder(new CModule());
        var cf = cb.BeginFunction("f", "_f");
        var cs = cb.AllocFrame("SystemInt32");
        cb.EmitAssign(cs, cb.Const(1, "SystemInt32"));
        cb.EmitIf(cb.SlotRef(cs),
            tb => tb.EmitStoreField("x", tb.Const(2, "SystemInt32")),
            eb => eb.EmitReturn());
        cb.EmitStoreField("y", cb.ExternCall("Op.__add__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { cb.SlotRef(cs), cb.Const(3, "SystemInt32") }, "SystemInt32"));
        cb.EmitWhile(cb.LoadField("flag", "SystemBoolean"), wb => wb.EmitBreak());

        Assert.Equal(hf.Dump(), CoreToHir(cf).Dump());
    }

    [Fact]
    public void CoreBuilder_DeduplicatesConstants()
    {
        var cb = new CoreBuilder(new CModule());
        cb.BeginFunction("f");
        var a = cb.Const(5, "SystemInt32");
        var b = cb.Const(5, "SystemInt32");
        Assert.Same(a, b);
    }

    [Fact]
    public void CoreBuilder_Output_FlattensIdenticallyToHirBuilderPlusHirToLir()
    {
        // End-to-end: CoreBuilder -> CoreFlatten must equal HirBuilder -> HirToLir.
        var hb = new HirBuilder(new HModule());
        var hf = hb.BeginFunction("g", "_g");
        var hsi = hb.AllocFrame("SystemInt32");
        hb.EmitFor(
            ib => ib.EmitAssign(hsi, ib.Const(0, "SystemInt32")),
            hb.LoadField("cond", "SystemBoolean"),
            ub => ub.EmitAssign(hsi, ub.Const(1, "SystemInt32")),
            bb => bb.EmitStoreField("acc", bb.SlotRef(hsi)));
        var lirA = HirToLir.Lower(hb.Module).Functions[0];

        var cb = new CoreBuilder(new CModule());
        var cf = cb.BeginFunction("g", "_g");
        var csi = cb.AllocFrame("SystemInt32");
        cb.EmitFor(
            ib => ib.EmitAssign(csi, ib.Const(0, "SystemInt32")),
            cb.LoadField("cond", "SystemBoolean"),
            ub => ub.EmitAssign(csi, ub.Const(1, "SystemInt32")),
            bb => bb.EmitStoreField("acc", bb.SlotRef(csi)));
        CoreFlatten.Lower(cf);
        FlatVerify.Verify(cf);
        var lirB = CoreFlattenBridge.ToLFunction(cf);

        Assert.Equal(lirA.Dump(), lirB.Dump());
    }
}
