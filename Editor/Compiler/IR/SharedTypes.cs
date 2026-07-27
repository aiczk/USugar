using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

// ============================================================================
// Shared types used across the Core IR and UASM generation.
// ============================================================================

/// <summary>A concrete Udon heap/operand type. This is deliberately distinct from a Roslyn
/// runtime type: several C# types may share one storage representation.</summary>
public readonly struct StorageType : IEquatable<StorageType>
{
    public readonly UdonTypeId Id;
    public string Name => Id.Name;

    public StorageType(string name)
        : this(UdonTypeIdentity.FromCanonicalStorageName(name))
    {
    }

    internal StorageType(UdonTypeId id)
    {
        if (string.IsNullOrEmpty(id.Name))
            throw new ArgumentException(
                "A storage type identity is required.", nameof(id));
        Id = id;
    }

    public bool Equals(StorageType other)
        => Id.Equals(other.Id);
    public override bool Equals(object obj) => obj is StorageType other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Name;

    // UASM signatures and serializers are string protocols, so leaving the storage domain is safe;
    // the guarded direction is RuntimeType -> StorageType, which remains explicit and centralized.
    public static explicit operator string(StorageType type) => type.Name;
    public static bool operator ==(StorageType left, StorageType right) => left.Equals(right);
    public static bool operator !=(StorageType left, StorageType right) => !left.Equals(right);
}

/// <summary>Canonical storage types used by the Udon ABI.</summary>
public static class StorageTypes
{
    public static readonly StorageType Void = new StorageType("SystemVoid");
    public static readonly StorageType Boolean = new StorageType("SystemBoolean");
    public static readonly StorageType Byte = new StorageType("SystemByte");
    public static readonly StorageType Char = new StorageType("SystemChar");
    public static readonly StorageType Int32 = new StorageType("SystemInt32");
    public static readonly StorageType UInt32 = new StorageType("SystemUInt32");
    public static readonly StorageType Int64 = new StorageType("SystemInt64");
    public static readonly StorageType UInt64 = new StorageType("SystemUInt64");
    public static readonly StorageType Single = new StorageType("SystemSingle");
    public static readonly StorageType Double = new StorageType("SystemDouble");
    public static readonly StorageType String = new StorageType("SystemString");
    public static readonly StorageType Object = new StorageType("SystemObject");
    public static readonly StorageType Type = new StorageType("SystemType");
    public static readonly StorageType ObjectArray = new StorageType("SystemObjectArray");
    public static readonly StorageType ByteArray = new StorageType("SystemByteArray");
    public static readonly StorageType CharArray = new StorageType("SystemCharArray");
    public static readonly StorageType Int64Array = new StorageType("SystemInt64Array");
    public static readonly StorageType UnityObject = new StorageType("UnityEngineObject");
    public static readonly StorageType Component = new StorageType("UnityEngineComponent");
    public static readonly StorageType ComponentArray = new StorageType("UnityEngineComponentArray");
    public static readonly StorageType GameObject = new StorageType("UnityEngineGameObject");
    public static readonly StorageType Transform = new StorageType("UnityEngineTransform");
    public static readonly StorageType UdonBehaviour = new StorageType("VRCUdonUdonBehaviour");
    public static readonly StorageType UdonEventReceiver = new StorageType("VRCUdonCommonInterfacesIUdonEventReceiver");
}

/// <summary>
/// Slot classification for variable lifetime management.
/// Pinned slots have fixed UASM names; Frame/Scratch are virtual and coalesced later.
/// </summary>
public enum SlotClass
{
    /// <summary>User field, synced/exported, ABI param/ret, this, delegate convention. Fixed UASM name.</summary>
    Pinned,
    /// <summary>Local that lives across internal calls. Function-private, not shared.</summary>
    Frame,
    /// <summary>Temp that does not span calls. Aggressively reused within a function by CoalesceSlots.</summary>
    Scratch,
}

/// <summary>Declaration of a virtual slot (variable) in the Core IR.</summary>
public sealed class SlotDecl
{
    public readonly int Id;
    public readonly StorageType Type;
    public readonly SlotClass Class;
    /// <summary>Non-null for Pinned slots — the fixed UASM variable name.</summary>
    public readonly string FixedName;

    public SlotDecl(int id, StorageType type, SlotClass slotClass, string fixedName = null)
    {
        Id = id;
        Type = type;
        Class = slotClass;
        FixedName = fixedName;
    }

    public override string ToString()
    {
        var sb = new StringBuilder($"slot{Id}:{Type}[{Class}]");
        if (FixedName != null) sb.Append($" = \"{FixedName}\"");
        return sb.ToString();
    }
}

/// <summary>Flags for field and variable declarations (export, sync).</summary>
[Flags]
public enum FieldFlags
{
    None = 0,
    Export = 1 << 0,
    Sync = 1 << 1,
}

public enum StorageDomain
{
    User,
    Generated,
}

/// <summary>Module-level field declaration (heap variable).</summary>
public sealed class FieldDecl
{
    bool _frozen;
    object _defaultValue;
    FieldFlags _flags;
    string _syncMode;

