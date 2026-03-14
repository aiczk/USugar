using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class LirOptimizerTests
{
    // ── Helpers ──

    static LModule MakeModule(LFunction func)
    {
        var module = new LModule { ClassName = "Test" };
        module.Functions.Add(func);
        return module;
    }

    static LFunction MakeFunc(string name = "test") => new(name);

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

        bb0.Insts.Add(new LMove(0, new LConst(42, "SystemInt32"), "SystemInt32"));
        bb0.Term = new LJump(bb1.Id);

        bb1.Term = new LJump(bb2.Id); // empty, jump-only

        bb2.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        // Thread + remove empty + merge → single block with the move and LReturn
        Assert.Single(func.Blocks);
        var entry = func.Blocks[0];
        Assert.Single(entry.Insts);
        Assert.IsType<LReturn>(entry.Term);
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

        bb0.Insts.Add(new LMove(0, new LConst(1, "SystemInt32"), "SystemInt32"));
        bb0.Term = new LJump(bb1.Id);
        bb1.Term = new LJump(bb2.Id);
        bb2.Term = new LJump(bb3.Id);
        bb3.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        // Thread + remove empty + merge → single block
        Assert.Single(func.Blocks);
        var entry = func.Blocks[0];
        Assert.Single(entry.Insts);
        Assert.IsType<LReturn>(entry.Term);
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

        bb0.Insts.Add(new LMove(0, new LConst(1, "SystemInt32"), "SystemInt32"));
        bb0.Term = new LJump(bb1.Id);

        bb1.Insts.Add(new LMove(1, new LConst(2, "SystemInt32"), "SystemInt32"));
        bb1.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        Assert.Single(func.Blocks);
        var merged = func.Blocks[0];
        Assert.Equal(2, merged.Insts.Count);
        Assert.IsType<LReturn>(merged.Term);
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

        bb0.Term = new LBranch(new LSlotRef(0, "SystemBoolean"), bb1.Id, bb2.Id);

        bb1.Insts.Add(new LMove(1, new LConst(1, "SystemInt32"), "SystemInt32"));
        bb1.Term = new LJump(bb2.Id);

        bb2.Insts.Add(new LMove(0, new LConst(99, "SystemInt32"), "SystemInt32"));
        bb2.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        // bb2 should still exist as a separate block (two predecessors: bb0, bb1)
        Assert.True(func.Blocks.Count >= 2);
        // The block with LReturn should still have the move instruction
        var retBlock = func.Blocks.First(b => b.Term is LReturn);
        Assert.Contains(retBlock.Insts, i => i is LMove m && m.DestSlot == 0);
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

        bb0.Term = new LReturn();
        bb1.Insts.Add(new LMove(0, new LConst(999, "SystemInt32"), "SystemInt32"));
        bb1.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        Assert.Single(func.Blocks);
        Assert.Equal(bb0.Id, func.Blocks[0].Id);
    }

    // ========================================================================
    // SimplifyBranches
    // ========================================================================

    [Fact]
    public void SimplifyCFG_TrivialBranch_ConvertedToJump()
    {
        // bb0: branch(cond, bb1, bb1) → simplified to jump, then merged
        // After full optimization: single block with LReturn
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        bb0.Term = new LBranch(new LSlotRef(0, "SystemBoolean"), bb1.Id, bb1.Id);
        bb1.Insts.Add(new LMove(0, new LConst(1, "SystemInt32"), "SystemInt32"));
        bb1.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        // Simplify branch → jump, then merge → single block
        Assert.Single(func.Blocks);
        var entry = func.Blocks[0];
        Assert.IsType<LReturn>(entry.Term);
        Assert.Single(entry.Insts); // bb1's instruction merged in
    }

    // ========================================================================
    // Combined scenarios
    // ========================================================================

    [Fact]
    public void SimplifyCFG_EmptyFunction_NoError()
    {
        var func = MakeFunc();
        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module); // should not throw
        Assert.Empty(func.Blocks);
    }

    [Fact]
    public void SimplifyCFG_SingleBlock_NoChange()
    {
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Insts.Add(new LMove(0, new LConst(1, "SystemInt32"), "SystemInt32"));
        bb0.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        Assert.Single(func.Blocks);
        Assert.Single(func.Blocks[0].Insts);
        Assert.IsType<LReturn>(func.Blocks[0].Term);
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

        bb0.Term = new LBranch(new LSlotRef(0, "SystemBoolean"), bb1.Id, bb2.Id);
        bb1.Term = new LJump(bb3.Id); // empty, jump-only
        bb2.Insts.Add(new LMove(0, new LConst(1, "SystemInt32"), "SystemInt32"));
        bb2.Term = new LReturn();
        bb3.Term = new LReturn();

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        // After optimization, bb0 should branch directly to bb3 (not through bb1)
        var entry = func.Blocks[0];
        if (entry.Term is LBranch br)
        {
            Assert.Equal(bb3.Id, br.TrueBlockId);
        }
        else
        {
            // Could have been further simplified
            Assert.IsType<LJump>(entry.Term);
        }
    }

    [Fact]
    public void SimplifyCFG_SelfLoop_Preserved()
    {
        // bb0: jump bb0 (infinite loop) — should not be removed
        var func = MakeFunc();
        var bb0 = func.NewBlock();
        bb0.Term = new LJump(bb0.Id);

        var module = MakeModule(func);
        LirOptimizer.SimplifyCFG(module);

        Assert.Single(func.Blocks);
        Assert.IsType<LJump>(func.Blocks[0].Term);
        Assert.Equal(bb0.Id, ((LJump)func.Blocks[0].Term).TargetBlockId);
    }
}
