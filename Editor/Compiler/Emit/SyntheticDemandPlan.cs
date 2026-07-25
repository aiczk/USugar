using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Immutable synthetic-helper demand set. It is published after the source-operation census and
/// before any method body is lowered.
/// </summary>
internal sealed class SyntheticDemandPlan
{
    readonly IReadOnlyDictionary<string, StructuredFunction> _closureBridgeFunctions;

    public readonly IReadOnlyCollection<DelegateBridgeDemand> ReceiverBridges;
    public readonly IReadOnlyCollection<DelegateBridgeDemand> DelegateBridges;
    public readonly IReadOnlyDictionary<string, MulticastSigPlan> MulticastSignatures;
    public readonly IReadOnlyCollection<INamedTypeSymbol> EnumToStringTypes;
    public readonly IReadOnlyCollection<DelegateBridgeDemand> SignatureAdapterBridges;
    public readonly IReadOnlyDictionary<string, DelegateWrapperDemand> WrapperSignatures;

    public SyntheticDemandPlan(
        IDictionary<string, StructuredFunction> closureBridgeFunctions,
        IEnumerable<DelegateBridgeDemand> receiverBridges,
        IEnumerable<DelegateBridgeDemand> delegateBridges,
        IDictionary<string, MulticastSigPlan> multicastSignatures,
        IEnumerable<INamedTypeSymbol> enumToStringTypes,
        IEnumerable<DelegateBridgeDemand> signatureAdapterBridges,
        IDictionary<string, DelegateWrapperDemand> wrapperSignatures)
    {
        _closureBridgeFunctions = CopyMap(closureBridgeFunctions);
        ReceiverBridges = Array.AsReadOnly(receiverBridges.ToArray());
        DelegateBridges = Array.AsReadOnly(delegateBridges.ToArray());
        MulticastSignatures = CopyMap(multicastSignatures);
        EnumToStringTypes = Array.AsReadOnly(enumToStringTypes.ToArray());
        SignatureAdapterBridges = Array.AsReadOnly(signatureAdapterBridges.ToArray());
        WrapperSignatures = CopyMap(wrapperSignatures);
    }

    public bool TryGetClosureBridge(string name, out StructuredFunction function)
        => _closureBridgeFunctions.TryGetValue(name, out function);

    static IReadOnlyDictionary<string, TValue> CopyMap<TValue>(
        IDictionary<string, TValue> source)
        => new ReadOnlyDictionary<string, TValue>(
            new Dictionary<string, TValue>(source, StringComparer.Ordinal));
}
