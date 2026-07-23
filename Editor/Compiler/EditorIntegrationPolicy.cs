using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Unity-independent policy shared by the editor integration layer and its headless tests.
/// Unity's CompilationPipeline remains authoritative for which files belong to an assembly;
/// this class only normalizes those identities and enforces exact, unambiguous matching.
/// </summary>
static class USugarEditorIntegrationPolicy
{
    public static bool IsCSharpSource(string path)
        => !string.IsNullOrWhiteSpace(path)
           && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeSourcePath(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Source path is required.", nameof(path));

        var combined = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
        return Path.GetFullPath(combined)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    public static bool RequiresOpaqueObjectArrayStorage(
        Type proxyType,
        Type systemType,
        Func<Type, bool> isExternType,
        Func<Type, bool> isBehaviourType)
    {
        if (proxyType == null) throw new ArgumentNullException(nameof(proxyType));
        if (isExternType == null) throw new ArgumentNullException(nameof(isExternType));
        if (isBehaviourType == null) throw new ArgumentNullException(nameof(isBehaviourType));
        if (systemType != typeof(object[]) || proxyType == typeof(object[]))
            return false;
        if (!proxyType.IsArray || proxyType.GetElementType()?.IsArray != true)
            return true;

        var leaf = proxyType;
        while (leaf.IsArray)
            leaf = leaf.GetElementType();

        if (typeof(Delegate).IsAssignableFrom(leaf)
            || leaf.FullName?.StartsWith(
                "System.ValueTuple`", StringComparison.Ordinal) == true)
            return true;
        if (leaf.IsEnum || isBehaviourType(leaf))
            return false;
        return !isExternType(leaf);
    }
}

enum USugarCompileHealth
{
    Unknown,
    Clean,
    Failed,
}

/// <summary>
/// Unity-independent compile-request state. A request always invalidates the previous clean result,
/// and completing an older request never consumes a newer request that arrived while it was running.
/// </summary>
sealed class USugarCompileRequestState
{
    public int RequestedVersion { get; private set; }
    public int CompiledVersion { get; private set; }
    public USugarCompileHealth Health { get; private set; } = USugarCompileHealth.Unknown;
    public bool HasPendingRequest => RequestedVersion > CompiledVersion;

    public int Request()
    {
        Health = USugarCompileHealth.Unknown;
        return ++RequestedVersion;
    }

    public void MarkUnknown() => Health = USugarCompileHealth.Unknown;
    public void MarkClean() => Health = USugarCompileHealth.Clean;
    public void MarkFailed() => Health = USugarCompileHealth.Failed;

    public void Complete(int version)
    {
        if (version < 0 || version > RequestedVersion)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (version > CompiledVersion)
            CompiledVersion = version;
        if (HasPendingRequest)
            Health = USugarCompileHealth.Unknown;
    }
}

/// <summary>
/// Exact source-path index. There is deliberately no "first candidate" fallback: a missing
/// or ambiguous binding must stop asset application instead of writing a program to the wrong asset.
/// </summary>
sealed class USugarExactSourcePathIndex<T>
{
    readonly Dictionary<string, List<T>> _items;
    readonly IEqualityComparer<T> _itemComparer;

    public USugarExactSourcePathIndex(
        StringComparer pathComparer = null,
        IEqualityComparer<T> itemComparer = null)
    {
        _items = new Dictionary<string, List<T>>(
            pathComparer ?? StringComparer.OrdinalIgnoreCase);
        _itemComparer = itemComparer ?? EqualityComparer<T>.Default;
    }

    public IEnumerable<T> Items => _items.Values.SelectMany(items => items);

    public void Add(string normalizedSourcePath, T item)
    {
        if (string.IsNullOrEmpty(normalizedSourcePath))
            throw new ArgumentException("Normalized source path is required.", nameof(normalizedSourcePath));
        if (!_items.TryGetValue(normalizedSourcePath, out var items))
        {
            items = new List<T>();
            _items.Add(normalizedSourcePath, items);
        }
        items.Add(item);
    }

    public bool TryResolveUnique(string normalizedSourcePath, out T item, out string error)
        => TryResolveUnique(new[] { normalizedSourcePath }, out item, out error);

    public bool TryResolveUnique(
        IEnumerable<string> normalizedSourcePaths,
        out T item,
        out string error)
    {
        item = default;
        var paths = NormalizePaths(normalizedSourcePaths);
        var candidates = GetCandidates(paths);

        if (candidates.Count == 0)
        {
            error = paths.Length == 1
                ? $"No program asset is bound to source '{paths[0]}'."
                : "No program asset is bound to any declaration source: "
                  + string.Join(", ", paths);
            return false;
        }
        if (candidates.Count != 1)
        {
            error = $"{candidates.Count} program assets are bound to source "
                    + $"declarations [{string.Join(", ", paths)}]; the binding is ambiguous.";
            return false;
        }

        item = candidates.Single();
        error = null;
        return true;
    }

    public IReadOnlyList<T> GetCandidates(IEnumerable<string> normalizedSourcePaths)
    {
        var paths = NormalizePaths(normalizedSourcePaths);
        var candidates = new HashSet<T>(_itemComparer);
        foreach (var path in paths)
            if (_items.TryGetValue(path, out var pathCandidates))
                candidates.UnionWith(pathCandidates);
        return candidates.ToArray();
    }

    string[] NormalizePaths(IEnumerable<string> normalizedSourcePaths)
    {
        if (normalizedSourcePaths == null)
            throw new ArgumentNullException(nameof(normalizedSourcePaths));
        return normalizedSourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(_items.Comparer)
            .ToArray();
    }
}

/// <summary>
/// Stock UdonSharp parses every source in an enabled assembly for semantic context, but only emits
/// a concrete, non-generic behaviour that owns a program asset. Helper behaviours are not roots.
/// </summary>
static class USugarProgramRootPolicy
{
    public static bool ShouldEmit(
        INamedTypeSymbol behaviour,
        bool hasProgramAsset,
        out string error)
    {
        if (behaviour == null) throw new ArgumentNullException(nameof(behaviour));
        error = null;
        if (!hasProgramAsset)
            return false;
        if (behaviour.IsAbstract)
        {
            error = $"Abstract UdonSharpBehaviour '{behaviour.ToDisplayString()}' "
                    + "cannot own a program asset.";
            return false;
        }
        if (behaviour.IsGenericType)
        {
            error = $"Generic UdonSharpBehaviour '{behaviour.ToDisplayString()}' "
                    + "cannot own a program asset.";
            return false;
        }
        return true;
    }
}

static class USugarNetworkMetadataPolicy
{
    public static IReadOnlyList<KeyValuePair<IMethodSymbol, MethodLayout>>
        GetCallableMethods(TypeLayout layout)
    {
        if (layout == null) throw new ArgumentNullException(nameof(layout));
        return layout.Methods
            .Where(pair => LayoutPlanner.IsNetworkCallable(pair.Key))
            .OrderBy(pair => pair.Value.ExportName, StringComparer.Ordinal)
            .ThenBy(
                pair => pair.Key.ToDisplayString(),
                StringComparer.Ordinal)
            .ToArray();
    }
}
