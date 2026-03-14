using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// SSA-based optimization passes for the IR.
/// All passes operate on IrFunction and preserve SSA form.
/// </summary>
public static class IrOptimizer
{
    /// <summary>Run the full optimization pipeline on a module.</summary>
    public static void Optimize(IrModule module, int optLevel = 5)
    {
        foreach (var func in module.Functions)
            OptimizeFunction(func, optLevel);
    }

    /// <summary>Run the full optimization pipeline on a single function.</summary>
    public static void OptimizeFunction(IrFunction func, int optLevel = 5)
    {
        SimplifyCFG(func);
        if (optLevel >= 1) ConstantFolding(func);
        if (optLevel >= 1) CopyPropagation(func);
        DCE(func);
        if (optLevel >= 2) SimplifyCFG(func);
        if (optLevel >= 3) GVN(func);
        if (optLevel >= 4) LICM(func);
        DCE(func);
        SimplifyCFG(func);
    }

    // ========================================================================
    // SimplifyCFG: Simplify control flow graph
    // ========================================================================

    /// <summary>
    /// Simplify CFG: thread jumps, remove empty blocks, merge blocks, remove unreachable.
    /// </summary>
    public static void SimplifyCFG(IrFunction func)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            changed |= ThreadJumps(func);
            changed |= RemoveEmptyBlocks(func);
            changed |= MergeBlocks(func);
            changed |= RemoveUnreachableBlocks(func);
        }
    }

    /// <summary>Redirect jumps that target blocks consisting only of a Jump.</summary>
    static bool ThreadJumps(IrFunction func)
    {
        bool changed = false;
        foreach (var block in func.Blocks)
        {
            var term = block.Terminator;
            if (term is Jump jump && jump.Target != block)
            {
                var target = ResolveJumpChain(jump.Target);
                if (target != jump.Target)
                {
                    UpdatePhiPredecessors(target, jump.Target, block);
                    IrFunction.UnlinkBlocks(block, jump.Target);
                    jump.Target = target;
                    IrFunction.LinkBlocks(block, target);
                    changed = true;
                }
            }
            else if (term is Branch branch)
            {
                var trueResolved = ResolveJumpChain(branch.TrueTarget);
                var falseResolved = ResolveJumpChain(branch.FalseTarget);
                if (trueResolved != branch.TrueTarget || falseResolved != branch.FalseTarget)
                {
                    // Update Phi entries in resolved targets: replace old intermediate block with this block
                    if (trueResolved != branch.TrueTarget)
                        UpdatePhiPredecessors(trueResolved, branch.TrueTarget, block);
                    if (falseResolved != branch.FalseTarget)
                        UpdatePhiPredecessors(falseResolved, branch.FalseTarget, block);
                    branch.TrueTarget = trueResolved;
                    branch.FalseTarget = falseResolved;
                    func.RebuildEdges();
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>Update Phi entries in target block: replace references to oldPred with newPred.</summary>
    static void UpdatePhiPredecessors(IrBlock target, IrBlock oldPred, IrBlock newPred)
    {
        foreach (var inst in target.Insts)
        {
            if (inst is not Phi phi) break; // Phis are always at the top
            for (int i = 0; i < phi.Entries.Count; i++)
            {
                if (phi.Entries[i].Block == oldPred)
                    phi.Entries[i] = (phi.Entries[i].Value, newPred);
            }
        }
    }

    static IrBlock ResolveJumpChain(IrBlock block)
    {
        var visited = new HashSet<int>();
        while (block.Insts.Count == 1 && block.Terminator is Jump j
               && visited.Add(block.Id))
        {
            block = j.Target;
        }
        return block;
    }

    /// <summary>Remove blocks that contain only an unconditional jump (no Phis in target).</summary>
    static bool RemoveEmptyBlocks(IrFunction func)
    {
        bool changed = false;
        var toRemove = new List<IrBlock>();

        foreach (var block in func.Blocks)
        {
            if (block == func.Entry) continue;
            if (block.Insts.Count != 1) continue;
            if (block.Terminator is not Jump jump) continue;
            var target = jump.Target;
            if (target == block) continue;

            // Don't remove if target has Phis that reference this block
            bool hasPhiRef = false;
            foreach (var inst in target.Insts)
            {
                if (inst is not Phi phi) break;
                foreach (var (_, b) in phi.Entries)
                    if (b == block) { hasPhiRef = true; break; }
                if (hasPhiRef) break;
            }
            if (hasPhiRef) continue;

            // Redirect all predecessors to target
            foreach (var pred in block.Predecessors.ToArray())
            {
                RedirectTerminator(pred, block, target);
                IrFunction.UnlinkBlocks(pred, block);
                IrFunction.LinkBlocks(pred, target);
            }

            IrFunction.UnlinkBlocks(block, target);
            toRemove.Add(block);
            changed = true;
        }

        foreach (var block in toRemove)
            func.Blocks.Remove(block);
        return changed;
    }

    /// <summary>Merge block with its unique successor when successor has only one predecessor.</summary>
    static bool MergeBlocks(IrFunction func)
    {
        bool changed = false;

        for (int i = 0; i < func.Blocks.Count; i++)
        {
            var block = func.Blocks[i];
            if (block.Terminator is not Jump jump) continue;
            var succ = jump.Target;
            if (succ == block) continue;
            if (succ.Predecessors.Count != 1) continue;
            if (succ == func.Entry) continue;

            // Merge: remove jump, append successor's instructions
            block.Insts.RemoveAt(block.Insts.Count - 1); // remove Jump
            block.Insts.AddRange(succ.Insts);

            // Update edges
            IrFunction.UnlinkBlocks(block, succ);
            foreach (var succSucc in succ.Successors.ToArray())
            {
                IrFunction.UnlinkBlocks(succ, succSucc);
                IrFunction.LinkBlocks(block, succSucc);
                // Update terminator references
                RedirectTerminator(block, succ, succSucc);
                // Update Phi entries
                RedirectPhiEntries(succSucc, succ, block);
            }

            func.Blocks.Remove(succ);
            i--; // re-check this block
            changed = true;
        }

        return changed;
    }

    /// <summary>Remove blocks not reachable from entry.</summary>
    static bool RemoveUnreachableBlocks(IrFunction func)
    {
        var reachable = new HashSet<int>();
        var worklist = new Queue<IrBlock>();
        worklist.Enqueue(func.Entry);
        reachable.Add(func.Entry.Id);

        while (worklist.Count > 0)
        {
            var block = worklist.Dequeue();
            foreach (var succ in block.Successors)
            {
                if (reachable.Add(succ.Id))
                    worklist.Enqueue(succ);
            }
        }

        var toRemove = func.Blocks.Where(b => !reachable.Contains(b.Id)).ToList();
        if (toRemove.Count == 0) return false;

        foreach (var block in toRemove)
        {
            // Remove Phi entries referencing this block
            foreach (var succ in block.Successors)
            {
                foreach (var inst in succ.Insts)
                {
                    if (inst is not Phi phi) break;
                    phi.Entries.RemoveAll(e => e.Block == block);
                }
                succ.Predecessors.Remove(block);
            }
            func.Blocks.Remove(block);
        }

        return true;
    }

    static void RedirectTerminator(IrBlock block, IrBlock from, IrBlock to)
    {
        switch (block.Terminator)
        {
            case Jump j when j.Target == from:
                j.Target = to;
                break;
            case Branch b:
                if (b.TrueTarget == from) b.TrueTarget = to;
                if (b.FalseTarget == from) b.FalseTarget = to;
                break;
        }
    }

    static void RedirectPhiEntries(IrBlock block, IrBlock from, IrBlock to)
    {
        foreach (var inst in block.Insts)
        {
            if (inst is not Phi phi) break;
            for (int i = 0; i < phi.Entries.Count; i++)
            {
                var (val, b) = phi.Entries[i];
                if (b == from)
                    phi.Entries[i] = (val, to);
            }
        }
    }

    // ========================================================================
    // Constant Folding
    // ========================================================================

    /// <summary>Evaluate pure extern calls with constant arguments at compile time.</summary>
    public static void ConstantFolding(IrFunction func)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in func.Blocks)
            {
                for (int i = 0; i < block.Insts.Count; i++)
                {
                    if (block.Insts[i] is not CallExtern call) continue;
                    if (!call.IsPure || call.Result == null) continue;

                    // All args must be constants
                    bool allConst = true;
                    foreach (var arg in call.Args)
                    {
                        if (arg is not IrConst) { allConst = false; break; }
                    }
                    if (!allConst) continue;

                    var result = TryEvaluateExtern(call.ExternSig,
                        Array.ConvertAll(call.Args, a => ((IrConst)a).Value));
                    if (result == null) continue;

                    // Replace call with constant
                    var constVal = new IrConst(result, call.Result.Type);
                    ReplaceAllUses(func, call.Result, constVal);
                    block.Insts.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }
    }

    static object TryEvaluateExtern(string sig, object[] args)
    {
        // Extract operation name: Type.__op_Name__Params__RetType
        var dotIdx = sig.IndexOf(".__");
        if (dotIdx < 0) return null;
        var afterDot = sig.Substring(dotIdx + 1);

        try
        {
            if (afterDot.StartsWith("__op_Addition__") && args.Length == 2)
                return EvalBinary(args[0], args[1], (a, b) => a + b, (a, b) => a + b, (a, b) => a + b);
            if (afterDot.StartsWith("__op_Subtraction__") && args.Length == 2)
                return EvalBinary(args[0], args[1], (a, b) => a - b, (a, b) => a - b, (a, b) => a - b);
            if (afterDot.StartsWith("__op_Multiplication__") && args.Length == 2)
                return EvalBinary(args[0], args[1], (a, b) => a * b, (a, b) => a * b, (a, b) => a * b);
            if (afterDot.StartsWith("__op_Division__") && args.Length == 2)
            {
                // Avoid division by zero
                if (args[1] is int i2 && i2 == 0) return null;
                if (args[1] is float f2 && f2 == 0f) return null;
                if (args[1] is double d2 && d2 == 0.0) return null;
                return EvalBinary(args[0], args[1], (a, b) => a / b, (a, b) => a / b, (a, b) => a / b);
            }
            if (afterDot.StartsWith("__op_Remainder__") && args.Length == 2)
            {
                if (args[1] is int i2 && i2 == 0) return null;
                return EvalBinary(args[0], args[1], (a, b) => a % b, (a, b) => a % b, (a, b) => a % b);
            }
            if (afterDot.StartsWith("__op_Equality__") && args.Length == 2)
                return EvalComparison(args[0], args[1], (a, b) => a == b, (a, b) => a == b, (a, b) => a == b);
            if (afterDot.StartsWith("__op_Inequality__") && args.Length == 2)
                return EvalComparison(args[0], args[1], (a, b) => a != b, (a, b) => a != b, (a, b) => a != b);
            if (afterDot.StartsWith("__op_LessThan__") && args.Length == 2)
                return EvalComparison(args[0], args[1], (a, b) => a < b, (a, b) => a < b, (a, b) => a < b);
            if (afterDot.StartsWith("__op_GreaterThan__") && args.Length == 2)
                return EvalComparison(args[0], args[1], (a, b) => a > b, (a, b) => a > b, (a, b) => a > b);
            if (afterDot.StartsWith("__op_UnaryNegation__") && args.Length == 1)
            {
                if (args[0] is int i) return -i;
                if (args[0] is float f) return -f;
                if (args[0] is double d) return -d;
            }
            if (afterDot.StartsWith("__op_LogicalAnd__") && args.Length == 2)
            {
                if (args[0] is int a && args[1] is int b) return a & b;
            }
            if (afterDot.StartsWith("__op_LogicalOr__") && args.Length == 2)
            {
                if (args[0] is int a && args[1] is int b) return a | b;
            }
            if (afterDot.StartsWith("__op_LogicalXor__") && args.Length == 2)
            {
                if (args[0] is int a && args[1] is int b) return a ^ b;
            }
        }
        catch { return null; }

        return null;
    }

    static object EvalBinary(object a, object b,
        Func<int, int, int> intOp, Func<float, float, float> floatOp, Func<double, double, double> doubleOp)
    {
        if (a is int ai && b is int bi) return intOp(ai, bi);
        if (a is float af && b is float bf) return floatOp(af, bf);
        if (a is double ad && b is double bd) return doubleOp(ad, bd);
        return null;
    }

    static object EvalComparison(object a, object b,
        Func<int, int, bool> intOp, Func<float, float, bool> floatOp, Func<double, double, bool> doubleOp)
    {
        if (a is int ai && b is int bi) return intOp(ai, bi);
        if (a is float af && b is float bf) return floatOp(af, bf);
        if (a is double ad && b is double bd) return doubleOp(ad, bd);
        return null;
    }

    // ========================================================================
    // Copy Propagation
    // ========================================================================

    /// <summary>Replace uses of Copy destinations with their sources.</summary>
    public static void CopyPropagation(IrFunction func)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in func.Blocks)
            {
                for (int i = 0; i < block.Insts.Count; i++)
                {
                    if (block.Insts[i] is not Copy copy) continue;
                    ReplaceAllUses(func, copy.Result, copy.Source);
                    block.Insts.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }
    }

    // ========================================================================
    // Dead Code Elimination (DCE)
    // ========================================================================

    /// <summary>Remove instructions whose results are never used.</summary>
    public static void DCE(IrFunction func)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;

            // Build use counts
            var useCounts = new Dictionary<int, int>(); // VReg.Id → use count
            foreach (var block in func.Blocks)
            {
                foreach (var inst in block.Insts)
                {
                    foreach (var op in inst.Operands)
                        if (op is VReg reg)
                            useCounts[reg.Id] = useCounts.GetValueOrDefault(reg.Id) + 1;

                    // Phi entries also count as uses
                    if (inst is Phi phi)
                        foreach (var (val, _) in phi.Entries)
                            if (val is VReg reg)
                                useCounts[reg.Id] = useCounts.GetValueOrDefault(reg.Id) + 1;
                }
            }

            foreach (var block in func.Blocks)
            {
                for (int i = 0; i < block.Insts.Count; i++)
                {
                    var inst = block.Insts[i];
                    if (inst.Dest == null) continue;
                    if (useCounts.GetValueOrDefault(inst.Dest.Id) > 0) continue;

                    // Don't remove instructions with side effects
                    if (inst is CallExtern call && !call.IsPure) continue;
                    if (inst is CallInternal) continue;
                    if (inst is StoreField) continue;

                    // Don't remove terminators
                    if (inst is Jump or Branch or Return or Unreachable) continue;

                    block.Insts.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }
    }

    // ========================================================================
    // Global Value Numbering (GVN)
    // ========================================================================

    /// <summary>
    /// Replace redundant computations with earlier equivalent results.
    /// Uses dominator-based value numbering.
    /// </summary>
    public static void GVN(IrFunction func)
    {
        var idom = ComputeIDomMap(func);
        var domChildren = BuildDomTree(func, idom);

        // Value table: instruction signature → VReg result
        var valueTable = new Dictionary<string, VReg>();

        GVNBlock(func.Entry, domChildren, valueTable);
    }

    static void GVNBlock(IrBlock block, Dictionary<IrBlock, List<IrBlock>> domChildren,
        Dictionary<string, VReg> valueTable)
    {
        var added = new List<string>();

        for (int i = 0; i < block.Insts.Count; i++)
        {
            var inst = block.Insts[i];
            if (inst is not CallExtern call || !call.IsPure || call.Result == null)
                continue;

            var key = ComputeGVNKey(call);
            if (valueTable.TryGetValue(key, out var existing))
            {
                // Replace with existing value
                ReplaceAllUsesInBlock(block, i + 1, call.Result, existing);
                // Also replace in dominated blocks
                ReplaceAllUsesInFunc(block, call.Result, existing, new HashSet<int> { block.Id });
                block.Insts.RemoveAt(i);
                i--;
            }
            else
            {
                valueTable[key] = call.Result;
                added.Add(key);
            }
        }

        if (domChildren.TryGetValue(block, out var children))
        {
            foreach (var child in children)
                GVNBlock(child, domChildren, valueTable);
        }

        // Remove entries added in this scope
        foreach (var key in added)
            valueTable.Remove(key);
    }

    static string ComputeGVNKey(CallExtern call)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(call.ExternSig);
        foreach (var arg in call.Args)
        {
            sb.Append(',');
            if (arg is VReg reg) sb.Append($"v{reg.Id}");
            else if (arg is IrConst c) sb.Append($"c:{c.Value}:{c.Type}");
        }
        return sb.ToString();
    }

    static void ReplaceAllUsesInBlock(IrBlock block, int startIdx, VReg oldVal, IrValue newVal)
    {
        for (int i = startIdx; i < block.Insts.Count; i++)
            block.Insts[i] = ReplaceInInst(block.Insts[i], oldVal, newVal);
    }

    static void ReplaceAllUsesInFunc(IrBlock excludeBlock, VReg oldVal, IrValue newVal, HashSet<int> visited)
    {
        foreach (var succ in excludeBlock.Successors)
        {
            if (!visited.Add(succ.Id)) continue;
            for (int i = 0; i < succ.Insts.Count; i++)
                succ.Insts[i] = ReplaceInInst(succ.Insts[i], oldVal, newVal);
            ReplaceAllUsesInFunc(succ, oldVal, newVal, visited);
        }
    }

    // ========================================================================
    // Loop Invariant Code Motion (LICM)
    // ========================================================================

    /// <summary>Hoist loop-invariant computations to preheader blocks.</summary>
    public static void LICM(IrFunction func)
    {
        var idom = ComputeIDomMap(func);
        var loops = DetectNaturalLoops(func, idom);

        foreach (var loop in loops)
        {
            // Find or create preheader
            var header = loop.Header;
            var preheader = FindOrCreatePreheader(func, loop);

            // Collect instructions that can be hoisted
            var toHoist = new List<(IrBlock block, int index, IrInst inst)>();

            foreach (var block in loop.Blocks)
            {
                for (int i = 0; i < block.Insts.Count; i++)
                {
                    var inst = block.Insts[i];
                    if (inst is not CallExtern call || !call.IsPure || call.Result == null) continue;

                    // Check: all operands are either constants or defined outside the loop
                    bool allInvariant = true;
                    foreach (var op in call.Args)
                    {
                        if (op is IrConst) continue;
                        if (op is VReg reg && !IsDefinedInLoop(reg, loop))
                            continue;
                        allInvariant = false;
                        break;
                    }

                    if (allInvariant)
                        toHoist.Add((block, i, inst));
                }
            }

            // Hoist (insert before preheader's terminator)
            foreach (var (block, _, inst) in toHoist)
            {
                block.Insts.Remove(inst);
                var insertPos = preheader.Insts.Count > 0 ? preheader.Insts.Count - 1 : 0;
                preheader.Insts.Insert(insertPos, inst);
            }
        }
    }

    struct NaturalLoop
    {
        public IrBlock Header;
        public HashSet<IrBlock> Blocks;
        public IrBlock BackEdgeSource;
    }

    static List<NaturalLoop> DetectNaturalLoops(IrFunction func, Dictionary<IrBlock, IrBlock> idom)
    {
        var loops = new List<NaturalLoop>();

        foreach (var block in func.Blocks)
        {
            foreach (var succ in block.Successors)
            {
                // Back edge: block → succ where succ dominates block
                if (Dominates(idom, succ, block, func.Entry))
                {
                    var loopBlocks = new HashSet<IrBlock> { succ };
                    var worklist = new Stack<IrBlock>();
                    if (block != succ)
                    {
                        loopBlocks.Add(block);
                        worklist.Push(block);
                    }
                    while (worklist.Count > 0)
                    {
                        var b = worklist.Pop();
                        foreach (var pred in b.Predecessors)
                        {
                            if (loopBlocks.Add(pred))
                                worklist.Push(pred);
                        }
                    }
                    loops.Add(new NaturalLoop
                    {
                        Header = succ,
                        Blocks = loopBlocks,
                        BackEdgeSource = block,
                    });
                }
            }
        }

        return loops;
    }

    static IrBlock FindOrCreatePreheader(IrFunction func, NaturalLoop loop)
    {
        var header = loop.Header;
        var outsidePreds = header.Predecessors.Where(p => !loop.Blocks.Contains(p)).ToList();

        if (outsidePreds.Count == 1 && outsidePreds[0].Successors.Count == 1)
            return outsidePreds[0]; // Already a preheader

        // Create preheader
        var preheader = func.NewBlock();
        preheader.Append(new Jump(header));
        IrFunction.LinkBlocks(preheader, header);

        // Redirect outside predecessors to preheader
        foreach (var pred in outsidePreds)
        {
            RedirectTerminator(pred, header, preheader);
            IrFunction.UnlinkBlocks(pred, header);
            IrFunction.LinkBlocks(pred, preheader);
        }

        // Update Phi entries
        foreach (var inst in header.Insts)
        {
            if (inst is not Phi phi) break;
            for (int i = 0; i < phi.Entries.Count; i++)
            {
                var (val, b) = phi.Entries[i];
                if (outsidePreds.Contains(b))
                    phi.Entries[i] = (val, preheader);
            }
        }

        return preheader;
    }

    static bool IsDefinedInLoop(VReg reg, NaturalLoop loop)
    {
        foreach (var block in loop.Blocks)
            foreach (var inst in block.Insts)
                if (inst.Dest != null && inst.Dest.Id == reg.Id)
                    return true;
        return false;
    }

    static bool Dominates(Dictionary<IrBlock, IrBlock> idom, IrBlock dominator, IrBlock block, IrBlock entry)
    {
        if (dominator == block) return true;
        var current = block;
        while (idom.TryGetValue(current, out var parent))
        {
            if (parent == dominator) return true;
            if (parent == current) break;
            current = parent;
        }
        return dominator == entry;
    }

    // ========================================================================
    // Shared Utilities
    // ========================================================================

    /// <summary>Replace all uses of oldVal with newVal throughout the function.</summary>
    public static void ReplaceAllUses(IrFunction func, VReg oldVal, IrValue newVal)
    {
        foreach (var block in func.Blocks)
        {
            for (int i = 0; i < block.Insts.Count; i++)
                block.Insts[i] = ReplaceInInst(block.Insts[i], oldVal, newVal);
        }
    }

    static IrInst ReplaceInInst(IrInst inst, VReg oldVal, IrValue newVal)
    {
        switch (inst)
        {
            case CallExtern call:
                var newArgs = ReplaceInArray(call.Args, oldVal, newVal);
                return newArgs != call.Args
                    ? new CallExtern(call.Result, call.ExternSig, newArgs, call.IsPure)
                    : call;

            case CallInternal call:
                var newArgs2 = ReplaceInArray(call.Args, oldVal, newVal);
                return newArgs2 != call.Args
                    ? new CallInternal(call.Result, call.Target, newArgs2)
                    : call;

            case StoreField store:
                var newStoreVal = ReplaceVal(store.Value, oldVal, newVal);
                return newStoreVal != store.Value
                    ? new StoreField(store.FieldName, newStoreVal)
                    : store;

            case Select sel:
                var nc = ReplaceVal(sel.Cond, oldVal, newVal);
                var nt = ReplaceVal(sel.TrueValue, oldVal, newVal);
                var nf = ReplaceVal(sel.FalseValue, oldVal, newVal);
                return (nc != sel.Cond || nt != sel.TrueValue || nf != sel.FalseValue)
                    ? new Select(sel.Result, nc, nt, nf)
                    : sel;

            case Phi phi:
                bool phiChanged = false;
                for (int i = 0; i < phi.Entries.Count; i++)
                {
                    var (val, block) = phi.Entries[i];
                    var nv = ReplaceVal(val, oldVal, newVal);
                    if (nv != val) { phi.Entries[i] = (nv, block); phiChanged = true; }
                }
                return inst;

            case Branch branch:
                var newCond = ReplaceVal(branch.Cond, oldVal, newVal);
                return newCond != branch.Cond
                    ? new Branch(newCond, branch.TrueTarget, branch.FalseTarget)
                    : branch;

            case Return ret:
                if (ret.Value == null) return ret;
                var newRetVal = ReplaceVal(ret.Value, oldVal, newVal);
                return newRetVal != ret.Value ? new Return(newRetVal) : ret;

            case Copy copy:
                var newSrc = ReplaceVal(copy.Source, oldVal, newVal);
                return newSrc != copy.Source ? new Copy(copy.Result, newSrc) : copy;

            default:
                return inst;
        }
    }

    static IrValue ReplaceVal(IrValue val, VReg oldVal, IrValue newVal)
        => val is VReg reg && reg.Id == oldVal.Id ? newVal : val;

    static IrValue[] ReplaceInArray(IrValue[] arr, VReg oldVal, IrValue newVal)
    {
        IrValue[] result = null;
        for (int i = 0; i < arr.Length; i++)
        {
            var replaced = ReplaceVal(arr[i], oldVal, newVal);
            if (replaced != arr[i])
            {
                result ??= (IrValue[])arr.Clone();
                result[i] = replaced;
            }
        }
        return result ?? arr;
    }

    // ── Dominance computation ──

    static Dictionary<IrBlock, IrBlock> ComputeIDomMap(IrFunction func)
    {
        var rpo = ComputeRPO(func);
        var blockIndex = new Dictionary<int, int>();
        for (int i = 0; i < rpo.Count; i++)
            blockIndex[rpo[i].Id] = i;

        var idom = new int[rpo.Count];
        for (int i = 0; i < idom.Length; i++) idom[i] = -1;
        idom[0] = 0;

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 1; i < rpo.Count; i++)
            {
                var block = rpo[i];
                int newIdom = -1;
                foreach (var pred in block.Predecessors)
                {
                    if (!blockIndex.TryGetValue(pred.Id, out var predIdx)) continue;
                    if (idom[predIdx] == -1) continue;
                    newIdom = newIdom == -1 ? predIdx : Intersect(idom, newIdom, predIdx);
                }
                if (newIdom != -1 && newIdom != idom[i])
                {
                    idom[i] = newIdom;
                    changed = true;
                }
            }
        }

        var result = new Dictionary<IrBlock, IrBlock>();
        for (int i = 0; i < rpo.Count; i++)
        {
            if (idom[i] >= 0 && idom[i] != i)
                result[rpo[i]] = rpo[idom[i]];
        }
        return result;
    }

    static int Intersect(int[] idom, int b1, int b2)
    {
        while (b1 != b2)
        {
            while (b1 > b2) b1 = idom[b1];
            while (b2 > b1) b2 = idom[b2];
        }
        return b1;
    }

    static Dictionary<IrBlock, List<IrBlock>> BuildDomTree(IrFunction func, Dictionary<IrBlock, IrBlock> idom)
    {
        var children = new Dictionary<IrBlock, List<IrBlock>>();
        foreach (var block in func.Blocks)
            children[block] = new List<IrBlock>();
        foreach (var (child, parent) in idom)
        {
            if (children.ContainsKey(parent))
                children[parent].Add(child);
        }
        return children;
    }

    static List<IrBlock> ComputeRPO(IrFunction func)
    {
        var visited = new HashSet<int>();
        var postorder = new List<IrBlock>();
        Visit(func.Entry, visited, postorder);
        postorder.Reverse();
        return postorder;

        static void Visit(IrBlock block, HashSet<int> visited, List<IrBlock> postorder)
        {
            if (!visited.Add(block.Id)) return;
            foreach (var succ in block.Successors)
                Visit(succ, visited, postorder);
            postorder.Add(block);
        }
    }
}
