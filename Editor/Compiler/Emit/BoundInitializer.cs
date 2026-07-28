using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Per-emitter authority for field-initializer operation trees. The syntax identifies
/// the source initializer; constructed field symbols and closed type environments stay
/// outside this table and are resolved independently for every class specialization.
/// </summary>
internal sealed class InitializerOperationTable
{
    readonly Func<SyntaxNode, IOperation> _materialize;
    readonly Dictionary<SyntaxNode, IOperation> _operations =
        new(SyntaxReferenceComparer.Instance);

    public InitializerOperationTable(
        Compilation compilation,
        Func<SyntaxNode, IOperation> materialize = null)
    {
        if (compilation == null)
            throw new ArgumentNullException(nameof(compilation));
        _materialize = materialize
            ?? (syntax =>
            {
                var model = compilation.GetSemanticModel(
                    syntax.SyntaxTree);
                return (model.GetOperation(syntax.Parent)
                            as ISymbolInitializerOperation)
                        ?.Value
                    ?? model.GetOperation(syntax);
            });
    }

    public IOperation Get(SyntaxNode initializerValue)
    {
        if (initializerValue == null)
            throw new ArgumentNullException(nameof(initializerValue));
        if (_operations.TryGetValue(
                initializerValue, out var operation))
            return operation;
        operation = _materialize(initializerValue);
        _operations.Add(initializerValue, operation);
        return operation;
    }

    sealed class SyntaxReferenceComparer
        : IEqualityComparer<SyntaxNode>
    {
        internal static readonly SyntaxReferenceComparer Instance = new();

        public bool Equals(SyntaxNode x, SyntaxNode y)
            => ReferenceEquals(x, y);

        public int GetHashCode(SyntaxNode obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}

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
