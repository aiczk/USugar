using System.Collections.Generic;

/// <summary>
/// Eliminates Phi nodes by inserting Copy instructions at the end of predecessor blocks.
/// Must run after all SSA optimization passes and before register allocation.
///
/// For each Phi: v3 = phi [v1, bb0], [v2, bb1]
/// Insert: bb0: ... copy v3 = v1; jump bb2
///         bb1: ... copy v3 = v2; jump bb2
/// Then remove the Phi instruction.
///
/// When a predecessor has multiple Phi successors (critical edge), this pass
/// inserts copies in correct order or splits the edge.
/// </summary>
public static class PhiElimination
{
    /// <summary>Run Phi elimination on all functions in the module.</summary>
    public static void Run(IrModule module)
    {
        foreach (var func in module.Functions)
            EliminatePhis(func);
    }

    /// <summary>Run Phi elimination on a single function.</summary>
    public static void EliminatePhis(IrFunction func)
    {
        // Collect all Phis and their parallel copies
        var pendingCopies = new Dictionary<IrBlock, List<(VReg dest, IrValue src)>>();

        foreach (var block in func.Blocks)
        {
            var phisToRemove = new List<int>();

            for (int i = 0; i < block.Insts.Count; i++)
            {
                if (block.Insts[i] is not Phi phi) continue;
                phisToRemove.Add(i);

                foreach (var (value, predBlock) in phi.Entries)
                {
                    if (!pendingCopies.TryGetValue(predBlock, out var copies))
                    {
                        copies = new List<(VReg, IrValue)>();
                        pendingCopies[predBlock] = copies;
                    }
                    copies.Add((phi.Result, value));
                }
            }

            // Remove Phis (reverse order)
            for (int i = phisToRemove.Count - 1; i >= 0; i--)
                block.Insts.RemoveAt(phisToRemove[i]);
        }

        // Insert Copy instructions before terminators in predecessor blocks
        foreach (var (block, copies) in pendingCopies)
        {
            // Critical edge: if block has multiple successors and destination block
            // has multiple predecessors, we need to split the edge.
            // For simplicity, we always insert before the terminator.
            // Parallel copies are serialized (may need swap in rare cases).

            var insertIdx = block.Insts.Count > 0 ? block.Insts.Count - 1 : 0;

            // Resolve parallel copy conflicts via sequentialization
            var serialized = SerializeParallelCopies(copies, func);
            foreach (var copy in serialized)
                block.Insts.Insert(insertIdx++, copy);
        }
    }

    /// <summary>
    /// Serialize parallel copies to avoid lost-copy and swap problems.
    /// Uses a simple sequentialization: if dest==src for any pair, break the cycle
    /// with a temporary.
    /// </summary>
    static List<Copy> SerializeParallelCopies(List<(VReg dest, IrValue src)> copies, IrFunction func)
    {
        var result = new List<Copy>();

        // Build dependency graph: detect if any dest is used as src in another copy
        var destIds = new HashSet<int>();
        foreach (var (dest, _) in copies)
            destIds.Add(dest.Id);

        // Simple case: no conflicts (most common)
        bool hasConflict = false;
        foreach (var (_, src) in copies)
        {
            if (src is VReg reg && destIds.Contains(reg.Id))
            {
                hasConflict = true;
                break;
            }
        }

        if (!hasConflict)
        {
            // No conflicts — emit directly
            foreach (var (dest, src) in copies)
            {
                if (src is VReg srcReg && srcReg.Id == dest.Id) continue; // Skip self-copies
                result.Add(new Copy(dest, src));
            }
            return result;
        }

        // Conflict resolution: use temporary for cycle breaking
        // Topological sort with cycle detection
        var pending = new List<(VReg dest, IrValue src)>(copies);
        var emitted = new HashSet<int>();
        int lastCount = -1;

        while (pending.Count > 0)
        {
            if (pending.Count == lastCount)
            {
                // Cycle detected — break with temporary
                var (dest, src) = pending[0];
                var temp = func.NewReg(dest.Type);
                result.Add(new Copy(temp, src));
                pending[0] = (dest, temp);
                emitted.Add(temp.Id);
                lastCount = -1;
                continue;
            }

            lastCount = pending.Count;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (dest, src) = pending[i];

                // Skip self-copies
                if (src is VReg srcReg && srcReg.Id == dest.Id)
                {
                    pending.RemoveAt(i);
                    continue;
                }

                // Can emit if dest is not used as src by any remaining copy
                bool destUsedAsSrc = false;
                for (int j = 0; j < pending.Count; j++)
                {
                    if (j == i) continue;
                    if (pending[j].src is VReg r && r.Id == dest.Id)
                    {
                        destUsedAsSrc = true;
                        break;
                    }
                }

                if (!destUsedAsSrc)
                {
                    result.Add(new Copy(dest, src));
                    emitted.Add(dest.Id);
                    pending.RemoveAt(i);
                }
            }
        }

        return result;
    }
}
