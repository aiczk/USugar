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
        // EscapeTarget (C3): the per-op delegate-creation classification — every function whose bridge
        // address this op can mint into a bundle (§4.1). Three mappings: the method group's static
        // OriginalDefinition; the [X1] leaf-override definition whose body the planner's bridge actually
        // runs (LeafMethodRefTarget, delegated like the other emitter deps); and a lambda's own symbol.
        // Yielded UNGATED — the internal-method membership set exists only after the recursion worklist
        // dries, so the filter stays consumer-side, like the reach roles' registration-gate bucketing.
        if (op is IDelegateCreationOperation dc)
        {
            if (dc.Target is IMethodReferenceOperation mr && mr.Method != null)
            {
                yield return new ResolvedTarget(mr.Method.OriginalDefinition, TargetRole.EscapeTarget);
                if (_emitter.LeafMethodRefTarget(mr) is { } leafT)
                    yield return new ResolvedTarget(leafT, TargetRole.EscapeTarget);
            }
            else if (dc.Target is IAnonymousFunctionOperation af && af.Symbol != null)
                yield return new ResolvedTarget(af.Symbol, TargetRole.EscapeTarget);
        }
        foreach (var t in ResolveReachEdges(op))
            yield return t;
    }

    /// <summary>The reach-role targets only (no CallEdge). The reach worklist runs in Phase-1, BEFORE the
    /// VirtualDispatch INSTANCE is seeded (Emit seeds it after the compile-plan build); the reach roles use
    /// only STATIC VirtualDispatch helpers (IsDispatchSite / SlotIntroducer / MostDerivedImpl), so this stays
    /// callable at reach time, whereas the CallEdge arm's EnumerateInternalCallTargets touches the instance.</summary>
    public IEnumerable<ResolvedTarget> ResolveReachEdges(IOperation op)
        => ReachEdges(op, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));

    /// <summary>CA rewrite (M5 prerequisite): the concrete user classes `op` instantiates at runtime — a
    /// direct `new C()` plus any class minted transitively inside C's field initializers (which live off the
    /// walked body tree). The `minted` dedup bounds cyclic field-init mints. This is the instantiation-set
    /// the worklist unions into ReachableBodies.MintedClasses (the typeobj registry seed); the ctor/virtual/
    /// base bodies host their own nested mints, which the worklist discovers when it walks them as reach
    /// targets. Mirrors CollectClassMintReach's `result.MintedClasses.Add(ct)` provenance at def granularity.</summary>
    public IEnumerable<INamedTypeSymbol> ResolveMintedTypes(IOperation op)
        => MintedTypes(op, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));

    IEnumerable<INamedTypeSymbol> MintedTypes(IOperation op, HashSet<INamedTypeSymbol> seen)
    {
        if (op is IObjectCreationOperation oc && oc.Type is INamedTypeSymbol ct
            && EmitPolicy.IsUserClassType(ct) && seen.Add(ct))
        {
            yield return ct;
            foreach (var initOp in _emitter.EnumerateClassFieldInitOps(ct))
                foreach (var m in MintedTypesWalk(initOp, seen))
                    yield return m;
        }
    }

    IEnumerable<INamedTypeSymbol> MintedTypesWalk(IOperation op, HashSet<INamedTypeSymbol> seen)
    {
        foreach (var m in MintedTypes(op, seen)) yield return m;
        foreach (var child in op.ChildOps())
            foreach (var m in MintedTypesWalk(child, seen)) yield return m;
    }

    /// <summary>CA rewrite (M5a SS2A): the DROPPED generic / open-container foreign statics — the `SuppDef`
    /// leg of CollectForeignStaticCallsInOperation (the complement of <see cref="EnumerateForeignStaticReach"/>).
    /// These stay registration-free, but their bodies host closures and are recursion nodes, so the worklist
    /// walks them in a supplementary fixpoint into ReachableBodies.GenericForeignStaticBodies. Yields
    /// OriginalDefinitions (the supplementary sets are def-keyed).</summary>
    public IEnumerable<IMethodSymbol> ResolveForeignStaticSuppDefs(IOperation op)
    {
        if (op is IInvocationOperation inv && _emitter.IsForeignStatic(inv.TargetMethod))
        {
            var original = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
            if (original.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original.OriginalDefinition;
        }
        if (op is IMethodReferenceOperation mref && _emitter.IsForeignStatic(mref.Method))
        {
            var original = mref.Method.ReducedFrom ?? mref.Method;
            if (original.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original.OriginalDefinition;
        }
        if (op is IPropertyReferenceOperation spr && spr.Property.IsStatic && UasmEmitter.IsComputedProperty(spr.Property))
        {
            if (spr.Property.GetMethod is { } sg && _emitter.IsForeignStatic(sg)
                && (sg.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(sg)))
                yield return sg.OriginalDefinition;
            if (spr.Property.SetMethod is { } ss && _emitter.IsForeignStatic(ss)
                && (ss.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(ss)))
                yield return ss.OriginalDefinition;
        }
    }

    /// <summary>C3: the VARIANT escape facet — (target, declared sig-S) pairs for a method-group
    /// delegate-creation site whose target's own sig differs from the delegate type's Invoke sig
    /// (sig-S = the sig adapter's protocol sig, Stage 1.75 §2.2). A Sig payload has no place on
    /// ResolvedTarget, so this is a dedicated per-op entry point (precedent:
    /// <see cref="ResolveForeignStaticSuppDefs"/>). Ungated like the EscapeTarget arm — the
    /// internal-method membership filter stays consumer-side. Applies the SAME [X1] leaf-override
    /// mapping as the EscapeTarget arm (the former deliberate omission was CLOSED at C3 stage 2,
    /// 2026-07-16): a variant binding of a this-receiver virtual method escapes the LEAF definition
    /// under the adapter's sig-S too — the base def is typically not even a graph node, so keying
    /// sig-S off it alone left a delegate-dispatch cycle through the override's body without its
    /// synthetic edge/reenterSig and under-spilled its frames (the [Z1] family; VM-proven 205 vs
    /// CLR 715). The frozen legacy oracle RETAINS the omission — the census shape
    /// facet_variant_leaf_override_reentry asserts the exact production-minus-legacy delta. The
    /// lambda arm stays omitted (a lambda's sig is inferred from the delegate type, so it can never
    /// be variant).</summary>
    public IEnumerable<(IMethodSymbol Method, string Sig)> ResolveVariantEscapeSigs(IOperation op)
    {
        if (op is IDelegateCreationOperation { Target: IMethodReferenceOperation { Method: { } } mrOp } variantDc
            && variantDc.Type is INamedTypeSymbol vDlgType && vDlgType.DelegateInvokeMethod is { } vInvoke)
        {
            var t = mrOp.Method.OriginalDefinition;
            var sigS = DelegateAbi.BuildSigPart(vInvoke);
            if (sigS != DelegateAbi.BuildSigPart(t))
                yield return (t, sigS);
            if (_emitter.LeafMethodRefTarget(mrOp) is { } leafT && sigS != DelegateAbi.BuildSigPart(leafT))
                yield return (leafT, sigS);
        }
    }

    /// <summary>CA rewrite (M5a): open-constructed generic base-instance targets — the `_openGenericBaseDefs`
    /// leg of CollectBaseInstanceCallsInOperation (an open generic base call, or a generic base method group
    /// through `this`). Registration-free but a MAIN-fixpoint recursion/reach root. Yields OriginalDefinitions.</summary>
    public IEnumerable<IMethodSymbol> ResolveOpenBaseGenericDefs(IOperation op)
    {
        if (op is IInvocationOperation inv && _emitter.IsBaseInstanceMethod(inv.TargetMethod)
            && inv.TargetMethod.IsGenericMethod
            && inv.TargetMethod.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
            yield return inv.TargetMethod.OriginalDefinition;
        if (op is IMethodReferenceOperation gmref && gmref.Method.IsGenericMethod
            && _emitter.IsBaseInstanceMethod(gmref.Method)
            && (gmref.Instance == null
                || gmref.Instance is IInstanceReferenceOperation { Syntax: not BaseExpressionSyntax }))
            yield return gmref.Method.OriginalDefinition;
    }

    // The reach-role targets of a SINGLE op — no CallEdge, no child recursion, EXCEPT the mint arm, which
    // reaches C's field-init / base-ctor / virtual-impl bodies that live off the walked op tree (so the
    // worklist never sees them) and must therefore be discovered here.
    //   Facet coverage: EscapeTarget lives in ResolveEdges (C3), not here — escape is a recursion-phase
    //   facet, not a reach target; open/generic foreign-static + open base-generic drops feed capture
    //   roots (a supplementary facet) and are not reach targets here.
    IEnumerable<ResolvedTarget> ReachEdges(IOperation op, HashSet<INamedTypeSymbol> minted)
    {
        // Reach targets carry the classifier's CONSTRUCTED symbol (per-spec) — NOT its OriginalDefinition:
        // the struct-member registration gate (IsCollectibleStructMember) and per-spec registration need the
        // constructed identity (Box<int>.Get, not the open Box<T>.Get). Def-keyed consumers (recursion/reach
        // roots, BodyByDef) project to OriginalDefinition themselves. CallEdge above is already def-keyed by
        // its classifier (the recursion graph is def-keyed).
        //
        // ReachStructMember: the per-op user-struct/class member enumerator (ctor / instance method /
        // computed property / subpattern / operator / conversion) plus the implicit using-Dispose.
        foreach (var m in UasmEmitter.EnumerateStructMemberRefs(op))
            yield return new ResolvedTarget(m, TargetRole.ReachStructMember);
        foreach (var m in EnumerateUsingDispose(op))
            yield return new ResolvedTarget(m, TargetRole.ReachStructMember);

        // ReachForeignStatic: a closed, non-generic foreign static reached by call / method-group / static
        // computed-property.
        foreach (var m in EnumerateForeignStaticReach(op))
            yield return new ResolvedTarget(m, TargetRole.ReachForeignStatic);

        // ReachBaseInstance: a base instance method/accessor copy reached through `base.` or an inherited call.
        foreach (var m in EnumerateBaseInstanceReach(op))
            yield return new ResolvedTarget(m, TargetRole.ReachBaseInstance);

        // Instantiation reach: minting a user class runs its field initializers, its explicit ctor, its
        // implicit base-ctor chain, and its virtual-slot impls — bodies that live in the class declaration,
        // not the walked op tree. Faithful port of CollectClassMintReach's direct seeding, deduped by
        // `minted` (bounds transitive nested mints inside field initializers).
        if (op is IObjectCreationOperation oc && oc.Type is INamedTypeSymbol ct
            && EmitPolicy.IsUserClassType(ct) && minted.Add(ct))
        {
            // C's own explicit ctor (incl. the parameterless one EnumerateStructMemberRefs skips at
            // Arguments.Length==0) — a reach root whose body is emitted at mint.
            if (oc.Constructor is { IsImplicitlyDeclared: false } ownCtor)
                yield return new ResolvedTarget(ownCtor, TargetRole.ReachStructMember);
            // field-init member refs (off-body — transitive through nested mints, deduped by `minted`).
            foreach (var initOp in _emitter.EnumerateClassFieldInitOps(ct))
                foreach (var t in ReachWalk(initOp, minted))
                    yield return t;
            // base explicit parameterless ctors called by the implicit ctor chain.
            for (var bt = ct.BaseType; bt is INamedTypeSymbol && EmitPolicy.IsUserClassType(bt); bt = bt.BaseType)
            {
                var baseCtor = bt.InstanceConstructors.FirstOrDefault(
                    c => c.Parameters.Length == 0 && !c.IsImplicitlyDeclared);
                if (baseCtor != null)
                    yield return new ResolvedTarget(baseCtor, TargetRole.ReachStructMember);
            }
            // virtual-slot most-derived impls reachable only through the inline typeobj dispatch chain
            // (seed by slot, most-derived first, to avoid phantom-minting shadowed base methods).
            var seededSlots = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            for (var vt = (ITypeSymbol)ct; vt is INamedTypeSymbol vtn && EmitPolicy.IsUserClassType(vtn); vt = vtn.BaseType)
                foreach (var vm in vt.GetMembers().OfType<IMethodSymbol>())
                    if ((vm.IsVirtual || vm.IsOverride || vm.IsAbstract) && vm.MethodKind == MethodKind.Ordinary
                        && seededSlots.Add(VirtualDispatch.SlotIntroducer(vm))
                        && VirtualDispatch.MostDerivedImpl(ct, VirtualDispatch.SlotIntroducer(vm)) is { } impl)
                        yield return new ResolvedTarget(impl, TargetRole.ReachStructMember);
        }
    }

    // Transitively yield the reach edges over an off-body op tree (a field initializer): a field-init call
    // is a reach, not a synthetic caller edge, so CallEdge is excluded (mirrors CollectClassMintReach's
    // Walk routing field inits through the reach collectors only).
    IEnumerable<ResolvedTarget> ReachWalk(IOperation op, HashSet<INamedTypeSymbol> minted)
    {
        foreach (var t in ReachEdges(op, minted)) yield return t;
        foreach (var child in op.ChildOps())
            foreach (var t in ReachWalk(child, minted)) yield return t;
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
