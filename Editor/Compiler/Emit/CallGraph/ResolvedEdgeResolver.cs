using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>CA call-graph rewrite (M0): the single resolver that unifies the two current classifiers
/// (EnumerateInternalCallTargets + EnumerateStructMemberRefs) and the reach collectors behind ONE per-op
/// entry, yielding every resolved (method, role) target the op reaches at runtime. Consumers filter by role.
///
/// M0 is a thin role-tagging facade: where a per-op-pure classifier already exists on the emitter it
/// delegates (single-sourced, DRY); where only a recursive collector exists (foreign-static / base-instance)
/// it carries the per-op iteration here and delegates the predicates to the emitter. The logic stays
/// single-sourced until M5 relocates it in and deletes the emitter's copies. Byte-neutral in M0 (no consumer
/// reads this yet); correctness is proven at each M1+ cutover by golden + DiffFuzz (the owner-approved gate).
/// Built per-class in Phase-2 — thread-safe (no shared mutable state beyond the emitter it reads).</summary>
public sealed class ResolvedEdgeResolver
{
    readonly UasmEmitter _emitter;

    public ResolvedEdgeResolver(UasmEmitter emitter) { _emitter = emitter; }

    public IEnumerable<ResolvedTarget> ResolveEdges(IOperation op)
    {
        // CallEdge: the per-op internal-call classifier (invocation / ctor / property accessor / operator),
        // including the v2b-2 virtual override set — a synthetic caller edge.
        foreach (var m in _emitter.EnumerateInternalCallTargets(op))
            yield return new ResolvedTarget(m.OriginalDefinition, TargetRole.CallEdge);

        // ReachStructMember: the per-op user-struct/class member enumerator (ctor / instance method /
        // computed property / subpattern / operator / conversion) plus the implicit using-Dispose.
        foreach (var m in UasmEmitter.EnumerateStructMemberRefs(op))
            yield return new ResolvedTarget(m.OriginalDefinition, TargetRole.ReachStructMember);
        foreach (var m in EnumerateUsingDispose(op))
            yield return new ResolvedTarget(m.OriginalDefinition, TargetRole.ReachStructMember);

        // ReachForeignStatic: a closed, non-generic foreign static reached by call / method-group / static
        // computed-property. (Open/generic drops feed capture roots — a supplementary facet deferred to the
        // capture-consumer cutover, not a reach target.)
        foreach (var m in EnumerateForeignStaticReach(op))
            yield return new ResolvedTarget(m.OriginalDefinition, TargetRole.ReachForeignStatic);

        // ReachBaseInstance: a base instance method/accessor copy reached through `base.` or an inherited call.
        foreach (var m in EnumerateBaseInstanceReach(op))
            yield return new ResolvedTarget(m.OriginalDefinition, TargetRole.ReachBaseInstance);
    }

    // ── per-op reach cores (the iteration structure the recursive collectors lack; predicates delegate to
    // the emitter so they stay single-sourced). Faithful ports of the reach-only (`result.Add`) arms of
    // CollectForeignStaticCallsInOperation / CollectBaseInstanceCallsInOperation / CollectUsingDispose. ──

    IEnumerable<IMethodSymbol> EnumerateForeignStaticReach(IOperation op)
    {
        if (op is IInvocationOperation inv && _emitter.IsForeignStatic(inv.TargetMethod))
        {
            var original = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
            if (!original.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original;
        }
        if (op is IMethodReferenceOperation mref && _emitter.IsForeignStatic(mref.Method))
        {
            var original = mref.Method.ReducedFrom ?? mref.Method;
            if (!original.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original;
        }
        if (op is IPropertyReferenceOperation spr && spr.Property.IsStatic && UasmEmitter.IsComputedProperty(spr.Property))
        {
            if (spr.Property.GetMethod is { } sg && _emitter.IsForeignStatic(sg)
                && !sg.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(sg))
                yield return sg;
            if (spr.Property.SetMethod is { } ss && _emitter.IsForeignStatic(ss)
                && !ss.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(ss))
                yield return ss;
        }
    }

    IEnumerable<IMethodSymbol> EnumerateBaseInstanceReach(IOperation op)
    {
        if (op is IInvocationOperation inv && _emitter.IsBaseInstanceMethod(inv.TargetMethod)
            && !(inv.TargetMethod.IsGenericMethod
                 && inv.TargetMethod.TypeArguments.Any(ta => ta is ITypeParameterSymbol)))
            yield return inv.TargetMethod;
        if (op is IPropertyReferenceOperation pr
            && pr.Instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax })
        {
            if (pr.Property.GetMethod is { } g && _emitter.IsBaseInstanceMethod(g)) yield return g;
            if (pr.Property.SetMethod is { } s && _emitter.IsBaseInstanceMethod(s)) yield return s;
        }
        if (op is IMethodReferenceOperation mref
            && mref.Instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax }
            && _emitter.IsBaseInstanceMethod(mref.Method))
            yield return mref.Method;
    }

    static IEnumerable<IMethodSymbol> EnumerateUsingDispose(IOperation op)
    {
        var resources = op is IUsingOperation uo ? uo.Resources
            : op is IUsingDeclarationOperation ud ? ud.DeclarationGroup
            : null;
        if (resources is IVariableDeclarationGroupOperation g)
            foreach (var decl in g.Declarations)
                foreach (var d in decl.Declarators)
                    if (d.Symbol.Type is INamedTypeSymbol dnt && EmitPolicy.FindStructDisposeMethod(dnt) is { } dispose)
                        yield return dispose;
    }
}
