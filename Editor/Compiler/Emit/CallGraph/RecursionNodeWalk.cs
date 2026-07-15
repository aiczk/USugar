using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>The walk-level products BuildRecursionInfo's facet assembly consumes: the recursion node set
/// (reach roots + local functions + lambdas) with each node's body, its internal-call edge set (filtered to
/// the named-call membership set), and its DIRECT this-field touches / accessor edges (the transitive
/// closure runs downstream, over the synthetic-edge-augmented graph). Produced by
/// <see cref="RecursionNodeWalk"/> (production) and by UasmEmitter.LegacyRecursionWalk (the M5b differential
/// oracle, deleted after C2/C4 prove the arms).</summary>
internal sealed class RecursionWalkResult
{
    /// <summary>The reach-root definitions (BodyByDef + GenericForeignStaticBodies, syntax-having) — the
    /// escape trio walks THESE bodies (full descent covers nested-function subtrees).</summary>
    public List<IMethodSymbol> Roots;
    /// <summary>Roots + local functions — the membership filter for named internal-call edges (lambdas are
    /// excluded: they are dispatched, never called by name).</summary>
    public HashSet<IMethodSymbol> MethodSet;
    /// <summary>Every recursion-graph node (roots, local functions, lambdas), deduped.</summary>
    public IMethodSymbol[] AllNodes;
    public Dictionary<IMethodSymbol, IOperation> Bodies;
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> Edges;
    public Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> DirectThisTouches;
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> AccessorEdges;
}

/// <summary>CA rewrite (M5b): the recursion facet of the unified resolver-driven traversal — ONE pass over
/// the recursion node set's bodies replacing BuildRecursionInfo's four private per-op walks
/// (CollectInternalCallees / CollectThisFieldTouches / CollectLocalFunctions / CollectLambdaNodes). Each op
/// is visited once, dispatching to <see cref="ResolvedEdgeResolver.ResolveEdges"/> for CallEdge targets
/// (TargetRole.CallEdge's production consumer) plus walk-local collection of this-field touches, accessor
/// edges, and nested-function declarations.
///
/// Node set = the reach fixpoint's definitions (BodyByDef + GenericForeignStaticBodies — the M5c-unified
/// node source) plus every local function and lambda the walk discovers transitively, node-by-construction:
/// an LF/lambda body is walked IN THE TREE WHERE IT WAS FOUND (no re-fetch — the legacy walk's one explicit
/// GetMethodBodyOperation arm is gone; red syntax is shared across trees, so the syntax-keyed facets are
/// unaffected). Attribution stops at LF/lambda boundaries — each is its own node — exactly like the legacy
/// walks' child-skip. Field-initializer trees are walked in declaration-discovery-only mode (a field init is
/// not a node; its lambdas are). Raw call targets are filtered against the final MethodSet only after the
/// worklist dries (a call to a local function discovered later must still become an edge).
///
/// Runs INSIDE BuildRecursionInfo (post VirtualDispatch seed — the CallEdge virtual arm needs the seeded
/// instance), per class in Phase 2; no shared mutable state, thread-safe.</summary>
internal sealed class RecursionNodeWalk
{
    readonly ResolvedEdgeResolver _resolver;
    readonly ReachableBodies _reach;
    readonly IEnumerable<IOperation> _fieldInitOps;

    public RecursionNodeWalk(ResolvedEdgeResolver resolver, ReachableBodies reach, IEnumerable<IOperation> fieldInitOps)
    {
        _resolver = resolver;
        _reach = reach;
        _fieldInitOps = fieldInitOps;
    }

    public RecursionWalkResult Run()
    {
        var cmp = SymbolEqualityComparer.Default;
        // The recursion node source is the single reach fixpoint result (M5c): every walked body
        // DEFINITION plus the supplementary generic-foreign-static bodies — proven byte-neutral against
        // the four former compensation concats by the (deleted) M5c differential + golden + DiffFuzz;
        // the live guard is now the RecursionFacetEquivalenceTests legacy-vs-shared differential.
        var roots = _reach.BodyByDef.Keys
            .Concat(_reach.GenericForeignStaticBodies.Keys)
            .Where(m => m.DeclaringSyntaxReferences.Length > 0)
            .Distinct(cmp)
            .Cast<IMethodSymbol>()
            .ToList();

        var bodies = new Dictionary<IMethodSymbol, IOperation>(cmp);
        var rawTargets = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(cmp);
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
            else if (node != null)
            {
                foreach (var t in _resolver.ResolveEdges(op))
                    if (t.Role == TargetRole.CallEdge)
                        rawTargets[node].Add(t.Method);
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
            var body = _reach.BodyByDef.TryGetValue(m, out var cached) ? cached
                : _reach.GenericForeignStaticBodies.TryGetValue(m, out var supp) ? supp
                : null; // unreachable: roots are drawn from exactly those key sets
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

        return new RecursionWalkResult
        {
            Roots = roots,
            MethodSet = methodSet,
            AllNodes = order.ToArray(),
            Bodies = bodies,
            Edges = edges,
            DirectThisTouches = touches,
            AccessorEdges = accessors,
        };
    }
}
