using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Frozen lexical identity for every closure definition in the pre-emission body graph.
/// Specialization arguments remain emission-specific, but the owner chain does not.
/// </summary>
public sealed class ClosureIdentityPlan
{
    readonly Dictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>> _owners;

    ClosureIdentityPlan(Dictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>> owners)
        => _owners = owners;

    public static ClosureIdentityPlan Build(IEnumerable<IMethodSymbol> methods)
    {
        var owners = new Dictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>>(
            SymbolEqualityComparer.Default);
        foreach (var method in methods)
        {
            if (!IsClosure(method)) continue;
            var definition = method.OriginalDefinition;
            if (owners.ContainsKey(definition)) continue;

            var chain = ImmutableArray.CreateBuilder<IMethodSymbol>();
            for (var symbol = definition.ContainingSymbol;
                 symbol is IMethodSymbol owner;
                 symbol = owner.ContainingSymbol)
                chain.Add(owner.OriginalDefinition);
            owners.Add(definition, chain.ToImmutable());
        }
        return new ClosureIdentityPlan(owners);
    }

    public ImmutableArray<IMethodSymbol> GetLexicalOwners(IMethodSymbol closure)
    {
        if (closure == null) throw new ArgumentNullException(nameof(closure));
        if (_owners.TryGetValue(closure.OriginalDefinition, out var owners)) return owners;
        throw new InvalidOperationException(
            $"Closure '{closure.ToDisplayString()}' was not present in the frozen callable body graph.");
    }

    public IMethodSymbol CanonicalDefinition(IMethodSymbol closure)
    {
        if (closure == null) throw new ArgumentNullException(nameof(closure));
        foreach (var definition in _owners.Keys)
            if (SameSourceDefinition(definition, closure)) return definition;
        throw new InvalidOperationException(
            $"Closure '{closure.ToDisplayString()}' was not present in the frozen callable body graph.");
    }

    public static bool SameSourceDefinition(IMethodSymbol left, IMethodSymbol right)
    {
        if (left == null || right == null) return false;
        if (SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition))
            return true;
        var l = left.DeclaringSyntaxReferences.Length > 0 ? left.DeclaringSyntaxReferences[0] : null;
        var r = right.DeclaringSyntaxReferences.Length > 0 ? right.DeclaringSyntaxReferences[0] : null;
        return l != null && r != null && ReferenceEquals(l.SyntaxTree, r.SyntaxTree)
            && l.Span.Equals(r.Span);
    }

    static bool IsClosure(IMethodSymbol method)
        => method.MethodKind is MethodKind.LocalFunction
            or MethodKind.LambdaMethod or MethodKind.AnonymousFunction;
}
