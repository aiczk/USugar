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

    // ── HirToLir tests ──

    [Fact]
    public void HirToLir_SimpleFunction_ProducesBlocks()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("_start", "_start");
        var slot = func.NewSlot("SystemInt32", SlotClass.Frame);
        func.Body.Stmts.Add(new HAssign(slot, new HConst(42, "SystemInt32")));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        Assert.Single(lmod.Functions);
        var lfunc = lmod.Functions[0];
        Assert.True(lfunc.Blocks.Count >= 1);
        Assert.IsType<LReturn>(lfunc.Entry.Term);
    }

    [Fact]
    public void HirToLir_IfElse_ProducesBranch()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var condSlot = func.NewSlot("SystemBoolean", SlotClass.Frame);
        var resultSlot = func.NewSlot("SystemInt32", SlotClass.Frame);

        var thenBlock = new HBlock();
        thenBlock.Stmts.Add(new HAssign(resultSlot, new HConst(1, "SystemInt32")));
        var elseBlock = new HBlock();
        elseBlock.Stmts.Add(new HAssign(resultSlot, new HConst(0, "SystemInt32")));

        func.Body.Stmts.Add(new HIf(new HSlotRef(condSlot, "SystemBoolean"), thenBlock, elseBlock));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Entry block should end with a branch
        Assert.IsType<LBranch>(lfunc.Entry.Term);
        // Should have entry + then + else + merge blocks
        Assert.True(lfunc.Blocks.Count >= 4);
    }

    [Fact]
    public void HirToLir_WhileLoop_ProducesHeaderAndBackEdge()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var condSlot = func.NewSlot("SystemBoolean", SlotClass.Frame);

        var body = new HBlock();
        body.Stmts.Add(new HExprStmt(new HSlotRef(condSlot, "SystemBoolean"))); // no-op

        func.Body.Stmts.Add(new HWhile(new HSlotRef(condSlot, "SystemBoolean"), body));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Entry jumps to header; header branches; body jumps back to header
        Assert.IsType<LJump>(lfunc.Entry.Term);
        // header block should have a branch terminator
        var headerBlockId = ((LJump)lfunc.Entry.Term).TargetBlockId;
        LBlock headerBlock = null;
        foreach (var b in lfunc.Blocks)
            if (b.Id == headerBlockId) { headerBlock = b; break; }
        Assert.NotNull(headerBlock);
        Assert.IsType<LBranch>(headerBlock.Term);
    }

    [Fact]
    public void HirToLir_ForLoop_ProducesCorrectStructure()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var i = func.NewSlot("SystemInt32", SlotClass.Frame);

        var init = new HBlock();
        init.Stmts.Add(new HAssign(i, new HConst(0, "SystemInt32")));

        var cond = new HExternCall(
            "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<HExpr> { new HSlotRef(i, "SystemInt32"), new HConst(10, "SystemInt32") },
            "SystemBoolean");

        var update = new HBlock();
        update.Stmts.Add(new HAssign(i, new HExternCall(
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { new HSlotRef(i, "SystemInt32"), new HConst(1, "SystemInt32") },
            "SystemInt32")));

        var body = new HBlock();

        func.Body.Stmts.Add(new HFor(init, cond, update, body));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Should have: entry (with init + jump) + header + body + continue + exit blocks
        Assert.True(lfunc.Blocks.Count >= 5);
    }

    [Fact]
    public void HirToLir_BreakContinue_JumpToCorrectBlocks()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var condSlot = func.NewSlot("SystemBoolean", SlotClass.Frame);

        var body = new HBlock();
        body.Stmts.Add(new HIf(
            new HSlotRef(condSlot, "SystemBoolean"),
            new HBlock(new List<HStmt> { new HBreak() }),
            new HBlock(new List<HStmt> { new HContinue() })));

        func.Body.Stmts.Add(new HWhile(new HSlotRef(condSlot, "SystemBoolean"), body));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // All blocks should have terminators
        foreach (var block in lfunc.Blocks)
            Assert.NotNull(block.Term);
    }

    [Fact]
    public void HirToLir_GotoLabel_ProducesJump()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var slot = func.NewSlot("SystemInt32", SlotClass.Frame);

        func.Body.Stmts.Add(new HGoto("target"));
        func.Body.Stmts.Add(new HAssign(slot, new HConst(1, "SystemInt32"))); // unreachable
        func.Body.Stmts.Add(new HLabelStmt("target"));
        func.Body.Stmts.Add(new HAssign(slot, new HConst(2, "SystemInt32")));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Entry should have a jump (goto target)
        Assert.IsType<LJump>(lfunc.Entry.Term);
        // No instructions in entry after the goto (unreachable assign skipped)
        Assert.Empty(lfunc.Entry.Insts);
    }

    [Fact]
    public void HirToLir_ExternCall_AllocatesScratchForResult()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var slot = func.NewSlot("SystemInt32", SlotClass.Frame);

        func.Body.Stmts.Add(new HAssign(slot,
            new HExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new List<HExpr> { new HConst(1, "SystemInt32"), new HConst(2, "SystemInt32") },
                "SystemInt32")));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Original slot + scratch for extern result
        Assert.True(lfunc.Slots.Count >= 2);
        Assert.Equal(SlotClass.Scratch, lfunc.Slots[lfunc.Slots.Count - 1].Class);
    }

    [Fact]
    public void HirToLir_Select_ProducesBranchAndMerge()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var condSlot = func.NewSlot("SystemBoolean", SlotClass.Frame);
        var resultSlot = func.NewSlot("SystemInt32", SlotClass.Frame);

        func.Body.Stmts.Add(new HAssign(resultSlot,
            new HSelect(
                new HSlotRef(condSlot, "SystemBoolean"),
                new HConst(1, "SystemInt32"),
                new HConst(0, "SystemInt32"),
                "SystemInt32")));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Entry should branch for the select
        Assert.IsType<LBranch>(lfunc.Entry.Term);
        // true block + false block + merge block
        Assert.True(lfunc.Blocks.Count >= 4);
    }

    [Fact]
    public void HirToLir_DoWhile_BodyBeforeCondition()
    {
        var hmod = new HModule();
        var func = hmod.AddFunction("test");
        var condSlot = func.NewSlot("SystemBoolean", SlotClass.Frame);

        var body = new HBlock();
        body.Stmts.Add(new HExprStmt(new HSlotRef(condSlot, "SystemBoolean")));

        func.Body.Stmts.Add(new HWhile(new HSlotRef(condSlot, "SystemBoolean"), body, isDoWhile: true));
        func.Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        var lfunc = lmod.Functions[0];

        // Entry jumps to body block (not header)
        Assert.IsType<LJump>(lfunc.Entry.Term);
        var bodyBlockId = ((LJump)lfunc.Entry.Term).TargetBlockId;
        // Body block should jump to header (condition check)
        LBlock bodyBlock = null;
        foreach (var b in lfunc.Blocks)
            if (b.Id == bodyBlockId) { bodyBlock = b; break; }
        Assert.NotNull(bodyBlock);
        Assert.IsType<LJump>(bodyBlock.Term);

        // Header should have branch (condition)
        var headerBlockId = ((LJump)bodyBlock.Term).TargetBlockId;
        LBlock headerBlock = null;
        foreach (var b in lfunc.Blocks)
            if (b.Id == headerBlockId) { headerBlock = b; break; }
        Assert.NotNull(headerBlock);
        Assert.IsType<LBranch>(headerBlock.Term);
    }

    [Fact]
    public void HirToLir_CopiesModuleFields()
    {
        var hmod = new HModule { ClassName = "MyClass" };
        hmod.Fields.Add(new FieldDecl("myField", "SystemInt32"));
        hmod.AddFunction("test").Body.Stmts.Add(new HReturn());

        var lmod = HirToLir.Lower(hmod);
        Assert.Equal("MyClass", lmod.ClassName);
        Assert.Single(lmod.Fields);
        Assert.Equal("myField", lmod.Fields[0].Name);
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

    // ── HirVerifier tests ──

    [Fact]
    public void Verifier_ValidFunction_Passes()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        var func = builder.BeginFunction("test");
        func.ReturnType = "SystemInt32";

        var slot = builder.AllocFrame("SystemInt32");
        builder.EmitAssign(slot, builder.Const(42, "SystemInt32"));
        builder.EmitReturn(builder.SlotRef(slot));

        HirVerifier.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_UndeclaredSlot_Throws()
    {
        var module = new HModule();
        var func = module.AddFunction("test");
        func.Body.Stmts.Add(new HAssign(99, new HConst(0, "SystemInt32")));

        Assert.Throws<VerificationException>(() => HirVerifier.Verify(module));
    }

    [Fact]
    public void Verifier_TypeMismatch_Throws()
    {
        var module = new HModule();
        var builder = new HirBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame("SystemInt32");
        // Assign a boolean to an int slot
        builder.EmitAssign(slot, builder.Const(true, "SystemBoolean"));

        Assert.Throws<VerificationException>(() => HirVerifier.Verify(module));
    }

    [Fact]
    public void Verifier_IfCondNotBoolean_Throws()
    {
        var module = new HModule();
        var func = module.AddFunction("test");
        func.NewSlot("SystemInt32", SlotClass.Frame);
        // if (intValue) — condition must be boolean
        func.Body.Stmts.Add(new HIf(
            new HSlotRef(0, "SystemInt32"),
            new HBlock(),
            new HBlock()));

        Assert.Throws<VerificationException>(() => HirVerifier.Verify(module));
    }

    [Fact]
    public void Verifier_BreakOutsideLoop_Throws()
    {
        var module = new HModule();
        var func = module.AddFunction("test");
        func.Body.Stmts.Add(new HBreak());

        Assert.Throws<VerificationException>(() => HirVerifier.Verify(module));
    }

    [Fact]
    public void Verifier_BreakInsideLoop_Passes()
    {
        var module = new HModule();
        var func = module.AddFunction("test");
        func.NewSlot("SystemBoolean", SlotClass.Scratch);

        var body = new HBlock();
        body.Stmts.Add(new HBreak());
        func.Body.Stmts.Add(new HWhile(new HSlotRef(0, "SystemBoolean"), body));

        HirVerifier.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_ReturnTypeMismatch_Throws()
    {
        var module = new HModule();
        var func = module.AddFunction("test");
        func.ReturnType = "SystemInt32";
        func.Body.Stmts.Add(new HReturn(new HConst("hello", "SystemString")));

        Assert.Throws<VerificationException>(() => HirVerifier.Verify(module));
    }
}
