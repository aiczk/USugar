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
                        // Don't null out destSlot for extern/internal calls:
                        // Udon VM requires the return value slot to be PUSHed even
                        // if the result is unused. Removing it breaks stack balance.
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
    // Slot Coalescing
    // ========================================================================

    /// <summary>
    /// Merge Scratch/Frame slots with non-overlapping lifetimes into fewer physical slots.
    /// Reduces the number of __intnl_* UASM variables.
    /// </summary>
    public static void CoalesceSlots(LModule module)
    {
        foreach (var func in module.Functions)
            CoalesceSlotsFunc(func);
    }

    static void CoalesceSlotsFunc(LFunction func)
    {
        if (func.Blocks.Count == 0 || func.Slots.Count == 0) return;

        // Step 1: Linearize instructions and compute liveness intervals
        var (written, lastUsed) = ComputeLivenessIntervals(func);

        // Collect coalesceable slots (Scratch or Frame, not Pinned)
        var coalesceable = new List<SlotDecl>();
        foreach (var slot in func.Slots)
        {
            if (slot.Class == SlotClass.Pinned) continue;
            if (!written.ContainsKey(slot.Id) && !lastUsed.ContainsKey(slot.Id)) continue;
            coalesceable.Add(slot);
        }

        if (coalesceable.Count == 0) return;

        // Step 2 & 3: Build interference graph and greedy color
        // Group by (Type, SlotClass) — only same-group slots can coalesce
        var groups = new Dictionary<(string Type, SlotClass Class), List<SlotDecl>>();
        foreach (var slot in coalesceable)
        {
            var key = (slot.Type, slot.Class);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<SlotDecl>();
                groups[key] = list;
            }
            list.Add(slot);
        }

        // For each group, do greedy interval coloring
        var mapping = new Dictionary<int, int>(); // oldSlotId → newSlotId

        foreach (var group in groups.Values)
        {
            if (group.Count <= 1) continue;

            // Sort by def position (earliest definition first)
            group.Sort((a, b) =>
            {
                var da = written.TryGetValue(a.Id, out var va) ? va : int.MaxValue;
                var db = written.TryGetValue(b.Id, out var vb) ? vb : int.MaxValue;
                return da.CompareTo(db);
            });

            // Greedy coloring: each "color" is represented by a slot ID
            // colors[i] = (slotId, lastUsePos) for color i
            var colors = new List<(int SlotId, int LastUse)>();

            foreach (var slot in group)
            {
                int def = written.TryGetValue(slot.Id, out var d) ? d : 0;
                int last = lastUsed.TryGetValue(slot.Id, out var u) ? u : def;

                // Find first color whose interval doesn't overlap
                int assigned = -1;
                for (int c = 0; c < colors.Count; c++)
                {
                    // Non-overlapping: color's last use < this slot's def
                    if (colors[c].LastUse < def)
                    {
                        assigned = c;
                        // Extend the color's last use
                        colors[c] = (colors[c].SlotId, last);
                        break;
                    }
                }

                if (assigned == -1)
                {
                    // New color needed — this slot keeps its own ID
                    colors.Add((slot.Id, last));
                }
                else
                {
                    // Map this slot to the color's representative slot
                    mapping[slot.Id] = colors[assigned].SlotId;
                }
            }
        }

        if (mapping.Count == 0) return;

        // Step 4: Rewrite all instructions and terminators
        foreach (var block in func.Blocks)
        {
            for (int i = 0; i < block.Insts.Count; i++)
                block.Insts[i] = RemapInst(block.Insts[i], mapping);

            block.Term = RemapTerminator(block.Term, mapping);
        }

        // Note: We do NOT remove coalesced slots from func.Slots because
        // LirToUasm indexes into Slots by slot ID (positional). The coalesced-away
        // slots simply won't be referenced by any instruction and GetSlotVar will
        // never be called for them, so no UASM variable will be emitted.
    }

    static (Dictionary<int, int> Written, Dictionary<int, int> LastUsed) ComputeLivenessIntervals(LFunction func)
    {
        var written = new Dictionary<int, int>();
        var lastUsed = new Dictionary<int, int>();

        // Compute RPO ordering
        var rpo = ComputeRPO(func);

        int pos = 0;
        foreach (var block in rpo)
        {
            foreach (var inst in block.Insts)
            {
                // Record reads first (a read at pos before a write at same pos)
                foreach (var slotId in GetReadSlotsInst(inst))
                    lastUsed[slotId] = pos;

                var dest = GetWrittenSlot(inst);
                if (dest.HasValue)
                {
                    if (!written.ContainsKey(dest.Value))
                        written[dest.Value] = pos;
                    // A write is also a "last use" for interval purposes
                    lastUsed[dest.Value] = pos;
                }

                pos++;
            }

            // Terminator reads
            foreach (var slotId in GetReadSlotsTerm(block.Term))
                lastUsed[slotId] = pos;

            pos++;
        }

        return (written, lastUsed);
    }

    static List<LBlock> ComputeRPO(LFunction func)
    {
        var visited = new HashSet<int>();
        var postOrder = new List<LBlock>();
        var blockMap = new Dictionary<int, LBlock>();
        foreach (var b in func.Blocks) blockMap[b.Id] = b;

        void Dfs(int blockId)
        {
            if (!visited.Add(blockId)) return;
            if (!blockMap.TryGetValue(blockId, out var block)) return;
            if (block.Term == null) { postOrder.Add(block); return; }
            foreach (var succ in GetSuccessors(block.Term))
                Dfs(succ);
            postOrder.Add(block);
        }

        if (func.Entry != null)
            Dfs(func.Entry.Id);

        postOrder.Reverse();
        return postOrder;
    }

    static int? GetWrittenSlot(LInst inst) => inst switch
    {
        LMove m => m.DestSlot,
        LLoadField lf => lf.DestSlot,
        LCallExtern ce => ce.DestSlot,
        LCallInternal ci => ci.DestSlot,
        _ => null,
    };

    static IEnumerable<int> GetReadSlotsInst(LInst inst)
    {
        switch (inst)
        {
            case LMove m:
                if (m.Src is LSlotRef sr) yield return sr.SlotId;
                break;
            case LStoreField sf:
                if (sf.Value is LSlotRef sr2) yield return sr2.SlotId;
                break;
            case LCallExtern ce:
                foreach (var arg in ce.Args)
                    if (arg is LSlotRef sr3) yield return sr3.SlotId;
                break;
            case LCallInternal ci:
                foreach (var arg in ci.Args)
                    if (arg is LSlotRef sr4) yield return sr4.SlotId;
                break;
        }
    }

    static IEnumerable<int> GetReadSlotsTerm(LTerminator term)
    {
        switch (term)
        {
            case LBranch br:
                if (br.Cond is LSlotRef sr) yield return sr.SlotId;
                break;
            case LReturn ret:
                if (ret.Value is LSlotRef sr2) yield return sr2.SlotId;
                break;
        }
    }

    static LOperand RemapOperand(LOperand op, Dictionary<int, int> mapping)
    {
        if (op is LSlotRef sr && mapping.TryGetValue(sr.SlotId, out var newId) && newId != sr.SlotId)
            return new LSlotRef(newId, sr.Type);
        return op;
    }

    static int RemapSlotId(int slotId, Dictionary<int, int> mapping)
        => mapping.TryGetValue(slotId, out var newId) ? newId : slotId;

    static int? RemapSlotIdNullable(int? slotId, Dictionary<int, int> mapping)
        => slotId.HasValue ? RemapSlotId(slotId.Value, mapping) : null;

    static List<LOperand> RemapArgs(List<LOperand> args, Dictionary<int, int> mapping)
    {
        var result = new List<LOperand>(args.Count);
        foreach (var arg in args)
            result.Add(RemapOperand(arg, mapping));
        return result;
    }

    static LInst RemapInst(LInst inst, Dictionary<int, int> mapping) => inst switch
    {
        LMove m => new LMove(RemapSlotId(m.DestSlot, mapping), RemapOperand(m.Src, mapping), m.Type),
        LLoadField lf => new LLoadField(RemapSlotId(lf.DestSlot, mapping), lf.FieldName, lf.Type),
        LStoreField sf => new LStoreField(sf.FieldName, RemapOperand(sf.Value, mapping)),
        LCallExtern ce => new LCallExtern(RemapSlotIdNullable(ce.DestSlot, mapping), ce.Sig, RemapArgs(ce.Args, mapping), ce.RetType, ce.IsPure),
        LCallInternal ci => new LCallInternal(RemapSlotIdNullable(ci.DestSlot, mapping), ci.FuncName, RemapArgs(ci.Args, mapping), ci.RetType),
        _ => inst,
    };

    static LTerminator RemapTerminator(LTerminator term, Dictionary<int, int> mapping) => term switch
    {
        LBranch br => new LBranch(RemapOperand(br.Cond, mapping), br.TrueBlockId, br.FalseBlockId),
        LReturn ret when ret.Value != null => new LReturn(RemapOperand(ret.Value, mapping)),
        _ => term,
    };

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
