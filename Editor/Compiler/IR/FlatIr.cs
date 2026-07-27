using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A basic block in the compiler's CFG.</summary>
public sealed class FlatBlock
{
    bool _frozen;
    CTerminator _terminator;
    string _hint;

    public IList<IFlatInstruction> Instructions { get; private set; }
    public int Id { get; }

    public CTerminator Terminator
    {
        get => _terminator;
        set
        {
            ThrowIfFrozen();
            _terminator = value;
        }
    }

    public string Hint
    {
        get => _hint;
        set
        {
            ThrowIfFrozen();
            _hint = value;
        }
    }

    public FlatBlock(int id)
        : this(id, null)
    {
    }

    public FlatBlock(int id, IEnumerable<IFlatInstruction> instructions,
        CTerminator terminator = null)
    {
        Id = id;
        Instructions = instructions == null
            ? new List<IFlatInstruction>()
            : new List<IFlatInstruction>(instructions);
        _terminator = terminator;
    }

    internal void Freeze()
    {
        if (_frozen)
            throw new InvalidOperationException(
                $"CFG block {Id} was frozen twice.");
        Instructions = Array.AsReadOnly(Instructions.ToArray());
        _frozen = true;
    }

    void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                $"CFG block {Id} is frozen.");
    }
}

/// <summary>A function represented directly as a flat control-flow graph.</summary>
public sealed class FlatFunction
{
    bool _frozen;
    int _nextBlockId;
    StorageType? _returnType;
    int _reentrantSiteCount;
    readonly HashSet<string> _recursiveCalleeNames =
        new(StringComparer.Ordinal);

    public string Name { get; }
    public string ExportName { get; }
    public IList<FlatBlock> Blocks { get; private set; } =
        new List<FlatBlock>();
    public IList<SlotDecl> Slots { get; private set; } =
        new List<SlotDecl>();
    public IList<string> ParamFieldNames { get; private set; } =
        new List<string>();
    public IList<ReturnSlot> ReturnSlots { get; private set; } =
        new List<ReturnSlot>();
    public IReadOnlyCollection<string> RecursiveCalleeNames
        => _recursiveCalleeNames;
    public IList<(string Name, StorageType Type)> RecursionSpillFields
        { get; private set; } =
            new List<(string, StorageType)>();

    public StorageType? ReturnType
    {
        get => _returnType;
        set
        {
            ThrowIfFrozen();
            _returnType = value;
        }
    }

    public int ReentrantSiteCount
    {
        get => _reentrantSiteCount;
        set
        {
            ThrowIfFrozen();
            _reentrantSiteCount = value;
        }
    }

    public FlatFunction(string name, string exportName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ExportName = exportName;
    }

    public int NewSlot(StorageType type, SlotClass slotClass,
        string fixedName = null)
    {
        ThrowIfFrozen();
        var id = Slots.Count;
        Slots.Add(new SlotDecl(id, type, slotClass, fixedName));
        return id;
    }

    public FlatBlock NewBlock()
    {
        ThrowIfFrozen();
        var block = new FlatBlock(_nextBlockId++);
        Blocks.Add(block);
        return block;
    }

    public void AddRecursiveCallee(string name)
    {
        ThrowIfFrozen();
        _recursiveCalleeNames.Add(
            name ?? throw new ArgumentNullException(nameof(name)));
    }

    public FlatBlock Entry => Blocks.Count > 0 ? Blocks[0] : null;
    public string ReturnFieldName
        => ReturnSlots.Count == 1 ? ReturnSlots[0].Id : null;

    internal void Freeze()
    {
        if (_frozen)
            throw new InvalidOperationException(
                $"CFG function '{Name}' was frozen twice.");
        foreach (var block in Blocks)
            block.Freeze();
        Blocks = Array.AsReadOnly(Blocks.ToArray());
        Slots = Array.AsReadOnly(Slots.ToArray());
        ParamFieldNames =
            Array.AsReadOnly(ParamFieldNames.ToArray());
        ReturnSlots = Array.AsReadOnly(ReturnSlots.ToArray());
        RecursionSpillFields =
            Array.AsReadOnly(RecursionSpillFields.ToArray());
        _frozen = true;
    }

