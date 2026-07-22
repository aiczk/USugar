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

    public void RegisterReceiverBridge(IMethodSymbol member, string name)
    {
        RequireMutable();
        _receiverBridges.TryAdd(name, new DelegateBridgeDemand(
            new DelegateBindingPlan(DelegateBindingKind.Receiver, member, name), member, null));
    }

    public void RegisterDelegateBridge(IMethodSymbol method, string name,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        var kind = method.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
            ? DelegateBindingKind.Closure : DelegateBindingKind.Direct;
        _delegateBridges.TryAdd(name, new DelegateBridgeDemand(
            new DelegateBindingPlan(kind, method, name), method, typeParamMap));
    }

    public void RegisterSigAdapter(IMethodSymbol target, IMethodSymbol invoke, string name,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        _sigAdapterBridges.TryAdd(name, new DelegateBridgeDemand(
            new DelegateBindingPlan(DelegateBindingKind.SignatureAdapter, target, name),
            invoke, typeParamMap));
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

    public void RegisterWrapper(string name, IMethodSymbol outerInvoke, IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        if (!_wrapperSigs.ContainsKey(name))
            _wrapperSigs.Add(name, new DelegateWrapperDemand(
                new DelegateBindingPlan(DelegateBindingKind.Wrapper, innerInvoke, name),
                outerInvoke, innerInvoke, typeParamMap));
    }

    public void Freeze()
    {
        if (IsFrozen) throw new InvalidOperationException("Synthetic demand plan was frozen twice.");
        IsFrozen = true;
    }
}
