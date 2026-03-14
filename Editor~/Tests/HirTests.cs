using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class HirTests
{
    [Fact]
    public void HModule_Dump_ContainsFunctionAndSlots()
    {
        var module = new HModule { ClassName = "TestClass" };
        var func = module.AddFunction("_start", "_start");
        func.ReturnType = null; // void

        var paramSlot = func.NewSlot("SystemInt32", SlotClass.Pinned, "x__param");
        var localSlot = func.NewSlot("SystemInt32", SlotClass.Frame);
        var tempSlot = func.NewSlot("SystemBoolean", SlotClass.Scratch);

        func.Body.Stmts.Add(new HAssign(localSlot,
            new HExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new List<HExpr> { new HSlotRef(paramSlot, "SystemInt32"), new HConst(1, "SystemInt32") },
                "SystemInt32")));
        func.Body.Stmts.Add(new HReturn());

        var dump = module.Dump();
        Assert.Contains("module TestClass", dump);
        Assert.Contains("function _start", dump);
        Assert.Contains("slot0:SystemInt32[Pinned]", dump);
        Assert.Contains("slot1:SystemInt32[Frame]", dump);
        Assert.Contains("slot2:SystemBoolean[Scratch]", dump);
        Assert.Contains("op_Addition", dump);
        Assert.Contains("return", dump);
    }

    [Fact]
    public void HIf_Dump_ShowsStructuredControl()
    {
        var func = new HFunction("test");
        var cond = new HSlotRef(0, "SystemBoolean");

        var thenBlock = new HBlock();
        thenBlock.Stmts.Add(new HAssign(1, new HConst(42, "SystemInt32")));

        var elseBlock = new HBlock();
        elseBlock.Stmts.Add(new HAssign(1, new HConst(0, "SystemInt32")));

        func.Body.Stmts.Add(new HIf(cond, thenBlock, elseBlock));

        var dump = func.Dump();
        Assert.Contains("if (", dump);
        Assert.Contains("else:", dump);
        Assert.Contains("const(42)", dump);
        Assert.Contains("const(0)", dump);
    }

    [Fact]
    public void HFor_Dump_ShowsLoop()
    {
        var func = new HFunction("test");

        var init = new HBlock();
        init.Stmts.Add(new HAssign(0, new HConst(0, "SystemInt32")));

        var cond = new HExternCall(
            "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<HExpr> { new HSlotRef(0, "SystemInt32"), new HConst(10, "SystemInt32") },
            "SystemBoolean");

        var update = new HBlock();
        update.Stmts.Add(new HAssign(0,
            new HExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new List<HExpr> { new HSlotRef(0, "SystemInt32"), new HConst(1, "SystemInt32") },
                "SystemInt32")));

        var body = new HBlock();
        body.Stmts.Add(new HExprStmt(
            new HExternCall("UnityEngineDebug.__Log__SystemObject__SystemVoid",
                new List<HExpr> { new HSlotRef(0, "SystemInt32") }, "SystemVoid")));

        func.Body.Stmts.Add(new HFor(init, cond, update, body));

        var dump = func.Dump();
        Assert.Contains("for (", dump);
        Assert.Contains("op_LessThan", dump);
    }

    [Fact]
    public void HWhile_DoWhile_DumpsCorrectly()
    {
        var func = new HFunction("test");
        var cond = new HSlotRef(0, "SystemBoolean");
        var body = new HBlock();
        body.Stmts.Add(new HBreak());

        func.Body.Stmts.Add(new HWhile(cond, body, isDoWhile: true));

        var dump = func.Dump();
        Assert.Contains("do-while", dump);
        Assert.Contains("break", dump);
    }

    [Fact]
    public void SlotDecl_ToString_ShowsClassAndFixedName()
    {
        var pinned = new SlotDecl(0, "SystemInt32", SlotClass.Pinned, "items");
        Assert.Contains("[Pinned]", pinned.ToString());
        Assert.Contains("\"items\"", pinned.ToString());

        var scratch = new SlotDecl(1, "SystemBoolean", SlotClass.Scratch);
        Assert.Contains("[Scratch]", scratch.ToString());
        Assert.DoesNotContain("\"", scratch.ToString());
    }

    // ── HirBuilder tests ──

    [Fact]
    public void HirBuilder_EmitAssign_AddsToCurrentBlock()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        var func = builder.BeginFunction("test");
        var slot = builder.AllocFrame("SystemInt32");
        builder.EmitAssign(slot, builder.Const(42, "SystemInt32"));
        builder.EmitReturn();

        Assert.Equal(2, func.Body.Stmts.Count);
        Assert.IsType<HAssign>(func.Body.Stmts[0]);
        Assert.IsType<HReturn>(func.Body.Stmts[1]);
    }

    [Fact]
    public void HirBuilder_EmitIf_CreatesStructuredControl()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        var func = builder.BeginFunction("test");
        var condSlot = builder.AllocScratch("SystemBoolean");

        builder.EmitIf(
            builder.SlotRef(condSlot),
            b => b.EmitAssign(condSlot, b.Const(true, "SystemBoolean")),
            b => b.EmitAssign(condSlot, b.Const(false, "SystemBoolean"))
        );

        Assert.Single(func.Body.Stmts);
        var ifStmt = Assert.IsType<HIf>(func.Body.Stmts[0]);
        Assert.Single(ifStmt.Then.Stmts);
        Assert.Single(ifStmt.Else.Stmts);
    }

    [Fact]
    public void HirBuilder_EmitFor_CreatesForLoop()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        var func = builder.BeginFunction("test");
        var i = builder.AllocFrame("SystemInt32");

        var cond = builder.ExternCall(
            "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<HExpr> { builder.SlotRef(i), builder.Const(10, "SystemInt32") },
            "SystemBoolean");

        builder.EmitFor(
            b => b.EmitAssign(i, b.Const(0, "SystemInt32")),
            cond,
            b => b.EmitAssign(i, b.ExternCall(
                "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new List<HExpr> { b.SlotRef(i), b.Const(1, "SystemInt32") },
                "SystemInt32")),
            b => b.EmitExprStmt(b.ExternCall(
                "UnityEngineDebug.__Log__SystemObject__SystemVoid",
                new List<HExpr> { b.SlotRef(i) }, "SystemVoid"))
        );

        Assert.Single(func.Body.Stmts);
        var forStmt = Assert.IsType<HFor>(func.Body.Stmts[0]);
        Assert.Single(forStmt.Init.Stmts);
        Assert.Single(forStmt.Update.Stmts);
        Assert.Single(forStmt.Body.Stmts);
    }

    [Fact]
    public void HirBuilder_ConstDedup_ReturnsSameInstance()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        builder.BeginFunction("test");

        var c1 = builder.Const(42, "SystemInt32");
        var c2 = builder.Const(42, "SystemInt32");
        var c3 = builder.Const(43, "SystemInt32");

        Assert.Same(c1, c2);
        Assert.NotSame(c1, c3);
    }

    [Fact]
    public void HirBuilder_SlotRef_ReturnsCorrectType()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        builder.BeginFunction("test");

        var slot = builder.AllocPinned("SystemString", "myField");
        var slotRef = builder.SlotRef(slot);

        Assert.Equal("SystemString", slotRef.Type);
        Assert.Equal(slot, slotRef.SlotId);
    }
}
