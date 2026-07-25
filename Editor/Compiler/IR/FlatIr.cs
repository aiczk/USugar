using System;
using System.Collections.Generic;

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

/// <summary>A function after structured control flow has been lowered to a flat CFG.</summary>
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

    internal FlatFunction(StructuredFunction source)
        : this(source?.Name ?? throw new ArgumentNullException(nameof(source)), source.ExportName)
    {
        Slots.AddRange(source.Slots);
        ReturnType = source.ReturnType;
        ParamFieldNames.AddRange(source.ParamFieldNames);
        ReturnSlots.AddRange(source.ReturnSlots);
        RecursiveCalleeNames.UnionWith(source.RecursiveCalleeNames);
        RecursionSpillFields.AddRange(source.RecursionSpillFields);
        ReentrantSiteCount = source.ReentrantSiteCount;
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

/// <summary>Top-level flat Core IR. Collection ownership is distinct from the structured input.</summary>
public sealed class FlatModule
{
    public readonly List<FlatFunction> Functions = new List<FlatFunction>();
    public readonly List<FieldDecl> Fields = new List<FieldDecl>();
    public readonly UdonTypeFactRegistry TypeFacts;
    public readonly UdonAbiCatalog AbiCatalog;
    public readonly string ClassName;

    internal FlatModule(StructuredModule source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        foreach (var field in source.Fields)
            Fields.Add(new FieldDecl(field.Name, field.Type, field.Domain)
            {
                DefaultValue = field.DefaultValue,
                Flags = field.Flags,
                SyncMode = field.SyncMode,
            });
        TypeFacts = source.TypeFacts;
        AbiCatalog = source.AbiCatalog;
        ClassName = source.ClassName;
    }

    public FlatModule(UdonTypeFactRegistry typeFacts = null, UdonAbiCatalog abiCatalog = null,
        string className = null)
    {
        TypeFacts = typeFacts ?? new UdonTypeFactRegistry();
        AbiCatalog = abiCatalog;
        ClassName = className;
    }

    public FlatFunction AddFunction(string name, string exportName = null)
    {
        var function = new FlatFunction(name, exportName);
        Functions.Add(function);
        return function;
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
}
