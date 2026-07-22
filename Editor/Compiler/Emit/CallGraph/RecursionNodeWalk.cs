using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>The walk-level products BuildRecursionInfo's facet assembly consumes: the recursion node set
/// (reach roots + local functions + lambdas) with each node's body, its internal-call edge set (filtered to
/// the named-call membership set), its DIRECT this-field touches / accessor edges (the transitive
/// closure runs downstream, over the synthetic-edge-augmented graph), and the C3 escape facet (the §4.1
/// escaped-target set + the variant sig-S pairs, membership-filtered). Produced by
/// <see cref="RecursionNodeWalk"/>.</summary>
internal sealed class CallableBodyGraph
{
    /// <summary>Every callable definition known before emission, including declarations without a body.</summary>
    public HashSet<IMethodSymbol> CallableDefinitions;
    /// <summary>Every recursion-graph node (roots, local functions, lambdas), deduped.</summary>
    public IMethodSymbol[] AllNodes;
    public Dictionary<IMethodSymbol, IOperation> Bodies;
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> Edges;
    public Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> DirectThisTouches;
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> AccessorEdges;
    /// <summary>§4.1 escape set BEFORE the §5.4 planner-bridge widening (which stays downstream in
    /// AssembleRecursionFacets): named method-group / [X1] leaf targets filtered to internal methods
    /// (roots + local functions), plus every delegate-created lambda symbol UNCONDITIONALLY — the
    /// legacy collector's lambda arm had no membership gate; a non-node lambda falls to the
    /// downstream escapeSig edges.ContainsKey gate.</summary>
    public HashSet<IMethodSymbol> EscapedTargets;
    /// <summary>Variance §2.2: (internal target, adapter protocol sig-S) pairs for variant
    /// method-group creations — one target may appear under several sig-S values, hence a list.</summary>
    public List<(IMethodSymbol Method, string Sig)> VariantEscapeSigs;
}

/// <summary>CA rewrite (M5b): the recursion facet of the unified resolver-driven traversal — ONE pass over
/// the recursion node set's bodies (it replaced BuildRecursionInfo's four private per-op walks, whose
/// frozen legacy-oracle copies were deleted at C4 retirement). Each op
/// is visited once, dispatching to <see cref="ResolvedEdgeResolver.ResolveEdges"/> for CallEdge targets
/// (TargetRole.CallEdge's production consumer) and EscapeTarget / variant escape-sig collection (C3 —
/// TargetRole.EscapeTarget's production consumer, absorbing the separate escape walks), plus walk-local
/// collection of this-field touches, accessor edges, and nested-function declarations.
///
/// Node set = the main reach fixpoint's definitions (BodyByDef) plus every supplementary
/// generic-foreign-static def, local function, and lambda the walk discovers transitively,
/// node-by-construction (supp defs at their reference sites via ResolveForeignStaticSuppDefs, gated on
/// GenericForeignStaticBodies membership — the C2-derived replacement for the former root concat):
/// an LF/lambda body is walked IN THE TREE WHERE IT WAS FOUND (no re-fetch — the legacy walk's one explicit
/// GetMethodBodyOperation arm is gone; red syntax is shared across trees, so the syntax-keyed facets are
/// unaffected). Attribution stops at LF/lambda boundaries — each is its own node — exactly like the legacy
/// walks' child-skip. Field-initializer trees are walked in declaration-discovery-only mode (a field init is
/// not a node; its lambdas are). Raw call targets and raw escape targets are filtered against the final
/// method set only after the worklist dries (a call to — or an escape of — a local function discovered
/// later must still count).
///
/// Runs INSIDE BuildRecursionInfo (post VirtualDispatch seed — the CallEdge virtual arm needs the seeded
/// instance), per class in Phase 2; no shared mutable state, thread-safe.</summary>
internal sealed class RecursionNodeWalk
{
    readonly ResolvedEdgeResolver _resolver;
    readonly ReachableBodies _reach;
    readonly IEnumerable<IOperation> _fieldInitOps;
    readonly IEnumerable<IMethodSymbol> _plannedCallables;

