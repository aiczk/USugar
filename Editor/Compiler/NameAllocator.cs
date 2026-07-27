using System.Collections.Generic;

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

    /// <summary>Normalize a source symbol name for use as a UASM identifier.</summary>
    public static string Sanitize(string name) => name.Replace('.', '_');

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

/// <summary>
/// The UASM LABEL namespace, distinct from NameAllocator's counter-qualified slot ids: these name
/// jump targets, not heap variables. CoreToUasm is the only consumer; keeping the spellings here
/// puts every "__"-prefixed identifier shape under one census-pinned owner.
/// </summary>
public static class LabelNames
{
    /// <summary>Entry label of a non-exported function: "__{function}".</summary>
    public static string FunctionEntry(string functionName) => "__" + functionName;

    /// <summary>Basic-block label: "__{function}_bb{id}".</summary>
    public static string Block(string functionName, int blockId)
        => "__" + functionName + "_bb" + blockId;

    /// <summary>Return-site label of an internal call: "__{function}__callret_{index}".</summary>
    public static string CallReturn(string functionName, int index)
        => "__" + functionName + "__callret_" + index;
}
