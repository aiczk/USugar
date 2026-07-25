using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

[System.Flags]
public enum MulticastOperations
{
    None = 0,
    Combine = 1,
    Remove = 2,
}

public readonly struct MulticastSigPlan
{
    public readonly IMethodSymbol Invoke;
    public readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;
    public readonly MulticastOperations Operations;

    public MulticastSigPlan(IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        MulticastOperations operations)
    {
        Invoke = invoke;
        TypeParamMap = typeParamMap;
        Operations = operations;
    }

    public MulticastSigPlan With(MulticastOperations operations)
        => new(Invoke, TypeParamMap, Operations | operations);
}

/// <summary>
/// Owns the per-class synthetic emission plan built before body emission and drained by UasmEmitter.
/// </summary>
public sealed class SyntheticContext
{
    public readonly HashSet<string> DelegateFields = new();

    // Per-spec closure bridges (design 2026-07-10 v3 SS2B): bridgeExportName -> the closure's
    // per-spec StructuredFunction. The pending-bridge drain resolves closure targets here (a bare
    // definition-symbol lookup cannot distinguish specs).
    readonly Dictionary<string, StructuredFunction> _closureBridgeFuncs = new();

    // MG auto-wrap (design 2026-07-11 v2): pending receiver-bridges — a class/struct instance method
    // group's bridge re-dispatches DelegateAbi.Env as the member's param0 (CA-M1 receiver ABI).
    readonly Dictionary<string, DelegateBridgeDemand> _receiverBridges = new();

    // Pending delegate bridges for dynamically hoisted lambdas/local functions. The carried map is
    // the creating method's immutable TypeParamMap by REFERENCE (per-EmitMethod fresh, never mutated,
    // so no snapshot copy is needed even though the drain runs after emission when the ambient map
    // is null).
    readonly Dictionary<string, DelegateBridgeDemand> _delegateBridges = new();

    // Multicast: sig-part -> signature plus the exact combine/remove operations used by this class.
    // Sites sharing a signature merge their flags; the drain emits one fan-out and only the helpers
    // actually referenced by lowering.
    readonly Dictionary<string, MulticastSigPlan> _multicastSigs = new();

    // B67: user enums whose ToString()/concat/interpolation needs the synthesized __enumstr_ helper.
    readonly HashSet<INamedTypeSymbol> _enumToString = new(SymbolEqualityComparer.Default);

    // Variance (2026-07-04 design 2.2, B-1): per-(target, sig-S) sig adapter bridges. DelegateInvoke
    // is the DESTINATION delegate's own Invoke (conv-var declarations), distinct from TargetMethod
    // (the real callee, InternalCall only). Dedup-by-name at emission.
    readonly Dictionary<string, DelegateBridgeDemand> _sigAdapterBridges = new();

    // Variance (2026-07-04 design 2.3, B-2): wrapper name -> (outer sig-S Invoke, inner sig-T
    // Invoke-or-method, resolved map). Keyed by WRAPPER NAME (unique per (outer,inner) sig pair) -
    // a wrapper's inner dispatch speaks the INNER bundle's own protocol.
    readonly Dictionary<string, DelegateWrapperDemand> _wrapperSigs = new();

    bool _emissionVerified;
    bool _demandsPublished;
    HashSet<string> _expectedDelegateSites;
    readonly Dictionary<string, DelegateBindingPlan> _plannedDelegateSites = new(StringComparer.Ordinal);
    readonly HashSet<string> _emittedDelegateSites = new(StringComparer.Ordinal);

    public void SetExpectedDelegateSites(IEnumerable<string> sites)
    {
        RequireMutable();
        if (_expectedDelegateSites != null)
            throw new InvalidOperationException("Delegate demand census was set twice.");
        _expectedDelegateSites = new HashSet<string>(
            sites ?? throw new ArgumentNullException(nameof(sites)), StringComparer.Ordinal);
    }

    public void PlanDelegateBinding(string key, DelegateBindingPlan binding)
    {
        RequireMutable();
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Delegate site key is required.", nameof(key));
        if (_expectedDelegateSites == null || !_expectedDelegateSites.Contains(key))
            throw new InvalidOperationException(
                $"Delegate binding plan at '{key}' was absent from the pre-emission demand census.");
        if (_plannedDelegateSites.TryGetValue(key, out var existing))
        {
            if (SameBinding(existing, binding)) return;
            throw new InvalidOperationException(
                $"Delegate site '{key}' planned conflicting bindings "
                + $"'{existing.BridgeName}' and '{binding.BridgeName}'.");
        }
        _plannedDelegateSites.Add(key, binding);
    }

    public void RecordDelegateBinding(string key, DelegateBindingPlan binding)
    {
        RequireMutable();
        if (!_plannedDelegateSites.TryGetValue(key, out var planned))
            throw new InvalidOperationException(
                $"Delegate binding at '{key}' was absent from the pre-emission binding plan.");
        if (!SameBinding(planned, binding))
            throw new InvalidOperationException(
                $"Delegate site '{key}' emitted binding '{binding.BridgeName}' but planned "
                + $"'{planned.BridgeName}'.");
        _emittedDelegateSites.Add(key);
    }

