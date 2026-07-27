using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    public static IReadOnlyList<string> SelectSourceAssemblyClosure(
        string rootAssemblyName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> assemblyReferences,
        ISet<string> sourceDomain)
    {
        if (string.IsNullOrWhiteSpace(rootAssemblyName))
            throw new ArgumentException(
                "Root assembly name is required.", nameof(rootAssemblyName));
        if (assemblyReferences == null)
            throw new ArgumentNullException(nameof(assemblyReferences));
        if (sourceDomain == null)
            throw new ArgumentNullException(nameof(sourceDomain));
        if (!sourceDomain.Contains(rootAssemblyName))
            throw new InvalidOperationException(
                $"Unity assembly '{rootAssemblyName}' is outside the USugar source domain.");

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string assemblyName)
        {
            if (!sourceDomain.Contains(assemblyName)
                || !selected.Add(assemblyName))
                return;
            if (!assemblyReferences.TryGetValue(
                    assemblyName, out var references))
                return;
            foreach (var reference in references
                         .Where(sourceDomain.Contains)
                         .OrderBy(name => name, StringComparer.Ordinal))
                Visit(reference);
        }

        Visit(rootAssemblyName);
        return selected
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> SelectPreprocessorDefines(
        IEnumerable<string> assemblyDefines,
        IEnumerable<string> activeEditorDefines,
        bool editorBuild)
    {
        if (assemblyDefines == null)
            throw new ArgumentNullException(nameof(assemblyDefines));
        if (activeEditorDefines == null)
            throw new ArgumentNullException(
                nameof(activeEditorDefines));
        var defines = new HashSet<string>(
            assemblyDefines.Where(define =>
                editorBuild
                || !define.StartsWith(
                    "UNITY_EDITOR",
                    StringComparison.Ordinal)),
            StringComparer.Ordinal);
        defines.UnionWith(activeEditorDefines.Where(define =>
            editorBuild
            || !define.StartsWith(
                "UNITY_EDITOR",
                StringComparison.Ordinal)));
        defines.Add("COMPILER_UDONSHARP");
        defines.Add("UDONSHARP");
        return defines
            .OrderBy(define => define, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool RequiresOpaqueObjectArrayStorage(
        Type proxyType,
        Type systemType,
        Func<Type, bool> isNativeAbiType,
        Func<Type, bool> isBehaviourType)
    {
        if (proxyType == null) throw new ArgumentNullException(nameof(proxyType));
        if (isNativeAbiType == null)
            throw new ArgumentNullException(nameof(isNativeAbiType));
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
        return !isNativeAbiType(leaf);
    }

    /// <summary>
    /// Projects the compiler-owned source field schema onto the field names used by
    /// UdonSharp's reflection formatter. The projection is deliberately closed:
    /// every CLR field must be an exact source field, an auto-property backing alias,
    /// or an explicitly non-serialized proxy-only field.
    /// </summary>
    public static IReadOnlyList<(
        FieldInfo Field,
        string SourceName,
        bool IsProxyOnly)> ProjectProxyFields(
        Type proxyType,
        Type exclusiveBaseType,
        IReadOnlyDictionary<string, Type> sourceFields)
    {
        if (proxyType == null) throw new ArgumentNullException(nameof(proxyType));
        if (exclusiveBaseType == null)
            throw new ArgumentNullException(nameof(exclusiveBaseType));
        if (sourceFields == null)
            throw new ArgumentNullException(nameof(sourceFields));
        if (!exclusiveBaseType.IsAssignableFrom(proxyType))
            throw new InvalidOperationException(
                $"CLR proxy type '{proxyType.FullName}' does not derive from "
                + $"'{exclusiveBaseType.FullName}'.");

        var hierarchy = new Stack<Type>();
        for (var current = proxyType;
             current != null && current != exclusiveBaseType;
             current = current.BaseType)
            hierarchy.Push(current);

        var result = new List<(
            FieldInfo Field,
            string SourceName,
            bool IsProxyOnly)>();
        while (hierarchy.Count > 0)
        {
            var owner = hierarchy.Pop();
            foreach (var field in owner.GetFields(
                         BindingFlags.Public
                         | BindingFlags.NonPublic
                         | BindingFlags.Instance
                         | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic) continue;
                if (sourceFields.TryGetValue(
                        field.Name, out var exactType))
                {
                    RequireProxyType(field, exactType, field.Name);
                    result.Add((field, field.Name, false));
                    continue;
                }

                if (TryGetAutoPropertyName(
                        field.Name, out var propertyName)
                    && sourceFields.TryGetValue(
                        propertyName, out var propertyType))
                {
                    RequireProxyType(field, propertyType, propertyName);
                    result.Add((field, propertyName, false));
                    continue;
                }

                if (field.IsDefined(
                        typeof(NonSerializedAttribute),
                        inherit: false))
                {
                    result.Add((field, null, true));
                    continue;
                }

                throw new InvalidOperationException(
                    $"CLR proxy field '{owner.FullName}.{field.Name}' "
                    + "has no compiler-owned Udon field, auto-property "
                    + "alias, or [NonSerialized] proxy-only classification.");
            }
        }
        return result;
    }

    internal static bool TryGetAutoPropertyName(
        string fieldName,
        out string propertyName)
    {
        const string suffix = ">k__BackingField";
        propertyName = null;
        if (string.IsNullOrEmpty(fieldName)
            || fieldName[0] != '<'
            || !fieldName.EndsWith(
                suffix, StringComparison.Ordinal))
            return false;
        var length = fieldName.Length - suffix.Length - 1;
        if (length <= 0) return false;
        propertyName = fieldName.Substring(1, length);
        return true;
    }

    static void RequireProxyType(
        FieldInfo field,
        Type sourceType,
        string sourceName)
    {
        if (sourceType == null)
            throw new InvalidOperationException(
                $"Compiler-owned field '{sourceName}' has no CLR type.");
        if (field.FieldType == sourceType) return;
        throw new InvalidOperationException(
            $"CLR proxy field '{field.DeclaringType?.FullName}."
            + $"{field.Name}' has type '{field.FieldType}', but "
            + $"compiler-owned field '{sourceName}' has type "
            + $"'{sourceType}'.");
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
            .Where(pair => LayoutPlanBuilder.IsNetworkCallable(pair.Key))
            .OrderBy(pair => pair.Value.ExportName, StringComparer.Ordinal)
            .ThenBy(
                pair => pair.Key.ToDisplayString(),
                StringComparer.Ordinal)
            .ToArray();
    }
}
