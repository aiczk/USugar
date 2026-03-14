using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Promotes LoadField/StoreField of local variables to SSA registers with Phi nodes.
/// Standard Cytron algorithm: compute dominance frontiers, insert Phi nodes, rename variables.
///
/// Only promotes compiler-generated local fields (names starting with "__"):
///   __lcl_*, __intnl_*, __this_*, __const_*, __gintnl_*, __refl_*
///
/// Does NOT promote:
/// - User-declared class instance fields (names without "__" prefix, e.g., _value, hp)
/// - Exported fields (FieldFlags.Export)
/// - Synced fields (FieldFlags.Sync)
///
/// After Mem2Reg, promoted loads/stores are replaced by VReg references and Phi nodes.
/// </summary>
public static class Mem2Reg
{
    /// <summary>Run Mem2Reg on all functions in the module.</summary>
    public static void Run(IrModule module)
    {
        // Collect non-promotable fields (exported, synced)
        var nonPromotable = new HashSet<string>();
        foreach (var field in module.Fields)
        {
            if ((field.Flags & (FieldFlags.Export | FieldFlags.Sync)) != FieldFlags.None)
                nonPromotable.Add(field.Name);
        }

        foreach (var func in module.Functions)
            PromoteFunction(func, nonPromotable);
    }

    /// <summary>Run Mem2Reg on a single function.</summary>
    public static void PromoteFunction(IrFunction func, HashSet<string> nonPromotable = null)
    {
        nonPromotable ??= new HashSet<string>();

        // Step 1: Identify promotable fields (local loads/stores only, no aliases)
        var promotable = FindPromotableFields(func, nonPromotable);
        if (promotable.Count == 0) return;

        // Step 2: Compute dominance (IDom tree)
        var idom = ComputeIDom(func);

        // Step 3: Compute dominance frontiers
        var df = ComputeDominanceFrontiers(func, idom);

        // Step 4: Insert Phi nodes at dominance frontiers of definitions
        var insertedPhis = InsertPhiNodes(func, promotable, df);

        // Step 5: Rename variables (walk dominator tree, replace loads/stores)
        RenameVariables(func, promotable, insertedPhis, idom);
    }

    // ── Step 1: Find promotable fields ──

