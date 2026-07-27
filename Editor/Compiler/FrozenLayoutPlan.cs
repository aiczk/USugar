using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;

/// <summary>
/// Immutable layout authority consumed by lowering. Construction copies every mutable collection
/// owned by <see cref="LayoutPlanBuilder"/>, so builders can never mutate an active compilation.
/// </summary>
public sealed class FrozenLayoutPlan
{
    readonly IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> _layouts;
    readonly HashSet<INamedTypeSymbol> _interfacesWithStructImplementor;
    readonly HashSet<INamedTypeSymbol> _interfacesWithUserClassImplementor;
    readonly HashSet<INamedTypeSymbol> _interfacesWithBehaviourImplementor;
    readonly IReadOnlyDictionary<INamedTypeSymbol, IReadOnlyList<(
        IMethodSymbol method,
        MethodLayout interfaceLayout,
        IMethodSymbol implMethod,
        MethodLayout classLayout)>> _bridges;

    public CompilationTypeCensus Census { get; }
    public IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> AllLayouts => _layouts;

    internal FrozenLayoutPlan(
        CompilationTypeCensus census,
        IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> layouts,
        IEnumerable<INamedTypeSymbol> interfacesWithStructImplementor,
        IEnumerable<INamedTypeSymbol> interfacesWithUserClassImplementor,
        IEnumerable<INamedTypeSymbol> interfacesWithBehaviourImplementor,
        IReadOnlyDictionary<INamedTypeSymbol, IReadOnlyList<(
            IMethodSymbol method,
            MethodLayout interfaceLayout,
            IMethodSymbol implMethod,
            MethodLayout classLayout)>> bridges)
    {
        Census = census ?? throw new ArgumentNullException(nameof(census));
        _layouts = new ReadOnlyDictionary<INamedTypeSymbol, TypeLayout>(
            new Dictionary<INamedTypeSymbol, TypeLayout>(
                layouts, SymbolEqualityComparer.Default));
        _interfacesWithStructImplementor = new HashSet<INamedTypeSymbol>(
            interfacesWithStructImplementor, SymbolEqualityComparer.Default);
        _interfacesWithUserClassImplementor = new HashSet<INamedTypeSymbol>(
            interfacesWithUserClassImplementor, SymbolEqualityComparer.Default);
        _interfacesWithBehaviourImplementor = new HashSet<INamedTypeSymbol>(
            interfacesWithBehaviourImplementor, SymbolEqualityComparer.Default);
        var bridgeCopies = new Dictionary<INamedTypeSymbol, IReadOnlyList<(
            IMethodSymbol method,
            MethodLayout interfaceLayout,
            IMethodSymbol implMethod,
            MethodLayout classLayout)>>(SymbolEqualityComparer.Default);
        foreach (var pair in bridges)
            bridgeCopies.Add(pair.Key, new List<(
                IMethodSymbol method,
                MethodLayout interfaceLayout,
                IMethodSymbol implMethod,
                MethodLayout classLayout)>(pair.Value).AsReadOnly());
        _bridges = new ReadOnlyDictionary<INamedTypeSymbol, IReadOnlyList<(
            IMethodSymbol method,
            MethodLayout interfaceLayout,
            IMethodSymbol implMethod,
            MethodLayout classLayout)>>(bridgeCopies);
    }

    public TypeLayout GetLayout(INamedTypeSymbol type)
    {
        if (!_layouts.TryGetValue(type, out var layout))
            throw new InvalidOperationException($"Type '{type.Name}' was not pre-planned.");
        return layout;
    }

    public bool InterfaceHasStructImplementor(INamedTypeSymbol iface)
        => _interfacesWithStructImplementor.Contains(iface);

    public bool InterfaceIsLocalUserClassOnly(INamedTypeSymbol iface)
        => iface != null
           && _interfacesWithUserClassImplementor.Contains(iface)
           && !_interfacesWithBehaviourImplementor.Contains(iface)
           && !_interfacesWithStructImplementor.Contains(iface);

    public bool InterfaceHasMixedRuntimeRepresentations(INamedTypeSymbol iface)
        => iface != null && (_interfacesWithStructImplementor.Contains(iface)
            || _interfacesWithUserClassImplementor.Contains(iface)
               && _interfacesWithBehaviourImplementor.Contains(iface));

    public IReadOnlyList<(IMethodSymbol method, MethodLayout interfaceLayout,
        IMethodSymbol implMethod, MethodLayout classLayout)> RequireBridges(
        INamedTypeSymbol classType)
    {
        if (_bridges.TryGetValue(classType, out var bridges))
            return bridges;
        throw new InvalidOperationException(
            $"Interface bridges for '{classType?.Name}' were not pre-planned.");
    }

    /// <summary>Walk an override to the declaring base whose layout owns the slot; the chain stops at
    /// UdonSharpBehaviour and at any type without source, which have no planned layout.</summary>
    static IMethodSymbol NormalizeOverrideChain(IMethodSymbol method)
    {
        while (method.IsOverride && method.OverriddenMethod != null)
        {
            var containingType = method.OverriddenMethod.ContainingType;
            if (containingType.Name == "UdonSharpBehaviour"
                || containingType.DeclaringSyntaxReferences.IsEmpty)
                break;
            method = method.OverriddenMethod;
        }
        return method;
    }

    public DelegateBridgeLayout GetDelegateBridgeLayout(IMethodSymbol method)
    {
        var normalized = NormalizeOverrideChain(method);
        var layout = GetLayout(normalized.ContainingType);
        if (layout.DelegateBridges.TryGetValue(normalized, out var bridge)) return bridge;
        throw new InvalidOperationException(
            $"No delegate bridge for '{method.Name}' on '{method.ContainingType.Name}'");
    }

    public MethodLayout GetCalleeLayout(IMethodSymbol target)
    {
        var layout = TryGetCalleeLayout(target);
        if (layout != null) return layout;
        throw new InvalidOperationException(
            $"Method {target.Name} not found in layout for {target.ContainingType.Name}");
    }

    public MethodLayout TryGetCalleeLayout(IMethodSymbol target)
    {
        var method = NormalizeOverrideChain(target);
        var layout = GetLayout(method.ContainingType);
        return layout.Methods.TryGetValue(method, out var methodLayout) ? methodLayout : null;
    }
}
