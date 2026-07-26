using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;

internal readonly struct BoundSyntheticDispatchKey
    : IEquatable<BoundSyntheticDispatchKey>
{
    public readonly INamedTypeSymbol ReceiverType;
    public readonly IMethodSymbol Target;

    public BoundSyntheticDispatchKey(
        INamedTypeSymbol receiverType,
        IMethodSymbol target)
    {
        ReceiverType = receiverType
            ?? throw new ArgumentNullException(nameof(receiverType));
        Target = target
            ?? throw new ArgumentNullException(nameof(target));
    }

    public bool Equals(BoundSyntheticDispatchKey other)
        => SymbolEqualityComparer.Default.Equals(
               ReceiverType, other.ReceiverType)
           && SymbolEqualityComparer.Default.Equals(
               Target, other.Target);

    public override bool Equals(object obj)
        => obj is BoundSyntheticDispatchKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return SymbolEqualityComparer.Default.GetHashCode(ReceiverType)
                   * 31
                   + SymbolEqualityComparer.Default.GetHashCode(
                       Target);
        }
    }
}
