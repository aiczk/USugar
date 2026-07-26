using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;

internal readonly struct BoundDeconstructionKey
    : IEquatable<BoundDeconstructionKey>
{
    public readonly SyntaxNode Syntax;
    public readonly CallSiteBindingScope Scope;

    public BoundDeconstructionKey(
        SyntaxNode syntax,
        CallSiteBindingScope scope)
    {
        Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
        Scope = scope;
    }

    public bool Equals(BoundDeconstructionKey other)
        => ReferenceEquals(Syntax, other.Syntax)
           && Scope.Equals(other.Scope);

    public override bool Equals(object obj)
        => obj is BoundDeconstructionKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return Syntax.GetHashCode() * 31 + Scope.GetHashCode();
        }
    }
}

/// <summary>
/// Roslyn's result for one deconstruction site. A null method is intentional:
/// tuple-return deconstruction has no selected user-defined Deconstruct method.
/// </summary>
internal sealed class BoundDeconstruction
{
    public readonly IMethodSymbol Method;

    public BoundDeconstruction(IMethodSymbol method)
        => Method = method;
}

/// <summary>Deconstruction binding results, closed per specialization.</summary>
internal sealed class BoundDeconstructionTable
{
    readonly IReadOnlyDictionary<BoundDeconstructionKey, BoundDeconstruction> _sites;

    internal BoundDeconstructionTable(
        IDictionary<BoundDeconstructionKey, BoundDeconstruction> sites)
        => _sites = new ReadOnlyDictionary<BoundDeconstructionKey, BoundDeconstruction>(
            new Dictionary<BoundDeconstructionKey, BoundDeconstruction>(sites));

    public BoundDeconstruction Require(
        IOperation operation,
        CallSiteBindingScope scope)
    {
        var key = new BoundDeconstructionKey(operation.Syntax, scope);
        if (_sites.TryGetValue(key, out var method)) return method;
        throw new InvalidOperationException(
            $"Deconstruction '{operation.Syntax}' was absent from the bound program.");
    }
}
