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
    public readonly string Name;

    public StorageType(string name)
        => Name = !string.IsNullOrEmpty(name)
            ? name
            : throw new ArgumentException("A storage type name is required.", nameof(name));

    public bool Equals(StorageType other)
        => string.Equals(Name, other.Name, StringComparison.Ordinal);
    public override bool Equals(object obj) => obj is StorageType other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name ?? "");
    public override string ToString() => Name;

    // Transitional source boundary: producers still mint Udon names as strings. RuntimeType has no
    // such conversion, so semantic runtime identity cannot accidentally enter storage APIs.
    public static implicit operator StorageType(string name) => new StorageType(name);
    public static explicit operator string(StorageType type) => type.Name;
    public static bool operator ==(StorageType left, StorageType right) => left.Equals(right);
    public static bool operator !=(StorageType left, StorageType right) => !left.Equals(right);
}

/// <summary>A closed C# runtime type identity. It must be lowered explicitly before entering Udon
/// storage because that lowering is non-injective for aggregates, delegates, nullable values, and
/// user classes.</summary>
public readonly struct RuntimeType : IEquatable<RuntimeType>
{
    public readonly ITypeSymbol Symbol;

    public RuntimeType(ITypeSymbol symbol)
        => Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));

    public bool Equals(RuntimeType other)
        => SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol);
    public override bool Equals(object obj) => obj is RuntimeType other && Equals(other);
    public override int GetHashCode()
        => Symbol == null ? 0 : SymbolEqualityComparer.Default.GetHashCode(Symbol);
    public override string ToString() => Symbol?.ToDisplayString() ?? "<default>";
    public static bool operator ==(RuntimeType left, RuntimeType right) => left.Equals(right);
    public static bool operator !=(RuntimeType left, RuntimeType right) => !left.Equals(right);
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
    public readonly string Type;
    public readonly SlotClass Class;
    /// <summary>Non-null for Pinned slots — the fixed UASM variable name.</summary>
    public readonly string FixedName;

    public SlotDecl(int id, string type, SlotClass slotClass, string fixedName = null)
    {
        Id = id;
        Type = type ?? throw new ArgumentNullException(nameof(type));
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

/// <summary>Module-level field declaration (heap variable).</summary>
public sealed class FieldDecl
{
    public readonly string Name;
    public readonly string Type;
    public object DefaultValue;
    public FieldFlags Flags;
    public string SyncMode; // "none", "linear", "smooth" (null = not synced)

    public FieldDecl(string name, string type)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type ?? throw new ArgumentNullException(nameof(type));
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

    public ConstKey(string udonType, object value)
    {
        UdonType = udonType ?? throw new ArgumentNullException(nameof(udonType));
        ValueKind = value?.GetType().FullName ?? "<null>";
        CanonicalValue = ConstFormat.CanonicalValue(value);
    }

    public bool Equals(ConstKey other)
        => string.Equals(UdonType, other.UdonType, StringComparison.Ordinal)
           && string.Equals(ValueKind, other.ValueKind, StringComparison.Ordinal)
           && string.Equals(CanonicalValue, other.CanonicalValue, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is ConstKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(UdonType);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ValueKind);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(CanonicalValue);
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
/// and Emit (EmitContext.EnsureRecursionStack) layers so both sides name the same heap fields.</summary>
public static class RecurStack
{
    public const string StackId = "__recurStack";
    public const string SpId = "__recurSp";
    public const int Size = 8192;
}

/// <summary>Result of UASM code generation.</summary>
public struct CodeGenResult
{
    public string Uasm;
    public uint HeapSize;
    public List<(string Id, string UdonType, object Value)> Constants;
    /// <summary>UASM with PC address annotations (for debugging). Null unless dump is enabled.</summary>
    public string AnnotatedUasm;
}
