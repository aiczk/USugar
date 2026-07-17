using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// ============================================================================
// Shared types used across the Core IR and UASM generation.
// ============================================================================

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
public static class ConstFormat
{
    public static string Key(string type, object value) => $"{type}_{Value(value)}";

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
