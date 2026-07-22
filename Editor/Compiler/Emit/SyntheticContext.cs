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
    public readonly Dictionary<string, CFunction> ClosureBridgeFuncs = new();

    // MG auto-wrap (design 2026-07-11 v2): pending receiver-bridges — a class/struct instance method
    // group's bridge re-dispatches DelegateAbi.Env as the member's param0 (CA-M1 receiver ABI).
    public readonly List<(IMethodSymbol Member, string BridgeName)> ReceiverBridges = new();

    // Pending delegate bridges for dynamically hoisted lambdas/local functions. The carried map is
    // the creating method's immutable TypeParamMap by REFERENCE (per-EmitMethod fresh, never mutated,
    // so no snapshot copy is needed even though the drain runs after emission when the ambient map
    // is null).
    public readonly List<(IMethodSymbol Method, string BridgeExportName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> DelegateBridges = new();

    // Multicast: sig-part -> signature plus the exact combine/remove operations used by this class.
    // Sites sharing a signature merge their flags; the drain emits one fan-out and only the helpers
    // actually referenced by lowering.
    public readonly Dictionary<string, MulticastSigPlan> MulticastSigs = new();

    // B67: user enums whose ToString()/concat/interpolation needs the synthesized __enumstr_ helper.
    public readonly HashSet<INamedTypeSymbol> EnumToString = new(SymbolEqualityComparer.Default);

    // Variance (2026-07-04 design 2.2, B-1): per-(target, sig-S) sig adapter bridges. DelegateInvoke
    // is the DESTINATION delegate's own Invoke (conv-var declarations), distinct from TargetMethod
    // (the real callee, InternalCall only). Dedup-by-name at emission.
    public readonly List<(IMethodSymbol TargetMethod, IMethodSymbol DelegateInvoke, string AdapterName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> SigAdapterBridges = new();

    // Variance (2026-07-04 design 2.3, B-2): wrapper name -> (outer sig-S Invoke, inner sig-T
    // Invoke-or-method, resolved map). Keyed by WRAPPER NAME (unique per (outer,inner) sig pair) -
    // a wrapper's inner dispatch speaks the INNER bundle's own protocol.
    public readonly Dictionary<string, (IMethodSymbol OuterInvoke, IMethodSymbol InnerInvoke, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> WrapperSigs = new();
}
