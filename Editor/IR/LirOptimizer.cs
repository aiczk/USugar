using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// LIR-level CFG simplification.
/// Since LIR has no Phi nodes, jump threading, block merging, and dead block
/// removal are purely mechanical — just update terminator target IDs.
/// </summary>
public static class LirOptimizer
{
    public static void SimplifyCFG(LModule module)
    {
        foreach (var func in module.Functions)
            SimplifyCFGFunc(func);
    }

    static void SimplifyCFGFunc(LFunction func)
    {
        if (func.Blocks.Count == 0) return;

        bool changed = true;
        while (changed)
        {
            changed = false;
            changed |= SimplifyBranches(func);
            changed |= ThreadJumps(func);
            changed |= RemoveEmptyBlocks(func);
            changed |= MergeBlocks(func);
        }
        RemoveUnreachableBlocks(func);
    }

    // ========================================================================
    // Simplify trivial branches: LBranch where trueBlock == falseBlock → LJump
    // ========================================================================

    static bool SimplifyBranches(LFunction func)
    {
        bool changed = false;
        foreach (var block in func.Blocks)
        {
            if (block.Term is LBranch br && br.TrueBlockId == br.FalseBlockId)
            {
                block.Term = new LJump(br.TrueBlockId);
                changed = true;
            }
        }
        return changed;
    }

    // ========================================================================
    // Thread jumps: if A→B and B is only LJump(C), redirect A→C
    // ========================================================================

    static bool ThreadJumps(LFunction func)
    {
        // Build a map of blockId → its sole jump target (if the block is empty + LJump)
        var jumpOnly = new Dictionary<int, int>();
        foreach (var block in func.Blocks)
        {
            if (block.Insts.Count == 0 && block.Term is LJump j)
                jumpOnly[block.Id] = j.TargetBlockId;
        }

        if (jumpOnly.Count == 0) return false;

        bool changed = false;
        foreach (var block in func.Blocks)
        {
            switch (block.Term)
            {
                case LJump j:
                {
                    var resolved = Resolve(j.TargetBlockId, jumpOnly);
                    if (resolved != j.TargetBlockId)
                    {
                        j.TargetBlockId = resolved;
                        changed = true;
                    }
                    break;
                }
                case LBranch br:
                {
                    var rt = Resolve(br.TrueBlockId, jumpOnly);
                    var rf = Resolve(br.FalseBlockId, jumpOnly);
                    if (rt != br.TrueBlockId || rf != br.FalseBlockId)
                    {
                        br.TrueBlockId = rt;
                        br.FalseBlockId = rf;
                        changed = true;
                    }
                    break;
                }
            }
        }
        return changed;
    }

    /// <summary>Follow jump-only chain to final target, with cycle guard.</summary>
    static int Resolve(int blockId, Dictionary<int, int> jumpOnly)
    {
        var visited = new HashSet<int>();
        int cur = blockId;
        while (jumpOnly.TryGetValue(cur, out var next) && visited.Add(cur))
            cur = next;
        return cur;
    }

    // ========================================================================
    // Remove empty blocks: empty instructions + LJump → redirect predecessors
    // ========================================================================

    static bool RemoveEmptyBlocks(LFunction func)
    {
        if (func.Blocks.Count <= 1) return false;

        var preds = ComputePredecessors(func);
        bool changed = false;

        // Don't remove the entry block
        var entryId = func.Entry.Id;

        for (int i = func.Blocks.Count - 1; i >= 0; i--)
        {
            var block = func.Blocks[i];
            if (block.Id == entryId) continue;
            if (block.Insts.Count != 0 || block.Term is not LJump j) continue;

            var target = j.TargetBlockId;
            if (target == block.Id) continue; // self-loop — keep

            // Redirect all predecessors
            foreach (var predId in preds[block.Id])
            {
                var pred = func.Blocks.First(b => b.Id == predId);
                RedirectTerminator(pred, block.Id, target);
            }

            func.Blocks.RemoveAt(i);
            changed = true;
        }
        return changed;
    }

    // ========================================================================
    // Merge blocks: A's sole successor is B, B's sole predecessor is A
    // ========================================================================

    static bool MergeBlocks(LFunction func)
    {
        if (func.Blocks.Count <= 1) return false;

        var preds = ComputePredecessors(func);
        bool changed = false;

        // Iterate until no more merges possible in this pass
        bool merged;
        do
        {
            merged = false;
            preds = ComputePredecessors(func);

            for (int i = 0; i < func.Blocks.Count; i++)
            {
                var block = func.Blocks[i];
                if (block.Term is not LJump j) continue;

                var succId = j.TargetBlockId;
                if (succId == block.Id) continue; // self-loop

                // B must have exactly one predecessor (A)
                if (!preds.TryGetValue(succId, out var succPreds) || succPreds.Count != 1)
                    continue;
                if (succPreds[0] != block.Id) continue;

                var succ = func.Blocks.FirstOrDefault(b => b.Id == succId);
                if (succ == null) continue;

                // Merge: append B's instructions and terminator to A
                block.Insts.AddRange(succ.Insts);
                block.Term = succ.Term;

                func.Blocks.Remove(succ);
                merged = true;
                changed = true;
                break; // restart — indices and preds are stale
            }
        } while (merged);

        return changed;
    }

    // ========================================================================
    // Remove unreachable blocks
    // ========================================================================

    static void RemoveUnreachableBlocks(LFunction func)
    {
        if (func.Blocks.Count <= 1) return;

        var reachable = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(func.Entry.Id);
        reachable.Add(func.Entry.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var block = func.Blocks.FirstOrDefault(b => b.Id == id);
            if (block?.Term == null) continue;

            foreach (var succId in GetSuccessors(block.Term))
            {
                if (reachable.Add(succId))
                    queue.Enqueue(succId);
            }
        }

        func.Blocks.RemoveAll(b => !reachable.Contains(b.Id));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    static Dictionary<int, List<int>> ComputePredecessors(LFunction func)
    {
        var preds = new Dictionary<int, List<int>>();
        foreach (var block in func.Blocks)
            preds[block.Id] = new List<int>();

        foreach (var block in func.Blocks)
        {
            foreach (var succId in GetSuccessors(block.Term))
            {
                if (preds.TryGetValue(succId, out var list))
                    list.Add(block.Id);
            }
        }
        return preds;
    }

    static IEnumerable<int> GetSuccessors(LTerminator term) => term switch
    {
        LJump j => new[] { j.TargetBlockId },
        LBranch b => new[] { b.TrueBlockId, b.FalseBlockId },
        LReturn => Array.Empty<int>(),
        _ => Array.Empty<int>(),
    };

    static void RedirectTerminator(LBlock block, int oldTarget, int newTarget)
    {
        switch (block.Term)
        {
            case LJump j:
                if (j.TargetBlockId == oldTarget)
                    j.TargetBlockId = newTarget;
                break;
            case LBranch br:
                if (br.TrueBlockId == oldTarget)
                    br.TrueBlockId = newTarget;
                if (br.FalseBlockId == oldTarget)
                    br.FalseBlockId = newTarget;
                break;
        }
    }
}
