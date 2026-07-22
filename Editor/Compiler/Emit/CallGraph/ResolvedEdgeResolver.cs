using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>CA call-graph rewrite: the single per-op resolver behind the reach worklist
/// (ResolverDrivenReach) and the recursion walk (RecursionNodeWalk), yielding every resolved
/// (method, role) target the op reaches at runtime. Consumers filter by role.
///
/// C4 (M5d) completed the M0 plan: the CallEdge classifier core (EnumerateInternalCallTargets + the
/// leaf/cross helpers) and EnumerateStructMemberRefs now LIVE here — the emitter keeps no copy of the
/// classification logic and delegates its per-site consumers (HasNonTailCallTo's matchers, TailSpared
/// marking, precise-dispatch provenance) back to this class. Remaining
/// emitter reads are context predicates/state only (the static IsForeignStatic / IsBaseInstanceMethod
/// keyed by _emitter.ClassSymbol, EnumerateClassFieldInitOps, the seeded VirtualDispatch+ClassTypes).
/// Built per-class — thread-safe (no shared mutable state beyond the emitter it reads).</summary>
public sealed class ResolvedEdgeResolver
{
    readonly UasmEmitter _emitter;

    public ResolvedEdgeResolver(UasmEmitter emitter) { _emitter = emitter; }

