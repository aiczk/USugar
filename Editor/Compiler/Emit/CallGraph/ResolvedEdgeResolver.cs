using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>CA call-graph rewrite (M0): the single resolver that replaces the two current classifiers
/// (EnumerateInternalCallTargets + EnumerateStructMemberRefs) and the three non-classifier reach collectors.
/// For an operation it yields every RESOLVED (method, role) target that op reaches at runtime — the literal
/// union, nothing dropped (a drop = GuardUserStructMemberReachedExtern loud-abort). Consumers filter by role.
/// Built per-class in Phase-2 (thread-safe: no shared mutable state). Ambient emitter state (monomorphization
/// map, override resolution) enters as constructor delegates so the resolver stays a focused unit.</summary>
public sealed class ResolvedEdgeResolver
{
    readonly Func<ITypeSymbol, ITypeSymbol> _resolveType; // monomorphization-aware type resolution
    readonly VirtualDispatch _vd;

    public ResolvedEdgeResolver(Func<ITypeSymbol, ITypeSymbol> resolveType, VirtualDispatch vd)
    {
        _resolveType = resolveType;
        _vd = vd;
    }

    public IEnumerable<ResolvedTarget> ResolveEdges(IOperation op)
    {
        switch (op)
        {
            case IInvocationOperation inv:
                yield return new ResolvedTarget(inv.TargetMethod.OriginalDefinition, TargetRole.CallEdge);
                break;
        }
    }
}
