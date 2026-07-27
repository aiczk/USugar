using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Persistent non-captured local symbol to generated field identity.</summary>
public readonly struct LocalBinding
{
    public readonly string Id;
    public LocalBinding(string id) => Id = id;
}

/// <summary>
/// Runtime-selectable prepared storage locations for one ref local. The selector is a flat slot,
/// so a ref reassignment emitted inside a branch changes the alias only when that branch executes.
/// </summary>
public sealed class RefLocalBinding
{
    readonly struct Candidate
    {
        public readonly Func<CLeaf> Read;
        public readonly Action<CLeaf> Write;

        public Candidate(Func<CLeaf> read, Action<CLeaf> write)
        {
            Read = read ?? throw new ArgumentNullException(nameof(read));
            Write = write ?? throw new ArgumentNullException(nameof(write));
        }
    }

    readonly CoreBuilder _builder;
    readonly StorageType _type;
    readonly List<Candidate> _candidates;
    readonly int _selectorSlot;

    public RefLocalBinding(CoreBuilder builder, StorageType type,
        Func<CLeaf> read, Action<CLeaf> write)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _type = type;
        _candidates = new List<Candidate> { new Candidate(read, write) };
        _selectorSlot = _builder.AllocFrame(StorageTypes.Int32);
        _builder.EmitAssign(_selectorSlot,
            _builder.Const(0, StorageTypes.Int32));
    }

    RefLocalBinding(RefLocalBinding source)
    {
        _builder = source._builder;
        _type = source._type;
        _candidates = new List<Candidate>(source._candidates);
        _selectorSlot = _builder.AllocFrame(StorageTypes.Int32);
        _builder.EmitAssign(_selectorSlot,
            _builder.SlotRef(source._selectorSlot));
    }

    public RefLocalBinding Clone() => new RefLocalBinding(this);

    public void Reassign(RefLocalBinding source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (ReferenceEquals(this, source)) return;
        if (_type != source._type)
            throw new InvalidOperationException(
                $"Cannot reassign ref local of '{_type}' from '{source._type}'.");

        var offset = _candidates.Count;
        _candidates.AddRange(source._candidates);
        CLeaf selector = _builder.SlotRef(source._selectorSlot);
        if (offset != 0)
            selector = _builder.ExternCall(
                UdonAbi.Int32Add,
                new List<CLeaf>
                {
                    selector,
                    _builder.Const(offset, StorageTypes.Int32)
                },
                StorageTypes.Int32);
        _builder.EmitAssign(_selectorSlot, selector);
    }

    public CLeaf Read()
    {
        if (_candidates.Count == 1)
            return _candidates[0].Read();

        var result = _builder.AllocScratch(_type);
        EmitSelected(0, candidate =>
            _builder.EmitAssign(result, candidate.Read()));
        return _builder.SlotRef(result);
    }

    public void Write(CLeaf value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (value.Type != _type)
            throw new InvalidOperationException(
                $"Cannot write '{value.Type}' through ref local of '{_type}'.");
        if (_candidates.Count == 1)
        {
            _candidates[0].Write(value);
            return;
        }
        EmitSelected(0, candidate => candidate.Write(value));
    }

    void EmitSelected(int index, Action<Candidate> emit)
    {
        if (index == _candidates.Count - 1)
        {
            emit(_candidates[index]);
            return;
        }
        var selected = _builder.ExternCall(
            UdonAbi.Int32Equality,
            new List<CLeaf>
            {
                _builder.SlotRef(_selectorSlot),
                _builder.Const(index, StorageTypes.Int32)
            },
            StorageTypes.Boolean);
        _builder.EmitIf(selected,
            _ => emit(_candidates[index]),
            _ => EmitSelected(index + 1, emit));
    }
}

/// <summary>
/// Owns emitted field declarations, generated storage names, and flat local bindings.
/// </summary>
public sealed class StorageContext
{
    readonly FlatModule _module;
    readonly Dictionary<string, int> _counters = new();
    readonly Dictionary<string, FieldDecl> _declarations = new(StringComparer.Ordinal);
    readonly Dictionary<StorageType, string> _thisVars = new();
    readonly Dictionary<string, string> _structConstIds = new();
    bool _recurStackDeclared;

