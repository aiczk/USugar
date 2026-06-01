using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Flat (post-flatten) Core IR optimizer tests: SimplifyCFG, DeadCodeElimination, CopyPropagation
/// and CoalesceSlots over flat CBlocks. Ported from LirOptimizerTests when LIR was absorbed into the
/// unified Core IR. A flat value-producing call is a <see cref="CExprStmt"/> wrapping a
/// <see cref="CExternCall"/> whose DestSlot is set; everything else mirrors the former LIR shape.
/// </summary>
public class CoreFlatOptimizerTests
{
    // ── Helpers ──

    static CModule MakeModule(CFunction func)
    {
        var module = new CModule { ClassName = "Test" };
        module.Functions.Add(func);
        return module;
    }

    static CFunction MakeFunc(string name = "test") => new(name) { Shape = Shape.Flat };

    static CExprStmt Call(int? dest, string sig, List<CValue> args, string retType) =>
        new(new CExternCall(sig, args, retType, dest));

    // ========================================================================
    // ThreadJumps
    // ========================================================================

    [Fact]
    public void SimplifyCFG_ThreadJump_RedirectsToFinal()
    {
        // bb0 → bb1 → bb2 (bb1 is empty jump-only)
        // After full optimization: bb1 removed, bb0 and bb2 merged into single block
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        var bb2 = func.NewBlock();

        bb0.Stmts.Add(new CAssign(0, new CConst(42, "SystemInt32")));
        bb0.Terminator = new CJump(bb1.Id);

        bb1.Terminator = new CJump(bb2.Id); // empty, jump-only

        bb2.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        // Thread + remove empty + merge → single block with the move and CRet
        Assert.Single(func.FlatBlocks);
        var entry = func.FlatBlocks[0];
        Assert.Single(entry.Stmts);
        Assert.IsType<CRet>(entry.Terminator);
    }

    [Fact]
    public void SimplifyCFG_ThreadJump_ChainedEmpty()
    {
        // bb0 → bb1 → bb2 → bb3 (bb1, bb2 are empty jump-only)
        // After full optimization: all empty blocks removed, bb0 merged with bb3
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        var bb2 = func.NewBlock();
        var bb3 = func.NewBlock();

        bb0.Stmts.Add(new CAssign(0, new CConst(1, "SystemInt32")));
        bb0.Terminator = new CJump(bb1.Id);
        bb1.Terminator = new CJump(bb2.Id);
        bb2.Terminator = new CJump(bb3.Id);
        bb3.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        // Thread + remove empty + merge → single block
        Assert.Single(func.FlatBlocks);
        var entry = func.FlatBlocks[0];
        Assert.Single(entry.Stmts);
        Assert.IsType<CRet>(entry.Terminator);
    }

    // ========================================================================
    // MergeBlocks
    // ========================================================================

    [Fact]
    public void SimplifyCFG_MergeBlocks_CombinesInstructions()
    {
        // bb0 (1 inst) → bb1 (1 inst, return), bb1 has only predecessor bb0
        // Expected: single block with both instructions
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        bb0.Stmts.Add(new CAssign(0, new CConst(1, "SystemInt32")));
        bb0.Terminator = new CJump(bb1.Id);

        bb1.Stmts.Add(new CAssign(1, new CConst(2, "SystemInt32")));
        bb1.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        Assert.Single(func.FlatBlocks);
        var merged = func.FlatBlocks[0];
        Assert.Equal(2, merged.Stmts.Count);
        Assert.IsType<CRet>(merged.Terminator);
    }