    public string Name { get; }
    public StorageType Type { get; }
    public StorageDomain Domain { get; }
    public object DefaultValue
    {
        get => _defaultValue;
        set
        {
            ThrowIfFrozen();
            _defaultValue = value;
        }
    }
    public FieldFlags Flags
    {
        get => _flags;
        set
        {
            ThrowIfFrozen();
            _flags = value;
        }
    }
    public string SyncMode
    {
        get => _syncMode;
        set
        {
            ThrowIfFrozen();
            _syncMode = value;
        }
    }

    public FieldDecl(string name, StorageType type, StorageDomain domain = StorageDomain.Generated)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type;
        Domain = domain;
    }

    internal void Freeze() => _frozen = true;

    void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                $"Field '{Name}' is frozen.");
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"field {Name}: {Type}");
        if ((Flags & FieldFlags.Export) != 0) sb.Append(" [export]");
        if ((Flags & FieldFlags.Sync) != 0) sb.Append($" [sync:{SyncMode ?? "none"}]");
        if (DefaultValue != null) sb.Append($" = {DefaultValue}");
        return sb.ToString();
    }
}

/// <summary>Single source for constant-pool keying, shared by CoreBuilder (CConst dedup at IR build
/// time) and CoreToUasm.GetConstVar (UASM __const_ variable dedup). Both pools MUST partition values
/// identically: the key decides which constants share a variable and hence the deterministic
/// __const_{type}_{n} data-section names — a drift between the two formats reshuffles golden UASM.
/// Culture-invariant throughout ("R" for float/double so distinct values never collide on a lossy
/// rendering; the raw object, not this string, is what reaches the data section).</summary>
public readonly struct ConstKey : IEquatable<ConstKey>
{
    public readonly string UdonType;
    public readonly string ValueKind;
    public readonly string CanonicalValue;
    readonly object _identityValue;

    public ConstKey(string udonType, object value)
    {
        UdonType = udonType ?? throw new ArgumentNullException(nameof(udonType));
        ValueKind = value?.GetType().FullName ?? "<null>";
        CanonicalValue = ConstFormat.CanonicalValue(value);
        _identityValue = ConstFormat.UsesReferenceIdentity(value) ? value : null;
    }

    public bool Equals(ConstKey other)
        => string.Equals(UdonType, other.UdonType, StringComparison.Ordinal)
           && string.Equals(ValueKind, other.ValueKind, StringComparison.Ordinal)
           && string.Equals(CanonicalValue, other.CanonicalValue, StringComparison.Ordinal)
           && (_identityValue == null && other._identityValue == null
               || ReferenceEquals(_identityValue, other._identityValue));

    public override bool Equals(object obj) => obj is ConstKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(UdonType);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ValueKind);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(CanonicalValue);
            if (_identityValue != null)
                hash = hash * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_identityValue);
            return hash;
        }
    }

    public override string ToString() => $"{UdonType}:{ValueKind}:{CanonicalValue}";
}

public static class ConstFormat
{
    public static ConstKey Key(string type, object value) => new(type, value);

    internal static string CanonicalValue(object value)
    {
        if (value == null) return "";
        if (value is float f) return BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);
        if (value is double d) return BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
        if (value is decimal dec) return string.Join(":", decimal.GetBits(dec).Select(
            bit => bit.ToString("X8", CultureInfo.InvariantCulture)));
        if (value is char ch) return ((int)ch).ToString("X4", CultureInfo.InvariantCulture);
        if (value is bool b) return b ? "1" : "0";
        if (value is string s) return s;
        if (value.GetType().IsEnum)
            return ((IFormattable)value).ToString("D", CultureInfo.InvariantCulture);
        if (value is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);
        // Constants outside the scalar families are reference values. Identity is the only sound
        // partition: two distinct Unity objects may share the same name/ToString representation.
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)
            .ToString("X8", CultureInfo.InvariantCulture);
    }

    internal static bool UsesReferenceIdentity(object value)
        => value != null
           && value is not string
           && value is not IFormattable
           && !value.GetType().IsEnum;

    public static string Value(object value)
    {
        if (value == null) return "null";
        if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
        if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
        if (value is bool b) return b ? "True" : "False";
        if (value is string s) return s;
        if (value is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString();
    }
}

/// <summary>Software-recursion-stack field names/size, shared by the IR (CoreFlatOptimizer spill/reload)
/// and Emit (LoweringState.EnsureRecursionStack) layers so both sides name the same heap fields.</summary>
public static class RecurStack
{
    public const string StackId = "__recurStack";
    public const string SpId = "__recurSp";
    public const int Size = 64;
}

/// <summary>One intentional storage-mismatched COPY retained as typed codegen provenance.</summary>
public readonly struct RepresentationCopySite
{
    public readonly uint Address;
    public readonly StorageType SourceType;
    public readonly StorageType DestinationType;
    public readonly RepresentationCastKind Kind;

    public RepresentationCopySite(uint address, StorageType sourceType,
        StorageType destinationType, RepresentationCastKind kind)
    {
        Address = address;
        SourceType = sourceType;
        DestinationType = destinationType;
        Kind = kind;
    }
}

/// <summary>Result of UASM code generation.</summary>
public struct CodeGenResult
{
    public string Uasm;
    public uint HeapSize;
    public List<(string Id, string UdonType, object Value)> Constants;
    public List<RepresentationCopySite> RepresentationCopies;
}
