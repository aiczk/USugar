using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Flat Core IR optimization. Only slot coalescing remains: measurement showed it delivers the entire
/// heap-variable reduction, while the former CFG-simplify / DCE / copy-propagation passes changed neither
/// EXTERN count nor runtime cost on real code (Udon is EXTERN-bound), so they were removed.
/// </summary>
public static class CoreFlatOptimizer
{
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
        // CoreToUasm indexes into Slots by slot ID (positional). The coalesced-away
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


    static IEnumerable<int> GetSuccessors(CTerminator term) => term switch
    {
        CJump j => new[] { j.TargetBlockId },
        CBranch b => new[] { b.TrueBlockId, b.FalseBlockId },
        CRet => Array.Empty<int>(),
        _ => Array.Empty<int>(),
    };

}
