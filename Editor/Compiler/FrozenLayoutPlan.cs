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

    public CompilationSession Session { get; }
    public CompilationTypeCensus Census { get; }
    public UdonTypeFactRegistry TypeFacts => Session.TypeFacts;
    public IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> AllLayouts => _layouts;

    internal FrozenLayoutPlan(
        CompilationSession session,
        CompilationTypeCensus census,
        IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> layouts,
        IEnumerable<INamedTypeSymbol> interfacesWithStructImplementor,
        IEnumerable<INamedTypeSymbol> interfacesWithUserClassImplementor,
        IEnumerable<INamedTypeSymbol> interfacesWithBehaviourImplementor)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
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

    public List<(IMethodSymbol method, MethodLayout interfaceLayout,
        IMethodSymbol implMethod, MethodLayout classLayout)> ComputeBridges(
        INamedTypeSymbol classType)
    {
        var bridges = new List<(IMethodSymbol, MethodLayout, IMethodSymbol, MethodLayout)>();
        var classLayout = GetLayout(classType);

        foreach (var iface in classType.AllInterfaces)
        {
            var ifaceLayout = GetLayout(iface);
            foreach (var (ifaceMethod, ifaceMl) in ifaceLayout.Methods)
            {
                var impl = classType.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
                if (impl != null && impl.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    if (ifaceMl.Returns.Count <= 1)
                        bridges.Add((ifaceMethod, ifaceMl, impl, ifaceMl));
                    continue;
                }
                if (impl == null) continue;

                var implOwner = impl;
                if (!classLayout.Methods.TryGetValue(implOwner, out var classMl))
                {
                    implOwner = null;
                    foreach (var (method, methodLayout) in classLayout.Methods)
                    {
                        for (var current = method.OverriddenMethod;
                             current != null;
                             current = current.OverriddenMethod)
                        {
                            if (!SymbolEqualityComparer.Default.Equals(current, impl)) continue;
                            implOwner = method;
                            classMl = methodLayout;
                            break;
                        }
                        if (implOwner != null) break;
                    }
                    if (implOwner == null) continue;
                }
                if (ifaceMl.Returns.Count > 1) continue;
                bridges.Add((ifaceMethod, ifaceMl, implOwner, classMl));
            }
        }

        return bridges;
    }

    public DelegateBridgeLayout GetDelegateBridgeLayout(IMethodSymbol method)
    {
        var normalized = method;
        while (normalized.IsOverride && normalized.OverriddenMethod != null)
        {
            var containingType = normalized.OverriddenMethod.ContainingType;
            if (containingType.Name == "UdonSharpBehaviour"
                || containingType.DeclaringSyntaxReferences.IsEmpty)
                break;
            normalized = normalized.OverriddenMethod;
        }
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
        var method = target;
        while (method.IsOverride && method.OverriddenMethod != null)
        {
            var containingType = method.OverriddenMethod.ContainingType;
            if (containingType.Name == "UdonSharpBehaviour"
                || containingType.DeclaringSyntaxReferences.IsEmpty)
                break;
            method = method.OverriddenMethod;
        }

        var layout = GetLayout(method.ContainingType);
        return layout.Methods.TryGetValue(method, out var methodLayout) ? methodLayout : null;
    }
}
