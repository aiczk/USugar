using System;
using System.Collections.Generic;

/// <summary>
/// The identifier token accepted by VRC's UAssembly scanner. Keep this as the single
/// compiler/test authority: the scanner accepts a wider continuation set than its
/// start set, and promotes these exact spellings to non-identifier keyword tokens.
/// </summary>
public static class UasmSymbolRules
{
    static readonly HashSet<string> ReservedWords =
        new(StringComparer.Ordinal)
        {
            "this", "null", "true", "false",
            "NOP", "PUSH", "POP", "JUMP_IF_FALSE", "JUMP",
            "EXTERN", "ANNOTATION", "JUMP_INDIRECT", "COPY",
        };

    public static bool IsIdentifierStart(char c)
        => char.IsLetter(c) || c == '_';

    public static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c == '_'
           || c == '<' || c == '>' || c == '[' || c == ']';

    public static string WhyInvalidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "the symbol is empty";
        if (!IsIdentifierStart(name[0]))
            return $"'{name[0]}' is not legal at the start of a UASM identifier";
        for (var i = 1; i < name.Length; i++)
            if (!IsIdentifierPart(name[i]))
                return $"'{name[i]}' is not legal in a UASM identifier";
        if (ReservedWords.Contains(name))
            return $"'{name}' is a reserved UASM token";
        return null;
    }

    public static bool IsIdentifier(string name)
        => WhyInvalidIdentifier(name) == null;

    public static void RequireIdentifier(string name, string context)
    {
        var why = WhyInvalidIdentifier(name);
        if (why != null)
            throw new InvalidOperationException(
                $"{context} '{name}' is not a legal UASM symbol: {why}.");
    }
}

/// <summary>
/// Fresh-name allocator for one complete UASM namespace. Fixed/user-authored names
/// are reserved up front; compiler-generated names retain their historical spelling
/// unless that spelling is already occupied.
/// </summary>
internal sealed class GeneratedNameAllocator
{
    readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public GeneratedNameAllocator()
    {
    }

    public GeneratedNameAllocator(IEnumerable<string> reservedNames)
    {
        if (reservedNames == null) return;
        foreach (var name in reservedNames)
            Reserve(name);
    }

    public void Reserve(string name)
    {
        if (!string.IsNullOrEmpty(name))
            _used.Add(name);
    }

    public string Allocate(string preferredName)
    {
        UasmSymbolRules.RequireIdentifier(
            preferredName, "Generated symbol");
        if (_used.Add(preferredName))
            return preferredName;

        for (var suffix = 1; ; suffix++)
        {
            var candidate = preferredName + "_" + suffix;
            if (_used.Add(candidate))
                return candidate;
        }
    }
}

/// <summary>
/// Manages counter-based unique ID allocation for UASM variable naming.
/// Wraps the __N_key naming convention used by UdonSharp.
/// </summary>
public class NameAllocator
{
    readonly Dictionary<string, int> _counters = new();

    public NameAllocator() { }

    public NameAllocator(IReadOnlyDictionary<string, int> initialCounters)
    {
        foreach (var kvp in initialCounters)
            _counters[kvp.Key] = kvp.Value;
    }

    public IReadOnlyDictionary<string, int> GetCounters()
        => new Dictionary<string, int>(_counters);

    /// <summary>
    /// Allocate the next counter value for the given key.
    /// First call for a key returns 0, second returns 1, etc.
    /// </summary>
    public int Allocate(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    /// <summary>Format a key + counter into "__N_key" form.</summary>
    public static string FormatId(string key, int counter) => $"__{counter}_{key}";

    public static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!UasmSymbolRules.IsIdentifierPart(chars[i]))
                chars[i] = '_';
        var sanitized = new string(chars);
        return UasmSymbolRules.IsIdentifierStart(sanitized[0])
            ? sanitized
            : "_" + sanitized;
    }

    // The method-layout naming contract (LayoutPlanBuilder exports + every synthetic bridge): allocator
    // keys "{name}__param"/"{name}__ret", counter-qualified slot ids "__N_{name}__param"/
    // "__N_{name}__ret", and the "{function}__body" entry label (past the sentinel push). These
    // formatters are the ONLY producers of the __param/__ret/__body shapes (census-pinned by
    // NamingContractCensusTests) — a one-byte re-spelling at a bridge site silently unbinds the
    // param/ret/label it was meant to address.

    /// <summary>Allocator key / bare id for a parameter slot: "{name}__param".</summary>
    public static string ParamKey(string name) => name + "__param";

    /// <summary>Allocator key / bare id for a return slot: "{name}__ret".</summary>
    public static string RetKey(string name) => name + "__ret";

    /// <summary>Counter-qualified parameter slot id: "__N_{name}__param".</summary>
    public static string ParamId(string name, int counter) => FormatId(ParamKey(name), counter);

    /// <summary>Counter-qualified return slot id: "__N_{name}__ret".</summary>
    public static string RetId(string name, int counter) => FormatId(RetKey(name), counter);

    /// <summary>Entry label of an exported function's body: "{functionName}__body".</summary>
    public static string BodyLabel(string functionName) => functionName + "__body";
}

public static class LabelNames
{
    public static string FunctionEntry(string functionName) => "__" + functionName;

    public static string Block(string functionName, int blockId)
        => "__" + functionName + "_bb" + blockId;

    public static string CallReturn(string functionName, int index)
        => "__" + functionName + "__callret_" + index;
}