    void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                $"CFG function '{Name}' is frozen.");
    }
}

/// <summary>The compiler's sole mutable IR module.</summary>
public sealed class FlatModule
{
    bool _frozen;
    BoundAbiPlan _abi;

    public IList<FlatFunction> Functions { get; private set; } =
        new List<FlatFunction>();
    public IList<FieldDecl> Fields { get; private set; } =
        new List<FieldDecl>();
    public UdonTypeFactRegistry TypeFacts { get; private set; }
    public string ClassName { get; }

    public FlatModule(
        UdonTypeFactRegistry typeFacts = null,
        UdonAbiCatalog abiCatalog = null,
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
        ThrowIfFrozen();
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

    public FlatFunction AddFunction(
        string name, string exportName = null)
    {
        ThrowIfFrozen();
        var function = new FlatFunction(name, exportName);
        Functions.Add(function);
        return function;
    }

    public void AddField(FieldDecl field)
    {
        ThrowIfFrozen();
        Fields.Add(field ?? throw new ArgumentNullException(nameof(field)));
    }

    internal FlatFunction RequireFunction(string name)
    {
        foreach (var function in Functions)
            if (string.Equals(
                function.Name, name, StringComparison.Ordinal))
                return function;
        throw new InvalidOperationException(
            $"CFG function '{name}' was absent from the module.");
    }

    internal void Freeze()
    {
        ThrowIfFrozen();
        foreach (var function in Functions)
            function.Freeze();
        foreach (var field in Fields)
            field.Freeze();
        Functions = Array.AsReadOnly(Functions.ToArray());
        Fields = Array.AsReadOnly(Fields.ToArray());
        _frozen = true;
    }

    void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "The CFG module is frozen.");
    }
}

/// <summary>
/// The sole phase token accepted by code generation. Verification freezes the mutable CFG in
/// place; functions, blocks, fields, and their collections cannot change across this boundary.
/// </summary>
internal sealed class VerifiedFlatModule
{
    public IReadOnlyList<FlatFunction> Functions { get; }
    public IReadOnlyList<FieldDecl> Fields { get; }
    public UdonTypeFactRegistry TypeFacts { get; }
    public string ClassName { get; }

    VerifiedFlatModule(FlatModule source)
    {
        source.Freeze();
        Functions = (IReadOnlyList<FlatFunction>)source.Functions;
        Fields = (IReadOnlyList<FieldDecl>)source.Fields;
        TypeFacts = source.TypeFacts;
        ClassName = source.ClassName;
    }

    public static VerifiedFlatModule VerifyAndFreeze(
        FlatModule module)
    {
        if (module == null)
            throw new ArgumentNullException(nameof(module));
        FlatVerify.Verify(module);
        FlatVerify.VerifySpillsInserted(module);
        return new VerifiedFlatModule(module);
    }
}

/// <summary>Canonical traversal order for reachable CFG blocks.</summary>
internal static class FlatCfgOrder
{
    public static List<FlatBlock> ComputeRpo(FlatFunction function)
    {
        var blocks = new Dictionary<int, FlatBlock>();
        foreach (var block in function.Blocks)
            blocks[block.Id] = block;

        var visited = new HashSet<int>();
        var postorder = new List<FlatBlock>();
        void Visit(FlatBlock block)
        {
            if (block == null || !visited.Add(block.Id))
                return;
            if (block.Terminator != null)
                foreach (var successor
                         in CTerminator.GetSuccessors(block.Terminator))
                    if (blocks.TryGetValue(successor, out var next))
                        Visit(next);
            postorder.Add(block);
        }

        Visit(function.Entry);
        postorder.Reverse();
        return postorder;
    }
}
