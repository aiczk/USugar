using System;
using System.Collections.Generic;
using System.Linq;

public class ControlFlowGraph
{
    public List<BasicBlock> Blocks = new();
    public BasicBlock Entry;
    public BasicBlock Exit;
    public Dictionary<string, int> VarToIndex = new();
    public string[] IndexToVar;
    Dictionary<int, BasicBlock> _blockById = new();
    int _varCount;
    Dictionary<string, string> _varTypes = new();
    Dictionary<string, object> _constValues = new();

    public static ControlFlowGraph Build(UasmModule module)
    {
        var insts = module.GetInstructions();
        var labels = module.GetLabels();
        var cfg = new ControlFlowGraph();
        if (insts.Count == 0) { cfg.InitEmpty(); return cfg; }

        // 1. Build label → instruction index map (first occurrence per label)
        var labelToInstIdx = new Dictionary<int, int>();
        for (int i = 0; i < insts.Count; i++)
            if (insts[i].Kind is InstKind.Label or InstKind.Export or InstKind.LabelAddr
                && insts[i].LabelIndex >= 0
                && !labelToInstIdx.ContainsKey(insts[i].LabelIndex))
                labelToInstIdx[insts[i].LabelIndex] = i;

        // 2. Find leaders
        var leaders = new HashSet<int> { 0 };
        for (int i = 0; i < insts.Count; i++)
        {
            if (InstInfo.IsJump(insts[i].Kind))
            {
                if (i + 1 < insts.Count) leaders.Add(i + 1);
                if (insts[i].Kind != InstKind.JumpIndirect
                    && labelToInstIdx.TryGetValue(insts[i].LabelIndex, out var target))
                    leaders.Add(target);
            }
            // Export = Udon event entry point (_start, _update, etc.).
            // The VM jumps directly to this address, so it must be a block leader
            // to keep control flow edges accurate.
            if (insts[i].Kind == InstKind.Export)
                leaders.Add(i);
            // PushLabel pushes a return address for JUMP_INDIRECT (simulated call/return).
            // The target label is a potential indirect jump destination, so it must start
            // its own block — otherwise liveness analysis would miss the incoming edge.
            if (insts[i].Kind == InstKind.PushLabel
                && labelToInstIdx.TryGetValue(insts[i].LabelIndex, out var plTarget))
                leaders.Add(plTarget);
        }

        // 3. Create blocks
        var sortedLeaders = leaders.OrderBy(x => x).ToList();
        var instIdxToBlock = new Dictionary<int, BasicBlock>();
        for (int b = 0; b < sortedLeaders.Count; b++)
        {
            var block = new BasicBlock { Id = b };
            int start = sortedLeaders[b];
            int end = (b + 1 < sortedLeaders.Count) ? sortedLeaders[b + 1] : insts.Count;
            for (int i = start; i < end; i++)
                block.Instructions.Add(insts[i]);
            cfg.Blocks.Add(block);
            instIdxToBlock[start] = block;
        }

        // Virtual exit block
        cfg.Exit = new BasicBlock { Id = cfg.Blocks.Count };

        // 4. Connect edges
        // Collect JumpIndirect targets: exports + PushLabel targets
        var jumpIndirectTargets = new List<BasicBlock>();
        foreach (var block in cfg.Blocks)
            if (block.Instructions.Any(inst => inst.Kind == InstKind.Export))
                jumpIndirectTargets.Add(block);

        // PushLabel targets are also JumpIndirect destinations (return addresses)
        var pushLabelIndices = new HashSet<int>();
        for (int i = 0; i < insts.Count; i++)
            if (insts[i].Kind == InstKind.PushLabel)
                pushLabelIndices.Add(insts[i].LabelIndex);
        foreach (var lblIdx in pushLabelIndices)
        {
            if (!labelToInstIdx.TryGetValue(lblIdx, out var instIdx)) continue;
            var targetBlock = instIdxToBlock.TryGetValue(instIdx, out var tb)
                ? tb
                : FindContainingBlock(cfg.Blocks, sortedLeaders, instIdx);
            if (!jumpIndirectTargets.Contains(targetBlock))
                jumpIndirectTargets.Add(targetBlock);
        }

        for (int b = 0; b < cfg.Blocks.Count; b++)
        {
            var block = cfg.Blocks[b];
            var lastReal = block.Instructions.FindLastIndex(
                inst => InstInfo.Size[inst.Kind] > 0);
            if (lastReal < 0)
            {
                // Block has only metadata — fall through
                if (b + 1 < cfg.Blocks.Count)
                    AddEdge(block, cfg.Blocks[b + 1]);
                continue;
            }

            var lastInst = block.Instructions[lastReal];
            switch (lastInst.Kind)
            {
                case InstKind.Jump:
                    if (labelToInstIdx.TryGetValue(lastInst.LabelIndex, out var jt)
                        && instIdxToBlock.TryGetValue(jt, out var jtBlock))
                        AddEdge(block, jtBlock);
                    else if (labelToInstIdx.TryGetValue(lastInst.LabelIndex, out var jt2))
                        AddEdge(block, FindContainingBlock(cfg.Blocks, sortedLeaders, jt2));
                    break;

                case InstKind.JumpIfFalse:
                    if (labelToInstIdx.TryGetValue(lastInst.LabelIndex, out var ft)
                        && instIdxToBlock.TryGetValue(ft, out var ftBlock))
                        AddEdge(block, ftBlock);
                    else if (labelToInstIdx.TryGetValue(lastInst.LabelIndex, out var ft2))
                        AddEdge(block, FindContainingBlock(cfg.Blocks, sortedLeaders, ft2));
                    // True branch: fall through
                    if (b + 1 < cfg.Blocks.Count)
                        AddEdge(block, cfg.Blocks[b + 1]);
                    break;

                case InstKind.JumpIndirect:
                    foreach (var target in jumpIndirectTargets)
                        AddEdge(block, target);
                    AddEdge(block, cfg.Exit);
                    break;

                default:
                    if (b + 1 < cfg.Blocks.Count)
                        AddEdge(block, cfg.Blocks[b + 1]);
                    else
                        AddEdge(block, cfg.Exit);
                    break;
            }
        }

        cfg.Entry = cfg.Blocks[0];
        cfg.RebuildBlockIndex();
        cfg.BuildVarIndex(insts);
        cfg._varTypes = module.GetVariableTypes();
        cfg._constValues = module.GetConstValues();
        return cfg;
    }

    void RebuildBlockIndex()
    {
        _blockById.Clear();
        foreach (var b in Blocks)
            _blockById[b.Id] = b;
    }

    static void AddEdge(BasicBlock from, BasicBlock to)
    {
        if (to == null) return;
        if (!from.Successors.Contains(to)) from.Successors.Add(to);
        if (!to.Predecessors.Contains(from)) to.Predecessors.Add(from);
    }

    static void RemoveEdge(BasicBlock from, BasicBlock to)
    {
        from.Successors.Remove(to);
        to.Predecessors.Remove(from);
    }

