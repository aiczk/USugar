using System;
using System.Collections.Generic;
using System.Linq;

public struct VarTableEntry
{
    public string Id, UdonType, DefaultValue;
    public VarFlags Flags;
    public object ConstValue;
    public string SyncMode; // "none", "linear", "smooth" (null = not synced)
}

public class VariableTable
{
    readonly List<VarTableEntry> _entries = new();
    readonly HashSet<string> _declaredIds = new();
    readonly Dictionary<string, int> _counters = new();
    readonly Dictionary<string, string> _constPool = new();
    readonly Dictionary<string, string> _declaredTypes = new(); // id → udonType
    readonly Dictionary<(string, object), string> _structConstPool = new(); // (udonType, value) → id
    readonly Dictionary<string, string> _thisVars = new(); // udonType → id
    readonly Stack<Dictionary<string, string>> _scopeStack = new();
    Dictionary<string, string> _currentScope = new();


    public VariableTable()
    {
        AddEntry("__refl_typeid", "SystemInt64", null, VarFlags.None);
        AddEntry("__refl_typename", "SystemString", null, VarFlags.None);
        AddEntry("__intnl_returnJump_SystemUInt32_0", "SystemUInt32", "0xFFFFFFFF", VarFlags.None);
    }

    public void SetReflectionValues(long typeId, string typeName)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Id == "__refl_typeid")
            {
                e.ConstValue = typeId;
                _entries[i] = e;
            }
            else if (e.Id == "__refl_typename")
            {
                e.ConstValue = typeName;
                _entries[i] = e;
            }
        }
    }

    public void DeclareReflTypeIds(long[] typeIds)
    {
        var id = "__refl_typeids";
        if (_declaredIds.Contains(id)) return;
        _entries.Add(new VarTableEntry
        {
            Id = id,
            UdonType = "SystemInt64Array",
            DefaultValue = null,
            Flags = VarFlags.None,
            ConstValue = typeIds
        });
        _declaredIds.Add(id);
        _declaredTypes[id] = "SystemInt64Array";
    }

    void AddEntry(string id, string udonType, string defaultValue, VarFlags flags)
    {
        _entries.Add(new VarTableEntry { Id = id, UdonType = udonType, DefaultValue = defaultValue, Flags = flags });
        _declaredIds.Add(id);
        _declaredTypes[id] = udonType;
    }

    int NextIndex(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    public string DeclareVar(string id, string udonType)
    {
        if (_declaredIds.Contains(id))
        {
            if (_declaredTypes[id] != udonType)
                throw new InvalidOperationException(
                    $"Variable '{id}' already declared as {_declaredTypes[id]}, cannot redeclare as {udonType}");
            return id;
        }
        AddEntry(id, udonType, null, VarFlags.None);
        return id;
    }

    /// <summary>Declare a variable only if it doesn't already exist. Returns true if newly declared.</summary>
    public bool TryDeclareVar(string id, string udonType)
    {
        if (_declaredIds.Contains(id))
        {
            if (_declaredTypes[id] != udonType)
                throw new InvalidOperationException(
                    $"Variable '{id}' already declared as {_declaredTypes[id]}, cannot redeclare as {udonType}");
            return false;
        }
        AddEntry(id, udonType, null, VarFlags.None);
        return true;
    }

    public string DeclareField(string name, string udonType, VarFlags flags = VarFlags.None, string defaultValue = null, string syncMode = null)
    {
        if (_declaredIds.Contains(name))
        {
            if (_declaredTypes[name] != udonType)
                throw new InvalidOperationException(
                    $"Field '{name}' already declared as {_declaredTypes[name]}, cannot redeclare as {udonType}");
            return name;
        }
        if (defaultValue != null)
        {
            // Write field defaults via ConstValue → ApplyConstantValues (heap write at compile time),
            // NOT via UASM data section DefaultValue.
            //
            // Why: The UASM assembler only parses a few primitive types in data section defaults.
            // Complex types (Color, Vector3, enum) would fail to parse. ApplyConstantValues
            // bypasses the assembler entirely — it writes CLR objects directly to the Udon heap
            // after assembly, before the program is loaded.
            //
            // Timing matters: heap values are set at compile time, so they're present before
            // ANY Udon event fires. If we used _start to initialize fields instead, _onEnable
            // (which fires first) would see uninitialized values.
            var typed = ParseConstValue(udonType, defaultValue);
            _entries.Add(new VarTableEntry { Id = name, UdonType = udonType, DefaultValue = null, Flags = flags, ConstValue = typed, SyncMode = syncMode });
        }
        else
        {
            _entries.Add(new VarTableEntry { Id = name, UdonType = udonType, DefaultValue = null, Flags = flags, SyncMode = syncMode });
        }
        _declaredIds.Add(name);
        _declaredTypes[name] = udonType;
        _currentScope[name] = name;
        return name;
    }

    public string DeclareLocal(string name, string udonType)
    {
        var idx = NextIndex($"lcl_{name}_{udonType}");
        var id = $"__lcl_{name}_{udonType}_{idx}";
        AddEntry(id, udonType, null, VarFlags.None);
        _currentScope[name] = id;
        return id;
    }

    public string DeclareTemp(string udonType)
    {
        var idx = NextIndex($"intnl_{udonType}");
        var id = $"__intnl_{udonType}_{idx}";
        AddEntry(id, udonType, null, VarFlags.None);
        return id;
    }



    public string DeclareConst(string udonType, string value)
    {
        var key = $"{udonType}_{value}";
        if (_constPool.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"const_{udonType}");
        var id = $"__const_{udonType}_{idx}";
        var typed = ParseConstValue(udonType, value);
        _entries.Add(new VarTableEntry { Id = id, UdonType = udonType, DefaultValue = null, Flags = VarFlags.None, ConstValue = typed });
        _declaredIds.Add(id);
        _declaredTypes[id] = udonType;
        _constPool[key] = id;
        return id;
    }

    // Returns entries where UdonType is "SystemType" and ConstValue is the Udon type name string.
    // USugarCompiler resolves these to actual CLR Type objects at apply time.
    public IEnumerable<VarTableEntry> GetTypeConstEntries()
        => _entries.Where(e => e.UdonType == "SystemType" && e.ConstValue is string);

    static object ParseConstValue(string udonType, string value)
    {
        if (value == "null") return null;
        return udonType switch
        {
            "SystemInt32" => value.StartsWith("0x") ? Convert.ToInt32(value, 16) : int.Parse(value),
            "SystemUInt32" => value.StartsWith("0x") ? Convert.ToUInt32(value, 16) : uint.Parse(value),
            "SystemInt64" => long.Parse(value),
            "SystemUInt64" => ulong.Parse(value),
            "SystemInt16" => short.Parse(value),
            "SystemUInt16" => ushort.Parse(value),
            "SystemSByte" => sbyte.Parse(value),
            "SystemSingle" => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemDouble" => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemBoolean" => bool.Parse(value),
            "SystemString" => value,
            "SystemByte" => byte.Parse(value),
            "SystemChar" => value[0],
            "SystemType" => value, // Udon type name, resolved to CLR Type at apply time
            // Enum types and other SDK types backed by integers (e.g., VRCSDK3DataTokenType).
            // Try widening numeric parse to handle long/ulong enum values.
            _ => long.TryParse(value, out var longVal)
                ? (longVal is >= int.MinValue and <= int.MaxValue ? (object)(int)longVal : longVal)
                : ulong.TryParse(value, out var ulongVal) ? (object)ulongVal : null,
        };
    }

    public List<VarTableEntry> GetConstEntries()
        => _entries.FindAll(e => e.ConstValue != null);

    public void SetConstValue(string id, object value)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == id)
            {
                var entry = _entries[i];
                entry.ConstValue = value;
                _entries[i] = entry;
                return;
            }
        }
    }

    public string DeclareStructConst(string udonType, object value)
    {
        var key = (udonType, value);
        if (_structConstPool.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"const_{udonType}");
        var id = $"__const_{udonType}_{idx}";
        _entries.Add(new VarTableEntry { Id = id, UdonType = udonType, DefaultValue = null, Flags = VarFlags.None, ConstValue = value });
        _declaredIds.Add(id);
        _declaredTypes[id] = udonType;
        _structConstPool[key] = id;
        return id;
    }

    public string DeclareEnumArray(string id, object[] values)
    {
        if (_declaredIds.Contains(id)) return id;
        _entries.Add(new VarTableEntry { Id = id, UdonType = "SystemObjectArray", ConstValue = values });
        _declaredIds.Add(id);
        _declaredTypes[id] = "SystemObjectArray";
        return id;
    }

    static readonly HashSet<string> SupportedThisTypes = new()
    {
        "UnityEngineGameObject", "UnityEngineTransform",
        "VRCUdonUdonBehaviour",
    };

    public string DeclareThis(string udonType)
    {
        // "this" variables use UdonGameObjectComponentHeapReference, which the UASM assembler
        // creates based on the declared type. At runtime, the heap reference resolves to the
        // actual GameObject/Transform/UdonBehaviour.
        //
        // The assembler only supports a fixed set of types for this resolution:
        //   GameObject, Transform, UdonBehaviour, IUdonBehaviour, Object.
        // Any other type (IUdonEventReceiver, Component, MonoBehaviour, etc.) must be remapped
        // to VRCUdonUdonBehaviour. This is safe because "this" always IS the UdonBehaviour,
        // and the caller will cast via extern if it needs a different interface.
        var heapType = SupportedThisTypes.Contains(udonType) ? udonType : "VRCUdonUdonBehaviour";
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        AddEntry(id, heapType, "this", VarFlags.None);
        return id;
    }

    public string DeclareThisOnce(string udonType)
    {
        if (_thisVars.TryGetValue(udonType, out var existing)) return existing;
        var id = DeclareThis(udonType);
        _thisVars[udonType] = id;
        return id;
    }

    public string Lookup(string name) => _currentScope.TryGetValue(name, out var id) ? id : FindInParentScopes(name);

    string FindInParentScopes(string name)
    {
        foreach (var scope in _scopeStack)
            if (scope.TryGetValue(name, out var id)) return id;
        return null;
    }

    public void PushScope()
    {
        _scopeStack.Push(_currentScope);
        _currentScope = new Dictionary<string, string>(_currentScope);
    }

    public void PopScope()
    {
        _currentScope = _scopeStack.Pop();
    }

    public string GetDeclaredType(string id)
        => _declaredTypes.TryGetValue(id, out var t) ? t : throw new InvalidOperationException($"Variable '{id}' not declared");

    public List<VarTableEntry> GetAllEntries() => _entries;
}