    internal IEnumerable<INamedTypeSymbol> EnumeratePortableClassTypes()
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var behaviourTypes = new List<INamedTypeSymbol>();
        var candidates = new List<INamedTypeSymbol>();
        foreach (var tree in _emitter.Compilation.SyntaxTrees)
        {
            var model = _emitter.Compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type) continue;
                if (ExternResolver.IsUdonSharpBehaviour(type)) behaviourTypes.Add(type);
                if (!type.IsAbstract && !ClassTypeObjectContext.ContainsTypeParameter(type)
                    && TypeClassifier.IsUserClass(type))
                    candidates.Add(type);
            }
        }

        var roots = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var interfaceRoots = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var surface in behaviourTypes.SelectMany(PortableSurfaceTypes).OfType<INamedTypeSymbol>())
        {
            if (ClassTypeObjectContext.ContainsTypeParameter(surface)) continue;
            if (TypeClassifier.IsUserClass(surface)) roots.Add(surface);
            else if (surface.TypeKind == TypeKind.Interface
                     && _emitter.Planner.InterfaceIsLocalUserClassOnly(surface))
                interfaceRoots.Add(surface);
        }
        foreach (var root in roots)
            if (!root.IsAbstract && seen.Add(root)) yield return root;
        foreach (var candidate in candidates)
            if ((roots.Any(root => VirtualDispatch.IsAssignable(candidate, root))
                 || interfaceRoots.Any(iface => candidate.AllInterfaces.Any(i =>
                     SymbolEqualityComparer.Default.Equals(i, iface))))
                && seen.Add(candidate))
                yield return candidate;

    }

    static IEnumerable<ITypeSymbol> PortableSurfaceTypes(INamedTypeSymbol behaviour)
    {
        foreach (var field in behaviour.GetMembers().OfType<IFieldSymbol>())
            if (!field.IsStatic && (field.DeclaredAccessibility == Accessibility.Public
                || field.GetAttributes().Any(a => a.AttributeClass?.Name is
                    "SerializeField" or "SerializeFieldAttribute")))
                yield return field.Type;
        foreach (var property in behaviour.GetMembers().OfType<IPropertySymbol>())
            if (!property.IsStatic && property.DeclaredAccessibility == Accessibility.Public)
                yield return property.Type;
        foreach (var method in behaviour.GetMembers().OfType<IMethodSymbol>())
            if (!method.IsStatic && method.DeclaredAccessibility == Accessibility.Public)
            {
                if (!method.ReturnsVoid) yield return method.ReturnType;
                foreach (var parameter in method.Parameters) yield return parameter.Type;
            }
    }

    internal IEnumerable<IMethodSymbol> ResolvePortableDispatchMethods(
        IOperation operation, IReadOnlyCollection<INamedTypeSymbol> portableClasses)
    {
        IMethodSymbol target = null;
        IOperation instance = null;
        INamedTypeSymbol receiverType = null;
        switch (operation)
        {
            case IInvocationOperation invocation:
                target = invocation.TargetMethod;
                instance = invocation.Instance;
                receiverType = instance?.Type as INamedTypeSymbol;
                break;
            case IPropertyReferenceOperation property:
                target = VirtualDispatch.FindAccessor(property.Property, getter: true)
                         ?? VirtualDispatch.FindAccessor(property.Property, getter: false);
                instance = property.Instance;
                receiverType = instance?.Type as INamedTypeSymbol;
                break;
        }
        if (target == null || receiverType == null) yield break;

        if (receiverType.TypeKind == TypeKind.Interface)
        {
            foreach (var concrete in portableClasses)
            {
                if (!concrete.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, receiverType)))
                    continue;
                if (concrete.FindImplementationForInterfaceMember(target) is IMethodSymbol impl && !impl.IsAbstract
                    && !(impl.AssociatedSymbol is IPropertySymbol autoProperty
                         && !UasmEmitter.IsComputedProperty(autoProperty)))
                    yield return impl;
            }
            yield break;
        }
        if (!VirtualDispatch.IsDispatchSite(target, instance, receiverType)) yield break;
        var slot = VirtualDispatch.SlotIntroducer(target);
        foreach (var concrete in portableClasses)
            if (VirtualDispatch.IsAssignable(concrete, receiverType)
                && VirtualDispatch.MostDerivedImpl(concrete, slot) is { } impl)
            {
                if (impl.AssociatedSymbol is IPropertySymbol autoProperty
                    && !UasmEmitter.IsComputedProperty(autoProperty))
                    continue;
                yield return impl;
            }
    }

    public IEnumerable<ResolvedTarget> ResolveEdges(IOperation op)
    {
        // CallEdge: the per-op internal-call classifier (invocation / ctor / property accessor / operator),
        // including the v2b-2 virtual override set — a synthetic caller edge.
        foreach (var m in EnumerateInternalCallTargets(op))
            yield return new ResolvedTarget(m.OriginalDefinition, TargetRole.CallEdge);
        // EscapeTarget (C3): the per-op delegate-creation classification — every function whose bridge
        // address this op can mint into a bundle (§4.1). Three mappings: the method group's static
        // OriginalDefinition; the [X1] leaf-override definition whose body the planner's bridge actually
        // runs (LeafMethodRefTarget); and a lambda's own symbol.
        // Yielded UNGATED — the internal-method membership set exists only after the recursion worklist
        // dries, so the filter stays consumer-side, like the reach roles' registration-gate bucketing.
        if (op is IDelegateCreationOperation dc)
        {
            if (dc.Target is IMethodReferenceOperation mr && mr.Method != null)
            {
                yield return new ResolvedTarget(mr.Method.OriginalDefinition, TargetRole.EscapeTarget);
                if (mr.Method.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } escapeIface
                    && _emitter.Planner.InterfaceIsLocalUserClassOnly(escapeIface))
                    foreach (var vt in _emitter.VirtualDispatchInstance.ResolveInterfaceTargets(escapeIface, mr.Method))
                        yield return new ResolvedTarget(vt.Impl.OriginalDefinition, TargetRole.EscapeTarget);
                if (LeafMethodRefTarget(mr) is { } leafT)
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
            && TypeClassifier.IsUserClass(ct) && seen.Add(ct))
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
        if (op is IInvocationOperation inv && UasmEmitter.IsForeignStatic(inv.TargetMethod, _emitter.ClassSymbol))
        {
            var original = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
            if (original.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original.OriginalDefinition;
        }
        if (op is IMethodReferenceOperation mref && UasmEmitter.IsForeignStatic(mref.Method, _emitter.ClassSymbol))
        {
            var original = mref.Method.ReducedFrom ?? mref.Method;
            if (original.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original.OriginalDefinition;
        }
        if (op is IPropertyReferenceOperation spr && spr.Property.IsStatic && UasmEmitter.IsComputedProperty(spr.Property))
        {
            if (spr.Property.GetMethod is { } sg && UasmEmitter.IsForeignStatic(sg, _emitter.ClassSymbol)
                && (sg.IsGenericMethod || !UasmEmitter.IsClosedForeignStaticTarget(sg)))
                yield return sg.OriginalDefinition;
            if (spr.Property.SetMethod is { } ss && UasmEmitter.IsForeignStatic(ss, _emitter.ClassSymbol)
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
            if (LeafMethodRefTarget(mrOp) is { } leafT && sigS != DelegateAbi.BuildSigPart(leafT))
                yield return (leafT, sigS);
        }
    }

    /// <summary>CA rewrite (M5a): open-constructed generic base-instance targets — the `_openGenericBaseDefs`
    /// leg of CollectBaseInstanceCallsInOperation (an open generic base call, or a generic base method group
    /// through `this`). Registration-free but a MAIN-fixpoint recursion/reach root. Yields OriginalDefinitions.</summary>
    public IEnumerable<IMethodSymbol> ResolveOpenBaseGenericDefs(IOperation op)
    {
        if (op is IInvocationOperation inv && UasmEmitter.IsBaseInstanceMethod(inv.TargetMethod, _emitter.ClassSymbol)
            && inv.TargetMethod.IsGenericMethod
            && inv.TargetMethod.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
            yield return inv.TargetMethod.OriginalDefinition;
        if (op is IMethodReferenceOperation gmref && gmref.Method.IsGenericMethod
            && UasmEmitter.IsBaseInstanceMethod(gmref.Method, _emitter.ClassSymbol)
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
        foreach (var m in EnumerateStructMemberRefs(op))
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
            && TypeClassifier.IsUserClass(ct) && minted.Add(ct))
        {
            // C's own explicit ctor (incl. the parameterless one EnumerateStructMemberRefs skips at
            // Arguments.Length==0) — a reach root whose body is emitted at mint.
            if (oc.Constructor is { IsImplicitlyDeclared: false } ownCtor)
                yield return new ResolvedTarget(ownCtor, TargetRole.ReachStructMember);
            // field-init member refs (off-body — transitive through nested mints, deduped by `minted`).
            foreach (var initOp in _emitter.EnumerateClassFieldInitOps(ct))
                foreach (var t in ReachWalk(initOp, minted))
                    yield return t;
            foreach (var iface in ct.AllInterfaces)
            {
                var ifaceLayout = _emitter.Planner.GetLayout(iface);
                foreach (var ifaceMethod in ifaceLayout.Methods.Keys)
                    if (ct.FindImplementationForInterfaceMember(ifaceMethod) is IMethodSymbol impl
                        && impl.ContainingType?.TypeKind != TypeKind.Interface)
                    {
                        if (impl.AssociatedSymbol is IPropertySymbol autoProperty
                            && !UasmEmitter.IsComputedProperty(autoProperty))
                            continue;
                        yield return new ResolvedTarget(impl, TargetRole.ReachStructMember);
                    }
            }
            // base explicit parameterless ctors called by the implicit ctor chain.
            for (var bt = ct.BaseType; bt is INamedTypeSymbol && TypeClassifier.IsUserClass(bt); bt = bt.BaseType)
            {
                var baseCtor = bt.InstanceConstructors.FirstOrDefault(
                    c => c.Parameters.Length == 0 && !c.IsImplicitlyDeclared);
                if (baseCtor != null)
                    yield return new ResolvedTarget(baseCtor, TargetRole.ReachStructMember);
            }
            // virtual-slot most-derived impls reachable only through the inline typeobj dispatch chain
            // (seed by slot, most-derived first, to avoid phantom-minting shadowed base methods).
            // CW1 lift: accessor slots seed too — but only COMPUTED accessor impls (an AUTO accessor
            // impl has no body; its dispatch arm is a layout-slot access, nothing to register).
            var seededSlots = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            for (var vt = (ITypeSymbol)ct; vt is INamedTypeSymbol vtn && TypeClassifier.IsUserClass(vtn); vt = vtn.BaseType)
                foreach (var vm in vt.GetMembers().OfType<IMethodSymbol>())
                    if ((vm.IsVirtual || vm.IsOverride || vm.IsAbstract)
                        && vm.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet
                        && seededSlots.Add(VirtualDispatch.SlotIntroducer(vm))
                        && VirtualDispatch.MostDerivedImpl(ct, VirtualDispatch.SlotIntroducer(vm)) is { } impl
                        && (impl.MethodKind == MethodKind.Ordinary
                            || impl.AssociatedSymbol is IPropertySymbol implProp && UasmEmitter.IsComputedProperty(implProp)))
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
        if (op is IInvocationOperation inv && UasmEmitter.IsForeignStatic(inv.TargetMethod, _emitter.ClassSymbol))
        {
            var original = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
            if (!original.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original;
        }
        if (op is IMethodReferenceOperation mref && UasmEmitter.IsForeignStatic(mref.Method, _emitter.ClassSymbol))
        {
            var original = mref.Method.ReducedFrom ?? mref.Method;
            if (!original.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(original))
                yield return original;
        }
        if (op is IPropertyReferenceOperation spr && spr.Property.IsStatic && UasmEmitter.IsComputedProperty(spr.Property))
        {
            if (spr.Property.GetMethod is { } sg && UasmEmitter.IsForeignStatic(sg, _emitter.ClassSymbol)
                && !sg.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(sg))
                yield return sg;
            if (spr.Property.SetMethod is { } ss && UasmEmitter.IsForeignStatic(ss, _emitter.ClassSymbol)
                && !ss.IsGenericMethod && UasmEmitter.IsClosedForeignStaticTarget(ss))
                yield return ss;
        }
    }

    IEnumerable<IMethodSymbol> EnumerateBaseInstanceReach(IOperation op)
    {
        if (op is IInvocationOperation inv && UasmEmitter.IsBaseInstanceMethod(inv.TargetMethod, _emitter.ClassSymbol)
            && !(inv.TargetMethod.IsGenericMethod
                 && inv.TargetMethod.TypeArguments.Any(ta => ta is ITypeParameterSymbol)))
            yield return inv.TargetMethod;
        if (op is IPropertyReferenceOperation pr
            && pr.Instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax })
        {
            if (pr.Property.GetMethod is { } g && UasmEmitter.IsBaseInstanceMethod(g, _emitter.ClassSymbol)) yield return g;
            if (pr.Property.SetMethod is { } s && UasmEmitter.IsBaseInstanceMethod(s, _emitter.ClassSymbol)) yield return s;
        }
        if (op is IMethodReferenceOperation mref
            && mref.Instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax }
            && UasmEmitter.IsBaseInstanceMethod(mref.Method, _emitter.ClassSymbol))
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

    // ── C4 (M5d): the CallEdge classifier core, relocated verbatim from UasmEmitter (pure move — the
    // emitter keeps no copy). PHASE CONSTRAINT (the documented split, see ResolveReachEdges): these arms
    // consume the SEEDED VirtualDispatch instance + ClassTypes census (the v2b-2 / CW5 virtual arms),
    // which exist only after Emit's compile-plan seed — the reach roles above stay callable pre-seed,
    // and a pre-seed CallEdge invocation fails loud at the classifier entry instead of silently yielding
    // no virtual/cross targets (pre-seed the census is empty and the dispatch instance null). ──

    /// <summary>[V1 unification] The per-NODE call-target classifier shared by the recursion-graph edge
    /// walk (RecursionNodeWalk, via <see cref="ResolveEdges"/>'s CallEdge arm) and the per-site non-tail classifier
    /// (<see cref="IsInternalCallTo"/>). Yields every method (OriginalDefinition, or the emission-faithful
    /// leaf/cross target) that a SINGLE operation node can dispatch to: an invocation's static /
    /// this-virtual-leaf-override / variable-or-interface-cross targets; a ctor; and a property or indexer
    /// reference's this / leaf-override / variable-or-interface-cross / user-struct accessor pairs (both
    /// get and set, conservatively — a write-position reference yielding the getter only over-spills,
    /// §8-3, never corrupts). Each arm was VM-proven necessary (wave-9 r2/r3 [W1..W3], wave-12 r2 [V1],
    /// wave-14 r4): recursion threaded through leaf-override / variable-receiver / fresh-struct-instance
    /// accessors was invisible to the SCC analysis and the accessor frame never spilled (e.g. 5 vs CLR 11,
    /// 305 vs 605, computed-property factorial 1 vs 120). Extracting the two formerly hand-mirrored switches
    /// into ONE enumerator removes the drift that caused those wave-14 r4 miscompiles — the arms can no
    /// longer fall out of lockstep. Includes the user-defined OPERATOR edge (binary / unary /
    /// compound-assignment / increment-decrement forms all carry an OperatorMethod) — B49: it formerly
    /// lived only in the recursion-graph edge walk, so IsInternalCallTo could not see it and a recursive
    /// struct operator was never frame-spilled (VM-proven ref=15/usugar=0); routing it through here makes
    /// both consumers agree and fixes the spill.</summary>
    internal IEnumerable<IMethodSymbol> EnumerateInternalCallTargets(IOperation op)
    {
        if (_emitter.VirtualDispatchInstance == null)
            throw new System.InvalidOperationException(
                "CallEdge classification invoked pre-seed: the v2b-2/CW5 virtual arms need the seeded "
                + "VirtualDispatch/ClassTypes context (Emit's compile-plan seed). Reach-phase callers "
                + "must use ResolveReachEdges.");
        switch (op)
        {
            case IInvocationOperation inv:
                yield return inv.TargetMethod.OriginalDefinition;
                if (LeafCallTarget(inv) is { } leafT) yield return leafT;
                if (IsCrossDispatchReceiver(inv.Instance, inv.TargetMethod)
                    && CrossDispatchLocalTarget(inv.TargetMethod) is { } crossT)
                    yield return crossT;
                // CA-v2b-2: a polymorphic call dispatches to EVERY override in its closed-world set. Yield
                // each so the recursion-graph edge walk AND the per-site non-tail spill classifier (both read
                // this one enumerator) see a recursive override — including a `this.M()` that re-enters
                // through a spawned object's ctor (Rb..ctor -> Rd.Make -> new Rd -> Rb..ctor). The dispatch-
                // site predicate is SHARED with the emission branch (VirtualDispatch.IsDispatchSite) so the
                // two cannot drift; the receiver's declared type is used here (Phase-1, no monomorphization
                // map). Over-yield only ever over-spills (sound).
                //
                // CW5: this def-keyed walk has no type-param map, so a receiver whose DECLARED type still
                // carries a type parameter (a `T n` constrained to a user class, or an open `Box<T>` this)
                // cannot be resolved the way emission resolves it (ResolveType) — yet emission DOES lower
                // the site to a typeobj chain. Pattern-matching INamedTypeSymbol alone yielded ZERO edges
                // there, hiding the cycle from the SCC analysis and under-spilling polymorphic recursion
                // (VM-proven 37 vs CLR 67: the frame clobbered on re-entry). Over-approximate instead:
                // yield the slot's most-derived impl for EVERY minted class (same base exclusion as
                // IsDispatchSite; over-yield only ever over-spills).
                if (ClassTypeObjectContext.ContainsTypeParameter(inv.Instance?.Type))
                {
                    if (VirtualDispatch.IsVirtualCall(inv.TargetMethod)
                        && !(inv.Instance is IInstanceReferenceOperation gir
                             && gir.Syntax is BaseExpressionSyntax))
                    {
                        var slotDef = VirtualDispatch.SlotIntroducer(inv.TargetMethod);
                        foreach (var concrete in _emitter.ClassTypes.MintedClasses)
                            if (VirtualDispatch.MostDerivedImpl(concrete, slotDef) is { } genImpl)
                                yield return genImpl.OriginalDefinition;
                    }
                }
                else if (inv.Instance?.Type is INamedTypeSymbol vrecv
                    && VirtualDispatch.IsDispatchSite(inv.TargetMethod, inv.Instance, vrecv))
                    foreach (var vt in _emitter.VirtualDispatchInstance.ResolveTargets(vrecv, inv.TargetMethod))
                        yield return vt.Impl.OriginalDefinition;
                if (inv.TargetMethod.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface
                    && _emitter.Planner.InterfaceIsLocalUserClassOnly(iface))
                    foreach (var vt in _emitter.VirtualDispatchInstance.ResolveInterfaceTargets(iface, inv.TargetMethod))
                        yield return vt.Impl.OriginalDefinition;
                break;
            case IObjectCreationOperation { Constructor: { } ctor }:
                yield return ctor.OriginalDefinition;
                break;
            case ITypeParameterObjectCreationOperation:
                // The operation tree retains `new T()` while emission is per closed specialization.
                // The typeobj census is already seeded here; conservatively connect the site to every
                // minted parameterless user-class ctor. Over-yield may spill more, under-yield is wrong.
                foreach (var concrete in _emitter.ClassTypes.MintedClasses)
                {
                    var ctor = concrete.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
                    if (ctor != null) yield return ctor.OriginalDefinition;
                }
                break;
            // M4b: an interpolation hole holding a v1-class value dispatches the object.ToString slot
            // (EmitClassToStringDispatch) — yield every override impl the chain can direct-call so a
            // cycle closed through stringification is visible to the SCC/spill analysis (the dispatch
            // fan-out mirrors emission's ResolveTargets; type-name arms call nothing and yield nothing).
            case IInterpolationOperation interpPart:
                foreach (var impl in ClassToStringImplDefs(interpPart.Expression))
                    yield return impl;
                break;
            // M4b: a string-concat operand holding a v1-class value dispatches the same slot — the
            // emission side unwraps the boxing conversion Concat(object,object) wraps operands in,
            // so the enumerator unwraps identically (the two must not drift).
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } concat
                when concat.Type?.SpecialType == SpecialType.System_String:
                foreach (var impl in ClassToStringImplDefs(UnwrapConversionOps(concat.LeftOperand)))
                    yield return impl;
                foreach (var impl in ClassToStringImplDefs(UnwrapConversionOps(concat.RightOperand)))
                    yield return impl;
                break;
            case IPropertyReferenceOperation pr:
                // this-receiver accessor call (both accessors + the emission-faithful leaf override).
                if (pr.Instance is IInstanceReferenceOperation)
                {
                    if (pr.Property.GetMethod is { } pg) yield return pg.OriginalDefinition;
                    if (pr.Property.SetMethod is { } ps) yield return ps.OriginalDefinition;
                    if (LeafPropertyTarget(pr) is { } lp)
                    {
                        if (lp.GetMethod is { } lg) yield return lg.OriginalDefinition;
                        if (lp.SetMethod is { } ls) yield return ls.OriginalDefinition;
                    }
                }
                // variable-receiver / interface-typed accessor dispatch that can land back on this program.
                if (IsCrossDispatchReceiver(pr.Instance, pr.Property))
                {
                    if (CrossDispatchLocalTarget(pr.Property.GetMethod) is { } cg) yield return cg;
                    if (CrossDispatchLocalTarget(pr.Property.SetMethod) is { } cs) yield return cs;
                }
                // computed property / indexer on a USER-STRUCT receiver — `this` OR a fresh struct
                // instance (structs compile into this program's accessor functions).
                if (pr.Property is { IsStatic: false } sprop
                    && sprop.ContainingType is INamedTypeSymbol sprct && TypeClassifier.IsObjectArrayEmulated(sprct))
                {
                    if (sprop.GetMethod is { } sg) yield return sg.OriginalDefinition;
                    if (sprop.SetMethod is { } ss) yield return ss.OriginalDefinition;
                }
                // CW1 lift: accessor dispatch fan-out — the accessor twin of the invocation arm's
                // v2b-2/CW5 arms. Both accessors yield conservatively (a reference alone doesn't
                // reveal read-vs-write context; over-yield only ever over-spills, §8-3).
                foreach (var impl in AccessorDispatchImplDefs(pr, VirtualDispatch.FindAccessor(pr.Property, getter: true)))
                    yield return impl;
                foreach (var impl in AccessorDispatchImplDefs(pr, VirtualDispatch.FindAccessor(pr.Property, getter: false)))
                    yield return impl;
                if (pr.Property.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } propIface
                    && _emitter.Planner.InterfaceIsLocalUserClassOnly(propIface))
                {
                    if (pr.Property.GetMethod is { } ig)
                        foreach (var vt in _emitter.VirtualDispatchInstance.ResolveInterfaceTargets(propIface, ig))
                            yield return vt.Impl.OriginalDefinition;
                    if (pr.Property.SetMethod is { } ise)
                        foreach (var vt in _emitter.VirtualDispatchInstance.ResolveInterfaceTargets(propIface, ise))
                            yield return vt.Impl.OriginalDefinition;
                }
                break;
            case IEventAssignmentOperation eventAssignment
                when eventAssignment.EventReference is IEventReferenceOperation eventReference:
            {
                var accessor = eventAssignment.Adds
                    ? eventReference.Event.AddMethod
                    : eventReference.Event.RemoveMethod;
                if (accessor?.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } eventIface
                    && _emitter.Planner.InterfaceIsLocalUserClassOnly(eventIface))
                {
                    foreach (var vt in _emitter.VirtualDispatchInstance.ResolveInterfaceTargets(eventIface, accessor))
                        yield return vt.Impl.OriginalDefinition;
                }
                else if (accessor != null && !accessor.IsImplicitlyDeclared)
                    yield return accessor.OriginalDefinition;
                break;
            }
            case IDeconstructionAssignmentOperation deconstruction
                when ResolveUserDeconstruct(deconstruction) is { } deconstruct:
                yield return deconstruct.OriginalDefinition;
                break;
            case IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol patternDeconstruct }:
                yield return patternDeconstruct.OriginalDefinition;
                break;
        }
        // User-defined operator call — every form that resolves one carries the OperatorMethod: a plain
        // `a + b` (IBinaryOperation) / `-a` (IUnaryOperation), a `a += b` (ICompoundAssignmentOperation),
        // and a `a++`/`--a` (IIncrementOrDecrementOperation). A BCL operator has a null OperatorMethod and
        // is naturally excluded; the consumers' internalMethods / callee filter restricts to registered
        // struct operators. (B49 — see the summary above.)
        var opMethod = OperationMethodFacts.OperatorMethod(op);
        if (opMethod != null) yield return opMethod.OriginalDefinition;
    }

    /// <summary>CW1 lift: the runtime impl-accessor DEFINITIONS a property/indexer reference can
    /// dispatch to — the accessor twin of the invocation arm's v2b-2/CW5 virtual arms, shared by
    /// <see cref="EnumerateInternalCallTargets"/>'s property arm and
    /// <see cref="PropertyAccessorMatches"/> so the two matchers cannot drift. Empty when the site is
    /// not a dispatch site. The CW5 over-yield polarity applies to a receiver whose DECLARED type
    /// still carries a type parameter AND to an instance-less reference on a user class (a property
    /// SUBPATTERN's member read — its receiver is the narrowed scrutinee, whose runtime type can be
    /// any minted subtype): both yield the slot's most-derived impl for EVERY minted class
    /// (over-yield only ever over-spills).</summary>
    IEnumerable<IMethodSymbol> AccessorDispatchImplDefs(IPropertyReferenceOperation pr, IMethodSymbol acc)
    {
        if (acc == null || !VirtualDispatch.IsVirtualCall(acc)) yield break;
        if (pr.Instance is IInstanceReferenceOperation ir && ir.Syntax is BaseExpressionSyntax) yield break;
        bool overYield = ClassTypeObjectContext.ContainsTypeParameter(pr.Instance?.Type)
            || pr.Instance == null && !pr.Property.IsStatic
               && pr.Property.ContainingType is INamedTypeSymbol ict && TypeClassifier.IsUserClass(ict);
        if (overYield)
        {
            var slotDef = VirtualDispatch.SlotIntroducer(acc);
            foreach (var concrete in _emitter.ClassTypes.MintedClasses)
                if (VirtualDispatch.MostDerivedImpl(concrete, slotDef) is { } genImpl)
                    yield return genImpl.OriginalDefinition;
        }
        else if (pr.Instance?.Type is INamedTypeSymbol arecv
                 && VirtualDispatch.IsDispatchSite(acc, pr.Instance, arecv))
            foreach (var vt in _emitter.VirtualDispatchInstance.ResolveTargets(arecv, acc))
                yield return vt.Impl.OriginalDefinition;
    }

    /// <summary>M4b: the runtime USER-override definitions a v1-class stringify site (interpolation hole /
    /// concat operand) can dispatch to through the object.ToString slot — the stringify twin of
    /// <see cref="AccessorDispatchImplDefs"/>, shared by the classifier's interpolation and concat arms.
    /// Empty when the value is not a v1 class (or no minted implementor overrides the slot: the type-name
    /// constant arms call nothing). The CW5 over-yield polarity applies to a value whose DECLARED type
    /// still carries a type parameter (emission resolves it through the monomorphization map this
    /// def-keyed walk lacks): yield the slot's most-derived user impl for EVERY minted class —
    /// over-yield only ever over-spills.</summary>
    IEnumerable<IMethodSymbol> ClassToStringImplDefs(IOperation valueOp)
    {
        if (valueOp?.Type is not { } t) yield break;
        var slotDef = ObjectToStringSlotDef();
        if (ClassTypeObjectContext.ContainsTypeParameter(t))
        {
            foreach (var concrete in _emitter.ClassTypes.MintedClasses)
                if (VirtualDispatch.MostDerivedImpl(concrete, slotDef) is { } genImpl
                    && TypeClassifier.IsUserClass(genImpl.ContainingType))
                    yield return genImpl.OriginalDefinition;
        }
        else if (t is INamedTypeSymbol nts && TypeClassifier.IsUserClass(nts))
            foreach (var vt in _emitter.VirtualDispatchInstance.ResolveTargets(nts, slotDef))
                if (TypeClassifier.IsUserClass(vt.Impl.ContainingType))
                    yield return vt.Impl.OriginalDefinition;
    }

    IMethodSymbol _objectToStringDef;
    IMethodSymbol ObjectToStringSlotDef()
        => _objectToStringDef ??= _emitter.Compilation.GetSpecialType(SpecialType.System_Object)
            .GetMembers("ToString").OfType<IMethodSymbol>()
            .First(m => !m.IsStatic && m.Parameters.Length == 0);

    static IOperation UnwrapConversionOps(IOperation op)
    {
        while (op is IConversionOperation conv) op = conv.Operand;
        return op;
    }

    /// <summary>Named-callee matcher over <see cref="EnumerateInternalCallTargets"/> — feeds the
    /// emitter's HasNonTailCallTo (via TailCallAnalysis) and the per-site TailSpared marking.</summary>
    internal bool IsInternalCallTo(IOperation op, IMethodSymbol callee, out IOperation call)
    {
        call = null;
        foreach (var t in EnumerateInternalCallTargets(op))
            if (SymbolEqualityComparer.Default.Equals(t, callee)) { call = op; return true; }
        return false;
    }

    // [Y5]/[Y6]/[Y13]: accessor-SPECIFIC twin of IsInternalCallTo's property arm — true when the
    // chosen accessor (static binding OR the emission-faithful leaf override) IS the callee. The
    // either-accessor match is correct for simple SET (only the setter runs) but too coarse for
    // compound/inc-dec, where the getter runs first and its result is read afterwards.
    internal bool PropertyAccessorMatches(IPropertyReferenceOperation pr, IMethodSymbol callee, bool getter)
    {
        var acc = getter ? pr.Property.GetMethod : pr.Property.SetMethod;
        // CW1 lift: at an accessor DISPATCH site the runtime callee is a resolved impl of the matching
        // kind, not the statically-bound accessor — match through the same fan-out the classifier's
        // property arm yields (BEFORE the cross arm below, which early-returns for the base-typed
        // variable receivers dispatch sites live on).
        foreach (var impl in AccessorDispatchImplDefs(pr, VirtualDispatch.FindAccessor(pr.Property, getter)))
            if (SymbolEqualityComparer.Default.Equals(impl, callee))
                return true;
        // Wave-12 r2 [V1]: variable-receiver / interface accessor dispatch — match the local method
        // the cross dispatch can land on (same rationale as IsInternalCallTo's cross arms).
        if (IsCrossDispatchReceiver(pr.Instance, pr.Property))
            return CrossDispatchLocalTarget(acc) is { } xacc
                && SymbolEqualityComparer.Default.Equals(xacc, callee);
        // Wave-14 r4: struct accessor on a fresh instance (a `next[d-1] += ..` / `next.P--` compound or
        // inc-dec through a struct-typed local) — the specific get/set accessor on a user-struct receiver
        // is the callee, independent of a `this` receiver (mirrors the IsInternalCallTo struct arm).
        if (pr.Property is { IsStatic: false } && pr.Property.ContainingType is INamedTypeSymbol saCt
            && TypeClassifier.IsObjectArrayEmulated(saCt)
            && acc != null && SymbolEqualityComparer.Default.Equals(acc.OriginalDefinition, callee))
            return true;
        if (pr.Instance is not IInstanceReferenceOperation) return false;
        if (acc != null && SymbolEqualityComparer.Default.Equals(acc.OriginalDefinition, callee))
            return true;
        if (LeafPropertyTarget(pr) is { } lp)
        {
            var leafAcc = getter ? lp.GetMethod : lp.SetMethod;
            if (leafAcc != null && SymbolEqualityComparer.Default.Equals(leafAcc.OriginalDefinition, callee))
                return true;
        }
        return false;
    }

    // ── Wave-9 round-3 [W1]/[W2]/[W3]: emission-faithful leaf-override resolution for the graph ──
    // Emission resolves a this-receiver virtual call to the most-derived override visible from the
    // compiled class (HandlerBase.ResolveMostDerivedOverride / ResolveDispatchProperty, sharing
    // HandlerBase.FindOverrideMethodInChain/FindOverridePropertyInChain walkers), but the
    // recursion graph recorded only the STATIC binding — so a runtime cycle closed through an
    // override (base body's virtual call/property read dispatching the leaf, or an override calling
    // base.M whose body virtual-calls back) had no static counterpart and its frames were never spilled
    // (VM-proven: 305 where the CLR gives 605; override<->base-copy 14 vs 12; fb=base.M bundle 17 vs 21).
    // These mirrors return the leaf's ORIGINAL DEFINITION (graph nodes are definition-keyed) or null
    // when the site keeps its static binding (no override visible / base. receiver / non-virtual).

    IMethodSymbol LeafCallTarget(IInvocationOperation inv)
    {
        var tm = inv.TargetMethod;
        if (!(tm.IsVirtual || tm.IsOverride || tm.IsAbstract) || tm.MethodKind != MethodKind.Ordinary)
            return null;
        if (inv.Instance is not IInstanceReferenceOperation iref
            || iref.Syntax is BaseExpressionSyntax) return null;
        var def = tm.OriginalDefinition;
        var leaf = ResolveLeafOverrideDef(def);
        return SymbolEqualityComparer.Default.Equals(leaf, def) ? null : leaf;
    }

    /// <summary>Wave-9 round-5 [X1]: leaf resolution for a this-receiver virtual METHOD-GROUP
    /// conversion (delegate creation), gated identically to LeafCallTarget — emission's bridge
    /// resolves these to the chain-root export running the leaf body, so the escape set mirrors it.
    /// Internal for the emitter's precise-dispatch provenance (TryResolvePreciseDispatchTargets).</summary>
    internal IMethodSymbol LeafMethodRefTarget(IMethodReferenceOperation mr)
    {
        var m = mr.Method;
        if (m == null || !(m.IsVirtual || m.IsOverride || m.IsAbstract) || m.MethodKind != MethodKind.Ordinary)
            return null;
        if (mr.Instance is not IInstanceReferenceOperation iref
            || iref.Syntax is BaseExpressionSyntax) return null;
        var def = m.OriginalDefinition;
        var leaf = ResolveLeafOverrideDef(def);
        return SymbolEqualityComparer.Default.Equals(leaf, def) ? null : leaf;
    }

    IPropertySymbol LeafPropertyTarget(IPropertyReferenceOperation pr)
    {
        var p = pr.Property;
        if (!(p.IsVirtual || p.IsOverride || p.IsAbstract)) return null;
        if (pr.Instance is not IInstanceReferenceOperation iref
            || iref.Syntax is BaseExpressionSyntax) return null;
        var def = p.OriginalDefinition;
        var cand = HandlerBase.FindOverridePropertyInChain(_emitter.ClassSymbol, def, p.Name);
        if (cand == null) return null;
        return SymbolEqualityComparer.Default.Equals(cand.OriginalDefinition, def) ? null : cand.OriginalDefinition;
    }

    // Definition-keyed twin of HandlerBase.ResolveMostDerivedOverride, sharing its
    // FindOverrideMethodInChain walker: the graph is keyed by OriginalDefinition, so unlike the emission
    // side there is no generic re-Construct here — just normalize the found override to its definition.
    IMethodSymbol ResolveLeafOverrideDef(IMethodSymbol def)
        => HandlerBase.FindOverrideMethodInChain(_emitter.ClassSymbol, def, def.Name)?.OriginalDefinition ?? def;

    // ── Wave-12 r2 [V1]: cross-dispatch landing target for the recursion graph ──
    // A method/accessor dispatched through a VARIABLE receiver (same-typed field/local, base-typed
    // reference) or an INTERFACE-typed receiver emits SetProgramVariable + SendCustomEvent — and when
    // the receiver holds `this` at runtime, the event re-enters THIS program synchronously, exactly
    // like a direct recursive call. These edges were invisible to the SCC analysis (the interface
    // flavor entirely; the class flavor had the static edge but no spill site at emission), so a
    // live local/param after the reentrant self-call was silently clobbered (VM-proven ref=36 vs 0
    // field/local/base flavors, 180 vs 0 interface, 75 vs 60 property accessor, 69 vs 27 mutual).
    // Returns the ORIGINAL DEFINITION of the local method the dispatch lands on when the receiver is
    // this program (the class family's most-derived override — mirroring the chain-root export
    // normalization the emission dispatches), or null when it can never land here (foreign class,
    // unimplemented interface, static). HandlerBase.CrossDispatchLocalCallee mirrors this for the
    // per-site Reentrant marking at emission.
    IMethodSymbol CrossDispatchLocalTarget(IMethodSymbol target)
    {
        if (target == null || target.IsStatic) return null;
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            var impl = (_emitter.ClassSymbol.FindImplementationForInterfaceMember(target)
                        ?? _emitter.ClassSymbol.FindImplementationForInterfaceMember(target.OriginalDefinition))
                       as IMethodSymbol;
            // FindImplementationForInterfaceMember returns the chain ROOT ([W5]) — the dispatch runs
            // the receiver program's most-derived override, so leaf-resolve like the class flavor.
            return impl == null ? null : ResolveLeafOverrideDef(impl.OriginalDefinition);
        }
        for (var t = _emitter.ClassSymbol; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target.ContainingType))
                return ResolveLeafOverrideDef(target.OriginalDefinition);
        return null;
    }

    /// <summary>[V1] arm shared by the classifier's invocation/property arms and PropertyAccessorMatches:
    /// true when <paramref name="instance"/> is a variable-receiver (or interface-typed) member access whose
    /// cross dispatch can land back on this program. Interface members dispatch cross for EVERY
    /// receiver shape (a `(IFace)this` cast wraps the instance reference in a conversion).</summary>
    static bool IsCrossDispatchReceiver(IOperation instance, ISymbol member)
        => instance != null
           && (instance is not IInstanceReferenceOperation
               || member.ContainingType?.TypeKind == TypeKind.Interface);

    /// <summary>The six non-using node shapes referencing a user-struct member (ctor/instance-method/
    /// computed-property/subpattern-property/operator/conversion), yielded as-observed (constructed
    /// symbol, ungated) — consumed by ReachEdges' ReachStructMember arm (the registration gate
    /// IsCollectibleStructMember and the recursion/capture-root .OriginalDefinition projection are
    /// consumer-side). `using`-resource dispose is NOT included here — EnumerateUsingDispose handles
    /// that resource shape separately and the two must not be merged.</summary>
    IEnumerable<IMethodSymbol> EnumerateStructMemberRefs(IOperation op)
    {
        // Parameterized user-struct / v1-class constructor: new V(...) / new C(...).
        if (op is IObjectCreationOperation oc && oc.Constructor != null
            && oc.Type is INamedTypeSymbol nt && TypeClassifier.IsObjectArrayEmulated(nt)
            && oc.Arguments.Length > 0 && !oc.Constructor.IsImplicitlyDeclared)
            yield return oc.Constructor;
        // User-struct instance method: v.Method(...). Feature G: yield the CONSTRUCTED symbol
        // (roadmap B36 residue — a struct declaring its OWN type parameter used to be collected by
        // OriginalDefinition and rejected loudly here; now the receiver's concrete T is carried
        // through, mirroring RegisterGenericSpecialization's per-spec discipline for the
        // containing-type dimension). Non-generic structs are unaffected: tm == tm.OriginalDefinition
        // there, so this is byte-identical for them.
        if (op is IInvocationOperation inv && inv.TargetMethod is { IsStatic: false } tm
            && tm.MethodKind == MethodKind.Ordinary && !tm.IsImplicitlyDeclared
            && tm.ContainingType is INamedTypeSymbol it && TypeClassifier.IsObjectArrayEmulated(it)
            // CA-v2b-2: at a virtual dispatch site the STATIC target (a base method) is not the runtime
            // callee — the inline chain calls the resolved override. Collecting the static base here walked a
            // never-dispatched base body (e.g. `RecBse.Spawn`'s `new RecBse()`) and phantom-minted a class no
            // live site ever creates. The real dispatch targets (the most-derived impl per minted class) are
            // seeded by the instantiation-reach virtual-slot walk, so skip the static base at dispatch sites.
            && !VirtualDispatch.IsDispatchSite(tm, inv.Instance, inv.Instance?.Type as INamedTypeSymbol))
            yield return tm;
        // CA-v2 M1: a `: base(...)` / `: this(...)` ctor initializer is an IInvocationOperation whose
        // target is a CONSTRUCTOR (MethodKind.Constructor, missed by the Ordinary arm above). The base
        // ctor function is otherwise never registered at Phase 1 -> its on-demand emission from the
        // derived ctor prologue lands mid-drain but its reach/recursion node is absent (VM-faulted:
        // the derived ctor jumped to an unemitted base ctor). Collect the explicit-ctor target here.
        if (op is IInvocationOperation cinv && cinv.TargetMethod is { MethodKind: MethodKind.Constructor } ctm
            && !ctm.IsImplicitlyDeclared
            && ctm.ContainingType is INamedTypeSymbol cit && TypeClassifier.IsObjectArrayEmulated(cit))
            yield return ctm;
        // MG auto-wrap (2026-07-11 wave-lite): a class/struct instance member reached ONLY as a METHOD
        // GROUP (`o.M` -> a receiver-bridge delegate) is otherwise invisible to this collector (which
        // sees invocations), so it registered on demand at emit time AFTER BuildRecursionInfo -> no
        // graph node, no reentrancy spill when the bundle re-enters it ([Z1]/[Y7] class, VM-proven
        // under-spill: a self-recursive class MG returned 25025 vs the CLR's 40025). Seed it here so
        // it becomes a reach root + graph node + escape target.
        if (op is IMethodReferenceOperation mgr && mgr.Method is { IsStatic: false } mgm
            && mgm.MethodKind == MethodKind.Ordinary && !mgm.IsImplicitlyDeclared
            && mgm.ContainingType is INamedTypeSymbol mgit && TypeClassifier.IsObjectArrayEmulated(mgit))
            yield return mgm;
        // Computed (non-auto) user-struct property: v.Prop (read) or v.Prop = x (write). Auto-properties use
        // their backing-field slot directly (no method), but a computed accessor must be inlined as a struct
        // instance method. Yield both accessors (the reference alone doesn't reveal read-vs-write context).
        // A user-struct indexer (s[i]) is just a parameterized computed property (never auto-backed), so it
        // is collected the same way — its accessors carry the index args after the synthetic receiver.
        if (op is IPropertyReferenceOperation pr
            && pr.Property is { IsStatic: false } prop
            && pr.Property.ContainingType is INamedTypeSymbol pit && TypeClassifier.IsObjectArrayEmulated(pit)
            && UasmEmitter.IsComputedProperty(prop))
        {
            // CW1 lift: at an accessor dispatch site the STATIC accessor is not the runtime callee —
            // the real targets (the most-derived impl per minted class) are seeded by the
            // instantiation-reach virtual-slot walk, so skip the static accessor there (the exact
            // mirror of the method arm's CA-v2b-2 dispatch-site skip above; collecting it would
            // phantom-walk a never-dispatched base accessor body).
            if (prop.GetMethod is { } pg
                && !VirtualDispatch.IsDispatchSite(pg, pr.Instance, pr.Instance?.Type as INamedTypeSymbol))
                yield return pg;
            if (prop.SetMethod is { } ps
                && !VirtualDispatch.IsDispatchSite(ps, pr.Instance, pr.Instance?.Type as INamedTypeSymbol))
                yield return ps;
        }
        // Property-pattern subpattern: `p is { Doubled: ... }` reads Doubled via an IMPLICIT getter call,
        // not an explicit IPropertyReferenceOperation, so yield a computed user-struct property's getter
        // here too — else the pattern lowering emits a bogus accessor extern for an unregistered getter.
        if (op is IPropertySubpatternOperation sub && sub.Member is IPropertyReferenceOperation spr
            && spr.Property is { IsStatic: false } sprop
            && spr.Property.ContainingType is INamedTypeSymbol spit && TypeClassifier.IsObjectArrayEmulated(spit)
            && UasmEmitter.IsComputedProperty(sprop) && sprop.GetMethod != null)
            yield return sprop.GetMethod;
        if (op is IEventAssignmentOperation eventAssignment
            && eventAssignment.EventReference is IEventReferenceOperation eventReference)
        {
            var accessor = eventAssignment.Adds
                ? eventReference.Event.AddMethod
                : eventReference.Event.RemoveMethod;
            if (accessor is { IsImplicitlyDeclared: false }
                && accessor.ContainingType is INamedTypeSymbol eventOwner
                && TypeClassifier.IsObjectArrayEmulated(eventOwner))
                yield return accessor;
        }
        if (op is IDeconstructionAssignmentOperation deconstruction
            && ResolveUserDeconstruct(deconstruction) is { } deconstruct)
            yield return deconstruct;
        if (op is IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol patternDeconstruct })
            yield return patternDeconstruct;
        // User-struct operator: v1 + v2, -v, s += t, c++ (static operator methods). Compound-assignment and
        // increment/decrement carry their operator method too, so yield those so the emit side can JUMP to
        // the user operator instead of a bogus SystemObjectArray.__op_* extern.
        var opMethod = (op as IBinaryOperation)?.OperatorMethod
            ?? (op as IUnaryOperation)?.OperatorMethod
            ?? (op as ICompoundAssignmentOperation)?.OperatorMethod
            ?? (op as IIncrementOrDecrementOperation)?.OperatorMethod;
        if (opMethod is { MethodKind: MethodKind.UserDefinedOperator }
            && opMethod.ContainingType is INamedTypeSymbol ot && TypeClassifier.IsObjectArrayEmulated(ot))
            yield return opMethod;
        // User-struct CONVERSION operator (implicit/explicit). MethodKind is Conversion (not UserDefinedOperator),
        // so it needs its own arm — invoked implicitly by an IConversionOperation, routed to the method on emit.
        if (op is IConversionOperation convOp && convOp.OperatorMethod is { MethodKind: MethodKind.Conversion } convM
            && convM.ContainingType is INamedTypeSymbol convCt && TypeClassifier.IsObjectArrayEmulated(convCt))
            yield return convM;
    }

    IMethodSymbol ResolveUserDeconstruct(IDeconstructionAssignmentOperation operation)
    {
        if (operation.Syntax is not AssignmentExpressionSyntax assignment) return null;
        return _emitter.Compilation.GetSemanticModel(assignment.SyntaxTree)
            .GetDeconstructionInfo(assignment).Method;
    }
}