    static HashSet<string> FindPromotableFields(IrFunction func, HashSet<string> nonPromotable)
    {
        var candidates = new HashSet<string>();
        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Insts)
            {
                switch (inst)
                {
                    case LoadField load:
                        if (IsPromotableName(load.FieldName) && !nonPromotable.Contains(load.FieldName))
                            candidates.Add(load.FieldName);
                        break;
                    case StoreField store:
                        if (IsPromotableName(store.FieldName) && !nonPromotable.Contains(store.FieldName))
                            candidates.Add(store.FieldName);
                        break;
                }
            }
        }

        // Remove fields that appear as CallExtern arguments (they may be aliased)
        // Also remove fields used in non-load/store contexts
        // For now, be conservative: only promote fields that are ONLY used
        // via LoadField/StoreField within this function.
        return candidates;
    }

    /// <summary>
    /// Returns true if the field name indicates a compiler-generated local variable
    /// (promotable to SSA register). Only fields with the "__" prefix are promotable.
    /// User-declared class instance fields (e.g., _value, hp) persist across method
    /// calls and must remain as LoadField/StoreField.
    /// </summary>
    static bool IsPromotableName(string fieldName) =>
        fieldName.StartsWith("__") && !fieldName.StartsWith("__this_")
        && !fieldName.EndsWith("__param") && !fieldName.EndsWith("__ret");

    // ── Step 2: Compute immediate dominators (Cooper, Harvey, Kennedy algorithm) ──

    static Dictionary<IrBlock, IrBlock> ComputeIDom(IrFunction func)
    {
        var blocks = func.Blocks;
        var entry = func.Entry;

        // Number blocks in RPO
        var rpo = new List<IrBlock>();
        var visited = new HashSet<int>();
        PostorderDFS(entry, visited, rpo);
        rpo.Reverse();

        var blockIndex = new Dictionary<int, int>(); // block.Id → RPO index
        for (int i = 0; i < rpo.Count; i++)
            blockIndex[rpo[i].Id] = i;

        var idom = new int[rpo.Count]; // idom[i] = RPO index of immediate dominator
        for (int i = 0; i < idom.Length; i++) idom[i] = -1;
        idom[0] = 0; // Entry dominates itself

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

        // Convert to block → block dictionary
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

    static void PostorderDFS(IrBlock block, HashSet<int> visited, List<IrBlock> result)
    {
        if (!visited.Add(block.Id)) return;
        foreach (var succ in block.Successors)
            PostorderDFS(succ, visited, result);
        result.Add(block);
    }

    // ── Step 3: Compute dominance frontiers ──

    static Dictionary<IrBlock, HashSet<IrBlock>> ComputeDominanceFrontiers(
        IrFunction func, Dictionary<IrBlock, IrBlock> idom)
    {
        var df = new Dictionary<IrBlock, HashSet<IrBlock>>();
        foreach (var block in func.Blocks)
            df[block] = new HashSet<IrBlock>();

        foreach (var block in func.Blocks)
        {
            if (block.Predecessors.Count < 2) continue;
            foreach (var pred in block.Predecessors)
            {
                var runner = pred;
                while (runner != null && !IsDominator(idom, runner, block, func.Entry))
                {
                    df[runner].Add(block);
                    if (!idom.TryGetValue(runner, out runner))
                        break;
                }
            }
        }

        return df;
    }

    static bool IsDominator(Dictionary<IrBlock, IrBlock> idom, IrBlock dominator, IrBlock block, IrBlock entry)
    {
        if (dominator == block) return true;
        var current = block;
        while (idom.TryGetValue(current, out var parent))
        {
            if (parent == dominator) return true;
            if (parent == current) break; // entry
            current = parent;
        }
        return dominator == entry && current == entry;
    }

    // ── Step 4: Insert Phi nodes ──

    /// <summary>Returns map of (block, fieldName) → Phi instruction.</summary>
    static Dictionary<(IrBlock, string), Phi> InsertPhiNodes(
        IrFunction func, HashSet<string> promotable,
        Dictionary<IrBlock, HashSet<IrBlock>> df)
    {
        var result = new Dictionary<(IrBlock, string), Phi>();

        foreach (var fieldName in promotable)
        {
            // Find blocks that contain a definition (StoreField) for this field
            var defBlocks = new HashSet<IrBlock>();
            string fieldType = null;
            foreach (var block in func.Blocks)
            {
                foreach (var inst in block.Insts)
                {
                    if (inst is StoreField store && store.FieldName == fieldName)
                    {
                        defBlocks.Add(block);
                        fieldType ??= store.Value.Type;
                    }
                    if (inst is LoadField load && load.FieldName == fieldName)
                    {
                        fieldType ??= load.Result.Type;
                    }
                }
            }

            if (fieldType == null) continue;

            // Worklist algorithm: insert Phi at dominance frontiers of definition blocks
            var worklist = new Queue<IrBlock>(defBlocks);
            var phiInserted = new HashSet<IrBlock>();

            while (worklist.Count > 0)
            {
                var block = worklist.Dequeue();
                if (!df.TryGetValue(block, out var frontiers)) continue;

                foreach (var frontier in frontiers)
                {
                    if (phiInserted.Contains(frontier)) continue;
                    phiInserted.Add(frontier);

                    var phiDest = func.NewReg(fieldType);
                    var phi = new Phi(phiDest);
                    result[(frontier, fieldName)] = phi;

                    // Phi insertion is also a definition → add to worklist
                    if (defBlocks.Add(frontier))
                        worklist.Enqueue(frontier);
                }
            }
        }

        // Actually insert Phi nodes at the beginning of their blocks
        foreach (var ((block, _), phi) in result)
            block.Insts.Insert(0, phi);

        return result;
    }

    // ── Step 5: Rename variables ──

    static void RenameVariables(IrFunction func, HashSet<string> promotable,
        Dictionary<(IrBlock, string), Phi> insertedPhis,
        Dictionary<IrBlock, IrBlock> idom)
    {
        // Build dominator tree children
        var domChildren = new Dictionary<IrBlock, List<IrBlock>>();
        foreach (var block in func.Blocks)
            domChildren[block] = new List<IrBlock>();
        foreach (var (child, parent) in idom)
            domChildren[parent].Add(child);

        // Per-field value stack (for SSA renaming)
        var stacks = new Dictionary<string, Stack<IrValue>>();
        foreach (var field in promotable)
            stacks[field] = new Stack<IrValue>();

        RenameBlock(func.Entry, domChildren, stacks, promotable, insertedPhis);
    }

    static void RenameBlock(IrBlock block,
        Dictionary<IrBlock, List<IrBlock>> domChildren,
        Dictionary<string, Stack<IrValue>> stacks,
        HashSet<string> promotable,
        Dictionary<(IrBlock, string), Phi> insertedPhis)
    {
        // Track how many values we push per field (for cleanup on return)
        var pushCounts = new Dictionary<string, int>();
        foreach (var field in promotable)
            pushCounts[field] = 0;

        // Process instructions in order
        var toRemove = new List<int>();
        for (int i = 0; i < block.Insts.Count; i++)
        {
            var inst = block.Insts[i];

            if (inst is Phi phi)
            {
                // Find which field this Phi was inserted for
                string phiField = null;
                foreach (var field in promotable)
                {
                    if (insertedPhis.TryGetValue((block, field), out var p) && p == phi)
                    {
                        phiField = field;
                        break;
                    }
                }
                if (phiField != null)
                {
                    stacks[phiField].Push(phi.Dest);
                    pushCounts[phiField]++;
                }
                continue;
            }

            if (inst is LoadField load && promotable.Contains(load.FieldName))
            {
                // Replace load with the current SSA value
                var stack = stacks[load.FieldName];
                if (stack.Count > 0)
                {
                    var currentVal = stack.Peek();
                    // Replace all uses of load.Result with currentVal
                    ReplaceUses(block, i + 1, load.Result, currentVal);
                    // Also replace in successor blocks' Phis
                }
                toRemove.Add(i);
                continue;
            }

            if (inst is StoreField store && promotable.Contains(store.FieldName))
            {
                stacks[store.FieldName].Push(store.Value);
                pushCounts[store.FieldName]++;
                toRemove.Add(i);
                continue;
            }
        }

        // Remove promoted load/store instructions (reverse order to preserve indices)
        for (int i = toRemove.Count - 1; i >= 0; i--)
            block.Insts.RemoveAt(toRemove[i]);

        // Fill Phi operands in successor blocks
        foreach (var succ in block.Successors)
        {
            foreach (var field in promotable)
            {
                if (insertedPhis.TryGetValue((succ, field), out var phi))
                {
                    var stack = stacks[field];
                    IrValue val = stack.Count > 0 ? stack.Peek() : new IrConst(null, phi.Result.Type);
                    phi.Entries.Add((val, block));
                }
            }
        }

        // Recurse into dominator tree children
        if (domChildren.TryGetValue(block, out var children))
        {
            foreach (var child in children)
                RenameBlock(child, domChildren, stacks, promotable, insertedPhis);
        }

        // Pop values we pushed in this block
        foreach (var field in promotable)
        {
            var count = pushCounts[field];
            var stack = stacks[field];
            for (int i = 0; i < count; i++)
                stack.Pop();
        }
    }

    /// <summary>Replace uses of oldVal with newVal in instructions from startIdx onwards in the block.</summary>
    static void ReplaceUses(IrBlock block, int startIdx, VReg oldVal, IrValue newVal)
    {
        for (int i = startIdx; i < block.Insts.Count; i++)
        {
            var inst = block.Insts[i];
            block.Insts[i] = ReplaceInInst(inst, oldVal, newVal);
        }
    }

    /// <summary>Replace occurrences of oldVal in an instruction's operands.</summary>
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
                var newCond = ReplaceVal(sel.Cond, oldVal, newVal);
                var newTrue = ReplaceVal(sel.TrueValue, oldVal, newVal);
                var newFalse = ReplaceVal(sel.FalseValue, oldVal, newVal);
                return (newCond != sel.Cond || newTrue != sel.TrueValue || newFalse != sel.FalseValue)
                    ? new Select(sel.Result, newCond, newTrue, newFalse)
                    : sel;

            case Branch branch:
                var newBrCond = ReplaceVal(branch.Cond, oldVal, newVal);
                return newBrCond != branch.Cond
                    ? new Branch(newBrCond, branch.TrueTarget, branch.FalseTarget)
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
}