    [Fact]
    public void SimplifyCFG_MergeBlocks_DoesNotMergeWithMultiplePreds()
    {
        // bb0: branch(cond, bb1, bb2), bb1 has code → bb2, bb2 has code → return
        // bb2 has two predecessors (bb0 and bb1), so it must NOT be merged
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        var bb2 = func.NewBlock();

        bb0.Terminator = new CBranch(new CSlotRef(0, "SystemBoolean"), bb1.Id, bb2.Id);

        bb1.Stmts.Add(new CAssign(1, new CConst(1, "SystemInt32")));
        bb1.Terminator = new CJump(bb2.Id);

        bb2.Stmts.Add(new CAssign(0, new CConst(99, "SystemInt32")));
        bb2.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        // bb2 should still exist as a separate block (two predecessors: bb0, bb1)
        Assert.True(func.FlatBlocks.Count >= 2);
        // The block with CRet should still have the move instruction
        var retBlock = func.FlatBlocks.First(b => b.Terminator is CRet);
        Assert.Contains(retBlock.Stmts, i => i is CAssign m && m.DestSlot == 0);
    }

    // ========================================================================
    // RemoveUnreachable
    // ========================================================================

    [Fact]
    public void SimplifyCFG_RemoveUnreachable_DropsDeadBlock()
    {
        // bb0 → return, bb1 is unreachable
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        bb0.Terminator = new CRet();
        bb1.Stmts.Add(new CAssign(0, new CConst(999, "SystemInt32")));
        bb1.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        Assert.Single(func.FlatBlocks);
        Assert.Equal(bb0.Id, func.FlatBlocks[0].Id);
    }

    // ========================================================================
    // SimplifyBranches
    // ========================================================================

    [Fact]
    public void SimplifyCFG_TrivialBranch_ConvertedToJump()
    {
        // bb0: branch(cond, bb1, bb1) → simplified to jump, then merged
        // After full optimization: single block with CRet
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        bb0.Terminator = new CBranch(new CSlotRef(0, "SystemBoolean"), bb1.Id, bb1.Id);
        bb1.Stmts.Add(new CAssign(0, new CConst(1, "SystemInt32")));
        bb1.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        // Simplify branch → jump, then merge → single block
        Assert.Single(func.FlatBlocks);
        var entry = func.FlatBlocks[0];
        Assert.IsType<CRet>(entry.Terminator);
        Assert.Single(entry.Stmts); // bb1's instruction merged in
    }

    // ========================================================================
    // Combined scenarios
    // ========================================================================

