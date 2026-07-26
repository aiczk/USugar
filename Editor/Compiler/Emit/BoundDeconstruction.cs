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