    public void SimplifyCFG()
    {
        bool changed;
        do
        {
            changed = false;
            changed |= ThreadJumps();
            changed |= RemoveEmptyBlocks();
            changed |= MergeBlocks();
            changed |= RemoveUnreachableBlocks();
        } while (changed);
        RebuildBlockIndex();
    }

    bool ThreadJumps()
    {
        bool changed = false;
        foreach (var block in Blocks)
        {
            var lastReal = GetLastRealInstruction(block);
            if (lastReal < 0) continue;
            var inst = block.Instructions[lastReal];
            if (inst.Kind != InstKind.Jump) continue;

            var target = block.Successors.Count == 1 ? block.Successors[0] : null;
            if (target == null || target == Exit) continue;

            var finalTarget = ResolveJumpChain(target);
            if (finalTarget == target) continue;

            var targetLabel = GetBlockLabel(finalTarget);
            if (targetLabel >= 0)
            {
                RemoveEdge(block, target);
                AddEdge(block, finalTarget);
                inst.LabelIndex = targetLabel;
                block.Instructions[lastReal] = inst;
                changed = true;
            }
        }
        return changed;
    }

    bool RemoveEmptyBlocks()
    {
        bool changed = false;
        for (int i = Blocks.Count - 1; i >= 0; i--)
        {
            var block = Blocks[i];
            if (block == Entry) continue;
            if (block.Instructions.Any(inst => InstInfo.Size[inst.Kind] > 0)) continue;
            if (block.Successors.Count != 1) continue;

            var succ = block.Successors[0];
            // Preserve zero-size instructions (LabelAddr etc.) by moving to successor
            succ.Instructions.InsertRange(0, block.Instructions);
            foreach (var pred in block.Predecessors.ToList())
                RedirectEdge(pred, block, succ);
            RemoveBlock(block);
            changed = true;
        }
        return changed;
    }

    bool MergeBlocks()
    {
        bool changed = false;
        for (int i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            if (block.Successors.Count != 1) continue;
            var succ = block.Successors[0];
            if (succ == Exit) continue;
            if (succ.Predecessors.Count != 1) continue;
            if (succ == Entry) continue;

            // Remove trailing Jump from block if present
            var lastReal = GetLastRealInstruction(block);
            if (lastReal >= 0 && block.Instructions[lastReal].Kind == InstKind.Jump)
                block.Instructions.RemoveAt(lastReal);

            block.Instructions.AddRange(succ.Instructions);
            block.Successors.Clear();
            foreach (var s in succ.Successors)
            {
                block.Successors.Add(s);
                s.Predecessors.Remove(succ);
                s.Predecessors.Add(block);
            }
            Blocks.Remove(succ);
            changed = true;
            i--;
        }
        return changed;
    }

    bool RemoveUnreachableBlocks()
    {
        var reachable = new HashSet<int>();
        var queue = new Queue<BasicBlock>();
        queue.Enqueue(Entry);
        reachable.Add(Entry.Id);
        while (queue.Count > 0)
        {
            var b = queue.Dequeue();
            foreach (var s in b.Successors)
            {
                if (s == Exit) continue;
                if (reachable.Add(s.Id))
                    queue.Enqueue(s);
            }
        }
        var removed = Blocks.RemoveAll(b => !reachable.Contains(b.Id));
        if (removed > 0)
            foreach (var b in Blocks)
                b.Predecessors.RemoveAll(p => !reachable.Contains(p.Id));
        return removed > 0;
    }

    static int GetLastRealInstruction(BasicBlock block)
    {
        for (int i = block.Instructions.Count - 1; i >= 0; i--)
            if (InstInfo.Size[block.Instructions[i].Kind] > 0) return i;
        return -1;
    }

    BasicBlock ResolveJumpChain(BasicBlock target)
    {
        var visited = new HashSet<int>();
        var current = target;
        while (visited.Add(current.Id))
        {
            if (current.Instructions.All(i => InstInfo.IsZeroSize(i.Kind) || i.Kind == InstKind.Jump)
                && current.Successors.Count == 1 && current.Successors[0] != Exit)
                current = current.Successors[0];
            else
                break;
        }
        return current;
    }

    static int GetBlockLabel(BasicBlock block)
    {
        foreach (var inst in block.Instructions)
            if (inst.Kind is InstKind.Label or InstKind.Export or InstKind.LabelAddr)
                return inst.LabelIndex;
        return -1;
    }

    void RedirectEdge(BasicBlock pred, BasicBlock oldTarget, BasicBlock newTarget)
    {
        pred.Successors.Remove(oldTarget);
        if (!pred.Successors.Contains(newTarget)) pred.Successors.Add(newTarget);
        oldTarget.Predecessors.Remove(pred);
        if (!newTarget.Predecessors.Contains(pred)) newTarget.Predecessors.Add(pred);

        var label = GetBlockLabel(newTarget);
        if (label < 0) return;
        var oldLabel = GetBlockLabel(oldTarget);
        for (int i = 0; i < pred.Instructions.Count; i++)
        {
            var inst = pred.Instructions[i];
            if (inst.Kind is InstKind.Jump or InstKind.JumpIfFalse
                && inst.LabelIndex == oldLabel)
            {
                inst.LabelIndex = label;
                pred.Instructions[i] = inst;
            }
        }
    }

    void RemoveBlock(BasicBlock block)
    {
        foreach (var s in block.Successors) s.Predecessors.Remove(block);
        foreach (var p in block.Predecessors) p.Successors.Remove(block);
        Blocks.Remove(block);
    }

    static BasicBlock FindContainingBlock(List<BasicBlock> blocks, List<int> leaders, int instIdx)
    {
        for (int i = leaders.Count - 1; i >= 0; i--)
            if (leaders[i] <= instIdx)
                return blocks[i];
        return blocks[0];
    }

    void InitEmpty()
    {
        Entry = new BasicBlock { Id = 0 };
        Exit = new BasicBlock { Id = 1 };
        Blocks.Add(Entry);
        AddEdge(Entry, Exit);
    }

    void BuildVarIndex(List<Inst> insts)
    {
        _varCount = 0;
        foreach (var inst in insts)
        {
            if (inst.Kind == InstKind.Push && inst.Operand != null
                && !VarToIndex.ContainsKey(inst.Operand))
                VarToIndex[inst.Operand] = _varCount++;
            if (inst.Kind == InstKind.JumpIndirect && inst.Operand != null
                && !VarToIndex.ContainsKey(inst.Operand))
                VarToIndex[inst.Operand] = _varCount++;
        }
        IndexToVar = new string[_varCount];
        foreach (var (name, idx) in VarToIndex)
            IndexToVar[idx] = name;
    }

    // Save/restore liveness for ReduceVariables.
    // The 2nd ComputeLiveness (post-GVN/LICM) produces precise live ranges
    // that can cause ReduceVariables to merge variables too aggressively.
    // Save the 1st (conservative) liveness before the 2nd pass, then restore
    // it before ReduceVariables to ensure safe variable merging.
    Dictionary<int, (BitSet LiveIn, BitSet LiveOut)> _savedLiveness;