    public readonly Dictionary<ILocalSymbol, LocalBinding> LocalBindings = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<ILocalSymbol, RefLocalBinding>
        RefLocalBindings = new(SymbolEqualityComparer.Default);

    public StorageContext(FlatModule module) => _module = module;

    int NextIndex(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    public string DeclareField(string name, StorageType type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        Declare(new FieldDecl(name, type, StorageDomain.User)
            { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode });
        return name;
    }

    public string DeclareGeneratedField(string name, StorageType type,
        object defaultValue = null)
    {
        Declare(new FieldDecl(name, type, StorageDomain.Generated) { DefaultValue = defaultValue });
        return name;
    }

    public string DeclareVar(string id, StorageType type)
    {
        Declare(new FieldDecl(id, type, StorageDomain.Generated));
        return id;
    }

    public bool TryDeclareVar(string id, StorageType type)
    {
        if (_declarations.TryGetValue(id, out var existing))
        {
            EnsureCompatible(existing, new FieldDecl(id, type, StorageDomain.Generated));
            return false;
        }
        Declare(new FieldDecl(id, type, StorageDomain.Generated));
        return true;
    }

    public string DeclareLocal(string name, StorageType type)
    {
        var idx = NextIndex($"lcl_{name}_{type.Name}");
        var id = $"__lcl_{name}_{type.Name}_{idx}";
        Declare(new FieldDecl(id, type, StorageDomain.Generated));
        return id;
    }

    public string DeclareThis(StorageType udonType)
    {
        StorageType heapType = SupportedThisTypes.Contains(udonType)
            ? udonType
            : StorageTypes.UdonBehaviour;
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        Declare(new FieldDecl(id, heapType, StorageDomain.Generated) { DefaultValue = "this" });
        return id;
    }

    public string DeclareThisOnce(StorageType udonType)
    {
        if (_thisVars.TryGetValue(udonType, out var existing)) return existing;
        var id = DeclareThis(udonType);
        _thisVars[udonType] = id;
        return id;
    }

    static readonly HashSet<StorageType> SupportedThisTypes = new()
    {
        StorageTypes.GameObject, StorageTypes.Transform,
        StorageTypes.UdonBehaviour,
    };

    public void EnsureRecursionStack()
    {
        if (_recurStackDeclared) return;
        _recurStackDeclared = true;
        Declare(new FieldDecl(RecurStack.StackId, StorageTypes.ObjectArray, StorageDomain.Generated)
            { DefaultValue = new object[RecurStack.Size] });
        Declare(new FieldDecl(RecurStack.SpId, StorageTypes.Int32, StorageDomain.Generated)
            { DefaultValue = 0 });
    }

    internal void DeclarePlannedField(FieldDecl declaration)
        => Declare(declaration ?? throw new ArgumentNullException(nameof(declaration)));

    public string DeclareStructConst(StorageType type, object value)
    {
        var key = $"{type.Name}_{value}";
        if (_structConstIds.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"structconst_{type.Name}");
        var id = $"__const_{type.Name}_{idx}";
        Declare(new FieldDecl(id, type, StorageDomain.Generated) { DefaultValue = value });
        _structConstIds[key] = id;
        return id;
    }

    public StorageType? GetFieldType(string id)
        => _declarations.TryGetValue(id, out var declaration) ? declaration.Type : null;

    void Declare(FieldDecl declaration)
    {
        if (_declarations.TryGetValue(declaration.Name, out var existing))
        {
            EnsureCompatible(existing, declaration);
            return;
        }
        _declarations.Add(declaration.Name, declaration);
        _module.Fields.Add(declaration);
    }

    static void EnsureCompatible(FieldDecl existing, FieldDecl requested)
    {
        if (existing.Domain == requested.Domain
            && existing.Type == requested.Type
            && existing.Flags == requested.Flags
            && string.Equals(existing.SyncMode, requested.SyncMode, StringComparison.Ordinal)
            && Equals(existing.DefaultValue, requested.DefaultValue))
            return;
        throw new InvalidOperationException(
            $"Storage '{requested.Name}' declaration conflicts: "
            + $"existing {existing.Domain}/{existing.Type}, requested {requested.Domain}/{requested.Type}.");
    }
}