    public RecursionNodeWalk(ResolvedEdgeResolver resolver, ReachableBodies reach,
        IEnumerable<IOperation> fieldInitOps, IEnumerable<IMethodSymbol> plannedCallables)
    {
        _resolver = resolver;
        _reach = reach;
        _fieldInitOps = fieldInitOps;
        _plannedCallables = plannedCallables;
    }

    public CallableBodyGraph Run()
    {
        var cmp = SymbolEqualityComparer.Default;
        // C2 (M5c residual): the seed node source is the MAIN reach fixpoint only. The supplementary
        // generic-foreign-static defs are no longer pre-seeded by a root concat — the walk derives them
        // at their reference sites (the supp-discovery arm in Visit), reproducing the reach-side supp
        // fixpoint by construction. GenericForeignStaticBodies is read only as a membership gate + body
        // cache, so the F1/F2 registration-ordinal guard (ReachableBodies.cs) — which protects the
        // Phase-1 REGISTRATION projections — is untouched by this Phase-2 read.
        // No syntax filter / Distinct here: BodyByDef keys are enqueue-gated on having syntax and are
        // unique by dictionary construction — both legs only ever served the deleted supp-key concat.
        var roots = _reach.BodyByDef.Keys.ToList();

        var bodies = new Dictionary<IMethodSymbol, IOperation>(cmp);
        var rawTargets = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(cmp);
        var rawEscapes = new HashSet<IMethodSymbol>(cmp);
        var rawVariantSigs = new List<(IMethodSymbol Method, string Sig)>();
        var touches = new Dictionary<IMethodSymbol, HashSet<IFieldSymbol>>(cmp);
        var accessors = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(cmp);
        var localFuncs = new HashSet<IMethodSymbol>(cmp);
        var order = new List<IMethodSymbol>();
        var queue = new Queue<IMethodSymbol>();

        // Every node is registered UNCONDITIONALLY, null body included — an auto-property accessor's
        // operation is a bodyless null yet must still be a graph node (empty edge/touch sets); dropping
        // null-body nodes would lose them from RecursionGraphNodes.
        void AddNode(IMethodSymbol sym, IOperation body)
        {
            if (bodies.ContainsKey(sym)) return;
            bodies[sym] = body;
            rawTargets[sym] = new HashSet<IMethodSymbol>(cmp);
            touches[sym] = new HashSet<IFieldSymbol>(cmp);
            accessors[sym] = new HashSet<IMethodSymbol>(cmp);
            order.Add(sym);
            queue.Enqueue(sym);
        }

        // The one visit per op. `node` is the attribution target; null = declaration discovery only
        // (field-initializer trees, and the degenerate symbol-less nested-function subtrees the legacy
        // walks also descended without attribution).
        void Visit(IOperation op, IMethodSymbol node)
        {
            if (op == null) return;
            // C2 (M5c residual): supplementary generic-foreign-static roots are DERIVED here — the same
            // resolver leg that fed the reach-side supp fixpoint, gated on membership in its result —
            // instead of pre-seeded by a GenericForeignStaticBodies.Keys root concat. Runs in EVERY
            // visit mode (declaration-discovery included): the reach walk discovered supp defs from
            // field-initializer trees too, so the derivation must match that op coverage exactly.
            foreach (var d in _resolver.ResolveForeignStaticSuppDefs(op))
                if (!bodies.ContainsKey(d) && _reach.GenericForeignStaticBodies.TryGetValue(d, out var suppBody))
                {
                    roots.Add(d);
                    // Unwrap for the same corner the seed loop pins: a generic STATIC local function in
                    // a foreign static lands in the supp dict as an ILocalFunctionOperation; discovered
                    // use-before-declaration it would otherwise seed wrapped and lose its edges.
                    // Pinned by facet_foreign_generic_static_local_function.
                    AddNode(d, (suppBody as ILocalFunctionOperation)?.Body ?? suppBody);
                }
            if (op is ILocalFunctionOperation lf)
            {
                if (lf.Symbol != null)
                {
                    var def = lf.Symbol.OriginalDefinition;
                    localFuncs.Add(def);
                    AddNode(def, lf.Body);
                    return; // own node — the worklist walks its body
                }
                node = null;
            }
            else if (op is IAnonymousFunctionOperation af)
            {
                if (af.Symbol != null && af.Body != null)
                {
                    AddNode(af.Symbol, af.Body);
                    return; // own node — the worklist walks its body
                }
                node = null;
            }
            else
            {
                // One ResolveEdges dispatch serves both roles. CallEdge attribution is node-gated
                // (a field-init call is a reach, not a caller edge — the legacy walks never ran the
                // callee collector over field-initializer trees), but EscapeTarget / variant escape-sig
                // collection (C3) runs in EVERY visit mode: the legacy escape walks descended the
                // field-initializer trees too (a field init `Action f = M;` escapes M). Raw escapes are
                // membership-filtered after the worklist dries, exactly like rawTargets.
                foreach (var t in _resolver.ResolveEdges(op))
                {
                    if (t.Role == TargetRole.CallEdge && node != null)
                        rawTargets[node].Add(t.Method);
                    else if (t.Role == TargetRole.EscapeTarget)
                        rawEscapes.Add(t.Method);
                }
                foreach (var p in _resolver.ResolveVariantEscapeSigs(op))
                    rawVariantSigs.Add(p);
                if (node != null)
                    switch (op)
                    {
                        case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fr when !fr.Field.IsStatic:
                            touches[node].Add(fr.Field.OriginalDefinition);
                            break;
                        case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } pr:
                            if (pr.Property.GetMethod != null) accessors[node].Add(pr.Property.GetMethod.OriginalDefinition);
                            if (pr.Property.SetMethod != null) accessors[node].Add(pr.Property.SetMethod.OriginalDefinition);
                            break;
                    }
            }
            foreach (var child in op.ChildOps())
                Visit(child, node);
        }