    [Fact]
    public void SimplifyCFG_EmptyFunction_NoError()
    {
        var func = MakeFunc();
        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module); // should not throw
        Assert.Empty(func.FlatBlocks);
    }

    [Fact]
    public void SimplifyCFG_SingleBlock_NoChange()
    {
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(1, "SystemInt32")));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        Assert.Single(func.FlatBlocks);
        Assert.Single(func.FlatBlocks[0].Stmts);
        Assert.IsType<CRet>(func.FlatBlocks[0].Terminator);
    }

    [Fact]
    public void SimplifyCFG_BranchThreading_RedirectsThroughEmptyBlocks()
    {
        // bb0: branch(cond, bb1, bb2), bb1 is empty → bb3, bb2 has code → return
        // Expected: branch redirected to (bb3, bb2)
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        var bb2 = func.NewBlock();
        var bb3 = func.NewBlock();

        bb0.Terminator = new CBranch(new CSlotRef(0, "SystemBoolean"), bb1.Id, bb2.Id);
        bb1.Terminator = new CJump(bb3.Id); // empty, jump-only
        bb2.Stmts.Add(new CAssign(0, new CConst(1, "SystemInt32")));
        bb2.Terminator = new CRet();
        bb3.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        // After optimization, bb0 should branch directly to bb3 (not through bb1)
        var entry = func.FlatBlocks[0];
        if (entry.Terminator is CBranch br)
        {
            Assert.Equal(bb3.Id, br.TrueBlockId);
        }
        else
        {
            // Could have been further simplified
            Assert.IsType<CJump>(entry.Terminator);
        }
    }

    [Fact]
    public void SimplifyCFG_SelfLoop_Preserved()
    {
        // bb0: jump bb0 (infinite loop) — should not be removed
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Terminator = new CJump(bb0.Id);

        var module = MakeModule(func);
        CoreFlatOptimizer.SimplifyCFG(module);

        Assert.Single(func.FlatBlocks);
        Assert.IsType<CJump>(func.FlatBlocks[0].Terminator);
        Assert.Equal(bb0.Id, ((CJump)func.FlatBlocks[0].Terminator).TargetBlockId);
    }

    // ========================================================================
    // DeadCodeElimination
    // ========================================================================

    [Fact]
    public void DCE_UnusedMove_Removed()
    {
        // slot0 = const(42) — never read → removed
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(42, "SystemInt32")));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.DeadCodeElimination(module);

        Assert.Empty(bb0.Stmts);
    }

    [Fact]
    public void DCE_UnusedLoadField_Removed()
    {
        // slot0 = load [myField] — never read → removed
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CLoadField(0, "myField", "SystemInt32"));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.DeadCodeElimination(module);

        Assert.Empty(bb0.Stmts);
    }

    [Fact]
    public void DCE_UsedMove_Kept()
    {
        // slot0 = const(true), branch on slot0 → kept
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(true, "SystemBoolean")));
        bb0.Terminator = new CBranch(new CSlotRef(0, "SystemBoolean"), bb1.Id, bb1.Id);
        bb1.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.DeadCodeElimination(module);

        Assert.Single(bb0.Stmts);
        Assert.IsType<CAssign>(bb0.Stmts[0]);
    }

    [Fact]
    public void DCE_ExternUnusedDest_KeptWithDest()
    {
        // slot0 = extern "Foo"() — slot0 never read but dest kept
        // (Udon VM requires return slot PUSH even if unused)
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(Call(0, "Foo__SystemInt32", new List<CValue>(), "SystemInt32"));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.DeadCodeElimination(module);

        Assert.Single(bb0.Stmts);
        var es = Assert.IsType<CExprStmt>(bb0.Stmts[0]);
        var call = Assert.IsType<CExternCall>(es.Expr);
        Assert.Equal(0, call.DestSlot); // dest preserved for stack balance
    }

    // ========================================================================
    // CopyPropagation
    // ========================================================================

    [Fact]
    public void CopyProp_ConstMove_Propagated()
    {
        // slot0 = const(42), return slot0 → return const(42)
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(42, "SystemInt32")));
        bb0.Terminator = new CRet(new CSlotRef(0, "SystemInt32"));

        var module = MakeModule(func);
        CoreFlatOptimizer.CopyPropagation(module);

        var ret = Assert.IsType<CRet>(bb0.Terminator);
        var c = Assert.IsType<CConst>(ret.Value);
        Assert.Equal(42, c.Value);
    }

    [Fact]
    public void CopyProp_MultipleWrites_NotPropagated()
    {
        // slot0 = const(1), slot0 = const(2), return slot0 → NOT propagated
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(1, "SystemInt32")));
        bb0.Stmts.Add(new CAssign(0, new CConst(2, "SystemInt32")));
        bb0.Terminator = new CRet(new CSlotRef(0, "SystemInt32"));

        var module = MakeModule(func);
        CoreFlatOptimizer.CopyPropagation(module);

        var ret = Assert.IsType<CRet>(bb0.Terminator);
        Assert.IsType<CSlotRef>(ret.Value); // not propagated
    }

    [Fact]
    public void CopyProp_NonConst_NotPropagated()
    {
        // slot0 = slot1, return slot0 → NOT propagated (conservative: only const)
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CSlotRef(1, "SystemInt32")));
        bb0.Terminator = new CRet(new CSlotRef(0, "SystemInt32"));

        var module = MakeModule(func);
        CoreFlatOptimizer.CopyPropagation(module);

        var ret = Assert.IsType<CRet>(bb0.Terminator);
        var sr = Assert.IsType<CSlotRef>(ret.Value);
        Assert.Equal(0, sr.SlotId); // not propagated
    }

    // ========================================================================
    // Slot Coalescing
    // ========================================================================

    [Fact]
    public void Coalesce_NonOverlapping_Merged()
    {
        // slot0 (Scratch, Int32): def at inst 0, used at inst 1
        // slot1 (Scratch, Int32): def at inst 2, used at inst 3
        // Non-overlapping → merged to same ID
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));            // pos 0: def slot0
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));    // pos 1: use slot0 (last use)
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));            // pos 2: def slot1
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, "SystemInt32")));    // pos 3: use slot1 (last use)
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot1 should be remapped to slot0 (non-overlapping, same type, same class)
        // Check that the third instruction writes to slot0
        var move2 = Assert.IsType<CAssign>(bb0.Stmts[2]);
        Assert.Equal(0, move2.DestSlot);

        // And the fourth instruction reads slot0
        var store2 = Assert.IsType<CStoreField>(bb0.Stmts[3]);
        var sr = Assert.IsType<CSlotRef>(store2.Value);
        Assert.Equal(0, sr.SlotId);

        // Slot list retains both entries (positional indexing), but slot1 is unused
        Assert.Equal(2, func.Slots.Count);
    }

    [Fact]
    public void Coalesce_Overlapping_Kept()
    {
        // slot0 and slot1 are both live at the same time → must not merge
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));            // pos 0: def slot0
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));            // pos 1: def slot1 (slot0 still live)
        bb0.Stmts.Add(Call(null, "Foo__SystemVoid",
            new List<CValue> { new CSlotRef(0, "SystemInt32"), new CSlotRef(1, "SystemInt32") },
            "SystemVoid"));                                                       // pos 2: use both
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // Both slots must remain (overlapping lifetimes)
        Assert.Equal(2, func.Slots.Count);

        // Instruction operands should still reference different slots
        var es = Assert.IsType<CExprStmt>(bb0.Stmts[2]);
        var call = Assert.IsType<CExternCall>(es.Expr);
        var ids = call.Args.OfType<CSlotRef>().Select(s => s.SlotId).Distinct().ToList();
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Coalesce_DifferentTypes_NotMerged()
    {
        // slot0 (Int32) and slot1 (Boolean): non-overlapping but different types → separate
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemBoolean", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(42, "SystemInt32")));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));
        bb0.Stmts.Add(new CAssign(1, new CConst(true, "SystemBoolean")));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, "SystemBoolean")));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // Both slots must remain (different types)
        Assert.Equal(2, func.Slots.Count);
    }

    [Fact]
    public void Coalesce_Pinned_NeverMerged()
    {
        // Two Pinned slots with non-overlapping lifetimes → never coalesced
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Pinned, "__param_x"));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Pinned, "__param_y"));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, "SystemInt32")));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // Both Pinned slots preserved
        Assert.Equal(2, func.Slots.Count);
        Assert.All(func.Slots, s => Assert.Equal(SlotClass.Pinned, s.Class));

        // Operands unchanged
        var move1 = Assert.IsType<CAssign>(bb0.Stmts[0]);
        Assert.Equal(0, move1.DestSlot);
        var move2 = Assert.IsType<CAssign>(bb0.Stmts[2]);
        Assert.Equal(1, move2.DestSlot);
    }

    [Fact]
    public void Coalesce_LoopBackEdge_PreventsInvalidMerge()
    {
        // slot0 used in loop header, slot1 used in loop body
        // Back-edge from body to header → slot0 is live throughout body
        // Must NOT merge slot0 and slot1
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));

        var header = func.NewBlock(); // bb0
        var body = func.NewBlock();   // bb1
        var exit = func.NewBlock();   // bb2

        // header: slot0 = condition, branch on slot0
        header.Stmts.Add(new CLoadField(0, "cond", "SystemInt32"));
        header.Terminator = new CBranch(new CSlotRef(0, "SystemInt32"), body.Id, exit.Id);

        // body: slot1 = 42, use slot1, jump back to header
        body.Stmts.Add(new CAssign(1, new CConst(42, "SystemInt32")));
        body.Stmts.Add(new CStoreField("result", new CSlotRef(1, "SystemInt32")));
        body.Terminator = new CJump(header.Id); // back-edge

        // exit: return
        exit.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot0 and slot1 must NOT be merged (slot0 alive through body via back-edge)
        var bodyMove = Assert.IsType<CAssign>(body.Stmts[0]);
        Assert.NotEqual(0, bodyMove.DestSlot); // slot1 must keep its own ID
    }

    [Fact]
    public void Coalesce_LoopWithNonOverlapping_StillMerges()
    {
        // slot0 defined and used only in body (not in header)
        // slot1 defined and used only after loop
        // These should still merge even with a loop present
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, "SystemBoolean", SlotClass.Scratch));

        var header = func.NewBlock();
        var body = func.NewBlock();
        var exit = func.NewBlock();

        header.Stmts.Add(new CLoadField(2, "flag", "SystemBoolean"));
        header.Terminator = new CBranch(new CSlotRef(2, "SystemBoolean"), body.Id, exit.Id);

        // body: use slot0 entirely within body
        body.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));
        body.Stmts.Add(new CStoreField("x", new CSlotRef(0, "SystemInt32")));
        body.Terminator = new CJump(header.Id);

        // exit: use slot1 entirely after loop
        exit.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));
        exit.Stmts.Add(new CStoreField("y", new CSlotRef(1, "SystemInt32")));
        exit.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot0 and slot1 CAN be merged (non-overlapping, one in body one after)
        // RPO visits exit before body, so slot1 (exit) gets the lower def position
        // and becomes the representative. slot0 (body) merges into slot1.
        var bodyMove = Assert.IsType<CAssign>(body.Stmts[0]);
        var exitMove = Assert.IsType<CAssign>(exit.Stmts[0]);
        Assert.Equal(exitMove.DestSlot, bodyMove.DestSlot); // merged to same slot
    }

    [Fact]
    public void Coalesce_RewritesOperands()
    {
        // Verify all instruction types get operands remapped after coalescing
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, "SystemBoolean", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        // slot0: def and last use in first two instructions
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));            // def slot0
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));    // last use slot0

        // slot1: def after slot0 is dead → should coalesce to slot0
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));            // def slot1
        bb0.Stmts.Add(Call(null, "Bar__SystemVoid",
            new List<CValue> { new CSlotRef(1, "SystemInt32") },
            "SystemVoid"));                                                       // use slot1 as arg

        // slot2 (Boolean): used in branch
        bb0.Stmts.Add(new CAssign(2, new CConst(true, "SystemBoolean")));
        bb0.Terminator = new CBranch(new CSlotRef(2, "SystemBoolean"), bb1.Id, bb1.Id);

        // slot1 also used in return value in bb1
        bb1.Terminator = new CRet(new CSlotRef(1, "SystemInt32"));

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot1 should be remapped to slot0 (non-overlapping Int32 Scratch)
        // Verify CAssign dest rewritten
        var move2 = Assert.IsType<CAssign>(bb0.Stmts[2]);
        Assert.Equal(0, move2.DestSlot);

        // Verify extern-call arg rewritten
        var es = Assert.IsType<CExprStmt>(bb0.Stmts[3]);
        var call = Assert.IsType<CExternCall>(es.Expr);
        var argRef = Assert.IsType<CSlotRef>(call.Args[0]);
        Assert.Equal(0, argRef.SlotId);

        // Verify CRet value rewritten
        var ret = Assert.IsType<CRet>(bb1.Terminator);
        var retRef = Assert.IsType<CSlotRef>(ret.Value);
        Assert.Equal(0, retRef.SlotId);

        // Verify CBranch condition NOT rewritten (slot2 is Boolean, different type)
        var br = Assert.IsType<CBranch>(bb0.Terminator);
        var condRef = Assert.IsType<CSlotRef>(br.Cond);
        Assert.Equal(2, condRef.SlotId);
    }
}
