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
