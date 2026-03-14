using System;
using System.Collections.Generic;
using System.Text;

// ============================================================================
// Shared types used across HIR, LIR, and UASM generation.
// ============================================================================

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

/// <summary>Result of UASM code generation.</summary>
public struct CodeGenResult
{
    public string Uasm;
    public uint HeapSize;
    public List<(string Id, string UdonType, object Value)> Constants;
    /// <summary>UASM with PC address annotations (for debugging). Null unless dump is enabled.</summary>
    public string AnnotatedUasm;
}
