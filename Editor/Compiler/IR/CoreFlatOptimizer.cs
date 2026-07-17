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

    const string RecurStackId = RecurStack.StackId;
    const string RecurSpId = RecurStack.SpId;

    /// <summary>
    /// Wrap each recursive-edge internal call with a software-stack spill/reload of the frame values it would
    /// clobber on re-entry: the function's named frame fields (params / frame-locals / receiver, recorded at
    /// emit time) PLUS only the scratch/frame slots LIVE ACROSS that call (computed from the post-coalesce
    /// liveness here). Run AFTER CoalesceSlots so the slot set is the small physical set — under A-normal form
    /// an emit-time total-spill of every (numerous) logical slot overflows the 8192-entry recursion stack.
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

                    // Wave-12 r2 [V1]: a reentrant cross-dispatch SendCustomEvent carries its param
                    // copy-ins (SetProgramVariable stmts emitted immediately before it, same block by
                    // construction) INSIDE the spill window — a same-program reentrant callee shares
                    // the caller's param heap vars, so a copy-in preceding the save would be captured
                    // post-clobber and the reload would restore the clobbered value. The copy-ins
                    // write no slots (void externs), so the liveness computed at the flagged
                    // instruction is unchanged at the earlier save point.
                    int preWindow = PreSpillStmtCount(inst);
                    if (preWindow > 0)
                    {
                        var saveStmts = new List<CStmt>();
                        EmitSpill(func, saveStmts, func.RecursionSpillFields, liveSlots);
                        newStmts.InsertRange(Math.Max(newStmts.Count - preWindow, 0), saveStmts);
                    }
                    else
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

    /// <summary>Live-out of a block: the union of its successors' live-in, plus the terminator's own reads.
    /// Shared by <see cref="ComputeBlockLiveIn"/> (fixpoint) and <see cref="ComputeLiveOutPerInstruction"/>
    /// (one more pass over the converged result).</summary>
    static HashSet<int> BlockOut(CBlock b, Dictionary<int, HashSet<int>> blockLiveIn)
    {
        var outSet = new HashSet<int>();
        if (b.Terminator != null)
        {
            foreach (var s in CTerminator.GetSuccessors(b.Terminator))
                if (blockLiveIn.TryGetValue(s, out var li)) outSet.UnionWith(li);
            foreach (var r in GetReadSlotsTerm(b.Terminator)) outSet.Add(r);
        }
        return outSet;
    }

    /// <summary>Per-block live-in via standard backward dataflow over the flat CFG to a fixpoint — the one
    /// exact liveness the whole pass trusts. Computed once per <see cref="CoalesceSlotsFunc"/> call and
    /// shared by <see cref="ComputeLivenessIntervals"/> (interval derivation) and
    /// <see cref="VerifyNoInterference"/> (the checker), and recomputed independently by
    /// <see cref="InsertRecursionSpillsFunc"/> in its later post-coalesce phase.</summary>
    static Dictionary<int, HashSet<int>> ComputeBlockLiveIn(CFunction func)
    {
        var blockLiveIn = new Dictionary<int, HashSet<int>>();
        foreach (var b in func.FlatBlocks) blockLiveIn[b.Id] = new HashSet<int>();

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int bi = func.FlatBlocks.Count - 1; bi >= 0; bi--) // reverse order speeds convergence
            {
                var b = func.FlatBlocks[bi];
                var live = BlockOut(b, blockLiveIn);
                for (int i = b.Stmts.Count - 1; i >= 0; i--)
                {
                    var d = GetWrittenSlot(b.Stmts[i]);
                    if (d.HasValue) live.Remove(d.Value);
                    foreach (var r in GetReadSlotsInst(b.Stmts[i])) live.Add(r);
                }
                if (!blockLiveIn[b.Id].SetEquals(live)) { blockLiveIn[b.Id] = live; changed = true; }
            }
        }
        return blockLiveIn;
    }

    /// <summary>Per-instruction live-out (slots whose post-instruction value is read before being overwritten),
    /// via standard backward dataflow over the flat CFG to a fixpoint.</summary>
    static Dictionary<CStmt, HashSet<int>> ComputeLiveOutPerInstruction(CFunction func, Dictionary<int, HashSet<int>> blockLiveIn = null)
    {
        blockLiveIn ??= ComputeBlockLiveIn(func);

        var result = new Dictionary<CStmt, HashSet<int>>();
        foreach (var b in func.FlatBlocks)
        {
            var live = BlockOut(b, blockLiveIn);
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
    internal static bool IsRecursiveCall(CStmt inst, HashSet<string> names) => inst switch
    {
        CExprStmt { Expr: CInternalCall ic } => !ic.TailSpared && names.Contains(ic.FuncName),
        CAssign { Value: CInternalCall ic } => !ic.TailSpared && names.Contains(ic.FuncName),
        CExprStmt { Expr: CExternCall } => false, // extern call — never an internal recursive edge
        CAssign => false,                         // leaf-valued copy
        CStoreField => false,
        CLoadField => false,
        _ => throw new VerificationException(
            $"Unknown flat CStmt kind in CoreFlatOptimizer.IsRecursiveCall: {StmtKind(inst)}"),
    };

    internal static bool IsReentrantFlagged(CStmt inst) => inst switch
    {
        CExprStmt { Expr: CInternalCall ic } => ic.Reentrant,
        CExprStmt { Expr: CExternCall ec } => ec.Reentrant,
        CAssign { Value: CInternalCall ic } => ic.Reentrant,
        CAssign { Value: CExternCall ec } => ec.Reentrant,
        CAssign => false, // leaf-valued copy
        CStoreField => false,
        CLoadField => false,
        _ => throw new VerificationException(
            $"Unknown flat CStmt kind in CoreFlatOptimizer.IsReentrantFlagged: {StmtKind(inst)}"),
    };

    // Wave-12 r2 [V1]: statements to pull inside the spill window ahead of a flagged cross-dispatch
    // SendCustomEvent (its SetProgramVariable copy-ins — see CExternCall.PreSpillStmts).
    internal static int PreSpillStmtCount(CStmt inst) => inst switch
    {
        CExprStmt { Expr: CExternCall ec } => ec.PreSpillStmts,
        CExprStmt { Expr: CInternalCall } => 0, // pre-spill window is a cross-dispatch extern concept
        CAssign => 0,
        CStoreField => 0,
        CLoadField => 0,
        _ => throw new VerificationException(
            $"Unknown flat CStmt kind in CoreFlatOptimizer.PreSpillStmtCount: {StmtKind(inst)}"),
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
            ExternResolver.BuildArraySetSignature("SystemObjectArray", "SystemObject"),
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
            ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
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

        // Step 1: exact per-block liveness (fixpoint) — computed ONCE and shared by both the interval
        // derivation below and the independent VerifyNoInterference checker (both consume the same
        // liveness now, so the former's two-fixpoints-per-pass redundancy is gone).
        var blockLiveIn = ComputeBlockLiveIn(func);
        var (written, lastUsed) = ComputeLivenessIntervals(func, blockLiveIn);

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

        // Fast approximate allocator + sound independent checker: the interval coloring above assumes an
        // unasserted block-ordering fact of this compiler's own loop lowering. Recompute liveness properly
        // (fixpoint dataflow, not a linearized interval) and reject the merge map before it is ever applied.
        VerifyNoInterference(func, mapping, blockLiveIn);

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

    /// <summary>Interference check for a proposed slot-coalescing merge map, run BEFORE the map is applied.
    /// The interval coloring in <see cref="CoalesceSlotsFunc"/> compares linearized [start,end] intervals;
    /// this checks the finer per-instruction set membership (<see cref="ComputeLiveOutPerInstruction"/>) and
    /// asserts no two DISTINCT source slots mapped to the same physical slot are ever simultaneously live.
    /// Independence note: the intervals are now DERIVED from this same fixpoint liveness (shared
    /// <paramref name="blockLiveIn"/>), so this is no longer an independent re-derivation guarding an
    /// approximation — it guards the interval-derivation code. The intervals are a strict superset of the
    /// per-instruction live sets (every slot in liveOut(p) has p inside its interval), so this checker can
    /// never fire on a map the derivation produces; if it DOES, that is a derivation bug. Internal (not
    /// private) so tests can drive it directly with a hand-built bad mapping.
    /// Self-shadowing note (audit 2026-07-17 ①): this checker consumes the same
    /// GetWrittenSlot / GetReadSlotsInst / GetReadSlotsTerm / CTerminator.GetSuccessors family the
    /// intervals are built from, so a silently-defaulting arm there used to blind BOTH sides at once —
    /// a new flat kind read/wrote nothing, the intervals shrank, and the checker agreed. Those switches
    /// now throw <see cref="VerificationException"/> on any unknown kind (pinned by
    /// FlatRoleSwitchCensusTests), so the shared-enumeration gap is loud before either side can be wrong.</summary>
    internal static void VerifyNoInterference(CFunction func, Dictionary<int, int> mapping, Dictionary<int, HashSet<int>> blockLiveIn = null)
    {
        if (mapping.Count == 0) return;

        var liveOut = ComputeLiveOutPerInstruction(func, blockLiveIn);
        foreach (var live in liveOut.Values)
        {
            var physicalToSource = new Dictionary<int, int>();
            foreach (var slotId in live)
            {
                int physical = mapping.TryGetValue(slotId, out var np) ? np : slotId;
                if (physicalToSource.TryGetValue(physical, out var otherSlot))
                {
                    if (otherSlot != slotId)
                        throw new InvalidOperationException(
                            $"CoalesceSlots interference violation in {func.Name}: slot{otherSlot} and " +
                            $"slot{slotId} are simultaneously live but both would be coalesced to slot{physical}");
                }
                else
                {
                    physicalToSource[physical] = slotId;
                }
            }
        }
    }

    /// <summary>Each coalesceable slot's [start,end] interval over the RPO linearization, derived DIRECTLY
    /// from the exact fixpoint liveness (<paramref name="blockLiveIn"/>) instead of an RPO-linear
    /// first-write/last-use approximation. The interval spans every linearized position at which the slot's
    /// value matters: where it is defined, where it is read, and every position it is live-OUT of. Two
    /// former correction patches fall out for free: a slot used before any def is live-in from block entry
    /// (start pulls back naturally — the old B39 pullback for a GetComponent-style unassigned fallthrough),
    /// and a loop-carried slot is live-out across the back-edge latch terminator (end extends naturally —
    /// the old back-edge extension). Every slot in liveOut(p) has p inside its interval, so the derived
    /// intervals are a strict superset of the per-instruction live sets <see cref="VerifyNoInterference"/>
    /// checks — that checker can therefore never fire on a map built from these intervals.</summary>
    static (Dictionary<int, int> Written, Dictionary<int, int> LastUsed) ComputeLivenessIntervals(
        CFunction func, Dictionary<int, HashSet<int>> blockLiveIn)
    {
        var rpo = ComputeRPO(func);

        // Positional scheme identical to the flat CFG's: each statement one position, the terminator one
        // position after the block's statements.
        var blockStartPos = new Dictionary<int, int>();
        int p = 0;
        foreach (var block in rpo)
        {
            blockStartPos[block.Id] = p;
            p += block.Stmts.Count + 1;
        }

        var start = new Dictionary<int, int>();
        var end = new Dictionary<int, int>();
        void Mark(int slot, int pos)
        {
            if (!start.TryGetValue(slot, out var s) || pos < s) start[slot] = pos;
            if (!end.TryGetValue(slot, out var e) || pos > e) end[slot] = pos;
        }

        foreach (var block in rpo)
        {
            int basePos = blockStartPos[block.Id];
            int termPos = basePos + block.Stmts.Count;

            // live-out of the terminator = union of successor block live-ins.
            var live = new HashSet<int>();
            if (block.Terminator != null)
                foreach (var succ in CTerminator.GetSuccessors(block.Terminator))
                    if (blockLiveIn.TryGetValue(succ, out var li)) live.UnionWith(li);

            foreach (var slot in live) Mark(slot, termPos);        // live across the terminator
            foreach (var r in GetReadSlotsTerm(block.Terminator))  // terminator's own reads (branch/ret)
            {
                Mark(r, termPos);
                live.Add(r);
            }
            // live is now live-in of the terminator = live-out of the last statement.

            for (int i = block.Stmts.Count - 1; i >= 0; i--)
            {
                int pos = basePos + i;
                foreach (var slot in live) Mark(slot, pos);        // slot live-OUT of statement i
                var d = GetWrittenSlot(block.Stmts[i]);
                if (d.HasValue) { Mark(d.Value, pos); live.Remove(d.Value); }
                foreach (var r in GetReadSlotsInst(block.Stmts[i])) { Mark(r, pos); live.Add(r); }
            }
        }

        return (start, end);
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
            foreach (var succ in CTerminator.GetSuccessors(block.Terminator))
                Dfs(succ);
            postOrder.Add(block);
        }

        if (func.Entry != null)
            Dfs(func.Entry.Id);

        postOrder.Reverse();
        return postOrder;
    }

    // The switches below are EXHAUSTIVE over the flat-role kinds (FlatVerify.VerifyInstruction /
    // VerifyTerminator's arms are the domain): kinds that legitimately read/write/remap NOTHING get
    // explicit arms, and the default throws — a silently-defaulting arm here corrupts liveness, and
    // VerifyNoInterference (derived from these same functions) would stay green while it happened.

    internal static int? GetWrittenSlot(CStmt inst) => inst switch
    {
        CAssign m => m.DestSlot,
        CLoadField lf => lf.DestSlot,
        CStoreField => null, // writes a heap field, never a slot
        CExprStmt { Expr: CExternCall ce } => ce.DestSlot,
        CExprStmt { Expr: CInternalCall ci } => ci.DestSlot,
        _ => throw new VerificationException(
            $"Unknown flat CStmt kind in CoreFlatOptimizer.GetWrittenSlot: {StmtKind(inst)}"),
    };

    internal static IEnumerable<int> GetReadSlotsInst(CStmt inst)
    {
        switch (inst)
        {
            case CAssign m:
                if (m.Value is CSlotRef sr) yield return sr.SlotId;
                break;
            case CStoreField sf:
                if (sf.Value is CSlotRef sr2) yield return sr2.SlotId;
                break;
            case CLoadField:
                break; // reads a heap field, never a slot
            case CExprStmt { Expr: CExternCall ce }:
                foreach (var arg in ce.Args)
                    if (arg is CSlotRef sr3) yield return sr3.SlotId;
                break;
            case CExprStmt { Expr: CInternalCall ci }:
                foreach (var arg in ci.Args)
                    if (arg is CSlotRef sr4) yield return sr4.SlotId;
                break;
            default:
                throw new VerificationException(
                    $"Unknown flat CStmt kind in CoreFlatOptimizer.GetReadSlotsInst: {StmtKind(inst)}");
        }
    }

    internal static IEnumerable<int> GetReadSlotsTerm(CTerminator term)
    {
        switch (term)
        {
            case CJump:
                break; // no slot operands
            case CBranch br:
                if (br.Cond is CSlotRef sr) yield return sr.SlotId;
                break;
            case CRet ret:
                if (ret.Value is CSlotRef sr2) yield return sr2.SlotId;
                break;
            default:
                throw new VerificationException(
                    $"Unknown CTerminator kind in CoreFlatOptimizer.GetReadSlotsTerm: {TermKind(term)}");
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

    internal static CStmt RemapInst(CStmt inst, Dictionary<int, int> mapping) => inst switch
    {
        CAssign m => new CAssign(RemapSlotId(m.DestSlot, mapping), RemapOperand(m.Value, mapping)),
        CLoadField lf => new CLoadField(RemapSlotId(lf.DestSlot, mapping), lf.FieldName, lf.Type),
        CStoreField sf => new CStoreField(sf.FieldName, RemapLeaf(sf.Value, mapping)),
        // Reentrant (and round-9 [Y3] TailSpared) MUST be copied: this rebuild is the second
        // flag-killing reconstruction site (with CoreFlatten.LowerExpr — design §4.3); FlatVerify
        // checks Reentrant conservation after the pass.
        CExprStmt { Expr: CExternCall ce } => new CExprStmt(ce.With(RemapArgs(ce.Args, mapping), RemapSlotIdNullable(ce.DestSlot, mapping))),
        CExprStmt { Expr: CInternalCall ci } => new CExprStmt(ci.With(RemapArgs(ci.Args, mapping), RemapSlotIdNullable(ci.DestSlot, mapping))),
        _ => throw new VerificationException(
            $"Unknown flat CStmt kind in CoreFlatOptimizer.RemapInst: {StmtKind(inst)}"),
    };

    internal static CTerminator RemapTerminator(CTerminator term, Dictionary<int, int> mapping) => term switch
    {
        CJump => term, // no slot operands
        CBranch br => new CBranch(RemapLeaf(br.Cond, mapping), br.TrueBlockId, br.FalseBlockId),
        CRet ret when ret.Value != null => new CRet(RemapLeaf(ret.Value, mapping)),
        CRet => term,  // void return
        _ => throw new VerificationException(
            $"Unknown CTerminator kind in CoreFlatOptimizer.RemapTerminator: {TermKind(term)}"),
    };

    // Kind naming for the loud defaults above (mirrors CoreVerify's "Unknown CStmt type" voice; a
    // CExprStmt names its payload too, since the flat arms discriminate on it).
    static string StmtKind(CStmt inst) => inst switch
    {
        null => "null",
        CExprStmt es => $"CExprStmt({es.Expr.GetType().Name})",
        _ => inst.GetType().Name,
    };

    static string TermKind(CTerminator term) => term == null ? "null" : term.GetType().Name;

}
