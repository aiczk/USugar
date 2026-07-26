using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A basic block in flat Core IR. Structured control flow cannot enter its typed
/// instruction list; control flow is represented exclusively by <see cref="Terminator"/>.</summary>
public sealed class FlatBlock
{
    public readonly List<IFlatInstruction> Instructions = new List<IFlatInstruction>();
    public CTerminator Terminator;
    public readonly int Id;
    public string Hint;

    public FlatBlock(int id) => Id = id;

    public FlatBlock(int id, IEnumerable<IFlatInstruction> instructions,
        CTerminator terminator = null)
    {
        Id = id;
        if (instructions != null) Instructions.AddRange(instructions);
        Terminator = terminator;
    }
}

/// <summary>A function represented directly as a flat control-flow graph.</summary>
public sealed class FlatFunction
{
    public readonly string Name;
    public readonly string ExportName;
    public readonly List<FlatBlock> Blocks = new List<FlatBlock>();
    public readonly List<SlotDecl> Slots = new List<SlotDecl>();
    public StorageType? ReturnType;
    public readonly List<string> ParamFieldNames = new List<string>();
    public readonly List<ReturnSlot> ReturnSlots = new List<ReturnSlot>();
    public readonly HashSet<string> RecursiveCalleeNames = new HashSet<string>();
    public readonly List<(string Name, StorageType Type)> RecursionSpillFields =
        new List<(string, StorageType)>();
    public int ReentrantSiteCount;

    int _nextBlockId;

    public FlatFunction(string name, string exportName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ExportName = exportName;
    }

    public int NewSlot(StorageType type, SlotClass slotClass, string fixedName = null)
    {
        var id = Slots.Count;
        Slots.Add(new SlotDecl(id, type, slotClass, fixedName));
        return id;
    }

    public FlatBlock NewBlock()
    {
        var block = new FlatBlock(_nextBlockId++);
        Blocks.Add(block);
        return block;
    }

    public FlatBlock Entry => Blocks.Count > 0 ? Blocks[0] : null;
    public string ReturnFieldName => ReturnSlots.Count == 1 ? ReturnSlots[0].Id : null;
}

/// <summary>The compiler's sole mutable IR module.</summary>
public sealed class FlatModule
{
    public readonly List<FlatFunction> Functions = new List<FlatFunction>();
    public readonly List<FieldDecl> Fields = new List<FieldDecl>();
    public UdonTypeFactRegistry TypeFacts { get; private set; }
    BoundAbiPlan _abi;
    public string ClassName { get; }

    public FlatModule(UdonTypeFactRegistry typeFacts = null, UdonAbiCatalog abiCatalog = null,
        string className = null)
    {
        TypeFacts = typeFacts ?? new UdonTypeFactRegistry();
        if (abiCatalog != null)
            _abi = BoundAbiPlan.ExactCatalog(abiCatalog);
        ClassName = className;
    }

    internal BoundAbiPlan RequireAbi()
        => _abi ?? throw new InvalidOperationException(
            "The CFG module ABI was not published.");

    internal void PublishSemantics(
        BoundAbiPlan abi,
        UdonTypeFactRegistry typeFacts)
    {
        if (_abi != null)
            throw new InvalidOperationException(
                "The CFG module semantics were published twice.");
        if (typeFacts == null)
            throw new ArgumentNullException(nameof(typeFacts));
        if (!typeFacts.IsFrozen)
            throw new InvalidOperationException(
                "CFG construction requires a frozen type-fact snapshot.");
        _abi = abi ?? throw new ArgumentNullException(nameof(abi));
        TypeFacts = typeFacts;
    }

    public FlatFunction AddFunction(string name, string exportName = null)
    {
        var function = new FlatFunction(name, exportName);
        Functions.Add(function);
        return function;
    }

    internal FlatFunction RequireFunction(string name)
    {
        foreach (var function in Functions)
            if (string.Equals(function.Name, name, StringComparison.Ordinal))
                return function;
        throw new InvalidOperationException(
            $"CFG function '{name}' was absent from the module.");
    }
}

/// <summary>
/// Immutable field declaration published only after the complete flat module has verified.
/// </summary>
public sealed class VerifiedFieldDecl
{
    public string Name { get; }
    public StorageType Type { get; }
    public object DefaultValue { get; }
    public FieldFlags Flags { get; }
    public string SyncMode { get; }
    public StorageDomain Domain { get; }

    internal VerifiedFieldDecl(FieldDecl source)
    {
        Name = source.Name;
        Type = source.Type;
        DefaultValue = source.DefaultValue;
        Flags = source.Flags;
        SyncMode = source.SyncMode;
        Domain = source.Domain;
    }
}

/// <summary>An immutable basic block in a verified flat control-flow graph.</summary>
public sealed class VerifiedFlatBlock
{
    public int Id { get; }
    public string Hint { get; }
    public IReadOnlyList<IFlatInstruction> Instructions { get; }
    public CTerminator Terminator { get; }

    internal VerifiedFlatBlock(FlatBlock source)
    {
        Id = source.Id;
        Hint = source.Hint;
        Instructions = Array.AsReadOnly(source.Instructions.ToArray());
        Terminator = CloneTerminator(source.Terminator);
    }

