using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

/// <summary>
/// Defines the reflection metadata contract shared by behaviour programs and the generic
/// GetComponent shim.  The producer and consumer must make the same decision about whether a
/// lookup uses the exact type id or the assignable-type id chain.
/// </summary>
internal static class UdonBehaviourTypeMetadata
{
    public static string TypeName(ITypeSymbol type)
        => type?.ToDisplayString() ?? throw new ArgumentNullException(nameof(type));

    public static long TypeId(ITypeSymbol type) => TypeId(TypeName(type));

    public static long TypeId(string typeName)
    {
        if (typeName == null) throw new ArgumentNullException(nameof(typeName));
        using var sha = SHA256.Create();
        return BitConverter.ToInt64(sha.ComputeHash(Encoding.UTF8.GetBytes(typeName)), 0);
    }

    public static long[] AssignableTypeIds(INamedTypeSymbol type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        var ids = new List<long>();
        for (var current = type;
             current != null && current.Name != "UdonSharpBehaviour";
             current = current.BaseType)
            ids.Add(TypeId(current));
        return ids.ToArray();
    }

    /// <summary>
    /// A lookup for a source type must inspect the assignable-id chain when a concrete behaviour
    /// derived from that type exists in this compilation.
    /// </summary>
    public static bool LookupRequiresAssignableIds(
        ITypeSymbol targetType,
        CompilationTypeCensus census)
    {
        if (targetType == null) throw new ArgumentNullException(nameof(targetType));
        if (census == null) throw new ArgumentNullException(nameof(census));
        return census.Classes.Any(candidate =>
            !SymbolEqualityComparer.Default.Equals(candidate, targetType)
            && IsDerivedFrom(candidate, targetType));
    }

    /// <summary>
    /// Emit the id chain for inherited programs and for direct UdonSharpBehaviour subclasses that
    /// are a base of another compiled behaviour.  The latter is essential: a GetComponent&lt;Base&gt;
    /// consumer asks every candidate for __refl_typeids, including an exact Base instance.
    /// </summary>
    public static bool ProgramRequiresAssignableIds(
        INamedTypeSymbol programType,
        CompilationTypeCensus census)
        => AssignableTypeIds(programType).Length > 1
           || LookupRequiresAssignableIds(programType, census);

    static bool IsDerivedFrom(INamedTypeSymbol candidate, ITypeSymbol target)
    {
        for (var current = candidate.BaseType; current != null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        return false;
    }
}