        foreach (var m in roots)
        {
            // C2-proven load-bearing (2026-07-15): the tracked gates stayed green without the unwrap
            // (coverage hole) but the targeted probe went red — a STATIC local function declared inside
            // a foreign static rides the ForeignStatics reach leg into BodyByDef with an
            // ILocalFunctionOperation body; seeded wrapped, Visit's LF arm dedups against the node's own
            // key and returns without walking, silently dropping the LF's edges (a self-recursive local
            // function lost its spill edge). Pinned by facet_foreign_static_local_function.
            var body = _reach.BodyByDef[m]; // seed roots are exactly BodyByDef keys
            AddNode(m, (body as ILocalFunctionOperation)?.Body ?? body);
        }
        foreach (var initOp in _fieldInitOps)
            Visit(initOp, null);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            Visit(bodies[node], node);
        }

        var methodSet = new HashSet<IMethodSymbol>(roots, cmp);
        foreach (var l in localFuncs) methodSet.Add(l);

        var edges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(cmp);
        foreach (var node in order)
        {
            var filtered = new HashSet<IMethodSymbol>(cmp);
            foreach (var t in rawTargets[node])
                if (methodSet.Contains(t))
                    filtered.Add(t);
            edges[node] = filtered;
        }

        // C3: the consumer-side membership filter for the resolver's UNGATED EscapeTarget yields —
        // named method-group / [X1] leaf targets must be internal methods (roots + local functions),
        // while a delegate-created lambda escapes unconditionally, reproducing the legacy collector's
        // ungated lambda arm (a lambda is never called by name, so it is never in methodSet; a
        // non-node lambda is dropped downstream by the escapeSig edges.ContainsKey gate).
        var escaped = new HashSet<IMethodSymbol>(cmp);
        foreach (var t in rawEscapes)
            if (t.MethodKind == MethodKind.AnonymousFunction || methodSet.Contains(t))
                escaped.Add(t);
        var variantSigs = new List<(IMethodSymbol Method, string Sig)>();
        foreach (var p in rawVariantSigs)
            if (methodSet.Contains(p.Method))
                variantSigs.Add(p);

        var callableDefinitions = new HashSet<IMethodSymbol>(
            _plannedCallables.Select(method => method.OriginalDefinition), cmp);
        foreach (var node in order)
            callableDefinitions.Add(node.OriginalDefinition);

        return new CallableBodyGraph
        {
            CallableDefinitions = callableDefinitions,
            AllNodes = order.ToArray(),
            Bodies = bodies,
            Edges = edges,
            DirectThisTouches = touches,
            AccessorEdges = accessors,
            EscapedTargets = escaped,
            VariantEscapeSigs = variantSigs,
        };
    }
}
