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

    // ========================================================================
    // Recursion frame spill / reload (post-coalesce, liveness-aware)
    // ========================================================================

    // Mirror EmitContext.RecurStackId/RecurSpId as literals to avoid an IR→Emit layering dependency.
    const string RecurStackId = "__recurStack";
    const string RecurSpId = "__recurSp";

    /// <summary>
    /// Wrap each recursive-edge internal call with a software-stack spill/reload of the frame values it would
    /// clobber on re-entry: the function's named frame fields (params / frame-locals / receiver, recorded at
    /// emit time) PLUS only the scratch/frame slots LIVE ACROSS that call (computed from the post-coalesce
    /// liveness here). Run AFTER CoalesceSlots so the slot set is the small physical set — under A-normal form
    /// an emit-time total-spill of every (numerous) logical slot overflows the 512-entry recursion stack.
    /// </summary>
    public static void InsertRecursionSpills(CModule module)
    {
        foreach (var func in module.Functions)
            InsertRecursionSpillsFunc(func);
    }

    // Wave-9 round-5 [X4]: spill-temp coalesce trigger. Spill/reload wraps allocate ~10 fresh
    // scratch slots per spilled value per site AFTER CoalesceSlots has run, so a many-site /
    // many-value function can mint hundreds of never-merged heap symbols and push the program past
    // the SDK assembler's 512-entry UdonHeap (VmFault on legal C#). Above this threshold the fresh
    // temps are interval-coalesced among THEMSELVES (identity below it — byte-stable for every
    // pinned shape: nontail_recursion.uasm, the only committed snapshot with __recurStack, has 29
    // __intnl_ vars in total).
    const int SpillTempCoalesceThreshold = 64;

    static void InsertRecursionSpillsFunc(CFunction func)
    {
        // Spill work exists when the function has named recursive callees OR Reentrant-flagged
        // delegate-dispatch sites (design §4.3 — flag count is tracked on the function).
        if ((func.RecursiveCalleeNames.Count == 0 && func.ReentrantSiteCount == 0) || func.FlatBlocks.Count == 0) return;

        var firstSpillSlot = func.Slots.Count; // [X4] every slot from here on is a fresh spill temp

        // PRECISE per-instruction live-out: the slots whose value AFTER an instruction is still read before
        // being overwritten. A single [firstDef,lastUse] interval is wrong here — CoalesceSlots reuses one
        // physical slot for non-overlapping logical values, so its interval can span a call across which it is
        // actually DEAD (its old value consumed, a new value written after). Live-out captures the gap.
        var liveOut = ComputeLiveOutPerInstruction(func);

        foreach (var block in func.FlatBlocks)
        {
            var newStmts = new List<CStmt>(block.Stmts.Count + 8);
            foreach (var inst in block.Stmts)
            {
                if (IsSpillSite(inst, func.RecursiveCalleeNames))
                {
                    // Spill the slots live across the call (live-out) EXCEPT the call's own result slot — that
                    // is written by the call (not clobbered by the recursion), so it must not be saved/restored.
                    var dest = GetWrittenSlot(inst);
                    var liveSlots = new List<SlotDecl>();
                    if (liveOut.TryGetValue(inst, out var lo))
                        foreach (var sid in lo)
                        {
                            if (dest.HasValue && sid == dest.Value) continue;
                            if (sid < 0 || sid >= func.Slots.Count) continue;
                            var slot = func.Slots[sid];
                            if (slot.Class == SlotClass.Pinned) continue;
                            liveSlots.Add(slot);
                        }
                    liveSlots.Sort((a, b) => a.Id.CompareTo(b.Id)); // deterministic spill order

                    EmitSpill(func, newStmts, func.RecursionSpillFields, liveSlots);
                    newStmts.Add(inst);
                    EmitReload(func, newStmts, func.RecursionSpillFields, liveSlots);
                }
                else
                {
                    newStmts.Add(inst);
                }
            }
            block.Stmts.Clear();
            block.Stmts.AddRange(newStmts);
        }

        // [X4]: coalesce the fresh spill temps among themselves when their count crosses the
        // threshold (the restricted pass never touches pre-existing slots — pre-spill code is
        // byte-identical; FlatVerify still runs after, in IrPipeline).
        if (func.Slots.Count - firstSpillSlot > SpillTempCoalesceThreshold)
            CoalesceSlotsFunc(func, firstSpillSlot);
    }

    /// <summary>Per-instruction live-out (slots whose post-instruction value is read before being overwritten),
    /// via standard backward dataflow over the flat CFG to a fixpoint.</summary>
    static Dictionary<CStmt, HashSet<int>> ComputeLiveOutPerInstruction(CFunction func)
    {
        var blockLiveIn = new Dictionary<int, HashSet<int>>();
        foreach (var b in func.FlatBlocks) blockLiveIn[b.Id] = new HashSet<int>();

        HashSet<int> BlockOut(CBlock b)
        {
            var outSet = new HashSet<int>();
            if (b.Terminator != null)
            {
                foreach (var s in GetSuccessors(b.Terminator))
                    if (blockLiveIn.TryGetValue(s, out var li)) outSet.UnionWith(li);
                foreach (var r in GetReadSlotsTerm(b.Terminator)) outSet.Add(r);
            }
            return outSet;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int bi = func.FlatBlocks.Count - 1; bi >= 0; bi--) // reverse order speeds convergence
            {
                var b = func.FlatBlocks[bi];
                var live = BlockOut(b);
                for (int i = b.Stmts.Count - 1; i >= 0; i--)
                {
                    var d = GetWrittenSlot(b.Stmts[i]);
                    if (d.HasValue) live.Remove(d.Value);
                    foreach (var r in GetReadSlotsInst(b.Stmts[i])) live.Add(r);
                }
                if (!blockLiveIn[b.Id].SetEquals(live)) { blockLiveIn[b.Id] = live; changed = true; }
            }
        }

        var result = new Dictionary<CStmt, HashSet<int>>();
        foreach (var b in func.FlatBlocks)
        {
            var live = BlockOut(b);
            for (int i = b.Stmts.Count - 1; i >= 0; i--)
            {
                result[b.Stmts[i]] = new HashSet<int>(live); // live-out of instruction i
                var d = GetWrittenSlot(b.Stmts[i]);
                if (d.HasValue) live.Remove(d.Value);
                foreach (var r in GetReadSlotsInst(b.Stmts[i])) live.Add(r);
            }
        }
        return result;
    }

    // A spill site is a named recursive-edge internal call OR a Reentrant-flagged delegate-dispatch
    // arm (__indirect / SendCustomEvent — design §4.3): both can re-enter the containing function and
    // clobber its frame, so both get the same spill/reload wrap.
    static bool IsSpillSite(CStmt inst, HashSet<string> names)
        => IsRecursiveCall(inst, names) || IsReentrantFlagged(inst);

    // Round-9 [Y3]: a TailSpared instruction is a recursive-edge call SITE in tail position — the
    // frame reads nothing after it, so it is exempt from the per-callee-name wrap (one non-tail
    // site used to make every site of that callee spill, overflowing the stack on deep mixed
    // tail/non-tail recursion; the dispatch arm has always been per-site via Reentrant).
    static bool IsRecursiveCall(CStmt inst, HashSet<string> names) => inst switch
    {
        CExprStmt { Expr: CInternalCall ic } => !ic.TailSpared && names.Contains(ic.FuncName),
        CAssign { Value: CInternalCall ic } => !ic.TailSpared && names.Contains(ic.FuncName),
        _ => false,
    };

    static bool IsReentrantFlagged(CStmt inst) => inst switch
    {
        CExprStmt { Expr: CInternalCall ic } => ic.Reentrant,
        CExprStmt { Expr: CExternCall ec } => ec.Reentrant,
        CAssign { Value: CInternalCall ic } => ic.Reentrant,
        CAssign { Value: CExternCall ec } => ec.Reentrant,
        _ => false,
    };

    // Push order: fields then slots (reload pops in reverse → LIFO balanced).
    static void EmitSpill(CFunction func, List<CStmt> output, List<(string Name, string Type)> fields, List<SlotDecl> slots)
    {
        foreach (var f in fields)
        {
            var t = func.NewSlot(f.Type, SlotClass.Scratch);
            output.Add(new CLoadField(t, f.Name, f.Type));
            SpillValue(func, output, new CSlotRef(t, f.Type));
        }
        foreach (var slot in slots)
            SpillValue(func, output, new CSlotRef(slot.Id, slot.Type));
    }

    static void EmitReload(CFunction func, List<CStmt> output, List<(string Name, string Type)> fields, List<SlotDecl> slots)
    {
        for (int i = slots.Count - 1; i >= 0; i--)
            ReloadValue(func, output, slots[i].Id, slots[i].Type, null);
        for (int i = fields.Count - 1; i >= 0; i--)
            ReloadValue(func, output, -1, fields[i].Type, fields[i].Name);
    }

    static void SpillValue(CFunction func, List<CStmt> output, CLeaf valueLeaf)
    {
        // __recurStack[__recurSp] = value (Udon boxes the typed value into the object[] element); __recurSp++
        var tStack = func.NewSlot("SystemObjectArray", SlotClass.Scratch);
        output.Add(new CLoadField(tStack, RecurStackId, "SystemObjectArray"));
        var tSp = func.NewSlot("SystemInt32", SlotClass.Scratch);
        output.Add(new CLoadField(tSp, RecurSpId, "SystemInt32"));
        output.Add(new CExprStmt(new CExternCall(
            "SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            new List<CLeaf> { new CSlotRef(tStack, "SystemObjectArray"), new CSlotRef(tSp, "SystemInt32"), valueLeaf },
            "SystemVoid")));
        SpDelta(func, output, +1);
    }

    static void ReloadValue(CFunction func, List<CStmt> output, int slotId, string type, string fieldName)
    {
        // __recurSp--; value = __recurStack[__recurSp]  (Udon unboxes the object[] element on typed COPY)
        SpDelta(func, output, -1);
        var tStack = func.NewSlot("SystemObjectArray", SlotClass.Scratch);
        output.Add(new CLoadField(tStack, RecurStackId, "SystemObjectArray"));
        var tSp = func.NewSlot("SystemInt32", SlotClass.Scratch);
        output.Add(new CLoadField(tSp, RecurSpId, "SystemInt32"));
        var tGet = func.NewSlot("SystemObject", SlotClass.Scratch);
        output.Add(new CExprStmt(new CExternCall(
            "SystemObjectArray.__Get__SystemInt32__SystemObject",
            new List<CLeaf> { new CSlotRef(tStack, "SystemObjectArray"), new CSlotRef(tSp, "SystemInt32") },
            "SystemObject", tGet)));
        if (fieldName != null)
            output.Add(new CStoreField(fieldName, new CSlotRef(tGet, "SystemObject")));
        else
            output.Add(new CAssign(slotId, new CSlotRef(tGet, "SystemObject")));
    }

    static void SpDelta(CFunction func, List<CStmt> output, int delta)
    {
        var tSp = func.NewSlot("SystemInt32", SlotClass.Scratch);
        output.Add(new CLoadField(tSp, RecurSpId, "SystemInt32"));
        var tNew = func.NewSlot("SystemInt32", SlotClass.Scratch);
        var sig = delta >= 0
            ? "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32"
            : "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32";
        output.Add(new CExprStmt(new CExternCall(sig,
            new List<CLeaf> { new CSlotRef(tSp, "SystemInt32"), new CConst(System.Math.Abs(delta), "SystemInt32") },
            "SystemInt32", tNew)));
        output.Add(new CStoreField(RecurSpId, new CSlotRef(tNew, "SystemInt32")));
    }

    /// <summary>Interval-coalesce a function's slots. <paramref name="minSlotId"/> restricts the
    /// pass to slots with Id ≥ minSlotId ([X4]: the post-spill run merges only the fresh spill
    /// temps among themselves; the default 0 is the full pre-spill pass).</summary>
    static void CoalesceSlotsFunc(CFunction func, int minSlotId = 0)
    {
        if (func.FlatBlocks.Count == 0 || func.Slots.Count == 0) return;

        // Step 1: Linearize instructions and compute liveness intervals
        var (written, lastUsed) = ComputeLivenessIntervals(func);

        // Collect coalesceable slots (Scratch or Frame, not Pinned)
        var coalesceable = new List<SlotDecl>();
        foreach (var slot in func.Slots)
        {
            if (slot.Class == SlotClass.Pinned) continue;
            if (slot.Id < minSlotId) continue; // [X4] restricted post-spill run
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

        // Step 4: Rewrite all instructions and terminators. Drop self-copies (CAssign s = s) that arise when
        // a copy's source and destination coalesce to the same physical slot — A-normal form's binding temps
        // (t = producer; slot = t) become slot = slot once t≡slot, a no-op that must be removed.
        foreach (var block in func.FlatBlocks)
        {
            var rewritten = new List<CStmt>(block.Stmts.Count);
            foreach (var stmt in block.Stmts)
            {
                var r = RemapInst(stmt, mapping);
                if (r is CAssign ca && ca.Value is CSlotRef csr && csr.SlotId == ca.DestSlot)
                    continue; // self-copy after coalescing — drop
                rewritten.Add(r);
            }
            block.Stmts.Clear();
            block.Stmts.AddRange(rewritten);

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

    // Leaf-typed remap: a leaf remaps to a leaf (slot rename), preserving the CLeaf type for ANF operand positions.
    static CLeaf RemapLeaf(CLeaf op, Dictionary<int, int> mapping)
    {
        if (op is CSlotRef sr && mapping.TryGetValue(sr.SlotId, out var newId) && newId != sr.SlotId)
            return new CSlotRef(newId, sr.Type);
        return op;
    }

    static int RemapSlotId(int slotId, Dictionary<int, int> mapping)
        => mapping.TryGetValue(slotId, out var newId) ? newId : slotId;

    static int? RemapSlotIdNullable(int? slotId, Dictionary<int, int> mapping)
        => slotId.HasValue ? RemapSlotId(slotId.Value, mapping) : null;

    static List<CLeaf> RemapArgs(List<CLeaf> args, Dictionary<int, int> mapping)
    {
        var result = new List<CLeaf>(args.Count);
        foreach (var arg in args)
            result.Add(RemapLeaf(arg, mapping));
        return result;
    }

    static CStmt RemapInst(CStmt inst, Dictionary<int, int> mapping) => inst switch
    {
        CAssign m => new CAssign(RemapSlotId(m.DestSlot, mapping), RemapOperand(m.Value, mapping)),
        CLoadField lf => new CLoadField(RemapSlotId(lf.DestSlot, mapping), lf.FieldName, lf.Type),
        CStoreField sf => new CStoreField(sf.FieldName, RemapLeaf(sf.Value, mapping)),
        // Reentrant (and round-9 [Y3] TailSpared) MUST be copied: this rebuild is the second
        // flag-killing reconstruction site (with CoreFlatten.LowerExpr — design §4.3); FlatVerify
        // checks Reentrant conservation after the pass.
        CExprStmt { Expr: CExternCall ce } => new CExprStmt(new CExternCall(ce.Sig, RemapArgs(ce.Args, mapping), ce.Type, RemapSlotIdNullable(ce.DestSlot, mapping), ce.Reentrant)),
        CExprStmt { Expr: CInternalCall ci } => new CExprStmt(new CInternalCall(ci.FuncName, RemapArgs(ci.Args, mapping), ci.Type, RemapSlotIdNullable(ci.DestSlot, mapping), ci.Reentrant, ci.TailSpared)),
        _ => inst,
    };

    static CTerminator RemapTerminator(CTerminator term, Dictionary<int, int> mapping) => term switch
    {
        CBranch br => new CBranch(RemapLeaf(br.Cond, mapping), br.TrueBlockId, br.FalseBlockId),
        CRet ret when ret.Value != null => new CRet(RemapLeaf(ret.Value, mapping)),
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
