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
/// Owns per-class synthetic emission queues populated during body emission and drained by UasmEmitter.
/// </summary>
public sealed class SyntheticContext
{
    public readonly HashSet<string> DelegateFields = new();

    // Per-spec closure bridges (design 2026-07-10 v3 SS2B): bridgeExportName -> the closure's
    // per-spec CFunction. The pending-bridge drain resolves closure targets here (a bare
    // definition-symbol lookup cannot distinguish specs).
    readonly Dictionary<string, CFunction> _closureBridgeFuncs = new();
    public IReadOnlyDictionary<string, CFunction> ClosureBridgeFuncs => _closureBridgeFuncs;

    // MG auto-wrap (design 2026-07-11 v2): pending receiver-bridges — a class/struct instance method
    // group's bridge re-dispatches DelegateAbi.Env as the member's param0 (CA-M1 receiver ABI).
    readonly Dictionary<string, DelegateBridgeDemand> _receiverBridges = new();
    public IReadOnlyCollection<DelegateBridgeDemand> ReceiverBridges => _receiverBridges.Values;

    // Pending delegate bridges for dynamically hoisted lambdas/local functions. The carried map is
    // the creating method's immutable TypeParamMap by REFERENCE (per-EmitMethod fresh, never mutated,
    // so no snapshot copy is needed even though the drain runs after emission when the ambient map
    // is null).
    readonly Dictionary<string, DelegateBridgeDemand> _delegateBridges = new();
    public IReadOnlyCollection<DelegateBridgeDemand> DelegateBridges => _delegateBridges.Values;

    // Multicast: sig-part -> signature plus the exact combine/remove operations used by this class.
    // Sites sharing a signature merge their flags; the drain emits one fan-out and only the helpers
    // actually referenced by lowering.
    readonly Dictionary<string, MulticastSigPlan> _multicastSigs = new();
    public IReadOnlyDictionary<string, MulticastSigPlan> MulticastSigs => _multicastSigs;

    // B67: user enums whose ToString()/concat/interpolation needs the synthesized __enumstr_ helper.
    readonly HashSet<INamedTypeSymbol> _enumToString = new(SymbolEqualityComparer.Default);
    public IReadOnlyCollection<INamedTypeSymbol> EnumToString => _enumToString;

    // Variance (2026-07-04 design 2.2, B-1): per-(target, sig-S) sig adapter bridges. DelegateInvoke
    // is the DESTINATION delegate's own Invoke (conv-var declarations), distinct from TargetMethod
    // (the real callee, InternalCall only). Dedup-by-name at emission.
    readonly Dictionary<string, DelegateBridgeDemand> _sigAdapterBridges = new();
    public IReadOnlyCollection<DelegateBridgeDemand> SigAdapterBridges => _sigAdapterBridges.Values;

    // Variance (2026-07-04 design 2.3, B-2): wrapper name -> (outer sig-S Invoke, inner sig-T
    // Invoke-or-method, resolved map). Keyed by WRAPPER NAME (unique per (outer,inner) sig pair) -
    // a wrapper's inner dispatch speaks the INNER bundle's own protocol.
    readonly Dictionary<string, DelegateWrapperDemand> _wrapperSigs = new();
    public IReadOnlyDictionary<string, DelegateWrapperDemand> WrapperSigs => _wrapperSigs;

    public bool IsFrozen { get; private set; }

    void RequireMutable()
    {
        if (IsFrozen) throw new InvalidOperationException("Synthetic demand plan is frozen.");
    }

    public void RegisterClosureBridge(string name, CFunction function)
    { RequireMutable(); _closureBridgeFuncs[name] = function; }

    public bool TryGetClosureBridge(string name, out CFunction function)
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
        _multicastSigs[signature] = _multicastSigs.TryGetValue(signature, out var existing)
            ? existing.With(operation)
            : new MulticastSigPlan(invoke, typeParamMap, operation);
    }

    public void RegisterEnumToString(INamedTypeSymbol enumType)
    { RequireMutable(); _enumToString.Add(enumType); }

    public void RegisterWrapper(DelegateBindingPlan binding, IMethodSymbol outerInvoke,
        IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.Wrapper)
            throw new ArgumentException("Wrapper demand requires a wrapper binding.", nameof(binding));
        if (!_wrapperSigs.ContainsKey(binding.BridgeName))
            _wrapperSigs.Add(binding.BridgeName, new DelegateWrapperDemand(
                binding,
                outerInvoke, innerInvoke, typeParamMap));
    }

    public void Freeze()
    {
        if (IsFrozen) throw new InvalidOperationException("Synthetic demand plan was frozen twice.");
        IsFrozen = true;
    }

    static void RegisterUnique(Dictionary<string, DelegateBridgeDemand> demands,
        DelegateBridgeDemand demand, string category)
    {
        var name = demand.Binding.BridgeName;
        if (!demands.TryGetValue(name, out var existing))
        {
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
}
