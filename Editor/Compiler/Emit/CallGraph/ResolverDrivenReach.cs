using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>CA rewrite (M5a cutover): the reach facet of the unified, resolver-driven worklist — the
/// PRODUCTION reach fixpoint behind UasmEmitter.BuildReachableBodiesViaResolver since M5a replaced the
/// legacy 5-collector BuildReachableBodies. It walks each reachable body's ops through the single
/// <see cref="ResolvedEdgeResolver"/> and buckets the yielded reach targets by role, running one
/// queue+visited fixpoint keyed by OriginalDefinition plus the SS2A supplementary generic-foreign-static
/// fixpoint (<see cref="ReachableBodies.GenericForeignStaticBodies"/>) and the open-base-generic roots
/// (<see cref="ReachableBodies.OpenGenericBaseDefs"/>).</summary>
internal sealed class ResolverDrivenReach
{
    readonly ResolvedEdgeResolver _resolver;
    readonly Func<IMethodSymbol, IOperation> _bodyOf;
    readonly Func<IEnumerable<IOperation>> _fieldInitOps;
    readonly Func<IMethodSymbol, bool> _isCollectibleStructMember;
    readonly Func<ISymbol, string> _stableKey;

    public ResolverDrivenReach(
        ResolvedEdgeResolver resolver,
        Func<IMethodSymbol, IOperation> bodyOf,
        Func<IEnumerable<IOperation>> fieldInitOps,
        Func<IMethodSymbol, bool> isCollectibleStructMember,
        Func<ISymbol, string> stableKey)
    {
        _resolver = resolver;
        _bodyOf = bodyOf;
        _fieldInitOps = fieldInitOps;
        _isCollectibleStructMember = isCollectibleStructMember;
        _stableKey = stableKey;
    }

    public ReachableBodies Build(IMethodSymbol[] entryMethods)
    {
        var cmp = SymbolEqualityComparer.Default;
        var result = new ReachableBodies();
        var foreignStatics = new HashSet<IMethodSymbol>(cmp);
        var structMembers = new HashSet<IMethodSymbol>(cmp);
        var baseCopies = new HashSet<IMethodSymbol>(cmp);
        var suppCaptureDefs = new HashSet<IMethodSymbol>(cmp);      // SS2A: dropped generic foreign statics
        // result.OpenGenericBaseDefs holds the open-base-generic main-fixpoint roots (exposed for the swap).
        var visited = new HashSet<IMethodSymbol>(cmp);
        var queue = new Queue<IMethodSymbol>();
        var portableClasses = _resolver.EnumeratePortableClassTypes().ToArray();

        void Walk(IOperation body)
        {
            if (body == null) return;
            foreach (var op in SelfAndDescendants(body))
            {
                foreach (var t in _resolver.ResolveReachEdges(op))
                {
                    switch (t.Role)
                    {
                        case TargetRole.ReachForeignStatic:
                            foreignStatics.Add(t.Method);
                            break;
                        case TargetRole.ReachBaseInstance:
                            baseCopies.Add(t.Method);
                            break;
                        case TargetRole.ReachStructMember:
                            result.StructMemberDefs.Add(t.Method.OriginalDefinition); // recursion roots: def-keyed
                            if (_isCollectibleStructMember(t.Method)) structMembers.Add(t.Method); // registration: constructed
                            break;
                        // CallEdge is a recursion-graph edge, not a reach root — ignored here.
                    }
                }
                foreach (var mc in _resolver.ResolveMintedTypes(op)) result.MintedClasses.Add(mc);
                foreach (var method in _resolver.ResolvePortableDispatchMethods(op, portableClasses))
                    if (_isCollectibleStructMember(method)) structMembers.Add(method);
                foreach (var d in _resolver.ResolveOpenBaseGenericDefs(op)) result.OpenGenericBaseDefs.Add(d);
                foreach (var d in _resolver.ResolveForeignStaticSuppDefs(op)) suppCaptureDefs.Add(d);
            }
        }

        void TryEnqueue(IMethodSymbol m)
        {
            if (m.DeclaringSyntaxReferences.Length > 0 && visited.Add(m.OriginalDefinition))
                queue.Enqueue(m.OriginalDefinition);
        }

        void EnqueueDiscovered()
        {
            foreach (var m in foreignStatics) TryEnqueue(m);
            foreach (var m in structMembers) TryEnqueue(m);
            foreach (var m in baseCopies) TryEnqueue(m);
            foreach (var m in result.OpenGenericBaseDefs) TryEnqueue(m);
            foreach (var m in result.StructMemberDefs) TryEnqueue(m);
        }

        // Cross-program class values can enter this behaviour without a local `new`. Seed every closed
        // source class into the wire-type registry and register its bodies, so incoming virtual/interface
        // dispatch has the same complete target set as locally minted values.
        foreach (var portableClass in portableClasses)
        {
            result.MintedClasses.Add(portableClass);
        }

        foreach (var m in entryMethods) TryEnqueue(m);
        foreach (var initOp in _fieldInitOps()) Walk(initOp);
        EnqueueDiscovered();

        void DrainMain()
        {
            while (queue.Count > 0)
            {
                var def = queue.Dequeue();
                var body = _bodyOf(def);
                result.BodyByDef[def] = body;
                Walk(body);
                EnqueueDiscovered();
            }
        }
        DrainMain();

        // SS2A supplementary fixpoint: the dropped generic foreign statics stay registration-free, but their
        // bodies are walked (into GenericForeignStaticBodies) and any reach they surface registers normally,
        // alternating with the main queue until both dry. Faithful port of BuildReachableBodies' supp loop.
        var suppQueue = new Queue<IMethodSymbol>();
        void EnqueueSupp()
        {
            foreach (var d in suppCaptureDefs)
                if (d.DeclaringSyntaxReferences.Length > 0
                    && !visited.Contains(d)
                    && !result.GenericForeignStaticBodies.ContainsKey(d))
                    suppQueue.Enqueue(d);
            suppCaptureDefs.Clear();
        }
        EnqueueSupp();
        while (suppQueue.Count > 0)
        {
            var def = suppQueue.Dequeue();
            if (result.GenericForeignStaticBodies.ContainsKey(def)) continue;
            var suppBody = _bodyOf(def);
            result.GenericForeignStaticBodies[def] = suppBody;
            Walk(suppBody);
            EnqueueDiscovered();
            DrainMain();
            EnqueueSupp();
        }

        result.ForeignStatics = foreignStatics.OrderBy(m => _stableKey(m), StringComparer.Ordinal).ToArray();
        result.StructMembers = structMembers.OrderBy(m => _stableKey(m), StringComparer.Ordinal).ToArray();
        result.BaseCopies = baseCopies.OrderBy(m => _stableKey(m), StringComparer.Ordinal).ToArray();
        return result;
    }

    static IEnumerable<IOperation> SelfAndDescendants(IOperation op)
    {
        yield return op;
        foreach (var c in op.ChildOps())
            foreach (var d in SelfAndDescendants(c))
                yield return d;
    }
}
