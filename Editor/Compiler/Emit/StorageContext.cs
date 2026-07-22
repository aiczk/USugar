using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns emitted field declarations, generated storage names, and flat local bindings.
/// </summary>
public sealed class StorageContext
{
    readonly CModule _module;
    readonly Dictionary<string, int> _counters = new();
    readonly HashSet<string> _declaredFieldNames = new();
    readonly Dictionary<StorageType, string> _thisVars = new();
    readonly Dictionary<string, string> _structConstIds = new();
    bool _recurStackDeclared;

    public readonly Dictionary<ILocalSymbol, EmitContext.LocalBinding> LocalBindings = new(SymbolEqualityComparer.Default);

    public StorageContext(CModule module) => _module = module;

    int NextIndex(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    public string DeclareField(string name, StorageType type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        if (_declaredFieldNames.Contains(name)) return name;
        var field = new FieldDecl(name, new StorageType(type.Name)) { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode };
        _module.Fields.Add(field);
        _declaredFieldNames.Add(name);
        return name;
    }

    public string DeclareVar(string id, StorageType type)
    {
        if (_declaredFieldNames.Contains(id)) return id;
        _module.Fields.Add(new FieldDecl(id, new StorageType(type.Name)));
        _declaredFieldNames.Add(id);
        return id;
    }

    public bool TryDeclareVar(string id, StorageType type)
    {
        if (_declaredFieldNames.Contains(id)) return false;
        _module.Fields.Add(new FieldDecl(id, new StorageType(type.Name)));
        _declaredFieldNames.Add(id);
        return true;
    }

    public string DeclareLocal(string name, StorageType type)
    {
        var idx = NextIndex($"lcl_{name}_{type.Name}");
        var id = $"__lcl_{name}_{type.Name}_{idx}";
        _module.Fields.Add(new FieldDecl(id, new StorageType(type.Name)));
        _declaredFieldNames.Add(id);
        return id;
    }

    public string DeclareThis(StorageType udonType)
    {
        StorageType heapType = SupportedThisTypes.Contains(udonType)
            ? udonType
            : new StorageType("VRCUdonUdonBehaviour");
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        _module.Fields.Add(new FieldDecl(id, new StorageType(heapType.Name)) { DefaultValue = "this" });
        _declaredFieldNames.Add(id);
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
        new StorageType("UnityEngineGameObject"), new StorageType("UnityEngineTransform"),
        new StorageType("VRCUdonUdonBehaviour"),
    };

    public void EnsureRecursionStack()
    {
        if (_recurStackDeclared) return;
        _recurStackDeclared = true;
        _module.Fields.Add(new FieldDecl(EmitContext.RecurStackId, new StorageType("SystemObjectArray")) { DefaultValue = new object[EmitContext.RecurStackSize] });
        _declaredFieldNames.Add(EmitContext.RecurStackId);
        _module.Fields.Add(new FieldDecl(EmitContext.RecurSpId, new StorageType("SystemInt32")) { DefaultValue = 0 });
        _declaredFieldNames.Add(EmitContext.RecurSpId);
    }

    public void SetFieldConstValue(string name, object value)
    {
        var field = _module.Fields.FirstOrDefault(f => f.Name == name);
        if (field != null) field.DefaultValue = value;
    }

    public bool IsFieldDeclared(string name) => _declaredFieldNames.Contains(name);

    public string DeclareStructConst(StorageType type, object value)
    {
        var key = $"{type.Name}_{value}";
        if (_structConstIds.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"structconst_{type.Name}");
        var id = $"__const_{type.Name}_{idx}";
        _module.Fields.Add(new FieldDecl(id, new StorageType(type.Name)) { DefaultValue = value });
        _declaredFieldNames.Add(id);
        _structConstIds[key] = id;
        return id;
    }

    public StorageType? GetFieldType(string id)
        => _module.Fields.FirstOrDefault(f => f.Name == id)?.Type;
}
