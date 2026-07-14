using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>CA call-graph rewrite (M0): the single resolver that replaces the two current classifiers
/// (EnumerateInternalCallTargets + EnumerateStructMemberRefs) and the three non-classifier reach collectors.
/// For an operation it yields every RESOLVED (method, role) target that op reaches at runtime — the literal
/// union, nothing dropped (a drop = GuardUserStructMemberReachedExtern loud-abort). Consumers filter by role.
/// Built per-class in Phase-2 (thread-safe: no shared mutable state). Ambient emitter state (monomorphization
/// map, override resolution) enters as constructor delegates / the class symbol so the resolver stays a
/// focused unit.</summary>
public sealed class ResolvedEdgeResolver
{
    readonly Func<ITypeSymbol, ITypeSymbol> _resolveType; // monomorphization-aware type resolution
    readonly VirtualDispatch _vd;
    readonly INamedTypeSymbol _classSymbol;

    public ResolvedEdgeResolver(Func<ITypeSymbol, ITypeSymbol> resolveType, VirtualDispatch vd, INamedTypeSymbol classSymbol)
    {
        _resolveType = resolveType;
        _vd = vd;
        _classSymbol = classSymbol;
    }

    public IEnumerable<ResolvedTarget> ResolveEdges(IOperation op)
    {
        switch (op)
        {
            case IInvocationOperation inv:
                // Faithful port of EnumerateInternalCallTargets' IInvocationOperation arm (UasmEmitter.cs):
                // static target, this-virtual leaf override, variable/interface cross-dispatch landing, and
                // the v2b-2 closed-world virtual override set. All are CallEdge — a synthetic caller edge.
                yield return new ResolvedTarget(inv.TargetMethod.OriginalDefinition, TargetRole.CallEdge);
                if (LeafCallTarget(inv) is { } leafT)
                    yield return new ResolvedTarget(leafT, TargetRole.CallEdge);
                if (IsCrossDispatchReceiver(inv.Instance, inv.TargetMethod)
                    && CrossDispatchLocalTarget(inv.TargetMethod) is { } crossT)
                    yield return new ResolvedTarget(crossT, TargetRole.CallEdge);
                if (inv.Instance?.Type is INamedTypeSymbol vrecv
                    && VirtualDispatch.IsDispatchSite(inv.TargetMethod, inv.Instance, vrecv))
                    foreach (var vt in _vd.ResolveTargets(vrecv, inv.TargetMethod))
                        yield return new ResolvedTarget(vt.Impl.OriginalDefinition, TargetRole.CallEdge);
                break;
        }
    }

    // ── Faithful ports of the emitter's leaf/cross resolution (UasmEmitter.cs LeafCallTarget /
    // CrossDispatchLocalTarget / IsCrossDispatchReceiver / ResolveLeafOverrideDef). The graph is keyed by
    // OriginalDefinition, so these normalize to definitions. They duplicate the emitter's copies for the
    // strangler-fig transition; the corpus equivalence gate (M0 Task 8) proves they don't drift, and M5
    // deletes the emitter's copies once the resolver is the sole consumer.
    IMethodSymbol LeafCallTarget(IInvocationOperation inv)
    {
        var tm = inv.TargetMethod;
        if (!(tm.IsVirtual || tm.IsOverride || tm.IsAbstract) || tm.MethodKind != MethodKind.Ordinary)
            return null;
        if (inv.Instance is not IInstanceReferenceOperation iref || iref.Syntax is BaseExpressionSyntax)
            return null;
        var def = tm.OriginalDefinition;
        var leaf = ResolveLeafOverrideDef(def);
        return SymbolEqualityComparer.Default.Equals(leaf, def) ? null : leaf;
    }

    IMethodSymbol ResolveLeafOverrideDef(IMethodSymbol def)
        => HandlerBase.FindOverrideMethodInChain(_classSymbol, def, def.Name)?.OriginalDefinition ?? def;

    static bool IsCrossDispatchReceiver(IOperation instance, ISymbol member)
        => instance != null
           && (instance is not IInstanceReferenceOperation
               || member.ContainingType?.TypeKind == TypeKind.Interface);

    IMethodSymbol CrossDispatchLocalTarget(IMethodSymbol target)
    {
        if (target == null || target.IsStatic) return null;
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            var impl = (_classSymbol.FindImplementationForInterfaceMember(target)
                        ?? _classSymbol.FindImplementationForInterfaceMember(target.OriginalDefinition))
                       as IMethodSymbol;
            return impl == null ? null : ResolveLeafOverrideDef(impl.OriginalDefinition);
        }
        for (var t = _classSymbol; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target.ContainingType))
                return ResolveLeafOverrideDef(target.OriginalDefinition);
        return null;
    }
}
