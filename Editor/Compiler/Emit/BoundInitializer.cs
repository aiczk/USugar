using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;

internal sealed class BoundInitializer
{
    public readonly CallSiteBindingScope Scope;
    public readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
        TypeParameterMap;

    public BoundInitializer(
        CallSiteBindingScope scope,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        Scope = scope;
#pragma warning disable RS1024 // Fresh symbol twins require declaration-identity comparison here.
        var copy = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
            TypeParamIdComparer.Instance);
#pragma warning restore RS1024
        if (typeParameterMap != null)
            foreach (var pair in typeParameterMap)
                copy.Add(pair.Key, pair.Value);
        TypeParameterMap =
            new ReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>(copy);
    }
}

internal readonly struct BoundInitializerKey : IEquatable<BoundInitializerKey>
{
    public readonly SyntaxNode Syntax;
    public readonly INamedTypeSymbol MintedType;

    public BoundInitializerKey(SyntaxNode syntax, INamedTypeSymbol mintedType)
    {
        Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
        MintedType = mintedType;
    }

    public bool Equals(BoundInitializerKey other)
        => ReferenceEquals(Syntax, other.Syntax)
           && SymbolEqualityComparer.Default.Equals(MintedType, other.MintedType);

    public override bool Equals(object obj)
        => obj is BoundInitializerKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return Syntax.GetHashCode() * 31
                   + (MintedType == null
                       ? 0
                       : SymbolEqualityComparer.Default.GetHashCode(MintedType));
        }
    }
}

internal sealed class BoundClassFieldInitializer
{
    public readonly IOperation Operation;
    public readonly int Slot;
    public readonly BoundInitializer Binding;

    public BoundClassFieldInitializer(
        IOperation operation,
        int slot,
        BoundInitializer binding)
    {
        Operation = operation
            ?? throw new ArgumentNullException(nameof(operation));
        Slot = slot;
        Binding = binding
            ?? throw new ArgumentNullException(nameof(binding));
    }
}