    public void SaveLiveness()
    {
        _savedLiveness = new Dictionary<int, (BitSet, BitSet)>();
        foreach (var block in Blocks)
            if (block.LiveOut.IsValid)
                _savedLiveness[block.Id] = (block.LiveIn.Copy(), block.LiveOut.Copy());
    }

    public void RestoreLiveness()
    {
        if (_savedLiveness == null) return;
        foreach (var block in Blocks)
            if (_savedLiveness.TryGetValue(block.Id, out var saved))
            {
                block.LiveIn = saved.LiveIn;
                block.LiveOut = saved.LiveOut;
            }
            // Blocks without saved data (e.g., LICM preheaders) keep their
            // current LiveIn/LiveOut from the 2nd ComputeLiveness.
        _savedLiveness = null;
    }

    public void ComputeLiveness()
    {
        foreach (var block in Blocks)
        {
            block.Gen = BitSet.Create(_varCount);
            block.Kill = BitSet.Create(_varCount);
            block.LiveIn = BitSet.Create(_varCount);
            block.LiveOut = BitSet.Create(_varCount);
        }

        foreach (var block in Blocks)
            ComputeGenKill(block);

        bool changed;
        do
        {
            changed = false;
            for (int i = Blocks.Count - 1; i >= 0; i--)
            {
                var block = Blocks[i];
                var newLiveOut = BitSet.Create(_varCount);
                foreach (var succ in block.Successors)
                {
                    if (succ == Exit) continue;
                    newLiveOut.UnionWith(succ.LiveIn);
                }
                block.LiveOut = newLiveOut;

                var newLiveIn = block.LiveOut.Copy();
                newLiveIn.ExceptWith(block.Kill);
                newLiveIn.UnionWith(block.Gen);

                if (!newLiveIn.SetEquals(block.LiveIn))
                {
                    block.LiveIn = newLiveIn;
                    changed = true;
                }
            }
        } while (changed);
    }

    void ComputeGenKill(BasicBlock block)
    {
        for (int i = 0; i < block.Instructions.Count; i++)
        {
            var inst = block.Instructions[i];
            if (inst.Kind != InstKind.Push || inst.Operand == null) continue;
            if (!VarToIndex.TryGetValue(inst.Operand, out var varIdx)) continue;

            if (GetWrittenVar(block, i) != null)
            {
                block.Kill.Set(varIdx);
            }
            else
            {
                // Regular read
                if (!block.Kill.Get(varIdx))
                    block.Gen.Set(varIdx);
            }
        }
    }

    static bool IsExternOutput(BasicBlock block, int pushIdx)
    {
        for (int j = pushIdx + 1; j < block.Instructions.Count; j++)
        {
            var kind = block.Instructions[j].Kind;
            if (kind == InstKind.Extern)
                return !InstInfo.IsVoidExtern(block.Instructions[j].Operand);
            // Skip block-terminating jumps — they don't consume stack entries
            if (kind == InstKind.Jump || kind == InstKind.JumpIfFalse || kind == InstKind.JumpIndirect)
                continue;
            if (InstInfo.Size[kind] > 0)
                return false;
        }
        // Cross-block: single successor only (safe — multiple successors
        // means diverging control flow, so the PUSH is unlikely an extern output)
        if (block.Successors.Count == 1 && block.Successors[0].Instructions.Count > 0)
        {
            var succ = block.Successors[0];
            for (int j = 0; j < succ.Instructions.Count; j++)
            {
                var kind = succ.Instructions[j].Kind;
                if (kind == InstKind.Extern)
                    return !InstInfo.IsVoidExtern(succ.Instructions[j].Operand);
                if (InstInfo.Size[kind] > 0)
                    return false;
            }
        }
        return false;
    }

