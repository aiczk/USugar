using System;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// The exact delegate creation and specialization environment materialized by source binding.
/// Operation identity is stable because BoundProgram retains the same Roslyn operation trees for emission.
/// </summary>
internal readonly struct BoundDelegateSiteKey
    : IEquatable<BoundDelegateSiteKey>
{
    public readonly IDelegateCreationOperation Operation;
    public readonly CallSiteBindingScope Scope;

    public BoundDelegateSiteKey(
        IDelegateCreationOperation operation,
        CallSiteBindingScope scope)
    {
        Operation = operation
            ?? throw new ArgumentNullException(nameof(operation));
        Scope = scope;
    }

    public bool Equals(BoundDelegateSiteKey other)
        => ReferenceEquals(Operation, other.Operation)
           && Scope.Equals(other.Scope);

    public override bool Equals(object obj)
        => obj is BoundDelegateSiteKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return Operation.GetHashCode() * 31
                   + Scope.GetHashCode();
        }
    }
}
