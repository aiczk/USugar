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
    // Dead Code Elimination
    // ========================================================================

    public static void DeadCodeElimination(LModule module)
    {
        foreach (var func in module.Functions)
            DCEFunc(func);
    }

    static void DCEFunc(LFunction func)
    {
        if (func.Blocks.Count == 0) return;

        bool changed = true;
        while (changed)
        {
            changed = false;
            var usedSlots = CollectUsedSlots(func);

            foreach (var block in func.Blocks)
            {
                for (int i = block.Insts.Count - 1; i >= 0; i--)
                {
                    var inst = block.Insts[i];
                    switch (inst)
                    {
                        case LMove m when !usedSlots.Contains(m.DestSlot):
                            block.Insts.RemoveAt(i);
                            changed = true;
                            break;
                        case LLoadField lf when !usedSlots.Contains(lf.DestSlot):
                            block.Insts.RemoveAt(i);
                            changed = true;
                            break;
                        case LCallExtern ce when ce.DestSlot.HasValue && !usedSlots.Contains(ce.DestSlot.Value):
                            block.Insts[i] = new LCallExtern(null, ce.Sig, ce.Args, ce.RetType, ce.IsPure);
                            changed = true;
                            break;
                        case LCallInternal ci when ci.DestSlot.HasValue && !usedSlots.Contains(ci.DestSlot.Value):
                            block.Insts[i] = new LCallInternal(null, ci.FuncName, ci.Args, ci.RetType);
                            changed = true;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Collect all slot IDs that are read (used as operands) anywhere in the function.</summary>
    static HashSet<int> CollectUsedSlots(LFunction func)
    {
        var used = new HashSet<int>();

        void AddOperand(LOperand op)
        {
            if (op is LSlotRef sr) used.Add(sr.SlotId);
        }

        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Insts)
            {
                switch (inst)
                {
                    case LMove m:
                        AddOperand(m.Src);
                        break;
                    case LStoreField sf:
                        AddOperand(sf.Value);
                        break;
                    case LCallExtern ce:
                        foreach (var arg in ce.Args) AddOperand(arg);
                        break;
                    case LCallInternal ci:
                        foreach (var arg in ci.Args) AddOperand(arg);
                        break;
                    case LLoadField:
                        break;
                }
            }

            switch (block.Term)
            {
                case LBranch br:
                    AddOperand(br.Cond);
                    break;
                case LReturn ret:
                    if (ret.Value != null) AddOperand(ret.Value);
                    break;
            }
        }

        return used;
    }

    // ========================================================================
    // Copy Propagation (constants only)
    // ========================================================================

    public static void CopyPropagation(LModule module)
    {
        foreach (var func in module.Functions)
            CopyPropFunc(func);
    }

    static void CopyPropFunc(LFunction func)
    {
        if (func.Blocks.Count == 0) return;

        // Count writes per slot
        var writeCounts = new Dictionary<int, int>();
        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Insts)
            {
                int? dest = inst switch
                {
                    LMove m => m.DestSlot,
                    LLoadField lf => lf.DestSlot,
                    LCallExtern ce => ce.DestSlot,
                    LCallInternal ci => ci.DestSlot,
                    _ => null,
                };
                if (dest.HasValue)
                {
                    writeCounts.TryGetValue(dest.Value, out var c);
                    writeCounts[dest.Value] = c + 1;
                }
            }
        }

        // Find single-write LMove with LConst source
        var constMap = new Dictionary<int, LConst>();
        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Insts)
            {
                if (inst is LMove m && m.Src is LConst lc
                    && writeCounts.TryGetValue(m.DestSlot, out var wc) && wc == 1)
                {
                    constMap[m.DestSlot] = lc;
                }
            }
        }

        if (constMap.Count == 0) return;

        // Replace uses
        LOperand Subst(LOperand op) =>
            op is LSlotRef sr && constMap.TryGetValue(sr.SlotId, out var replacement)
                ? replacement
                : op;

        foreach (var block in func.Blocks)
        {
            for (int i = 0; i < block.Insts.Count; i++)
            {
                switch (block.Insts[i])
                {
                    case LMove m:
                        var newSrc = Subst(m.Src);
                        if (newSrc != m.Src)
                            block.Insts[i] = new LMove(m.DestSlot, newSrc, m.Type);
                        break;
                    case LStoreField sf:
                        var newVal = Subst(sf.Value);
                        if (newVal != sf.Value)
                            block.Insts[i] = new LStoreField(sf.FieldName, newVal);
                        break;
                    case LCallExtern ce:
                        var ceArgs = SubstArgs(ce.Args, Subst);
                        if (ceArgs != null)
                            block.Insts[i] = new LCallExtern(ce.DestSlot, ce.Sig, ceArgs, ce.RetType, ce.IsPure);
                        break;
                    case LCallInternal ci:
                        var ciArgs = SubstArgs(ci.Args, Subst);
                        if (ciArgs != null)
                            block.Insts[i] = new LCallInternal(ci.DestSlot, ci.FuncName, ciArgs, ci.RetType);
                        break;
                }
            }

            switch (block.Term)
            {
                case LBranch br:
                    var newCond = Subst(br.Cond);
                    if (newCond != br.Cond)
                        block.Term = new LBranch(newCond, br.TrueBlockId, br.FalseBlockId);
                    break;
                case LReturn ret when ret.Value != null:
                    var newRet = Subst(ret.Value);
                    if (newRet != ret.Value)
                        block.Term = new LReturn(newRet);
                    break;
            }
        }
    }

    /// <summary>Substitute operands in argument list. Returns null if no changes.</summary>
    static List<LOperand> SubstArgs(List<LOperand> args, Func<LOperand, LOperand> subst)
    {
        bool any = false;
        var result = new List<LOperand>(args.Count);
        foreach (var arg in args)
        {
            var newArg = subst(arg);
            if (newArg != arg) any = true;
            result.Add(newArg);
        }
        return any ? result : null;
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
