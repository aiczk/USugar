using System;
using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 2 of the Core IR migration: proves CoreFlatten produces the SAME flat output as the
/// live HirToLir, across the control-flow shapes. Each synthetic HFunction is flattened by both
/// paths and the resulting LFunction.Dump() must be byte-identical (same blocks, instructions,
/// terminators, slot ids — including scratch numbering). This is the structural-equivalence gate
/// before CoreFlatten replaces HirToLir.
/// </summary>
public class CoreFlattenTests
{
    [Fact]
    public void CoreFlatten_MatchesHirToLir_AcrossControlFlow()
    {
        foreach (var (name, hf) in Cases())
        {
            var expected = ViaHirToLir(hf).Dump();
            var actual = ViaCoreFlatten(hf).Dump();
            Assert.True(expected == actual,
                $"CoreFlatten diverged from HirToLir for '{name}':\n=== HirToLir ===\n{expected}\n=== CoreFlatten ===\n{actual}");
        }
    }

    static LFunction ViaHirToLir(HFunction hf)
    {
        var mod = new HModule { ClassName = "T" };
        mod.Functions.Add(hf);
        return HirToLir.Lower(mod).Functions[0];
    }

    static LFunction ViaCoreFlatten(HFunction hf)
    {
        var cf = new CFunction(hf.Name, hf.ExportName) { ReturnType = hf.ReturnType };
        foreach (var p in hf.ParamFieldNames) cf.ParamFieldNames.Add(p);
        foreach (var r in hf.ReturnSlots) cf.ReturnSlots.Add(r);
        foreach (var s in hf.Slots) cf.Slots.Add(new SlotDecl(s.Id, s.Type, s.Class, s.FixedName));
        cf.Body = (CBlock)CNodeBridge.FromHStmt(hf.Body);
        CoreFlatten.Lower(cf);
        return CoreFlattenBridge.ToLFunction(cf);
    }

    static HFunction Build(Action<HFunction> setup)
    {
        var hf = new HFunction("f", "_f");
        setup(hf);
        return hf;
    }

    static IEnumerable<(string, HFunction)> Cases()
    {
        yield return ("simple", Build(hf =>
        {
            hf.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Frame));
            hf.Body.Stmts.Add(new HAssign(0, new HConst(1, "SystemInt32")));
            hf.Body.Stmts.Add(new HStoreField("x", new HSlotRef(0, "SystemInt32")));
            hf.Body.Stmts.Add(new HReturn());
        }));

        yield return ("if_else", Build(hf =>
        {
            hf.Slots.Add(new SlotDecl(0, "SystemBoolean", SlotClass.Frame));
            hf.Body.Stmts.Add(new HIf(new HSlotRef(0, "SystemBoolean"),
                new HBlock(new List<HStmt> { new HStoreField("x", new HConst(1, "SystemInt32")) }),
                new HBlock(new List<HStmt> { new HStoreField("x", new HConst(2, "SystemInt32")) })));
        }));

        yield return ("while_loop", Build(hf =>
        {
            hf.Slots.Add(new SlotDecl(0, "SystemBoolean", SlotClass.Frame));
            hf.Body.Stmts.Add(new HWhile(new HSlotRef(0, "SystemBoolean"),
                new HBlock(new List<HStmt> { new HStoreField("x", new HConst(1, "SystemInt32")), new HBreak() })));
        }));

        yield return ("do_while", Build(hf =>
        {
            hf.Slots.Add(new SlotDecl(0, "SystemBoolean", SlotClass.Frame));
            hf.Body.Stmts.Add(new HWhile(new HSlotRef(0, "SystemBoolean"),
                new HBlock(new List<HStmt> { new HStoreField("x", new HConst(1, "SystemInt32")), new HContinue() }),
                isDoWhile: true));
        }));

        yield return ("for_loop", Build(hf =>
        {
            hf.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Frame));
            hf.Slots.Add(new SlotDecl(1, "SystemBoolean", SlotClass.Frame));
            hf.Body.Stmts.Add(new HFor(
                new HBlock(new List<HStmt> { new HAssign(0, new HConst(0, "SystemInt32")) }),
                new HSlotRef(1, "SystemBoolean"),
                new HBlock(new List<HStmt> { new HAssign(0, new HConst(1, "SystemInt32")) }),
                new HBlock(new List<HStmt> { new HStoreField("acc", new HSlotRef(0, "SystemInt32")) })));
        }));

        yield return ("ternary", Build(hf =>
        {
            hf.Slots.Add(new SlotDecl(0, "SystemBoolean", SlotClass.Frame));
            hf.Body.Stmts.Add(new HStoreField("x", new HSelect(new HSlotRef(0, "SystemBoolean"),
                new HConst(1, "SystemInt32"), new HConst(2, "SystemInt32"), "SystemInt32")));
        }));

        yield return ("extern_nested", Build(hf =>
        {
            hf.Body.Stmts.Add(new HStoreField("x", new HExternCall(
                "Op.__add__SystemInt32_SystemInt32__SystemInt32",
                new List<HExpr>
                {
                    new HExternCall("Op.__neg__SystemInt32__SystemInt32",
                        new List<HExpr> { new HConst(1, "SystemInt32") }, "SystemInt32"),
                    new HConst(2, "SystemInt32"),
                }, "SystemInt32")));
        }));

        yield return ("field_load", Build(hf =>
        {
            hf.Body.Stmts.Add(new HStoreField("y", new HLoadField("x", "SystemInt32")));
        }));

        yield return ("goto_label", Build(hf =>
        {
            hf.Body.Stmts.Add(new HGoto("L"));
            hf.Body.Stmts.Add(new HStoreField("dead", new HConst(0, "SystemInt32")));
            hf.Body.Stmts.Add(new HLabelStmt("L"));
            hf.Body.Stmts.Add(new HStoreField("x", new HConst(1, "SystemInt32")));
        }));

        yield return ("cross_call", Build(hf =>
        {
            hf.Body.Stmts.Add(new HExprStmt(new HCrossBehaviourCall(
                new HLoadField("enemy", "VRCUdonCommonInterfacesIUdonEventReceiver"), "TakeDamage",
                new List<(string, HExpr)> { ("amount", new HConst(5, "SystemInt32")) },
                new List<ReturnSlot>(), "SystemVoid")));
        }));
    }
}
