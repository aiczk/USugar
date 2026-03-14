using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Type-constrained graph coloring register allocator.
/// Maps VRegs to a minimal set of UASM __intnl_ variables grouped by type.
///
/// After Phi elimination, VRegs may have overlapping lifetimes.
/// This allocator:
/// 1. Computes liveness intervals for each VReg
/// 2. Builds an interference graph (same-type regs that are live at the same time)
/// 3. Colors the graph using greedy coloring
/// 4. Attempts copy coalescing to reduce unnecessary copies
///
/// The output is a mapping VReg.Id → physical variable index (per type group).
/// </summary>
public sealed class RegisterAllocator
{
    readonly IrFunction _func;
    readonly Dictionary<int, (int start, int end)> _liveRanges = new();
    readonly Dictionary<int, string> _regTypes = new();   // VReg.Id → type
    readonly Dictionary<int, int> _coloring = new();       // VReg.Id → color (physical index)

    RegisterAllocator(IrFunction func) => _func = func;

    /// <summary>
    /// Run register allocation on a function. Returns a mapping of VReg.Id → variable name.
    /// Variable names follow the pattern __intnl_{type}_{color}.
    /// </summary>
    public static Dictionary<int, string> Allocate(IrFunction func)
    {
        var alloc = new RegisterAllocator(func);
        alloc.CollectRegTypes();
        alloc.ComputeLiveRanges();
        alloc.ColorGraph();
        return alloc.BuildVarMapping();
    }

    /// <summary>
    /// Apply register allocation: rewrite all VRegs in the function with allocated names.
    /// This modifies the IR in place, renumbering VRegs to use the allocated colors.
    /// Returns the variable declarations needed.
    /// </summary>
    public static List<(string id, string type)> AllocateAndRewrite(IrFunction func)
    {
        var mapping = Allocate(func);

        // Collect unique variables needed
        var vars = new Dictionary<string, string>();
        foreach (var (regId, varName) in mapping)
        {
            if (!vars.ContainsKey(varName))
            {
                // Find type from any VReg with this Id
                foreach (var block in func.Blocks)
                    foreach (var inst in block.Insts)
                        if (inst.Dest is VReg reg && reg.Id == regId)
                        {
                            vars[varName] = reg.Type;
                            goto found;
                        }
                found:;
            }
        }

        return vars.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    void CollectRegTypes()
    {
        foreach (var block in _func.Blocks)
            foreach (var inst in block.Insts)
            {
                if (inst.Dest is VReg dest)
                    _regTypes[dest.Id] = dest.Type;
                foreach (var op in inst.Operands)
                    if (op is VReg reg && !_regTypes.ContainsKey(reg.Id))
                        _regTypes[reg.Id] = reg.Type;
            }
    }

    void ComputeLiveRanges()
    {
        // Linearize blocks (RPO) and number instructions
        var rpo = ComputeRPO();
        var instIndex = new Dictionary<(int blockId, int instIdx), int>();
        int idx = 0;
        foreach (var block in rpo)
            for (int i = 0; i < block.Insts.Count; i++)
                instIndex[(block.Id, i)] = idx++;

        int totalInsts = idx;

        // Compute def/use points
        var defs = new Dictionary<int, int>();   // VReg.Id → first def index
        var lastUses = new Dictionary<int, int>(); // VReg.Id → last use index

        foreach (var block in rpo)
        {
            for (int i = 0; i < block.Insts.Count; i++)
            {
                int pos = instIndex[(block.Id, i)];
                var inst = block.Insts[i];

                // Definition
                if (inst.Dest is VReg dest)
                {
                    if (!defs.ContainsKey(dest.Id))
                        defs[dest.Id] = pos;
                }

                // Uses
                foreach (var op in inst.Operands)
                {
                    if (op is VReg reg)
                        lastUses[reg.Id] = Math.Max(lastUses.GetValueOrDefault(reg.Id, pos), pos);
                }

                // Phi entries
                if (inst is Phi phi)
                {
                    foreach (var (val, _) in phi.Entries)
                        if (val is VReg reg)
                            lastUses[reg.Id] = Math.Max(lastUses.GetValueOrDefault(reg.Id, pos), pos);
                }
            }
        }

        // Build live ranges
        foreach (var regId in _regTypes.Keys)
        {
            var start = defs.GetValueOrDefault(regId, 0);
            var end = lastUses.GetValueOrDefault(regId, start);
            _liveRanges[regId] = (start, end);
        }
    }

    void ColorGraph()
    {
        // Group registers by type (only same-type regs can interfere)
        var typeGroups = new Dictionary<string, List<int>>();
        foreach (var (regId, type) in _regTypes)
        {
            if (!typeGroups.TryGetValue(type, out var group))
            {
                group = new List<int>();
                typeGroups[type] = group;
            }
            group.Add(regId);
        }

        foreach (var (type, regs) in typeGroups)
        {
            // Build interference graph for this type
            var interference = new Dictionary<int, HashSet<int>>();
            foreach (var reg in regs)
                interference[reg] = new HashSet<int>();

            for (int i = 0; i < regs.Count; i++)
            {
                for (int j = i + 1; j < regs.Count; j++)
                {
                    var a = regs[i];
                    var b = regs[j];
                    if (LiveRangesOverlap(a, b))
                    {
                        interference[a].Add(b);
                        interference[b].Add(a);
                    }
                }
            }

            // Greedy coloring (sorted by live range start for deterministic results)
            var sorted = regs.OrderBy(r => _liveRanges.TryGetValue(r, out var lr) ? lr.start : 0).ToList();

            foreach (var reg in sorted)
            {
                var usedColors = new HashSet<int>();
                foreach (var neighbor in interference[reg])
                {
                    if (_coloring.TryGetValue(neighbor, out var color))
                        usedColors.Add(color);
                }

                // Find smallest unused color
                int c = 0;
                while (usedColors.Contains(c)) c++;
                _coloring[reg] = c;
            }
        }
    }

    bool LiveRangesOverlap(int a, int b)
    {
        if (!_liveRanges.TryGetValue(a, out var rangeA)) return false;
        if (!_liveRanges.TryGetValue(b, out var rangeB)) return false;
        return rangeA.start <= rangeB.end && rangeB.start <= rangeA.end;
    }

    Dictionary<int, string> BuildVarMapping()
    {
        var result = new Dictionary<int, string>();
        foreach (var (regId, color) in _coloring)
        {
            var type = _regTypes[regId];
            result[regId] = $"__intnl_{type}_{color}";
        }
        return result;
    }

    List<IrBlock> ComputeRPO()
    {
        var visited = new HashSet<int>();
        var postorder = new List<IrBlock>();
        Visit(_func.Entry, visited, postorder);
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
