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

/// <summary>
/// Exact lexical generic environment for every initializer execution shape.
/// A base initializer can have one entry per minted derived type.
/// </summary>
internal sealed class BoundInitializerTable
{
    readonly IReadOnlyDictionary<BoundInitializerKey, BoundInitializer> _sites;
    readonly IReadOnlyDictionary<
        INamedTypeSymbol,
        IReadOnlyList<BoundClassFieldInitializer>> _classes;

    internal BoundInitializerTable(
        IDictionary<BoundInitializerKey, BoundInitializer> sites,
        IDictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>> classes)
    {
        _sites = new ReadOnlyDictionary<BoundInitializerKey, BoundInitializer>(
            new Dictionary<BoundInitializerKey, BoundInitializer>(sites));
        if (classes == null)
            throw new ArgumentNullException(nameof(classes));
        var classCopy = new Dictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>>(
            SymbolEqualityComparer.Default);
        foreach (var pair in classes)
            classCopy.Add(
                pair.Key,
                Array.AsReadOnly(
                    (pair.Value
                     ?? Array.Empty<BoundClassFieldInitializer>())
                    .ToArray()));
        _classes = new ReadOnlyDictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>>(classCopy);
    }

    public BoundInitializer Require(
        IOperation operation,
        INamedTypeSymbol mintedType = null)
    {
        var key = new BoundInitializerKey(operation.Syntax, mintedType);
        if (_sites.TryGetValue(key, out var binding)) return binding;
        throw new InvalidOperationException(
            $"Initializer '{operation.Syntax}' was absent from the bound program "
            + $"for '{mintedType?.ToDisplayString() ?? "program fields"}'.");
    }

    public IReadOnlyList<BoundClassFieldInitializer> RequireClass(
        INamedTypeSymbol type)
    {
        if (type != null
            && _classes.TryGetValue(type, out var plan))
            return plan;
        throw new InvalidOperationException(
            $"Class initializer plan '{type?.ToDisplayString()}' "
            + "was absent from the bound program.");
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
