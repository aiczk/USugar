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
    readonly IReadOnlyDictionary<string, string> _closureBridgeTargets;
    readonly IReadOnlyDictionary<string, DelegateBindingPlan> _delegateBindings;
    readonly HashSet<INamedTypeSymbol> _enumToStringTypes;
    readonly HashSet<INamedTypeSymbol> _classToStringTypes;
    readonly IReadOnlyDictionary<string, DelegateBridgeDemand> _receiverBridges;
    readonly IReadOnlyDictionary<string, DelegateBridgeDemand> _delegateBridges;
    readonly IReadOnlyDictionary<string, DelegateBridgeDemand> _signatureAdapterBridges;

    public readonly IReadOnlyCollection<DelegateBridgeDemand> ReceiverBridges;
    public readonly IReadOnlyCollection<DelegateBridgeDemand> DelegateBridges;
    public readonly IReadOnlyCollection<string> DelegateSites;
    public readonly IReadOnlyDictionary<string, MulticastSigPlan> MulticastSignatures;
    public readonly IReadOnlyCollection<INamedTypeSymbol> EnumToStringTypes;
    public readonly IReadOnlyCollection<INamedTypeSymbol> ClassToStringTypes;
    public readonly IReadOnlyCollection<DelegateBridgeDemand> SignatureAdapterBridges;
    public readonly IReadOnlyDictionary<string, DelegateWrapperDemand> WrapperSignatures;

    public SyntheticDemandPlan(
        IDictionary<string, StructuredFunction> closureBridgeFunctions,
        IDictionary<string, DelegateBindingPlan> delegateBindings,
        IEnumerable<DelegateBridgeDemand> receiverBridges,
        IEnumerable<DelegateBridgeDemand> delegateBridges,
        IDictionary<string, MulticastSigPlan> multicastSignatures,
        IEnumerable<INamedTypeSymbol> enumToStringTypes,
        IEnumerable<INamedTypeSymbol> classToStringTypes,
        IEnumerable<DelegateBridgeDemand> signatureAdapterBridges,
        IDictionary<string, DelegateWrapperDemand> wrapperSignatures)
    {
        _closureBridgeTargets = new ReadOnlyDictionary<string, string>(
            closureBridgeFunctions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.Name
                    ?? throw new ArgumentException(
                        $"Closure bridge '{pair.Key}' has no target function.",
                        nameof(closureBridgeFunctions)),
                StringComparer.Ordinal));
        _delegateBindings = CopyMap(delegateBindings);
        DelegateSites = Array.AsReadOnly(
            delegateBindings.Keys.ToArray());
        var receiverBridgeArray = receiverBridges.ToArray();
        var delegateBridgeArray = delegateBridges.ToArray();
        var signatureAdapterArray = signatureAdapterBridges.ToArray();
        _receiverBridges = ByBridgeName(receiverBridgeArray);
        _delegateBridges = ByBridgeName(delegateBridgeArray);
        _signatureAdapterBridges = ByBridgeName(signatureAdapterArray);
        ReceiverBridges = Array.AsReadOnly(receiverBridgeArray);
        DelegateBridges = Array.AsReadOnly(delegateBridgeArray);
        MulticastSignatures = CopyMap(multicastSignatures);
        var enums = enumToStringTypes.ToArray();
        _enumToStringTypes = new HashSet<INamedTypeSymbol>(
            enums, SymbolEqualityComparer.Default);
        EnumToStringTypes = Array.AsReadOnly(enums);
        var classes = classToStringTypes.ToArray();
        _classToStringTypes = new HashSet<INamedTypeSymbol>(
            classes, SymbolEqualityComparer.Default);
        ClassToStringTypes = Array.AsReadOnly(classes);
        SignatureAdapterBridges = Array.AsReadOnly(signatureAdapterArray);
        WrapperSignatures = CopyMap(wrapperSignatures);
    }

    public bool TryGetClosureBridge(string name, out string functionName)
        => _closureBridgeTargets.TryGetValue(name, out functionName);

    public void RequireClosureBridge(
        string bridgeName,
        string functionName)
    {
        if (_closureBridgeTargets.TryGetValue(
                bridgeName, out var planned)
            && string.Equals(
                planned, functionName, StringComparison.Ordinal))
            return;
        throw new InvalidOperationException(
            $"Closure bridge '{bridgeName}' targeting '{functionName}' "
            + "was absent from the bound program.");
    }

    public DelegateBindingPlan RequireDelegateBinding(string site)
        => _delegateBindings.TryGetValue(site, out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Delegate site '{site}' was absent from the bound program.");

    public void RequireMulticast(string signature, MulticastOperations operation)
    {
        if (MulticastSignatures.TryGetValue(signature, out var planned)
            && (planned.Operations & operation) == operation)
            return;
        throw new InvalidOperationException(
            $"Multicast helper '{signature}' ({operation}) was absent from the bound program.");
    }

    public INamedTypeSymbol RequireEnumToString(INamedTypeSymbol enumType)
    {
        if (_enumToStringTypes.Contains(enumType)) return enumType;
        throw new InvalidOperationException(
            $"Enum ToString helper for '{enumType}' was absent from the bound program.");
    }

    public INamedTypeSymbol RequireClassToString(
        INamedTypeSymbol classType)
    {
        if (_classToStringTypes.Contains(classType))
            return classType;
        throw new InvalidOperationException(
            $"Class ToString dispatch for '{classType}' "
            + "was absent from the bound program.");
    }

    public string RequireWrapper(string wrapperName)
    {
        if (WrapperSignatures.ContainsKey(wrapperName)) return wrapperName;
        throw new InvalidOperationException(
            $"Delegate wrapper '{wrapperName}' was absent from the bound program.");
    }

    public DelegateBridgeDemand RequireReceiverBridge(string bridgeName)
        => RequireBridge(_receiverBridges, bridgeName, "receiver bridge");

    public DelegateBridgeDemand RequireDelegateBridge(string bridgeName)
        => RequireBridge(_delegateBridges, bridgeName, "delegate bridge");

    public DelegateBridgeDemand RequireSignatureAdapter(string bridgeName)
        => RequireBridge(_signatureAdapterBridges, bridgeName, "signature adapter");

    static DelegateBridgeDemand RequireBridge(
        IReadOnlyDictionary<string, DelegateBridgeDemand> bridges,
        string bridgeName,
        string category)
    {
        if (bridges.TryGetValue(bridgeName, out var demand))
            return demand;
        throw new InvalidOperationException(
            $"Synthetic {category} '{bridgeName}' was absent from the bound program.");
    }

    static IReadOnlyDictionary<string, TValue> CopyMap<TValue>(
        IDictionary<string, TValue> source)
        => new ReadOnlyDictionary<string, TValue>(
            new Dictionary<string, TValue>(source, StringComparer.Ordinal));

    static IReadOnlyDictionary<string, DelegateBridgeDemand> ByBridgeName(
        IEnumerable<DelegateBridgeDemand> demands)
        => new ReadOnlyDictionary<string, DelegateBridgeDemand>(
            demands.ToDictionary(
                demand => demand.Binding.BridgeName,
                StringComparer.Ordinal));
}
