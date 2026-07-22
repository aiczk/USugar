using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Structural finiteness policy for closed specialization discovery. Finite programs have
/// no arbitrary numeric cap; a repeated definition is rejected only when its arguments strictly
/// contain the ancestor arguments and would therefore generate an infinite ascending chain.</summary>
internal static class SpecializationExpansionPolicy
{
    public static void RequireFinite(IMethodSymbol current, IEnumerable<IMethodSymbol> ancestors)
    {
        foreach (var ancestor in ancestors)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition, ancestor.OriginalDefinition)) continue;
            var before = Arguments(ancestor);
            var after = Arguments(current);
            if (before.Count != after.Count || !StrictlyEmbeds(after, before)) continue;
            throw new NotSupportedException(
                $"Generic specialization expands recursively: '{Display(ancestor)}' -> '{Display(current)}'. "
                + "The closed-specialization set would be infinite.");
        }
    }

    public static void RequireFinite(INamedTypeSymbol current, IEnumerable<INamedTypeSymbol> ancestors)
    {
        foreach (var ancestor in ancestors)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition, ancestor.OriginalDefinition)) continue;
            if (!ContainsType(current, ancestor)
                || SymbolEqualityComparer.Default.Equals(current, ancestor)) continue;
            throw new NotSupportedException(
                "Generic type specialization expands recursively through field initializers: "
                + $"'{ancestor.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' -> "
                + $"'{current.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'. "
                + "The closed-specialization set would be infinite.");
        }
    }

    static IReadOnlyList<ITypeSymbol> Arguments(IMethodSymbol method)
    {
        var result = new List<ITypeSymbol>();
        var owners = new Stack<INamedTypeSymbol>();
        for (var type = method.ContainingType; type != null; type = type.ContainingType) owners.Push(type);
        while (owners.Count > 0) result.AddRange(owners.Pop().TypeArguments);
        if (method.IsGenericMethod) result.AddRange(method.TypeArguments);
        return result;
    }

    static bool StrictlyEmbeds(IReadOnlyList<ITypeSymbol> containers, IReadOnlyList<ITypeSymbol> values)
    {
        var strict = false;
        for (var i = 0; i < containers.Count; i++)
        {
            if (!ContainsType(containers[i], values[i])) return false;
            if (!SymbolEqualityComparer.Default.Equals(containers[i], values[i])) strict = true;
        }
        return strict;
    }

    static bool ContainsType(ITypeSymbol container, ITypeSymbol value)
    {
        if (SymbolEqualityComparer.Default.Equals(container, value)) return true;
        if (container is IArrayTypeSymbol array) return ContainsType(array.ElementType, value);
        if (container is not INamedTypeSymbol named) return false;
        foreach (var argument in named.TypeArguments)
            if (ContainsType(argument, value)) return true;
        return named.ContainingType != null && ContainsType(named.ContainingType, value);
    }

    static string Display(IMethodSymbol method)
        => method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
}