    Dictionary<string, int> ComputeWriteCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var block in Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var w = GetWrittenVar(block, i);
                if (w != null)
                {
                    counts.TryGetValue(w, out var c);
                    counts[w] = c + 1;
                }
            }
        return counts;
    }

    public bool CopyPropagation()
    {
        bool anyChanged = false;
        bool changed;
        do
        {
            changed = false;
            var writeCounts = ComputeWriteCounts();
            foreach (var block in Blocks)
            {
                for (int i = 0; i + 2 < block.Instructions.Count; i++)
                {
                    if (block.Instructions[i].Kind != InstKind.Push) continue;
                    if (block.Instructions[i + 1].Kind != InstKind.Push) continue;
                    if (block.Instructions[i + 2].Kind != InstKind.Copy) continue;

                    var src = block.Instructions[i].Operand;
                    var dst = block.Instructions[i + 1].Operand;
                    if (src == null || dst == null) continue;
                    if (!dst.StartsWith("__intnl_") || dst == "__intnl_returnJump_SystemUInt32_0") continue;

                    if (!CanPropagate(src, dst, writeCounts)) continue;

                    ReplaceReads(dst, src);
                    block.Instructions.RemoveRange(i, 3);
                    i--; // re-examine at same position after removal
                    changed = true;
                    anyChanged = true;
                }
            }
        } while (changed);
        return anyChanged;
    }

    bool CanPropagate(string src, string dst, Dictionary<string, int> writeCounts)
    {
        // Type safety: reject propagation across different UdonTypes
        if (_varTypes.TryGetValue(src, out var srcType)
            && _varTypes.TryGetValue(dst, out var dstType))
        {
            if (srcType != dstType) return false;
        }
        else if (_varTypes.ContainsKey(src) != _varTypes.ContainsKey(dst))
        {
            return false;
        }

        // dst must have exactly one write across all blocks
        writeCounts.TryGetValue(dst, out var dstWrites);
        if (dstWrites != 1) return false;

        if (src.StartsWith("__const_")) return true;

        // src must not be written anywhere
        writeCounts.TryGetValue(src, out var srcWrites);
        return srcWrites == 0;
    }

    void ReplaceReads(string oldVar, string newVar)
    {
        foreach (var block in Blocks)
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var inst = block.Instructions[i];
                if (inst.Kind == InstKind.Push && inst.Operand == oldVar)
                {
                    if (GetWrittenVar(block, i) == null)
                    {
                        inst.Operand = newVar;
                        block.Instructions[i] = inst;
                    }
                }
            }
    }

    public bool DeadStoreElimination()
    {
        bool anyChanged = false;
        foreach (var block in Blocks)
        {
            var live = block.LiveOut.Copy();
            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var inst = block.Instructions[i];
                if (inst.Kind != InstKind.Push || inst.Operand == null) continue;

                // Is this a CopyDst write?
                if (i + 1 < block.Instructions.Count
                    && block.Instructions[i + 1].Kind == InstKind.Copy
                    && i >= 1
                    && block.Instructions[i - 1].Kind == InstKind.Push)
                {
                    var dst = inst.Operand;
                    if (dst.StartsWith("__intnl_") && dst != "__intnl_returnJump_SystemUInt32_0"
                        && VarToIndex.TryGetValue(dst, out var dstIdx)
                        && !live.Get(dstIdx))
                    {
                        block.Instructions.RemoveRange(i - 1, 3);
                        i -= 1;
                        anyChanged = true;
                        continue;
                    }
                    if (VarToIndex.TryGetValue(dst, out var idx))
                        live.Clear(idx);
                    var src = block.Instructions[i - 1].Operand;
                    if (src != null && VarToIndex.TryGetValue(src, out var srcIdx))
                        live.Set(srcIdx);
                    i--;
                    continue;
                }

                // Is this an ExternOutput write?
                if (IsExternOutput(block, i))
                {
                    if (VarToIndex.TryGetValue(inst.Operand, out var idx))
                        live.Clear(idx);
                    continue;
                }

                // Regular read
                if (VarToIndex.TryGetValue(inst.Operand, out var readIdx))
                    live.Set(readIdx);
            }
        }
        return anyChanged;
    }

    public Dictionary<string, string> ReduceVariables()
    {
        // Extract UdonType from naming convention: __intnl_{UdonType}_{index}
        var varType = new Dictionary<string, string>();
        foreach (var name in VarToIndex.Keys)
        {
            if (!name.StartsWith("__intnl_") || name == "__intnl_returnJump_SystemUInt32_0") continue;
            var withoutPrefix = name.Substring("__intnl_".Length);
            var lastUnderscore = withoutPrefix.LastIndexOf('_');
            if (lastUnderscore < 0) continue;
            varType[name] = withoutPrefix.Substring(0, lastUnderscore);
        }

        // Build interference graph
        var interferes = new Dictionary<string, HashSet<string>>();
        foreach (var name in varType.Keys)
            interferes[name] = new HashSet<string>();

        foreach (var block in Blocks)
        {
            var live = block.LiveOut.Copy();
            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var inst = block.Instructions[i];
                if (inst.Kind != InstKind.Push || inst.Operand == null) continue;

                string writtenVar = GetWrittenVar(block, i);
                if (writtenVar != null)
                {
                    if (VarToIndex.TryGetValue(writtenVar, out var wIdx))
                    {
                        if (varType.ContainsKey(writtenVar))
                        {
                            // Internal write: add interference edges
                            for (int v = 0; v < _varCount; v++)
                            {
                                if (!live.Get(v)) continue;
                                var otherName = IndexToVar[v];
                                if (otherName == writtenVar) continue;
                                if (!varType.ContainsKey(otherName)) continue;
                                interferes[writtenVar].Add(otherName);
                                interferes[otherName].Add(writtenVar);
                            }
                        }
                        live.Clear(wIdx);
                    }
                    continue;
                }

                // Regular read
                if (VarToIndex.TryGetValue(inst.Operand, out var readIdx))
                    live.Set(readIdx);
            }
        }

        // Variables live at function entry interfere with each other
        var entryLive = Entry.LiveIn;
        var liveInternal = new List<string>();
        for (int v = 0; v < _varCount; v++)
            if (entryLive.Get(v) && varType.ContainsKey(IndexToVar[v]))
                liveInternal.Add(IndexToVar[v]);
        for (int a = 0; a < liveInternal.Count; a++)
            for (int b = a + 1; b < liveInternal.Count; b++)
            {
                interferes[liveInternal[a]].Add(liveInternal[b]);
                interferes[liveInternal[b]].Add(liveInternal[a]);
            }

        // Graph coloring per type group
        var byType = new Dictionary<string, List<string>>();
        foreach (var (name, type) in varType)
        {
            if (!byType.TryGetValue(type, out var list))
                byType[type] = list = new List<string>();
            list.Add(name);
        }

        var renameMap = new Dictionary<string, string>();
        foreach (var (_, vars) in byType)
        {
            if (vars.Count <= 1) continue;
            // Sort by degree descending for better coloring
            vars.Sort((a, b) => interferes[b].Count.CompareTo(interferes[a].Count));

            var color = new Dictionary<string, int>();
            foreach (var v in vars)
            {
                var usedColors = new HashSet<int>();
                foreach (var neighbor in interferes[v])
                    if (color.TryGetValue(neighbor, out var c))
                        usedColors.Add(c);
                int col = 0;
                while (usedColors.Contains(col)) col++;
                color[v] = col;
            }

            // Group by color → rename to first variable in each group
            var groups = new Dictionary<int, List<string>>();
            foreach (var (v, c) in color)
            {
                if (!groups.TryGetValue(c, out var g))
                    groups[c] = g = new List<string>();
                g.Add(v);
            }
            foreach (var (_, group) in groups)
            {
                if (group.Count <= 1) continue;
                group.Sort(); // deterministic: lexicographically first wins
                var target = group[0];
                for (int i = 1; i < group.Count; i++)
                    renameMap[group[i]] = target;
            }
        }

        // Apply renames to all block instructions
        if (renameMap.Count > 0)
            foreach (var block in Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                {
                    var inst = block.Instructions[i];
                    if (inst.Operand != null && renameMap.TryGetValue(inst.Operand, out var newName))
                    {
                        inst.Operand = newName;
                        block.Instructions[i] = inst;
                    }
                }

        return renameMap;
    }

    public bool ConstantFolding(UasmModule module)
    {
        bool anyChanged = false;
        foreach (var block in Blocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                if (block.Instructions[i].Kind != InstKind.Extern) continue;
                var externSig = block.Instructions[i].Operand;
                if (!PureExternTable.IsPure(externSig)) continue;
                if (InstInfo.IsVoidExtern(externSig)) continue;

                // Collect input PUSHes and output PUSH before the EXTERN
                // Pattern: PUSH in0; [PUSH in1;] PUSH output; EXTERN sig
                // For unary: 2 PUSHes before EXTERN (1 input + 1 output)
                // For binary: 3 PUSHes before EXTERN (2 inputs + 1 output)
                var pushes = new List<int>();
                for (int j = i - 1; j >= 0; j--)
                {
                    var k = block.Instructions[j].Kind;
                    if (k == InstKind.Push)
                        pushes.Insert(0, j);
                    else if (k is InstKind.Label or InstKind.LabelAddr or InstKind.Export)
                        break; // structural boundary — sentinel/preamble PUSHes must not be collected
                    else if (!InstInfo.IsZeroSize(k))
                        break;
                    if (pushes.Count >= 4) break; // max we ever need
                }

                if (pushes.Count < 2) continue;

                var outputIdx = pushes[pushes.Count - 1];
                var outputVar = block.Instructions[outputIdx].Operand;
                var inputIndices = pushes.GetRange(0, pushes.Count - 1);

                // All inputs must be const
                bool allConst = true;
                var inputVars = new string[inputIndices.Count];
                var inputValues = new object[inputIndices.Count];
                for (int k = 0; k < inputIndices.Count; k++)
                {
                    inputVars[k] = block.Instructions[inputIndices[k]].Operand;
                    if (inputVars[k] == null || !inputVars[k].StartsWith("__const_")
                        || !_constValues.TryGetValue(inputVars[k], out inputValues[k]))
                    {
                        allConst = false;
                        break;
                    }
                }
                if (!allConst) continue;

                // Try to evaluate
                var result = TryEvaluateExtern(externSig, inputValues);
                if (result == null) continue;

                // Determine result UdonType from extern signature (last __Type)
                var resultType = ExtractReturnType(externSig);
                if (resultType == null) continue;

                // Create or reuse const variable in module
                var constId = module.CreateConstVariable(resultType, result);

                // Ensure VarToIndex has the new const
                if (!VarToIndex.ContainsKey(constId))
                {
                    VarToIndex[constId] = _varCount;
                    var newIndex = new string[_varCount + 1];
                    Array.Copy(IndexToVar, newIndex, _varCount);
                    newIndex[_varCount] = constId;
                    IndexToVar = newIndex;
                    _varCount++;
                }

                // Also register in _constValues for cascading
                _constValues[constId] = result;

                // Replace: remove input PUSHes + output PUSH + EXTERN,
                // insert: PUSH constId; PUSH outputVar; COPY
                int firstPush = pushes[0];
                int removeCount = i - firstPush + 1;
                block.Instructions.RemoveRange(firstPush, removeCount);
                block.Instructions.Insert(firstPush, new Inst { Kind = InstKind.Push, Operand = constId });
                block.Instructions.Insert(firstPush + 1, new Inst { Kind = InstKind.Push, Operand = outputVar });
                block.Instructions.Insert(firstPush + 2, new Inst { Kind = InstKind.Copy });

                anyChanged = true;
                i = firstPush + 2; // continue after the replacement
            }
        }
        return anyChanged;
    }

    static object TryEvaluateExtern(string externSig, object[] inputs)
    {
        var dotIdx = externSig.IndexOf('.');
        if (dotIdx < 0) return null;
        var method = externSig.Substring(dotIdx + 1);

        // Extract just the operation name (up to first __ after op name)
        // e.g. "__op_Addition__SystemInt32_SystemInt32__SystemInt32" → "__op_Addition__"
        if (inputs.Length == 2) return TryEvalBinary(method, inputs[0], inputs[1]);
        if (inputs.Length == 1) return TryEvalUnary(method, inputs[0]);
        return null;
    }

    static object TryEvalBinary(string method, object left, object right)
    {
        // Integer zero-division guard
        if (method.StartsWith("__op_Division__") || method.StartsWith("__op_Remainder__"))
        {
            if (right is int ri && ri == 0) return null;
            if (right is long rl && rl == 0) return null;
        }

        if (left is int li && right is int ri2) return EvalInt(method, li, ri2);
        if (left is float lf && right is float rf) return EvalFloat(method, lf, rf);
        if (left is bool lb && right is bool rb) return EvalBool(method, lb, rb);
        if (left is long ll && right is long rl2) return EvalLong(method, ll, rl2);
        return null;
    }

    static object EvalInt(string method, int a, int b)
    {
        if (method.StartsWith("__op_Addition__")) return a + b;
        if (method.StartsWith("__op_Subtraction__")) return a - b;
        if (method.StartsWith("__op_Multiplication__")) return a * b;
        if (method.StartsWith("__op_Division__")) return a / b;
        if (method.StartsWith("__op_Remainder__")) return a % b;
        if (method.StartsWith("__op_LeftShift__")) return a << b;
        if (method.StartsWith("__op_RightShift__")) return a >> b;
        if (method.StartsWith("__op_LogicalAnd__")) return a & b;
        if (method.StartsWith("__op_LogicalOr__")) return a | b;
        if (method.StartsWith("__op_LogicalXor__")) return a ^ b;
        if (method.StartsWith("__op_Equality__")) return a == b;
        if (method.StartsWith("__op_Inequality__")) return a != b;
        if (method.StartsWith("__op_LessThan__")) return a < b;
        if (method.StartsWith("__op_GreaterThan__")) return a > b;
        if (method.StartsWith("__op_LessThanOrEqual__")) return a <= b;
        if (method.StartsWith("__op_GreaterThanOrEqual__")) return a >= b;
        return null;
    }

    static object EvalFloat(string method, float a, float b)
    {
        if (method.StartsWith("__op_Addition__")) return a + b;
        if (method.StartsWith("__op_Subtraction__")) return a - b;
        if (method.StartsWith("__op_Multiplication__")) return a * b;
        if (method.StartsWith("__op_Division__")) return a / b;
        if (method.StartsWith("__op_Remainder__")) return a % b;
        if (method.StartsWith("__op_Equality__")) return a == b;
        if (method.StartsWith("__op_Inequality__")) return a != b;
        if (method.StartsWith("__op_LessThan__")) return a < b;
        if (method.StartsWith("__op_GreaterThan__")) return a > b;
        if (method.StartsWith("__op_LessThanOrEqual__")) return a <= b;
        if (method.StartsWith("__op_GreaterThanOrEqual__")) return a >= b;
        return null;
    }

    static object EvalBool(string method, bool a, bool b)
    {
        if (method.StartsWith("__op_ConditionalAnd__")) return a && b;
        if (method.StartsWith("__op_ConditionalOr__")) return a || b;
        if (method.StartsWith("__op_Equality__")) return a == b;
        if (method.StartsWith("__op_Inequality__")) return a != b;
        if (method.StartsWith("__op_LogicalAnd__")) return a & b;
        if (method.StartsWith("__op_LogicalOr__")) return a | b;
        if (method.StartsWith("__op_LogicalXor__")) return a ^ b;
        return null;
    }

    static object EvalLong(string method, long a, long b)
    {
        if (method.StartsWith("__op_Addition__")) return a + b;
        if (method.StartsWith("__op_Subtraction__")) return a - b;
        if (method.StartsWith("__op_Multiplication__")) return a * b;
        if (method.StartsWith("__op_Division__")) return a / b;
        if (method.StartsWith("__op_Remainder__")) return a % b;
        if (method.StartsWith("__op_Equality__")) return a == b;
        if (method.StartsWith("__op_Inequality__")) return a != b;
        if (method.StartsWith("__op_LessThan__")) return a < b;
        if (method.StartsWith("__op_GreaterThan__")) return a > b;
        if (method.StartsWith("__op_LessThanOrEqual__")) return a <= b;
        if (method.StartsWith("__op_GreaterThanOrEqual__")) return a >= b;
        return null;
    }

    static object TryEvalUnary(string method, object operand)
    {
        if (operand is int i)
        {
            if (method.StartsWith("__op_UnaryMinus__") || method.StartsWith("__op_UnaryNegation__")) return -i;
        }
        if (operand is float f)
        {
            if (method.StartsWith("__op_UnaryMinus__") || method.StartsWith("__op_UnaryNegation__")) return -f;
        }
        if (operand is long l)
        {
            if (method.StartsWith("__op_UnaryMinus__") || method.StartsWith("__op_UnaryNegation__")) return -l;
        }
        if (operand is bool b)
        {
            if (method.StartsWith("__op_UnaryNegation__")) return !b;
        }

        // SystemConvert conversions
        if (method.StartsWith("__ToInt32__")) return ConvertToInt32(operand);
        if (method.StartsWith("__ToSingle__")) return ConvertToSingle(operand);
        if (method.StartsWith("__ToInt64__")) return ConvertToInt64(operand);
        if (method.StartsWith("__ToBoolean__")) return ConvertToBoolean(operand);

        return null;
    }

    static object ConvertToInt32(object v) => v switch
    {
        int i => i, float f => (int)f, long l => (int)l, bool b => b ? 1 : 0, _ => null
    };
    static object ConvertToSingle(object v) => v switch
    {
        int i => (float)i, float f => f, long l => (float)l, _ => null
    };
    static object ConvertToInt64(object v) => v switch
    {
        int i => (long)i, float f => (long)f, long l => l, _ => null
    };
    static object ConvertToBoolean(object v) => v switch
    {
        int i => i != 0, float f => f != 0f, long l => l != 0L, bool b => b, _ => null
    };

    static string ExtractReturnType(string externSig)
    {
        // Signature format: "Type.__method__Params__ReturnType"
        var lastDunder = externSig.LastIndexOf("__");
        if (lastDunder < 0) return null;
        return externSig.Substring(lastDunder + 2);
    }

    string GetWrittenVar(BasicBlock block, int i)
    {
        var inst = block.Instructions[i];
        if (inst.Kind != InstKind.Push || inst.Operand == null) return null;

        // CopyDst pattern: PUSH src; PUSH dst; COPY
        if (i + 1 < block.Instructions.Count
            && block.Instructions[i + 1].Kind == InstKind.Copy
            && i >= 1
            && block.Instructions[i - 1].Kind == InstKind.Push)
            return inst.Operand;

        // ExternOutput pattern
        if (IsExternOutput(block, i))
            return inst.Operand;

        return null;
    }

    List<BasicBlock> ComputeRPO()
    {
        var visited = new HashSet<int>();
        var postorder = new List<BasicBlock>();

        void Dfs(BasicBlock b)
        {
            if (!visited.Add(b.Id)) return;
            foreach (var s in b.Successors)
                if (s != Exit) Dfs(s);
            postorder.Add(b);
        }

        Dfs(Entry);
        postorder.Reverse();
        return postorder;
    }

    // --- Dominator Tree (Cooper-Harvey-Kennedy) ---
    // _domRoot: virtual root ID (-1) that dominates all entry points.
    // Udon programs have multiple entry points (exports), so no single
    // real block dominates all others.  The virtual root ensures export
    // blocks are siblings, preventing GVN from leaking values across
    // independent functions.
    int _domRoot = -1;
    Dictionary<int, int> _idom;
    Dictionary<int, List<int>> _domChildren;

    public void ComputeDominators()
    {
        var rpo = ComputeRPO();
        var rpoIndex = new Dictionary<int, int>(); // blockId → RPO index
        for (int i = 0; i < rpo.Count; i++)
            rpoIndex[rpo[i].Id] = i;

        // Identify entry blocks: exports (VM can enter directly) + Entry (address 0)
        var entryBlockIds = new HashSet<int> { Entry.Id };
        foreach (var block in Blocks)
            if (block.Instructions.Any(inst => inst.Kind == InstKind.Export))
                entryBlockIds.Add(block.Id);

        // Virtual root (-1) dominates all entry points.
        // This prevents GVN/LICM from assuming one function's values
        // are available in another function entered independently by the VM.
        _domRoot = -1;
        _idom = new Dictionary<int, int>();
        _idom[_domRoot] = _domRoot;
        rpoIndex[_domRoot] = -1; // before everything in RPO

        foreach (var eid in entryBlockIds)
            _idom[eid] = _domRoot;

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in rpo)
            {
                // Entry blocks are pinned to virtual root — skip them
                if (entryBlockIds.Contains(b.Id)) continue;

                int newIdom = -1;
                foreach (var pred in b.Predecessors)
                {
                    if (pred == Exit || !rpoIndex.ContainsKey(pred.Id)) continue;
                    if (!_idom.ContainsKey(pred.Id)) continue;
                    if (newIdom == -1)
                        newIdom = pred.Id;
                    else
                        newIdom = Intersect(newIdom, pred.Id, rpoIndex);
                }
                if (newIdom != -1 && (!_idom.TryGetValue(b.Id, out var old) || old != newIdom))
                {
                    _idom[b.Id] = newIdom;
                    changed = true;
                }
            }
        }

        // Build dominator tree children
        _domChildren = new Dictionary<int, List<int>>();
        _domChildren[_domRoot] = new List<int>();
        foreach (var b in Blocks)
            _domChildren[b.Id] = new List<int>();
        foreach (var (id, idom) in _idom)
            if (id != idom)
                _domChildren[idom].Add(id);
    }

    int Intersect(int b1, int b2, Dictionary<int, int> rpoIndex)
    {
        while (b1 != b2)
        {
            while (rpoIndex[b1] > rpoIndex[b2]) b1 = _idom[b1];
            while (rpoIndex[b2] > rpoIndex[b1]) b2 = _idom[b2];
        }
        return b1;
    }

    public bool Dominates(int a, int b)
    {
        if (a == b) return true;
        var current = b;
        while (_idom.TryGetValue(current, out var idom) && idom != current)
        {
            if (idom == a) return true;
            current = idom;
        }
        return false;
    }

    // --- GVN (Global Value Numbering) ---

    struct ValueKey : IEquatable<ValueKey>
    {
        public string ExternSig;
        public string[] InputVars;

        public bool Equals(ValueKey other)
        {
            if (ExternSig != other.ExternSig) return false;
            if (InputVars.Length != other.InputVars.Length) return false;
            for (int i = 0; i < InputVars.Length; i++)
                if (InputVars[i] != other.InputVars[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is ValueKey vk && Equals(vk);

        public override int GetHashCode()
        {
            int h = ExternSig.GetHashCode();
            foreach (var v in InputVars)
                h = h * 31 + (v?.GetHashCode() ?? 0);
            return h;
        }
    }

    public bool GlobalValueNumbering()
    {
        if (_idom == null) ComputeDominators();

        // Precompute: which variables are safe for GVN (consts or single-write)
        var writeCount = ComputeWriteCounts();

        bool IsSafeInput(string v) =>
            v != null && (v.StartsWith("__const_") || (writeCount.TryGetValue(v, out var c) && c == 1));

        bool anyChanged = false;
        var valueTable = new Dictionary<ValueKey, string>(); // key → result var
        var scopeStack = new Stack<List<ValueKey>>(); // for rollback

        void DfsGvn(int blockId)
        {
            var added = new List<ValueKey>();
            var block = _blockById.GetValueOrDefault(blockId);
            if (block == null) { ProcessChildren(); return; }

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                if (block.Instructions[i].Kind != InstKind.Extern) continue;
                var externSig = block.Instructions[i].Operand;
                if (!PureExternTable.IsPure(externSig)) continue;
                if (InstInfo.IsVoidExtern(externSig)) continue;

                // Collect pushes (stop at structural boundaries like Label/Export)
                var pushes = new List<int>();
                for (int j = i - 1; j >= 0; j--)
                {
                    var k = block.Instructions[j].Kind;
                    if (k == InstKind.Push)
                        pushes.Insert(0, j);
                    else if (k is InstKind.Label or InstKind.LabelAddr or InstKind.Export)
                        break;
                    else if (!InstInfo.IsZeroSize(k))
                        break;
                    if (pushes.Count >= 4) break;
                }
                if (pushes.Count < 2) continue;

                var outputIdx = pushes[pushes.Count - 1];
                var outputVar = block.Instructions[outputIdx].Operand;
                var inputIndices = pushes.GetRange(0, pushes.Count - 1);

                // All inputs must be safe
                var inputVars = new string[inputIndices.Count];
                bool allSafe = true;
                for (int k = 0; k < inputIndices.Count; k++)
                {
                    inputVars[k] = block.Instructions[inputIndices[k]].Operand;
                    if (!IsSafeInput(inputVars[k])) { allSafe = false; break; }
                }
                if (!allSafe) continue;

                var key = new ValueKey { ExternSig = externSig, InputVars = inputVars };

                if (valueTable.TryGetValue(key, out var existingResult)
                    && IsSafeInput(existingResult)) // result var must not have been overwritten
                {
                    // Replace: remove input PUSHes + output PUSH + EXTERN,
                    // insert: PUSH existingResult; PUSH outputVar; COPY
                    int firstPush = pushes[0];
                    int removeCount = i - firstPush + 1;
                    block.Instructions.RemoveRange(firstPush, removeCount);
                    block.Instructions.Insert(firstPush, new Inst { Kind = InstKind.Push, Operand = existingResult });
                    block.Instructions.Insert(firstPush + 1, new Inst { Kind = InstKind.Push, Operand = outputVar });
                    block.Instructions.Insert(firstPush + 2, new Inst { Kind = InstKind.Copy });
                    i = firstPush + 2;
                    anyChanged = true;
                }
                else
                {
                    valueTable[key] = outputVar;
                    added.Add(key);
                }
            }

            ProcessChildren();

            // Rollback
            foreach (var key in added)
                valueTable.Remove(key);

            void ProcessChildren()
            {
                if (_domChildren.TryGetValue(blockId, out var children))
                    foreach (var child in children)
                        DfsGvn(child);
            }
        }

        DfsGvn(_domRoot);
        return anyChanged;
    }

    // --- Loop Detection ---

    public struct NaturalLoop
    {
        public BasicBlock Header;
        public HashSet<int> Body; // block IDs
    }

    public List<NaturalLoop> DetectLoops()
    {
        if (_idom == null) ComputeDominators();

        var loops = new List<NaturalLoop>();
        // Find back edges: N→D where D dominates N
        // Skip blocks whose terminal edge is NOT a real loop back edge:
        // 1. JumpIndirect blocks — conservative over-approximation edges (function returns)
        // 2. Function call sites (PushLabel + Jump) — the Jump is a call, not a back edge
        foreach (var block in Blocks)
        {
            var lastReal = GetLastRealInstruction(block);
            if (lastReal < 0) continue;
            if (block.Instructions[lastReal].Kind == InstKind.JumpIndirect)
                continue;
            if (block.Instructions[lastReal].Kind == InstKind.Jump
                && block.Instructions.Any(i => i.Kind == InstKind.PushLabel))
                continue;

            foreach (var succ in block.Successors)
            {
                if (succ == Exit) continue;
                if (!Dominates(succ.Id, block.Id)) continue;

                // Back edge: block → succ (succ is loop header)
                var body = new HashSet<int> { succ.Id };
                var worklist = new Stack<int>();
                if (block.Id != succ.Id)
                {
                    body.Add(block.Id);
                    worklist.Push(block.Id);
                }
                while (worklist.Count > 0)
                {
                    var n = worklist.Pop();
                    var nb = _blockById.GetValueOrDefault(n);
                    if (nb == null) continue;
                    foreach (var pred in nb.Predecessors)
                    {
                        if (pred == Exit) continue;
                        if (body.Add(pred.Id))
                            worklist.Push(pred.Id);
                    }
                }
                loops.Add(new NaturalLoop { Header = succ, Body = body });
            }
        }

        return loops;
    }

    public BasicBlock EnsurePreheader(NaturalLoop loop, List<JumpLabel> labels)
    {
        var header = loop.Header;

        // Check if preheader already exists: single non-back-edge predecessor
        var outsidePreds = header.Predecessors
            .Where(p => p != Exit && !loop.Body.Contains(p.Id)).ToList();

        // No outside predecessors → loop is only reachable from itself; can't hoist safely
        if (outsidePreds.Count == 0) return null;

        if (outsidePreds.Count == 1)
        {
            var cand = outsidePreds[0];
            if (cand.Successors.Count == 1 && cand.Successors[0] == header)
                return cand;
        }

        // Create preheader block
        var preheader = new BasicBlock { Id = Blocks.Max(b => b.Id) + 1 };
        preheader.Gen = BitSet.Create(_varCount);
        preheader.Kill = BitSet.Create(_varCount);
        preheader.LiveIn = BitSet.Create(_varCount);
        preheader.LiveOut = BitSet.Create(_varCount);
        Blocks.Add(preheader);

        // Create a label for the preheader
        var preheaderLabel = EnsureBlockLabel(preheader, labels);
        var headerLabel = GetBlockLabel(header);

        // Redirect outside predecessors to preheader
        foreach (var pred in outsidePreds)
        {
            pred.Successors.Remove(header);
            if (!pred.Successors.Contains(preheader)) pred.Successors.Add(preheader);
            header.Predecessors.Remove(pred);
            if (!preheader.Predecessors.Contains(pred)) preheader.Predecessors.Add(pred);

            // Update jump labels in predecessor instructions
            if (headerLabel >= 0)
            {
                for (int j = 0; j < pred.Instructions.Count; j++)
                {
                    var inst = pred.Instructions[j];
                    if (inst.Kind is InstKind.Jump or InstKind.JumpIfFalse
                        && inst.LabelIndex == headerLabel)
                    {
                        inst.LabelIndex = preheaderLabel;
                        pred.Instructions[j] = inst;
                    }
                }
            }
        }

        // Preheader → header (with explicit jump)
        AddEdge(preheader, header);
        var headerLabelForJump = EnsureBlockLabel(header, labels);
        preheader.Instructions.Add(new Inst { Kind = InstKind.Jump, LabelIndex = headerLabelForJump });

        return preheader;
    }

    // --- LICM (Loop-Invariant Code Motion) ---

    public bool LoopInvariantCodeMotion(UasmModule module)
    {
        if (_idom == null) ComputeDominators();
        var loops = DetectLoops();
        if (loops.Count == 0) return false;

        // Compute which variables are written in each loop
        bool anyChanged = false;
        var labels = module.GetLabels();

        foreach (var loop in loops)
        {
            var writtenInLoop = new HashSet<string>();
            foreach (var blockId in loop.Body)
            {
                var block = _blockById.GetValueOrDefault(blockId);
                if (block == null) continue;
                for (int i = 0; i < block.Instructions.Count; i++)
                {
                    var w = GetWrittenVar(block, i);
                    if (w != null) writtenInLoop.Add(w);
                }
            }

            // Find invariant EXTERNs
            BasicBlock preheader = null;

            foreach (var blockId in loop.Body.ToList())
            {
                var block = _blockById.GetValueOrDefault(blockId);
                if (block == null) continue;

                for (int i = 0; i < block.Instructions.Count; i++)
                {
                    if (block.Instructions[i].Kind != InstKind.Extern) continue;
                    var externSig = block.Instructions[i].Operand;
                    if (!PureExternTable.IsPure(externSig)) continue;
                    if (InstInfo.IsVoidExtern(externSig)) continue;

                    // Collect pushes (stop at structural boundaries like Label/Export)
                    var pushes = new List<int>();
                    for (int j = i - 1; j >= 0; j--)
                    {
                        var k = block.Instructions[j].Kind;
                        if (k == InstKind.Push)
                            pushes.Insert(0, j);
                        else if (k is InstKind.Label or InstKind.LabelAddr or InstKind.Export)
                            break;
                        else if (!InstInfo.IsZeroSize(k))
                            break;
                        if (pushes.Count >= 4) break;
                    }
                    if (pushes.Count < 2) continue;

                    var outputIdx = pushes[pushes.Count - 1];
                    var outputVar = block.Instructions[outputIdx].Operand;
                    var inputIndices = pushes.GetRange(0, pushes.Count - 1);

                    // Check invariant conditions:
                    // 1. All inputs not written in loop
                    bool allInvariant = true;
                    for (int k = 0; k < inputIndices.Count; k++)
                    {
                        var inputVar = block.Instructions[inputIndices[k]].Operand;
                        if (inputVar != null && writtenInLoop.Contains(inputVar))
                        {
                            allInvariant = false;
                            break;
                        }
                    }
                    if (!allInvariant) continue;

                    // 2. Output not written elsewhere in loop (only this EXTERN writes it)
                    int outputWriteCount = 0;
                    foreach (var bid in loop.Body)
                    {
                        var b2 = _blockById.GetValueOrDefault(bid);
                        if (b2 == null) continue;
                        for (int j = 0; j < b2.Instructions.Count; j++)
                            if (GetWrittenVar(b2, j) == outputVar)
                                outputWriteCount++;
                    }
                    if (outputWriteCount != 1) continue;

                    // Hoist: move instructions to preheader
                    if (preheader == null)
                    {
                        preheader = EnsurePreheader(loop, labels);
                        if (preheader == null) break; // can't create preheader, skip this loop
                    }

                    int firstPush = pushes[0];
                    int count = i - firstPush + 1;
                    var hoisted = block.Instructions.GetRange(firstPush, count);
                    block.Instructions.RemoveRange(firstPush, count);

                    // Insert before the end of preheader (before any jump)
                    var insertPos = preheader.Instructions.Count;
                    var lastReal = GetLastRealInstruction(preheader);
                    if (lastReal >= 0 && InstInfo.IsJump(preheader.Instructions[lastReal].Kind))
                        insertPos = lastReal;
                    preheader.Instructions.InsertRange(insertPos, hoisted);

                    anyChanged = true;
                    i = firstPush - 1; // re-check from before the removed region
                }
            }
        }

        return anyChanged;
    }

    public void Linearize(UasmModule module)
    {
        var order = ComputeRPO();
        var insts = module.GetInstructions();
        var labels = module.GetLabels();
        insts.Clear();

        for (int b = 0; b < order.Count; b++)
        {
            var block = order[b];
            var nextBlock = (b + 1 < order.Count) ? order[b + 1] : null;

            foreach (var inst in block.Instructions)
                insts.Add(inst);

            int lastIdx = insts.Count - 1;
            while (lastIdx >= 0 && InstInfo.IsZeroSize(insts[lastIdx].Kind))
                lastIdx--;

            if (lastIdx < 0)
            {
                // Block has no real instructions — ensure fall-through is correct
                var succ = block.Successors.Find(s => s != Exit);
                if (succ != null && succ != nextBlock)
                {
                    var label = EnsureBlockLabel(succ, labels);
                    insts.Add(new Inst { Kind = InstKind.Jump, LabelIndex = label });
                }
                continue;
            }

            var lastInst = insts[lastIdx];

            switch (lastInst.Kind)
            {
                case InstKind.Jump:
                    // Remove jump if target is the next block (fall-through optimization)
                    if (nextBlock != null && BlockHasLabel(nextBlock, lastInst.LabelIndex))
                        insts.RemoveAt(lastIdx);
                    break;

                case InstKind.JumpIfFalse:
                {
                    // False branch is the jump target (specified by the instruction).
                    // True branch is the fall-through successor.
                    // If the true branch is not the next block in RPO, add explicit JUMP.
                    var falseLabel = lastInst.LabelIndex;
                    BasicBlock trueBranch = null;
                    foreach (var succ in block.Successors)
                    {
                        if (succ == Exit) continue;
                        if (!BlockHasLabel(succ, falseLabel))
                        {
                            trueBranch = succ;
                            break;
                        }
                    }
                    if (trueBranch != null && trueBranch != nextBlock)
                    {
                        var label = EnsureBlockLabel(trueBranch, labels);
                        insts.Add(new Inst { Kind = InstKind.Jump, LabelIndex = label });
                    }
                    break;
                }

                case InstKind.JumpIndirect:
                    // No fall-through — execution jumps to address in variable
                    break;

                default:
                {
                    // Non-branch instruction — needs fall-through to successor.
                    // If the successor is not the next block, add explicit JUMP.
                    var succ = block.Successors.Find(s => s != Exit);
                    if (succ != null && succ != nextBlock)
                    {
                        var label = EnsureBlockLabel(succ, labels);
                        insts.Add(new Inst { Kind = InstKind.Jump, LabelIndex = label });
                    }
                    break;
                }
            }
        }

        // Recalculate label addresses
        uint addr = 0;
        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            if (inst.Kind is InstKind.Label or InstKind.Export or InstKind.LabelAddr
                && inst.LabelIndex >= 0)
            {
                var lbl = labels[inst.LabelIndex];
                lbl.Address = addr;
                labels[inst.LabelIndex] = lbl;
            }
            addr += InstInfo.Size[inst.Kind];
        }
    }

    static int EnsureBlockLabel(BasicBlock block, List<JumpLabel> labels)
    {
        var existing = GetBlockLabel(block);
        if (existing >= 0) return existing;
        int idx = labels.Count;
        labels.Add(new JumpLabel { Name = $"__cfg_L{idx}", Address = 0, IsMarked = true });
        block.Instructions.Insert(0, new Inst { Kind = InstKind.Label, LabelIndex = idx });
        return idx;
    }

    static bool BlockHasLabel(BasicBlock block, int labelIndex)
    {
        foreach (var inst in block.Instructions)
            if (inst.Kind is InstKind.Label or InstKind.Export or InstKind.LabelAddr
                && inst.LabelIndex == labelIndex)
                return true;
        return false;
    }
}
