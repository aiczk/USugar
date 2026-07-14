using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>CA rewrite (M5a): the reach facet of the unified, resolver-driven worklist — the eventual
/// replacement for UasmEmitter.BuildReachableBodies. It walks each reachable body's ops through the single
/// <see cref="ResolvedEdgeResolver"/> and buckets the yielded reach targets by role, running one queue+visited
/// fixpoint keyed by OriginalDefinition. Built ALONGSIDE the legacy collector in M5a and diffed against it
/// (def granularity) to measure the facet gap before any cutover — nothing consumes it yet.
///
/// Deliberately NOT covered yet (measured gaps, closed before the swap): the SS2A supplementary
/// generic-foreign-static fixpoint (<see cref="ReachableBodies.GenericForeignStaticBodies"/>) and the
/// open-base-generic recursion roots (_openGenericBaseDefs). Per-spec (constructed generic-struct members):
/// this worklist is def-keyed, so its StructMembers carry OriginalDefinitions where the legacy set carries
/// constructed specs — equal under OriginalDefinition projection, reconciled at the per-spec (M6) layer.</summary>
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
        var openBaseGenericDefs = new HashSet<IMethodSymbol>(cmp);  // main-fixpoint roots (open base generics)
        var suppCaptureDefs = new HashSet<IMethodSymbol>(cmp);      // SS2A: dropped generic foreign statics
        var visited = new HashSet<IMethodSymbol>(cmp);
        var queue = new Queue<IMethodSymbol>();

        void Walk(IOperation body)
        {
            if (body == null) return;
            foreach (var op in SelfAndDescendants(body))
            {
                foreach (var t in _resolver.ResolveEdges(op))
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
                foreach (var d in _resolver.ResolveOpenBaseGenericDefs(op)) openBaseGenericDefs.Add(d);
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
            foreach (var m in openBaseGenericDefs) TryEnqueue(m);
            foreach (var m in result.StructMemberDefs) TryEnqueue(m);
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
