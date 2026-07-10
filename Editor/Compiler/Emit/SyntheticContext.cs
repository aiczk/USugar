using System.Collections.Generic;
using Microsoft.CodeAnalysis;

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

    // Pending delegate bridges for dynamically hoisted lambdas/local functions. The carried map is
    // the creating method's immutable TypeParamMap by REFERENCE (per-EmitMethod fresh, never mutated,
    // so no snapshot copy is needed even though the drain runs after emission when the ambient map
    // is null).
    public readonly List<(IMethodSymbol Method, string BridgeExportName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> DelegateBridges = new();

    // Multicast (2026-07-03 design 1): sig-part -> (Invoke, resolved map) for every delegate signature
    // this class combines/removes via +=/-=. Keyed on sig content so two sites sharing a signature
    // dedupe to one fan-out/helper set. Drives the per-class __dlg_fanout_/combine_/remove_ drain.
    public readonly Dictionary<string, (IMethodSymbol Invoke, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> MulticastSigs = new();

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
