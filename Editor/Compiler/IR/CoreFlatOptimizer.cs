using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// LIR-level CFG simplification.
/// Since LIR has no Phi nodes, jump threading, block merging, and dead block
/// removal are purely mechanical — just update terminator target IDs.
/// </summary>
public static class CoreFlatOptimizer
{
    public static void SimplifyCFG(CModule module)
    {
        foreach (var func in module.Functions)
            SimplifyCFGFunc(func);
    }

    static void SimplifyCFGFunc(CFunction func)
    {
        if (func.FlatBlocks.Count == 0) return;

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
    // Simplify trivial branches: CBranch where trueBlock == falseBlock → CJump
    // ========================================================================

    static bool SimplifyBranches(CFunction func)
    {
        bool changed = false;
        foreach (var block in func.FlatBlocks)
        {
            if (block.Terminator is CBranch br && br.TrueBlockId == br.FalseBlockId)
            {
                block.Terminator = new CJump(br.TrueBlockId);
                changed = true;
            }
        }
        return changed;
    }

    // ========================================================================
    // Thread jumps: if A→B and B is only CJump(C), redirect A→C
    // ========================================================================

    static bool ThreadJumps(CFunction func)
    {
        // Build a map of blockId → its sole jump target (if the block is empty + CJump)
        var jumpOnly = new Dictionary<int, int>();
        foreach (var block in func.FlatBlocks)
        {
            if (block.Stmts.Count == 0 && block.Terminator is CJump j)
                jumpOnly[block.Id] = j.TargetBlockId;
        }

        if (jumpOnly.Count == 0) return false;

        bool changed = false;
        foreach (var block in func.FlatBlocks)
        {
            switch (block.Terminator)
            {
                case CJump j:
                {
                    var resolved = Resolve(j.TargetBlockId, jumpOnly);
                    if (resolved != j.TargetBlockId)
                    {
                        j.TargetBlockId = resolved;
                        changed = true;
                    }
                    break;
                }
                case CBranch br:
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
    // Remove empty blocks: empty instructions + CJump → redirect predecessors
    // ========================================================================

    static bool RemoveEmptyBlocks(CFunction func)
    {
        if (func.FlatBlocks.Count <= 1) return false;

        var preds = ComputePredecessors(func);
        var blockMap = BuildBlockMap(func);
        bool changed = false;

        // Don't remove the entry block
        var entryId = func.Entry.Id;

        for (int i = func.FlatBlocks.Count - 1; i >= 0; i--)
        {
            var block = func.FlatBlocks[i];
            if (block.Id == entryId) continue;
            if (block.Stmts.Count != 0 || block.Terminator is not CJump j) continue;

            var target = j.TargetBlockId;
            if (target == block.Id) continue; // self-loop — keep

            // Redirect all predecessors
            foreach (var predId in preds[block.Id])
            {
                if (blockMap.TryGetValue(predId, out var pred))
                    RedirectTerminator(pred, block.Id, target);
            }

            blockMap.Remove(block.Id);
            func.FlatBlocks.RemoveAt(i);
            changed = true;
        }
        return changed;
    }

    // ========================================================================
    // Merge blocks: A's sole successor is B, B's sole predecessor is A
    // ========================================================================

    static bool MergeBlocks(CFunction func)
    {
        if (func.FlatBlocks.Count <= 1) return false;

        var preds = ComputePredecessors(func);
        bool changed = false;

        // Iterate until no more merges possible in this pass
        bool merged;
        do
        {
            merged = false;
            preds = ComputePredecessors(func);
            var blockMap = BuildBlockMap(func);

            for (int i = 0; i < func.FlatBlocks.Count; i++)
            {
                var block = func.FlatBlocks[i];
                if (block.Terminator is not CJump j) continue;

                var succId = j.TargetBlockId;
                if (succId == block.Id) continue; // self-loop

                // B must have exactly one predecessor (A)
                if (!preds.TryGetValue(succId, out var succPreds) || succPreds.Count != 1)
                    continue;
                if (succPreds[0] != block.Id) continue;

                if (!blockMap.TryGetValue(succId, out var succ)) continue;

                // Merge: append B's instructions and terminator to A
                block.Stmts.AddRange(succ.Stmts);
                block.Terminator = succ.Terminator;

                func.FlatBlocks.Remove(succ);
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

    static void RemoveUnreachableBlocks(CFunction func)
    {
        if (func.FlatBlocks.Count <= 1) return;

        var blockMap = BuildBlockMap(func);
        var reachable = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(func.Entry.Id);
        reachable.Add(func.Entry.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!blockMap.TryGetValue(id, out var block) || block.Terminator == null) continue;

            foreach (var succId in GetSuccessors(block.Terminator))
            {
                if (reachable.Add(succId))
                    queue.Enqueue(succId);
            }
        }

        func.FlatBlocks.RemoveAll(b => !reachable.Contains(b.Id));
    }

    // ========================================================================
    // Dead Code Elimination
    // ========================================================================

    public static void DeadCodeElimination(CModule module)
    {
        foreach (var func in module.Functions)
            DCEFunc(func);
    }

    static void DCEFunc(CFunction func)
    {
        if (func.FlatBlocks.Count == 0) return;

        bool changed = true;
        while (changed)
        {
            changed = false;
            var usedSlots = CollectUsedSlots(func);

            foreach (var block in func.FlatBlocks)
            {
                for (int i = block.Stmts.Count - 1; i >= 0; i--)
                {
                    var inst = block.Stmts[i];
                    switch (inst)
                    {
                        case CAssign m when !usedSlots.Contains(m.DestSlot):
                            block.Stmts.RemoveAt(i);
                            changed = true;
                            break;
                        case CLoadField lf when !usedSlots.Contains(lf.DestSlot):
                            block.Stmts.RemoveAt(i);
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
    static HashSet<int> CollectUsedSlots(CFunction func)
    {
        var used = new HashSet<int>();

        void AddOperand(CValue op)
        {
            if (op is CSlotRef sr) used.Add(sr.SlotId);
        }

        foreach (var block in func.FlatBlocks)
        {
            foreach (var inst in block.Stmts)
            {
                switch (inst)
                {
                    case CAssign m:
                        AddOperand(m.Value);
                        break;
                    case CStoreField sf:
                        AddOperand(sf.Value);
                        break;
                    case CExprStmt { Expr: CExternCall ce }:
                        foreach (var arg in ce.Args) AddOperand(arg);
                        break;
                    case CExprStmt { Expr: CInternalCall ci }:
                        foreach (var arg in ci.Args) AddOperand(arg);
                        break;
                    case CLoadField:
                        break;
                }
            }

            switch (block.Terminator)
            {
                case CBranch br:
                    AddOperand(br.Cond);
                    break;
                case CRet ret:
                    if (ret.Value != null) AddOperand(ret.Value);
                    break;
            }
        }

        return used;
    }

    // ========================================================================
    // Copy Propagation (constants only)
    // ========================================================================

    public static void CopyPropagation(CModule module)
    {
        foreach (var func in module.Functions)
            CopyPropFunc(func);
    }

    static void CopyPropFunc(CFunction func)
    {
        if (func.FlatBlocks.Count == 0) return;

        // Count writes per slot
        var writeCounts = new Dictionary<int, int>();
        foreach (var block in func.FlatBlocks)
        {
            foreach (var inst in block.Stmts)
            {
                int? dest = inst switch
                {
                    CAssign m => m.DestSlot,
                    CLoadField lf => lf.DestSlot,
                    CExprStmt { Expr: CExternCall ce } => ce.DestSlot,
                    CExprStmt { Expr: CInternalCall ci } => ci.DestSlot,
                    _ => null,
                };
                if (dest.HasValue)
                {
                    writeCounts.TryGetValue(dest.Value, out var c);
                    writeCounts[dest.Value] = c + 1;
                }
            }
        }

        // Find single-write CAssign with CConst source
        var constMap = new Dictionary<int, CConst>();
        foreach (var block in func.FlatBlocks)
        {
            foreach (var inst in block.Stmts)
            {
                if (inst is CAssign m && m.Value is CConst lc
                    && writeCounts.TryGetValue(m.DestSlot, out var wc) && wc == 1)
                {
                    constMap[m.DestSlot] = lc;
                }
            }
        }

        if (constMap.Count == 0) return;

        // Replace uses
        CValue Subst(CValue op) =>
            op is CSlotRef sr && constMap.TryGetValue(sr.SlotId, out var replacement)
                ? replacement
                : op;

        foreach (var block in func.FlatBlocks)
        {
            for (int i = 0; i < block.Stmts.Count; i++)
            {
                switch (block.Stmts[i])
                {
                    case CAssign m:
                        var newSrc = Subst(m.Value);
                        if (newSrc != m.Value)
                            block.Stmts[i] = new CAssign(m.DestSlot, newSrc);
                        break;
                    case CStoreField sf:
                        var newVal = Subst(sf.Value);
                        if (newVal != sf.Value)
                            block.Stmts[i] = new CStoreField(sf.FieldName, newVal);
                        break;
                    case CExprStmt { Expr: CExternCall ce }:
                        var ceArgs = SubstArgs(ce.Args, Subst);
                        if (ceArgs != null)
                            block.Stmts[i] = new CExprStmt(new CExternCall(ce.Sig, ceArgs, ce.Type, ce.DestSlot));
                        break;
                    case CExprStmt { Expr: CInternalCall ci }:
                        var ciArgs = SubstArgs(ci.Args, Subst);
                        if (ciArgs != null)
                            block.Stmts[i] = new CExprStmt(new CInternalCall(ci.FuncName, ciArgs, ci.Type, ci.DestSlot));
                        break;
                }
            }

            switch (block.Terminator)
            {
                case CBranch br:
                    var newCond = Subst(br.Cond);
                    if (newCond != br.Cond)
                        block.Terminator = new CBranch(newCond, br.TrueBlockId, br.FalseBlockId);
                    break;
                case CRet ret when ret.Value != null:
                    var newRet = Subst(ret.Value);
                    if (newRet != ret.Value)
                        block.Terminator = new CRet(newRet);
                    break;
            }
        }
    }

    /// <summary>Substitute operands in argument list. Returns null if no changes.</summary>
    static List<CValue> SubstArgs(List<CValue> args, Func<CValue, CValue> subst)
    {
        bool any = false;
        var result = new List<CValue>(args.Count);
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
    public static void CoalesceSlots(CModule module)
    {
        foreach (var func in module.Functions)
            CoalesceSlotsFunc(func);
    }

    static void CoalesceSlotsFunc(CFunction func)
    {
        if (func.FlatBlocks.Count == 0 || func.Slots.Count == 0) return;

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
        foreach (var block in func.FlatBlocks)
        {
            for (int i = 0; i < block.Stmts.Count; i++)
                block.Stmts[i] = RemapInst(block.Stmts[i], mapping);

            block.Terminator = RemapTerminator(block.Terminator, mapping);
        }

        // Note: We do NOT remove coalesced slots from func.Slots because
        // LirToUasm indexes into Slots by slot ID (positional). The coalesced-away
        // slots simply won't be referenced by any instruction and GetSlotVar will
        // never be called for them, so no UASM variable will be emitted.
    }

    static (Dictionary<int, int> Written, Dictionary<int, int> LastUsed) ComputeLivenessIntervals(CFunction func)
    {
        var written = new Dictionary<int, int>();
        var lastUsed = new Dictionary<int, int>();

        // Compute RPO ordering
        var rpo = ComputeRPO(func);

        int pos = 0;
        foreach (var block in rpo)
        {
            foreach (var inst in block.Stmts)
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
            foreach (var slotId in GetReadSlotsTerm(block.Terminator))
                lastUsed[slotId] = pos;

            pos++;
        }

        // Extend liveness for loop back-edges.
        // A back-edge is B→H where H has a lower RPO position than B.
        // Any slot alive at the loop header must stay alive through the entire loop body.
        var blockStartPos = new Dictionary<int, int>();
        var blockEndPos = new Dictionary<int, int>();

        int p = 0;
        foreach (var block in rpo)
        {
            blockStartPos[block.Id] = p;
            p += block.Stmts.Count + 1; // +1 for terminator
            blockEndPos[block.Id] = p - 1;
        }

        var rpoIndex = new Dictionary<int, int>();
        for (int i = 0; i < rpo.Count; i++)
            rpoIndex[rpo[i].Id] = i;

        foreach (var block in rpo)
        {
            if (block.Terminator == null) continue;
            foreach (var succId in GetSuccessors(block.Terminator))
            {
                if (!rpoIndex.ContainsKey(succId)) continue;
                if (rpoIndex[succId] <= rpoIndex[block.Id]) // back-edge
                {
                    int headerStart = blockStartPos[succId];
                    int loopEnd = blockEndPos[block.Id];

                    foreach (var slotId in written.Keys.ToList())
                    {
                        int def = written.TryGetValue(slotId, out var d) ? d : int.MaxValue;
                        int last = lastUsed.TryGetValue(slotId, out var u) ? u : -1;

                        if (def <= headerStart && last >= headerStart && last < loopEnd)
                        {
                            lastUsed[slotId] = loopEnd;
                        }
                    }
                }
            }
        }

        return (written, lastUsed);
    }

    static List<CBlock> ComputeRPO(CFunction func)
    {
        var visited = new HashSet<int>();
        var postOrder = new List<CBlock>();
        var blockMap = new Dictionary<int, CBlock>();
        foreach (var b in func.FlatBlocks) blockMap[b.Id] = b;

        void Dfs(int blockId)
        {
            if (!visited.Add(blockId)) return;
            if (!blockMap.TryGetValue(blockId, out var block)) return;
            if (block.Terminator == null) { postOrder.Add(block); return; }
            foreach (var succ in GetSuccessors(block.Terminator))
                Dfs(succ);
            postOrder.Add(block);
        }

        if (func.Entry != null)
            Dfs(func.Entry.Id);

        postOrder.Reverse();
        return postOrder;
    }

    static int? GetWrittenSlot(CStmt inst) => inst switch
    {
        CAssign m => m.DestSlot,
        CLoadField lf => lf.DestSlot,
        CExprStmt { Expr: CExternCall ce } => ce.DestSlot,
        CExprStmt { Expr: CInternalCall ci } => ci.DestSlot,
        _ => null,
    };

    static IEnumerable<int> GetReadSlotsInst(CStmt inst)
    {
        switch (inst)
        {
            case CAssign m:
                if (m.Value is CSlotRef sr) yield return sr.SlotId;
                break;
            case CStoreField sf:
                if (sf.Value is CSlotRef sr2) yield return sr2.SlotId;
                break;
            case CExprStmt { Expr: CExternCall ce }:
                foreach (var arg in ce.Args)
                    if (arg is CSlotRef sr3) yield return sr3.SlotId;
                break;
            case CExprStmt { Expr: CInternalCall ci }:
                foreach (var arg in ci.Args)
                    if (arg is CSlotRef sr4) yield return sr4.SlotId;
                break;
        }
    }

    static IEnumerable<int> GetReadSlotsTerm(CTerminator term)
    {
        switch (term)
        {
            case CBranch br:
                if (br.Cond is CSlotRef sr) yield return sr.SlotId;
                break;
            case CRet ret:
                if (ret.Value is CSlotRef sr2) yield return sr2.SlotId;
                break;
        }
    }

    static CValue RemapOperand(CValue op, Dictionary<int, int> mapping)
    {
        if (op is CSlotRef sr && mapping.TryGetValue(sr.SlotId, out var newId) && newId != sr.SlotId)
            return new CSlotRef(newId, sr.Type);
        return op;
    }

    static int RemapSlotId(int slotId, Dictionary<int, int> mapping)
        => mapping.TryGetValue(slotId, out var newId) ? newId : slotId;

    static int? RemapSlotIdNullable(int? slotId, Dictionary<int, int> mapping)
        => slotId.HasValue ? RemapSlotId(slotId.Value, mapping) : null;

    static List<CValue> RemapArgs(List<CValue> args, Dictionary<int, int> mapping)
    {
        var result = new List<CValue>(args.Count);
        foreach (var arg in args)
            result.Add(RemapOperand(arg, mapping));
        return result;
    }

    static CStmt RemapInst(CStmt inst, Dictionary<int, int> mapping) => inst switch
    {
        CAssign m => new CAssign(RemapSlotId(m.DestSlot, mapping), RemapOperand(m.Value, mapping)),
        CLoadField lf => new CLoadField(RemapSlotId(lf.DestSlot, mapping), lf.FieldName, lf.Type),
        CStoreField sf => new CStoreField(sf.FieldName, RemapOperand(sf.Value, mapping)),
        CExprStmt { Expr: CExternCall ce } => new CExprStmt(new CExternCall(ce.Sig, RemapArgs(ce.Args, mapping), ce.Type, RemapSlotIdNullable(ce.DestSlot, mapping))),
        CExprStmt { Expr: CInternalCall ci } => new CExprStmt(new CInternalCall(ci.FuncName, RemapArgs(ci.Args, mapping), ci.Type, RemapSlotIdNullable(ci.DestSlot, mapping))),
        _ => inst,
    };

    static CTerminator RemapTerminator(CTerminator term, Dictionary<int, int> mapping) => term switch
    {
        CBranch br => new CBranch(RemapOperand(br.Cond, mapping), br.TrueBlockId, br.FalseBlockId),
        CRet ret when ret.Value != null => new CRet(RemapOperand(ret.Value, mapping)),
        _ => term,
    };

    // ========================================================================
    // Helpers
    // ========================================================================

    static Dictionary<int, CBlock> BuildBlockMap(CFunction func)
    {
        var map = new Dictionary<int, CBlock>(func.FlatBlocks.Count);
        foreach (var b in func.FlatBlocks)
            map[b.Id] = b;
        return map;
    }

    static Dictionary<int, List<int>> ComputePredecessors(CFunction func)
    {
        var preds = new Dictionary<int, List<int>>();
        foreach (var block in func.FlatBlocks)
            preds[block.Id] = new List<int>();

        foreach (var block in func.FlatBlocks)
        {
            foreach (var succId in GetSuccessors(block.Terminator))
            {
                if (preds.TryGetValue(succId, out var list))
                    list.Add(block.Id);
            }
        }
        return preds;
    }

    static IEnumerable<int> GetSuccessors(CTerminator term) => term switch
    {
        CJump j => new[] { j.TargetBlockId },
        CBranch b => new[] { b.TrueBlockId, b.FalseBlockId },
        CRet => Array.Empty<int>(),
        _ => Array.Empty<int>(),
    };

    static void RedirectTerminator(CBlock block, int oldTarget, int newTarget)
    {
        switch (block.Terminator)
        {
            case CJump j:
                if (j.TargetBlockId == oldTarget)
                    j.TargetBlockId = newTarget;
                break;
            case CBranch br:
                if (br.TrueBlockId == oldTarget)
                    br.TrueBlockId = newTarget;
                if (br.FalseBlockId == oldTarget)
                    br.FalseBlockId = newTarget;
                break;
        }
    }
}
