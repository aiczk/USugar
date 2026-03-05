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

    /// <summary>
    /// Ensure the counter for key is at least usedValue + 1,
    /// so future Allocate() calls skip past already-used values.
    /// </summary>
    public void Reserve(string key, int usedValue)
    {
        _counters.TryGetValue(key, out var current);
        if (usedValue + 1 > current)
            _counters[key] = usedValue + 1;
    }

    /// <summary>Format a key + counter into "__N_key" form.</summary>
    public static string FormatId(string key, int counter) => $"__{counter}_{key}";

    /// <summary>Parse "__N_key" back into (counter, key). Returns null if format doesn't match.</summary>
    public static (int counter, string key)? ParseId(string id)
    {
        if (id == null || !id.StartsWith("__")) return null;
        var rest = id.Substring(2);
        var idx = rest.IndexOf('_');
        if (idx <= 0) return null;
        if (!int.TryParse(rest.Substring(0, idx), out var counter)) return null;
        return (counter, rest.Substring(idx + 1));
    }
}
