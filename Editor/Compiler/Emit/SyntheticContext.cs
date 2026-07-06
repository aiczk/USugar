using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns per-class synthetic emission queues populated during body emission and drained by UasmEmitter.
/// </summary>
public sealed class SyntheticContext
{
    public readonly HashSet<string> DelegateFields = new();

    public readonly List<(IMethodSymbol Method, string BridgeExportName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> DelegateBridges = new();

    public readonly Dictionary<string, (IMethodSymbol Invoke, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> MulticastSigs = new();

    public readonly HashSet<INamedTypeSymbol> EnumToString = new(SymbolEqualityComparer.Default);

    public readonly List<(IMethodSymbol TargetMethod, IMethodSymbol DelegateInvoke, string AdapterName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> SigAdapterBridges = new();

    public readonly Dictionary<string, (IMethodSymbol OuterInvoke, IMethodSymbol InnerInvoke, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> WrapperSigs = new();
}
