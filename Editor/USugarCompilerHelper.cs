using Microsoft.CodeAnalysis;
using System.Linq;

/// <summary>
/// Testable helpers extracted from USugarCompiler (Unity-dependent parts remain in USugarCompiler).
/// </summary>
public static class USugarCompilerHelper
{
    /// <summary>
    /// Walk inheritance chain to find [UdonBehaviourSyncMode] attribute.
    /// Returns the int value of BehaviourSyncMode enum, or -1 if not specified.
    /// </summary>
    public static bool IsFrameworkNamespace(INamespaceSymbol ns)
    {
        if (ns == null || ns.IsGlobalNamespace) return false;
        var root = ns;
        while (root.ContainingNamespace != null && !root.ContainingNamespace.IsGlobalNamespace)
            root = root.ContainingNamespace;
        return root.Name is "UnityEngine" or "VRC" or "TMPro" or "System" or "UdonSharp";
    }

    /// <summary>
    /// Like IsFrameworkNamespace but excludes UdonSharp — types in UdonSharp.* that are not
    /// UdonSharpBehaviour may be user-defined helper classes with generic methods to inline.
    /// </summary>
    public static bool IsExternNamespace(INamespaceSymbol ns)
    {
        if (ns == null || ns.IsGlobalNamespace) return false;
        var root = ns;
        while (root.ContainingNamespace is { IsGlobalNamespace: false })
            root = root.ContainingNamespace;
        return root.Name is "UnityEngine" or "VRC" or "TMPro" or "System";
    }

    public static int GetBehaviourSyncMode(INamedTypeSymbol type)
    {
        var current = type;
        while (current != null && current.Name != "UdonSharpBehaviour")
        {
            var attr = current.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "UdonBehaviourSyncModeAttribute");
            if (attr != null && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is int modeVal)
                return modeVal;
            current = current.BaseType;
        }
        return -1;
    }
}