    internal SyntheticDemandPlan PublishPlan()
    {
        RequireMutable();
        if (_demandsPublished)
            throw new InvalidOperationException("Synthetic demand plan was published twice.");
        if (_expectedDelegateSites == null)
            throw new InvalidOperationException("Synthetic demand plan has no delegate-site census.");
        foreach (var site in _expectedDelegateSites)
            if (!_plannedDelegateSites.ContainsKey(site))
                throw new InvalidOperationException(
                    $"Delegate site '{site}' was not bound during synthetic demand planning.");
        _demandsPublished = true;
        return new SyntheticDemandPlan(
            _closureBridgeFuncs,
            _receiverBridges.Values,
            _delegateBridges.Values,
            _multicastSigs,
            _enumToString,
            _sigAdapterBridges.Values,
            _wrapperSigs);
    }

    void RequireMutable()
    {
        if (_emissionVerified)
            throw new InvalidOperationException("Synthetic demand emission was already verified.");
    }

    public void RegisterClosureBridge(string name, StructuredFunction function)
    {
        RequireMutable();
        if (_demandsPublished && !_closureBridgeFuncs.ContainsKey(name))
            throw new InvalidOperationException(
                $"Closure bridge '{name}' was first discovered during body emission.");
        _closureBridgeFuncs[name] = function;
    }

    public bool TryGetClosureBridge(string name, out StructuredFunction function)
        => _closureBridgeFuncs.TryGetValue(name, out function);

    public void RegisterReceiverBridge(DelegateBindingPlan binding)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.Receiver)
            throw new ArgumentException("Receiver bridge demand requires a receiver binding.", nameof(binding));
        RegisterUnique(_receiverBridges,
            new DelegateBridgeDemand(binding, binding.TargetMethod, null), "receiver bridge");
    }

    public void RegisterDelegateBridge(DelegateBindingPlan binding,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        RegisterUnique(_delegateBridges,
            new DelegateBridgeDemand(binding, binding.TargetMethod, typeParamMap), "delegate bridge");
    }

    public void RegisterSigAdapter(DelegateBindingPlan binding, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.SignatureAdapter)
            throw new ArgumentException("Signature adapter demand requires an adapter binding.", nameof(binding));
        RegisterUnique(_sigAdapterBridges,
            new DelegateBridgeDemand(binding, invoke, typeParamMap), "signature adapter");
    }

    public void RegisterMulticast(string signature, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        MulticastOperations operation)
    {
        RequireMutable();
        if (_demandsPublished
            && (!_multicastSigs.TryGetValue(signature, out var planned)
                || (planned.Operations & operation) != operation))
            throw new InvalidOperationException(
                $"Multicast demand '{signature}' ({operation}) was first discovered during body emission.");
        _multicastSigs[signature] = _multicastSigs.TryGetValue(signature, out var existing)
            ? existing.With(operation)
            : new MulticastSigPlan(invoke, typeParamMap, operation);
    }

    public void RegisterEnumToString(INamedTypeSymbol enumType)
    {
        RequireMutable();
        if (_demandsPublished && !_enumToString.Contains(enumType))
            throw new InvalidOperationException(
                $"Enum ToString helper for '{enumType}' was first discovered during body emission.");
        _enumToString.Add(enumType);
    }

    public void RegisterWrapper(DelegateBindingPlan binding, IMethodSymbol outerInvoke,
        IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.Wrapper)
            throw new ArgumentException("Wrapper demand requires a wrapper binding.", nameof(binding));
        if (_demandsPublished && !_wrapperSigs.ContainsKey(binding.BridgeName))
            throw new InvalidOperationException(
                $"Delegate wrapper '{binding.BridgeName}' was first discovered during body emission.");
        if (!_wrapperSigs.ContainsKey(binding.BridgeName))
            _wrapperSigs.Add(binding.BridgeName, new DelegateWrapperDemand(
                binding,
                outerInvoke, innerInvoke, typeParamMap));
    }

    public void VerifyEmissionComplete()
    {
        if (_emissionVerified)
            throw new InvalidOperationException("Synthetic demand emission was verified twice.");
        if (_expectedDelegateSites == null)
            throw new InvalidOperationException("Synthetic demand plan has no delegate-site census.");
        foreach (var site in _expectedDelegateSites)
            if (!_emittedDelegateSites.Contains(site))
                throw new InvalidOperationException(
                    $"Planned delegate site '{site}' was not emitted during body emission.");
        _emissionVerified = true;
    }

    void RegisterUnique(Dictionary<string, DelegateBridgeDemand> demands,
        DelegateBridgeDemand demand, string category)
    {
        var name = demand.Binding.BridgeName;
        if (!demands.TryGetValue(name, out var existing))
        {
            if (_demandsPublished)
                throw new InvalidOperationException(
                    $"Synthetic {category} '{name}' was first discovered during body emission.");
            demands.Add(name, demand);
            return;
        }
        if (SameDemandMethod(existing.Binding.TargetMethod, demand.Binding.TargetMethod)
            && SameDemandMethod(existing.SignatureMethod, demand.SignatureMethod))
            return;
        throw new InvalidOperationException(
            $"Synthetic {category} name '{name}' maps to both "
            + $"'{existing.Binding.TargetMethod}' and '{demand.Binding.TargetMethod}'.");
    }

    static bool SameDemandMethod(IMethodSymbol left, IMethodSymbol right)
        => SymbolEqualityComparer.Default.Equals(left, right)
           || left != null && right != null
              && left.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
              && right.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
              && ClosureIdentityPlan.SameSourceDefinition(left, right);

    static bool SameBinding(DelegateBindingPlan left, DelegateBindingPlan right)
        => left != null && right != null
           && left.Kind == right.Kind
           && left.BridgeName == right.BridgeName
           && SameDemandMethod(left.TargetMethod, right.TargetMethod);
}
