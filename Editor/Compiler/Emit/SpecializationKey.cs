using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Exact CLR-symbol identity for one callable specialization. Storage names are deliberately absent:
/// distinct CLR types that share a Udon representation remain distinct binding keys.
/// </summary>
public readonly struct SpecializationKey : IEquatable<SpecializationKey>
{
    public readonly IMethodSymbol Definition;
    public readonly ImmutableArray<ITypeSymbol> Arguments;

    public SpecializationKey(IMethodSymbol definition, ImmutableArray<ITypeSymbol> arguments)
    {
        Definition = definition?.OriginalDefinition
            ?? throw new ArgumentNullException(nameof(definition));
        Arguments = arguments.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : arguments;
    }

    public static SpecializationKey ForMethod(IMethodSymbol method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        var containingArguments = method.ContainingType?.IsGenericType == true
            ? method.ContainingType.TypeArguments
            : ImmutableArray<ITypeSymbol>.Empty;
        var methodArguments = method.IsGenericMethod
            ? method.TypeArguments
            : ImmutableArray<ITypeSymbol>.Empty;
        return new SpecializationKey(
            method.OriginalDefinition,
            containingArguments.Concat(methodArguments).ToImmutableArray());
    }

    public bool Equals(SpecializationKey other)
    {
        if (!SymbolEqualityComparer.Default.Equals(Definition, other.Definition)
            || Arguments.Length != other.Arguments.Length)
            return false;
        for (var i = 0; i < Arguments.Length; i++)
            if (!SymbolEqualityComparer.Default.Equals(Arguments[i], other.Arguments[i]))
                return false;
        return true;
    }

    public override bool Equals(object obj)
        => obj is SpecializationKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = SymbolEqualityComparer.Default.GetHashCode(Definition);
            foreach (var argument in Arguments)
                hash = hash * 31 + SymbolEqualityComparer.Default.GetHashCode(argument);
            return hash;
        }
    }
}
