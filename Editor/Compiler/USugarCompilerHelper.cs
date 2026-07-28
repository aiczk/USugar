using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

/// <summary>
/// Testable helpers extracted from USugarCompiler (Unity-dependent parts remain in USugarCompiler).
/// </summary>
public static class USugarCompilerHelper
{
    public static string NamespaceRoot(INamespaceSymbol ns)
    {
        if (ns == null || ns.IsGlobalNamespace) return null;
        var root = ns;
        while (root.ContainingNamespace != null && !root.ContainingNamespace.IsGlobalNamespace)
            root = root.ContainingNamespace;
        return root.Name;
    }

    public static bool IsFrameworkNamespace(INamespaceSymbol ns)
        => NamespaceRoot(ns) is "UnityEngine" or "VRC" or "TMPro" or "System" or "UdonSharp";

    public static bool IsExternNamespace(INamespaceSymbol ns)
        => NamespaceRoot(ns) is "UnityEngine" or "VRC" or "TMPro" or "System";

    public static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null);
        }
    }
}