    static CTerminator CloneTerminator(CTerminator terminator)
    {
        switch (terminator)
        {
            case CJump jump:
                return new CJump(jump.TargetBlockId);
            case CBranch branch:
                return new CBranch(
                    branch.Cond, branch.TrueBlockId, branch.FalseBlockId);
            case CRet ret:
                return new CRet(ret.Value);
            default:
                throw new VerificationException(
                    $"Verified flat module contains unknown terminator "
                    + $"'{terminator?.GetType().Name ?? "<null>"}'.");
        }
    }
}

/// <summary>An immutable function snapshot in a verified flat module.</summary>
public sealed class VerifiedFlatFunction
{
    public string Name { get; }
    public string ExportName { get; }
    public IReadOnlyList<VerifiedFlatBlock> Blocks { get; }
    public IReadOnlyList<SlotDecl> Slots { get; }
    public StorageType? ReturnType { get; }
    public IReadOnlyList<string> ParamFieldNames { get; }
    public IReadOnlyList<ReturnSlot> ReturnSlots { get; }
    public IReadOnlyCollection<string> RecursiveCalleeNames { get; }
    public IReadOnlyList<(string Name, StorageType Type)> RecursionSpillFields { get; }
    public int ReentrantSiteCount { get; }

    internal VerifiedFlatFunction(FlatFunction source)
    {
        Name = source.Name;
        ExportName = source.ExportName;
        Blocks = Array.AsReadOnly(
            source.Blocks.Select(block => new VerifiedFlatBlock(block)).ToArray());
        Slots = Array.AsReadOnly(source.Slots
            .Select(slot => new SlotDecl(slot.Id, slot.Type, slot.Class, slot.FixedName))
            .ToArray());
        ReturnType = source.ReturnType;
        ParamFieldNames = Array.AsReadOnly(source.ParamFieldNames.ToArray());
        ReturnSlots = Array.AsReadOnly(source.ReturnSlots.ToArray());
        RecursiveCalleeNames = Array.AsReadOnly(source.RecursiveCalleeNames.ToArray());
        RecursionSpillFields = Array.AsReadOnly(source.RecursionSpillFields.ToArray());
        ReentrantSiteCount = source.ReentrantSiteCount;
    }

    public VerifiedFlatBlock Entry => Blocks.Count > 0 ? Blocks[0] : null;
    public string ReturnFieldName => ReturnSlots.Count == 1 ? ReturnSlots[0].Id : null;
}

/// <summary>
/// The sole IR accepted by code generation. Construction performs the final verification and
/// snapshots every mutable module/function/block collection, so no optimization or lowering pass
/// can run after this phase boundary.
/// </summary>
public sealed class VerifiedFlatModule
{
    public IReadOnlyList<VerifiedFlatFunction> Functions { get; }
    public IReadOnlyList<VerifiedFieldDecl> Fields { get; }
    public UdonTypeFactRegistry TypeFacts { get; }
    public string ClassName { get; }

    VerifiedFlatModule(FlatModule source)
    {
        Functions = Array.AsReadOnly(source.Functions
            .Select(function => new VerifiedFlatFunction(function)).ToArray());
        Fields = Array.AsReadOnly(source.Fields
            .Select(field => new VerifiedFieldDecl(field)).ToArray());
        TypeFacts = source.TypeFacts;
        ClassName = source.ClassName;
    }

    public static VerifiedFlatModule VerifyAndFreeze(FlatModule module)
    {
        if (module == null) throw new ArgumentNullException(nameof(module));
        FlatVerify.Verify(module);
        return new VerifiedFlatModule(module);
    }
}

/// <summary>Canonical traversal order for reachable blocks in flat Core IR.</summary>
public static class FlatCfgOrder
{
    public static List<FlatBlock> ComputeRpo(FlatFunction function)
    {
        var blocks = new Dictionary<int, FlatBlock>();
        foreach (var block in function.Blocks) blocks[block.Id] = block;

        var visited = new HashSet<int>();
        var postorder = new List<FlatBlock>();
        void Visit(FlatBlock block)
        {
            if (block == null || !visited.Add(block.Id)) return;
            if (block.Terminator != null)
                foreach (var successor in CTerminator.GetSuccessors(block.Terminator))
                    if (blocks.TryGetValue(successor, out var next)) Visit(next);
            postorder.Add(block);
        }

        Visit(function.Entry);
        postorder.Reverse();
        return postorder;
    }

    public static List<VerifiedFlatBlock> ComputeRpo(VerifiedFlatFunction function)
    {
        var blocks = new Dictionary<int, VerifiedFlatBlock>();
        foreach (var block in function.Blocks) blocks[block.Id] = block;

        var visited = new HashSet<int>();
        var postorder = new List<VerifiedFlatBlock>();
        void Visit(VerifiedFlatBlock block)
        {
            if (block == null || !visited.Add(block.Id)) return;
            foreach (var successor in CTerminator.GetSuccessors(block.Terminator))
                if (blocks.TryGetValue(successor, out var next)) Visit(next);
            postorder.Add(block);
        }

        Visit(function.Entry);
        postorder.Reverse();
        return postorder;
    }
}
