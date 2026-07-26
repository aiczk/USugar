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

/// <summary>
/// Closed-world dispatch decisions for compiler-generated call sites. Source
/// call sites live in <see cref="BoundCallSiteTable"/>.
/// </summary>
internal sealed class BoundSyntheticDispatchTable
{
    readonly IReadOnlyDictionary<
        BoundSyntheticDispatchKey, DispatchPlan> _sites;

    public readonly IMethodSymbol ObjectToStringSlot;

    public BoundSyntheticDispatchTable(
        IMethodSymbol objectToStringSlot,
        IDictionary<BoundSyntheticDispatchKey, DispatchPlan> sites)
    {
        ObjectToStringSlot = objectToStringSlot
            ?? throw new ArgumentNullException(nameof(objectToStringSlot));
        _sites = new ReadOnlyDictionary<
            BoundSyntheticDispatchKey, DispatchPlan>(
            new Dictionary<BoundSyntheticDispatchKey, DispatchPlan>(
                sites ?? throw new ArgumentNullException(nameof(sites))));
    }

    public DispatchPlan Require(
        INamedTypeSymbol receiverType,
        IMethodSymbol target)
    {
        var key = new BoundSyntheticDispatchKey(receiverType, target);
        if (_sites.TryGetValue(key, out var dispatch)) return dispatch;
        throw new InvalidOperationException(
            $"Synthetic dispatch '{receiverType}.{target.Name}' was absent "
            + "from the bound program.");
    }
}
