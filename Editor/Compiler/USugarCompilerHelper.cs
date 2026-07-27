using Microsoft.CodeAnalysis;

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
        => EmitPolicy.GetBehaviourSyncMode(type);
}
