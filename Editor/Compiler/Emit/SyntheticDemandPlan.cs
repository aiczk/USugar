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
    readonly IReadOnlyDictionary<
        BoundDelegateSiteKey, DelegateBindingPlan> _delegateBindings;
    readonly HashSet<INamedTypeSymbol> _enumToStringTypes;
    readonly HashSet<INamedTypeSymbol> _classToStringTypes;

    public readonly IReadOnlyCollection<DelegateBridgeDemand> ReceiverBridges;
    public readonly IReadOnlyCollection<DelegateBridgeDemand> DelegateBridges;
    public readonly IReadOnlyCollection<BoundDelegateSiteKey> DelegateSites;
    public readonly IReadOnlyDictionary<string, MulticastSigPlan> MulticastSignatures;
    public readonly IReadOnlyCollection<INamedTypeSymbol> EnumToStringTypes;
    public readonly IReadOnlyCollection<INamedTypeSymbol> ClassToStringTypes;
    public readonly IReadOnlyCollection<DelegateBridgeDemand> SignatureAdapterBridges;
    public readonly IReadOnlyDictionary<string, DelegateWrapperDemand> WrapperSignatures;

    public SyntheticDemandPlan(
        IDictionary<string, string> closureBridgeTargets,
        IDictionary<BoundDelegateSiteKey, DelegateBindingPlan>
            delegateBindings,
        IEnumerable<DelegateBridgeDemand> receiverBridges,
        IEnumerable<DelegateBridgeDemand> delegateBridges,
        IDictionary<string, MulticastSigPlan> multicastSignatures,
        IEnumerable<INamedTypeSymbol> enumToStringTypes,
        IEnumerable<INamedTypeSymbol> classToStringTypes,
        IEnumerable<DelegateBridgeDemand> signatureAdapterBridges,
        IDictionary<string, DelegateWrapperDemand> wrapperSignatures)
    {
        _closureBridgeTargets = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                closureBridgeTargets
                ?? throw new ArgumentNullException(
                    nameof(closureBridgeTargets)),
                StringComparer.Ordinal));
        _delegateBindings = new ReadOnlyDictionary<
            BoundDelegateSiteKey, DelegateBindingPlan>(
            new Dictionary<
                BoundDelegateSiteKey, DelegateBindingPlan>(
                delegateBindings
                ?? throw new ArgumentNullException(
                    nameof(delegateBindings))));
        DelegateSites = Array.AsReadOnly(
            _delegateBindings.Keys.ToArray());
        var receiverBridgeArray = receiverBridges.ToArray();
        var delegateBridgeArray = delegateBridges.ToArray();
        var signatureAdapterArray = signatureAdapterBridges.ToArray();
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

    public DelegateBindingPlan RequireDelegateBinding(
        BoundDelegateSiteKey site)
        => _delegateBindings.TryGetValue(site, out var binding)
            ? binding
            : throw new InvalidOperationException(
                $"Delegate site '{site.Operation.Syntax}' was absent "
                + "from the bound program.");

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

    static IReadOnlyDictionary<string, TValue> CopyMap<TValue>(
        IDictionary<string, TValue> source)
        => new ReadOnlyDictionary<string, TValue>(
            new Dictionary<string, TValue>(source, StringComparer.Ordinal));

}
