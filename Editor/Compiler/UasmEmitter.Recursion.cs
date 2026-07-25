using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class UasmEmitter
{
    // ── Recursion-cycle analysis ──

    // Build the internal-call graph over all registered methods and mark, for each method, the callees
    // that lie in its strongly-connected component (Tarjan). A call along such an edge can re-enter the
    // caller, so the caller's live values must be spilled to the software stack around the call (Udon's
    // flat heap shares param/local slots across frames). Includes direct self-recursion (self-loops).
    //
    // First-class-delegate extension (design §4): lambdas are graph nodes too; an EscapeSet E collects
    // every function whose bridge address can be minted into a bundle (same-class method groups, local
    // functions, lambdas); every function containing a delegate dispatch gets synthetic edges m→E
    // (an indirect dispatch can start any escaped function). Cycle members' NON-TAIL dispatch sites are
    // recorded syntax-keyed in EmitContext.Recursion.ReentrantDispatchSites for the §4.3 Reentrant-flag marking;
    // tail dispatch sites are spared so bundle-driven deep tail recursion never spills (§4.4).
    void BuildRecursionInfo(CallableBodyGraph bodyGraph)
    {
        // M5b: consume the resolver-driven callable graph frozen before emission. Capture analysis
        // consumes the same bodies; this phase only derives recursion-specific facets.
        // Runs HERE (post VirtualDispatch seed at Emit's head) — the CallEdge virtual arm needs the
        // seeded instance, so the pass must not move into the Phase-1 reach walk. The committed facet
        // census fixtures (RecursionFacetEquivalenceTests, Golden/FacetCensus) are the permanent
        // oracle; the legacy private walks they were diffed against were deleted at C4 retirement.
        var facets = AssembleRecursionFacets(bodyGraph);
        // Write-once populate of every analysis artifact.
        _ctx.RecursionContext.Info.Populate(facets.Recursive, facets.CycleEdges, facets.ThisTouches,
            facets.ReentrantSites, facets.TailSparedSites, facets.Nodes);
    }

    // The walk-independent back half: escape sets, synthetic dispatch edges, the this-field-touch
    // closure, Tarjan SCC, and the per-site Reentrant/TailSpared marking — consumes the walk-level
    // products (CallableBodyGraph) and returns the six RecursionInfo facets.
    (Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> Recursive,
        Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> CycleEdges,
        Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> ThisTouches,
        HashSet<SyntaxNode> ReentrantSites,
        HashSet<SyntaxNode> TailSparedSites,
        HashSet<IMethodSymbol> Nodes)
        AssembleRecursionFacets(CallableBodyGraph w)
    {
        var allNodes = w.AllNodes;
        var bodies = w.Bodies;
        var edges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(
            SymbolEqualityComparer.Default);
        foreach (var pair in w.Edges)
            edges.Add(pair.Key,
                new HashSet<IMethodSymbol>(pair.Value, SymbolEqualityComparer.Default));
        var thisTouches = new Dictionary<IMethodSymbol, HashSet<IFieldSymbol>>(
            SymbolEqualityComparer.Default);
        foreach (var pair in w.DirectThisTouches)
            thisTouches.Add(pair.Key,
                new HashSet<IFieldSymbol>(pair.Value, SymbolEqualityComparer.Default));
        var accessorEdges = w.AccessorEdges;

        // (b) EscapeSet E (§4.1 + §5.4 widening): conservative approximation of every function whose
        // bridge address can end up inside a dispatched bundle. Two sources:
        //   1. Same-class delegate-creation targets (method groups incl. local functions, and lambdas)
        //      — the walk's escape facet (C3: collected per-op by the resolver's EscapeTarget arm
        //      during the one pass, membership-filtered walk-side).
        //   2. §5.4 widening — every BRIDGE-BEARING method. A bundle can be minted in ANOTHER program
        //      (foreign-wired self-callback, fcd47 form; or SetProgramVariable-delivered) whose creation
        //      site is invisible to the walk's escape collection, yet dispatched here re-enters THIS
        //      program's method. The planner emits a speculative bridge per non-event user method, and
        //      each such method is already a graph node (a root), so it is an escape target too. The
        //      resulting SCC growth is contained by the sig-filter on the synthetic edges (c): a typed
        //      dispatch can only enter a bridge of the SAME signature. Variance (Stage 1.75 §2.2) keeps
        //      this sound WITHOUT rejecting the binding: a variant method-group target is escaped under
        //      its ADAPTER's protocol sig (sig-S), not its own — see variantEscapeSigs below (was
        //      previously "sound only while variance is rejected," the tracked coupling pin
        //      SigFilterCoupledToVarianceReject; that pin now asserts the widened-not-rejected form).
        // MEMBERSHIP-ONLY (§1.5): never drives emission order.
        var escape = new HashSet<IMethodSymbol>(w.EscapedTargets, SymbolEqualityComparer.Default);
        foreach (var m in _planner.GetLayout(_classSymbol).DelegateBridges.Keys)
        {
            var def = m.OriginalDefinition;
            if (bodies.ContainsKey(def)) escape.Add(def);
        }

        // Variance design (2026-07-04 §2.2): a target reached ONLY via a sig adapter is escaped under
        // the adapter's protocol sig (sig-S), which can differ from the target's OWN sig — collected
        // separately since a single target may be BOTH an exact-sig escape target (elsewhere) AND a
        // variant one (multiple entries per method, hence a list rather than the single-valued dict
        // below used for the exact-sig case).
        var variantEscapeSigs = w.VariantEscapeSigs;

        // (c) Synthetic SIG-FILTERED edges m→{e ∈ E : sig(e) == sig(one of m's dispatches)}: an indirect
        // dispatch of a delegate type T can only start an escaped function whose signature matches T's
        // Invoke method (§5.4). Real call edges are unchanged; the RecursiveCallees filter below
        // self-filters synthetic edges (no named call to match), so they create cycle membership —
        // consumed by the per-site Reentrant marking — without ever creating named-call spills. Signature
        // matching (SigsMatch) uses the concrete definition-level BuildSigPart, with a wildcard escape
        // hatch for type-param-involving signatures (see the escapeSig comment below).
        //
        // sig(e) = concrete open-definition BuildSigPart, or WILDCARD (null) when the signature
        // involves a type parameter. At analysis time there is no type-param map, so a generic escape
        // target (e.g. an inherited `FreeG<T>` dispatched as a monomorphized spec) and a concrete
        // dispatch (`Func<int,int>`) cannot be reliably matched by string — the OPEN sig of a generic
        // never equals the CONCRETE dispatch sig. Treating either side as wildcard when it involves a
        // type param restores the pre-widening connect-all behaviour for generic-involved dispatches
        // (sound: conservative), while keeping the exact sig-filter for the concrete common case
        // (contains the §5.4 widening). SigsMatch: equal, or either is wildcard. A method may appear
        // MULTIPLE times (once under its own exact sig, again under each sig-S it's variant-adapted to)
        // — hence a list, not a single-valued dict (Stage 1.75 §2.2).
        var escapeSig = new List<(IMethodSymbol Method, string Sig)>();
        foreach (var e in escape)
            if (edges.ContainsKey(e)) escapeSig.Add((e, DispatchSigOrWildcard(e)));
        foreach (var (vm, vSig) in variantEscapeSigs)
            if (edges.ContainsKey(vm)) escapeSig.Add((vm, vSig));
        // Wave-12 [V1]: sites whose bundle provenance is exact (see TryResolvePreciseDispatchTargets)
        // contribute edges to their KNOWN targets only, instead of sig-matching against the whole
        // widened escape set; every other site keeps the §5.4 blanket treatment. Keyed by operation
        // reference — the reentrant-marking loop below re-collects sites from the same shared bodies.
        var preciseDispatchTargets = new Dictionary<IOperation, HashSet<IMethodSymbol>>();
        foreach (var node in allNodes)
        {
            if (!bodies.TryGetValue(node, out var nodeBody) || nodeBody == null) continue;
            var dispatchSites = new List<IOperation>();
            CollectDelegateDispatchSites(nodeBody, dispatchSites);
            if (dispatchSites.Count == 0) continue;
            var nodeSigs = new List<string>();
            var nodeEdges = edges[node];
            foreach (var site in dispatchSites)
            {
                if (site is not IInvocationOperation dinv || dinv.TargetMethod == null) continue;
                if (TryResolvePreciseDispatchTargets(nodeBody, dinv, out var preciseTargets))
                {
                    preciseDispatchTargets[site] = preciseTargets;
                    foreach (var t in preciseTargets)
                        if (edges.ContainsKey(t)) nodeEdges.Add(t);
                }
                else
                    nodeSigs.Add(DispatchSigOrWildcard(dinv.TargetMethod));
            }
            if (nodeSigs.Count == 0) continue;
            foreach (var (escMethod, escSig) in escapeSig)
                foreach (var ds in nodeSigs)
                    if (SigsMatch(ds, escSig)) { nodeEdges.Add(escMethod); break; }
        }

        // Round-7 follow-up [Q5]: close the per-node DIRECT this-field touch sets (collected by the
        // walk — this-property references add accessor edges: a callee reading a manual property whose
        // getter touches the field is the same alias one hop deeper) transitively for the ref/out-
        // argument alias guard (see EmitContext.Recursion.ThisFieldTouches). The closure runs over the
        // same `edges` graph — synthetic dispatch edges included, conservative per §8-3.
        bool touchChanged = true;
        while (touchChanged)
        {
            touchChanged = false;
            foreach (var node in allNodes)
            {
                var mySet = thisTouches[node];
                foreach (var callee in edges[node])
                    if (thisTouches.TryGetValue(callee, out var calleeSet)
                        && !ReferenceEquals(calleeSet, mySet))
                        foreach (var f in calleeSet)
                            if (mySet.Add(f)) touchChanged = true;
                foreach (var callee in accessorEdges[node])
                    if (thisTouches.TryGetValue(callee, out var accSet)
                        && !ReferenceEquals(accSet, mySet))
                        foreach (var f in accSet)
                            if (mySet.Add(f)) touchChanged = true;
            }
        }
        var recursive = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var cycleEdges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var reentrantSites = new HashSet<SyntaxNode>();
        var tailSparedSites = new HashSet<SyntaxNode>();

        // Wave-9 round-9 [Y4]: forward reachability from each escaped function (over the same
        // `edges` graph, synthetic edges included), memoized across SCCs. A dispatch can only
        // START an escaped function, so it can only RE-ENTER its containing function when some
        // escaped function reaches that function's SCC.
        var escapeReach = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        HashSet<IMethodSymbol> ReachFrom(IMethodSymbol e)
        {
            if (escapeReach.TryGetValue(e, out var cached)) return cached;
            var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { e };
            var work = new Stack<IMethodSymbol>();
            work.Push(e);
            while (work.Count > 0)
            {
                var cur = work.Pop();
                if (!edges.TryGetValue(cur, out var succ)) continue;
                foreach (var s in succ)
                    if (seen.Add(s)) work.Push(s);
            }
            escapeReach[e] = seen;
            return seen;
        }

        foreach (var scc in TarjanScc(allNodes, edges))
        {
            var sccSet = new HashSet<IMethodSymbol>(scc, SymbolEqualityComparer.Default);
            // Non-trivial SCC (mutual cycle) OR a single method with a self-loop (direct self-recursion).
            bool isCycle = scc.Count > 1 || (scc.Count == 1 && edges[scc[0]].Contains(scc[0]));
            if (!isCycle) continue;
            // Wave-9 round-9 [Y4]: gate the per-site Reentrant marking on actual re-enterability.
            // When NO escaped function reaches this SCC, a dispatch inside it can never re-enter the
            // caller — and the spurious spill/reload (VM-proven) DISCARDED a same-environment write
            // made by a dispatched non-cycle closure: the lambda's write to its captured cell never
            // reached the declarer's post-dispatch read (acc=1 where the CLR gives 6 at depth 1).
            // Direct-call spills (RecursiveCallees) are real edges and stay ungated.
            // §5.4 sig-filter applied to the reachability gate (NOT only the synthetic edges): the set
            // of delegate signatures that can re-enter this SCC — an escaped function of signature S
            // reaches the SCC. A dispatch site is Reentrant only when ITS OWN signature is in this set.
            // Widening E (§5.4) can add a same-SCC method of a DIFFERENT signature (e.g. `M(int)` reaches
            // its own SCC); without the per-site sig gate that would spuriously mark a Void→Void `bump()`
            // dispatch in M reentrant even though a bundle of M's signature can never flow to it. Variant
            // targets contribute their ADAPTER's sig-S here (via escapeSig's variantEscapeSigs entries),
            // not their own — SigFilterCoupledToVarianceReject now pins the widened-not-rejected form.
            var reenterSigs = new List<string>();   // may hold WILDCARD (null) entries
            foreach (var (escMethod, escSig) in escapeSig)
                if (ReachFrom(escMethod).Overlaps(sccSet)) reenterSigs.Add(escSig);
            bool sccReenterable = reenterSigs.Count > 0;
            foreach (var caller in scc)
            {
                bodies.TryGetValue(caller, out var callerBody);
                // Wave-9 round-8 [Y3]: the UNFILTERED in-SCC edge set feeds the ref/out re-chain
                // guard (IsCycleEdge) — a tail `return M(m-1, ref w);` re-chain corrupts exactly
                // like the non-tail statement form, so the guard must not ride the tail filter.
                var allInScc = new HashSet<IMethodSymbol>(
                    edges[caller].Where(c => sccSet.Contains(c)), SymbolEqualityComparer.Default);
                if (allInScc.Count > 0) cycleEdges[caller] = allInScc;
                // Only edges with a NON-tail call need spilling: a tail call (`return Callee(..)`) reads
                // nothing after the call, so flat-heap clobbering is harmless — and spilling deep tail
                // recursion would needlessly exhaust the stack. (A constructor's `: base(...)` initializer is
                // always non-tail; TailCallAnalysis walks the ctor initializer so it is seen here.)
                var inScc = new HashSet<IMethodSymbol>(
                    edges[caller].Where(c => sccSet.Contains(c) && HasNonTailCallTo(callerBody, c)),
                    SymbolEqualityComparer.Default);
                if (inScc.Count > 0) recursive[caller] = inScc;

                if (callerBody == null) continue;

                // Wave-9 round-9 [Y3]: per-SITE tail classification for DIRECT calls on the
                // recursive (non-tail-carrying) edges above. The spill map gates per callee NAME,
                // so a callee with ONE non-tail site used to spill at EVERY site — tail sites of a
                // mixed tail/non-tail callee are recorded here (syntax-keyed, exactly like the
                // dispatch arm's per-site marking) and EmitCallToMethod flags them TailSpared.
                if (inScc.Count > 0)
                {
                    var directSites = new List<IOperation>();
                    CollectInvocationSites(callerBody, directSites);
                    foreach (var site in directSites)
                    {
                        if (site.Syntax == null) continue;
                        bool toRecursiveCallee = false;
                        foreach (var c in inScc)
                            if (EdgeResolver.IsInternalCallTo(site, c, out var matched) && ReferenceEquals(matched, site))
                            { toRecursiveCallee = true; break; }
                        if (toRecursiveCallee && !EmitPolicy.IsNonTailDispatchSite(callerBody, site))
                            tailSparedSites.Add(site.Syntax);
                    }
                }

                // §4.3: per-site Reentrant marking — a NON-TAIL dispatch inside a cycle member can
                // re-enter its containing function via any escaped function that reaches this SCC
                // (round-9 [Y4]: unreachable SCCs skip the marking entirely, see sccReenterable).
                // Keyed by red syntax node (shared across semantic models); tail sites are spared (§4.4).
                if (sccReenterable)
                {
                    var dispatchSites = new List<IOperation>();
                    CollectDelegateDispatchSites(callerBody, dispatchSites);
                    foreach (var site in dispatchSites)
                        if (site.Syntax != null && EmitPolicy.IsNonTailDispatchSite(callerBody, site)
                            && site is IInvocationOperation dsInv && dsInv.TargetMethod != null)
                        {
                            // Wave-12 [V1]: a provenance-exact site is Reentrant only when one of
                            // ITS OWN possible callees reaches this SCC (through that callee's full
                            // edge set, blanket edges included — a captured-field dispatch inside
                            // the callee still re-enters and still spills, FP5B4 form). The sig
                            // match against the whole widened escape set stays for every site whose
                            // bundle can be foreign-minted.
                            if (preciseDispatchTargets.TryGetValue(site, out var preciseTargets))
                            {
                                foreach (var t in preciseTargets)
                                    if (ReachFrom(t).Overlaps(sccSet)) { reentrantSites.Add(site.Syntax); break; }
                                continue;
                            }
                            var dsSig = DispatchSigOrWildcard(dsInv.TargetMethod);
                            foreach (var rs in reenterSigs)
                                if (SigsMatch(rs, dsSig)) { reentrantSites.Add(site.Syntax); break; }
                        }
                }

            }
        }
        // §5.5 (graft #2): RecursionGraphNodes is the definition-keyed graph-node set
        // (bodies.Keys = roots, local functions, lambdas) the post-emission armor reads.
        return (recursive, cycleEdges, thisTouches, reentrantSites, tailSparedSites,
            new HashSet<IMethodSymbol>(bodies.Keys, SymbolEqualityComparer.Default));
    }

    // §5.5 (graft #2): VerifyBridgeTargetsAreNodes — the wave-10 [Z1]-class emit-time-registration
    // hole detector. A CAPTURING delegate bridge carries an env and MUST have its frame protected
    // across reentrant dispatch; that protection is driven by its recursion-graph node (BuildRecursionInfo
    // above). Synthetic callables join one registry during body/synthetic emission, so this runs after
    // every bridge emitter, when the complete target set exists. A capturing target with no node means a
    // future registration path escaped the reentrancy analysis: fail loud at compile time, never emit
    // silently-unprotected. Non-capturing bridges (named methods, capture-free lambdas) carry no env and
    // are intentionally skipped — they have no reentrancy-sensitive frame state to lose.
    // The callable-body graph cannot discharge this check: synthetic targets are registered later.
    void VerifyBridgeTargetsAreNodes()
    {
        if (_ctx.Closures.CaptureScope == null || _ctx.RecursionContext.Info.RecursionGraphNodes == null) return;
        foreach (var callable in _ctx.Methods.SyntheticCallables.Values)
        {
            var def = callable.TargetDefinition;
            if (def == null) continue;
            if (!_ctx.Closures.CaptureScope.IsCapturingClosure(def)) continue;
            if (!_ctx.RecursionContext.Info.RecursionGraphNodes.Contains(def))
                throw new InvalidOperationException(
                    $"USugar internal error (§5.5 bridge-target armor): capturing delegate bridge "
                  + $"'{callable.Name}' targets '{def}', which has no recursion-graph node — its "
                  + "reentrancy spill protection would be silently missing. A registration path added a "
                  + "capturing bridge without seeding the recursion analysis (wave-10 [Z1] class).");
        }
    }

    // §5.4 sig-filter helpers. The delegate signature key is BuildSigPart, but only reliable when the
    // signature is CONCRETE. When it involves a type parameter (own generic method, or a param/return
    // referencing an enclosing generic's T), it has no analysis-time concrete form — return WILDCARD
    // (null) so it conservatively matches every dispatch (pre-widening connect-all for generics).
    static string DispatchSigOrWildcard(IMethodSymbol m)
        => SigInvolvesTypeParam(m) ? null : DelegateAbi.BuildSigPart(m);

    // Two signatures match if equal, or either is WILDCARD (a type-param-involving sig matches anything).
    static bool SigsMatch(string a, string b) => a == null || b == null || a == b;

    static bool SigInvolvesTypeParam(IMethodSymbol m)
    {
        if (m.IsGenericMethod) return true;
        static bool Has(ITypeSymbol t) => t switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol a => Has(a.ElementType),
            INamedTypeSymbol n => n.IsGenericType && n.TypeArguments.Any(Has),
            _ => false,
        };
        if (Has(m.ReturnType)) return true;
        foreach (var p in m.Parameters)
            if (Has(p.Type)) return true;
        return false;
    }

    // ── Wave-12 [V1]: per-site dispatch-target provenance ──
    // A dispatch that reads a LOCAL whose every write (declaration initializer / simple assignment,
    // anywhere in the body tree, nested closures included) is a delegate CREATION has a provably
    // exact callee set: locals are not foreign-writable through the sanctioned surface
    // (SetProgramVariable targets symbols by name only via the documented accepted-risk raw boundary,
    // ref locals and delegate-typed ref/out params are rejected), so the bundle can only be one the
    // scanned creations minted. The §5.4 same-signature widening — sound and required for
    // foreign-writable storage (fields, params, elements, foreign receivers) — over-approximated
    // these sites too: every same-sig bridge-bearing method joined the callee set, so a per-frame
    // closure-helper dispatch inside a recursion cycle was marked Reentrant and spilled the whole
    // frame at EVERY iteration's dispatch, overflowing the 512-entry __recurStack ~20% earlier than
    // the equivalent plain-call recursion (VM-proven VmFault at 102 frames on legal code, ErD_D100).
    // Precise iff: the dispatch instance (conversions unwrapped) is a local reference; the local's
    // DECLARATOR is inside this node's own body (a local declared in an enclosing method and
    // dispatched inside a hoisted closure keeps the blanket treatment — its defs live outside this
    // tree); no ref/out use, no compound/increment/deconstruction target anywhere; at least one
    // write exists; and every write's RHS resolves to a delegate creation (or null). Targets come from
    // the resolver's own EscapeTargetsOf — a precise site skips the blanket sig match, so re-deriving
    // the mapping here would make a missing arm a missing Reentrant mark.
    bool TryResolvePreciseDispatchTargets(IOperation callerBody, IInvocationOperation site,
        out HashSet<IMethodSymbol> targets)
    {
        targets = null;
        var instance = site.Instance;
        while (instance is IConversionOperation conv) instance = conv.Operand;
        if (instance is not ILocalReferenceOperation locRef || locRef.Local is not { } local)
            return false;

        var found = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        bool declFound = false, poisoned = false; int writeCount = 0;

        bool RhsIsCreation(IOperation rhs)
        {
            while (rhs is IConversionOperation c) rhs = c.Operand;
            switch (rhs)
            {
                case IDelegateCreationOperation dc:
                    return RhsIsCreation(dc.Target);
                case IAnonymousFunctionOperation af when af.Symbol != null:
                    found.Add(af.Symbol);
                    return true;
                case IMethodReferenceOperation mr when mr.Method != null:
                    foreach (var escapeTarget in EdgeResolver.EscapeTargetsOf(mr))
                        found.Add(escapeTarget);
                    return true;
                default:
                    // null / default contribute no callee; anything else breaks provenance.
                    return rhs.ConstantValue is { HasValue: true, Value: null }
                        || rhs is IDefaultValueOperation;
            }
        }

        void Walk(IOperation op)
        {
            if (op == null || poisoned) return;
            switch (op)
            {
                case IVariableDeclaratorOperation vd when SymbolEqualityComparer.Default.Equals(vd.Symbol, local):
                    declFound = true;
                    if (vd.Initializer?.Value is { } init)
                    {
                        writeCount++;
                        if (!RhsIsCreation(init)) { poisoned = true; return; }
                    }
                    break;
                case ISimpleAssignmentOperation sa
                    when sa.Target is ILocalReferenceOperation t && SymbolEqualityComparer.Default.Equals(t.Local, local):
                    writeCount++;
                    if (!RhsIsCreation(sa.Value)) { poisoned = true; return; }
                    Walk(sa.Value); // still scan the RHS subtree (a creation may nest another write)
                    return;
                case ISimpleAssignmentOperation sa2 when SubtreeReferencesLocal(sa2.Target):
                case IDeconstructionAssignmentOperation da when SubtreeReferencesLocal(da.Target):
                case ICompoundAssignmentOperation ca when SubtreeReferencesLocal(ca.Target):
                case IIncrementOrDecrementOperation io when SubtreeReferencesLocal(io.Target):
                case IArgumentOperation { Parameter: { RefKind: not RefKind.None } } arg when SubtreeReferencesLocal(arg.Value):
                    poisoned = true;
                    return;
            }
            foreach (var child in op.ChildOps())
                Walk(child);
        }

        bool SubtreeReferencesLocal(IOperation op)
        {
            if (op == null) return false;
            if (op is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local))
                return true;
            foreach (var child in op.ChildOps())
                if (SubtreeReferencesLocal(child)) return true;
            return false;
        }

        Walk(callerBody);
        if (poisoned || !declFound || writeCount == 0)
            return false;
        targets = found;
        return true;
    }

    // Collect the delegate-dispatch invocations attributed to THIS function (hoisted children skipped).
    static void CollectDelegateDispatchSites(IOperation op, List<IOperation> result)
    {
        if (op == null) return;
        if (EmitPolicy.IsDelegateDispatch(op)) result.Add(op);
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            CollectDelegateDispatchSites(child, result);
        }
    }

    // Wave-9 round-9 [Y3]: collect every invocation operation attributed to THIS function
    // (hoisted children skipped — same attribution rule as the dispatch-site collector above).
    static void CollectInvocationSites(IOperation op, List<IOperation> result)
    {
        if (op == null) return;
        if (op is IInvocationOperation) result.Add(op);
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            CollectInvocationSites(child, result);
        }
    }

    // True if the caller body contains a call to callee that is NOT in tail position (its result is used
    // by something after the call, so the caller's live values would be clobbered by a recursive re-entry).
    // The walk itself lives in TailCallAnalysis (shared with EmitPolicy.IsNonTailDispatchSite); this is
    // the named-callee matcher's parameterization of it — the matchers are the resolver's classifier
    // surface (C4), and `checkReturnInstanceLeg: true` / `ternaryPreciseReturn: false` reproduce this
    // classifier's own return-position behavior exactly (see TailCallAnalysis's file header for what
    // those two differences from the dispatch-site classifier actually are).
    bool HasNonTailCallTo(IOperation op, IMethodSymbol callee)
        => TailCallAnalysis.HasNonTailCall(op,
            (IOperation o, out IOperation matched) => EdgeResolver.IsInternalCallTo(o, callee, out matched),
            (pr, getter) => EdgeResolver.PropertyAccessorMatches(pr, callee, getter),
            checkReturnInstanceLeg: true,
            ternaryPreciseReturn: false);

    // Tarjan's strongly-connected-components algorithm (iterative, to avoid deep recursion on large graphs).
    static List<List<IMethodSymbol>> TarjanScc(IMethodSymbol[] nodes, Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> edges)
    {
        var index = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var lowlink = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var onStack = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var stack = new Stack<IMethodSymbol>();
        var sccs = new List<List<IMethodSymbol>>();
        int counter = 0;

        foreach (var start in nodes)
        {
            if (index.ContainsKey(start)) continue;
            // Iterative DFS: frame = (node, enumerator over its successors)
            var work = new Stack<(IMethodSymbol node, IEnumerator<IMethodSymbol> succ)>();
            index[start] = lowlink[start] = counter++;
            stack.Push(start); onStack.Add(start);
            work.Push((start, edges[start].GetEnumerator()));
            while (work.Count > 0)
            {
                var (node, succ) = work.Peek();
                bool descended = false;
                while (succ.MoveNext())
                {
                    var w = succ.Current;
                    if (!index.ContainsKey(w))
                    {
                        index[w] = lowlink[w] = counter++;
                        stack.Push(w); onStack.Add(w);
                        work.Push((w, edges[w].GetEnumerator()));
                        descended = true;
                        break;
                    }
                    if (onStack.Contains(w))
                        lowlink[node] = Math.Min(lowlink[node], index[w]);
                }
                if (descended) continue;
                // All successors processed: node is done.
                work.Pop();
                if (work.Count > 0)
                {
                    var parent = work.Peek().node;
                    lowlink[parent] = Math.Min(lowlink[parent], lowlink[node]);
                }
                if (lowlink[node] == index[node])
                {
                    var comp = new List<IMethodSymbol>();
                    IMethodSymbol w;
                    do { w = stack.Pop(); onStack.Remove(w); comp.Add(w); }
                    while (!SymbolEqualityComparer.Default.Equals(w, node));
                    sccs.Add(comp);
                }
            }
        }
        return sccs;
    }

    void VerifyRegisteredCallablesAreNodes(CallableBodyGraph graph)
    {
        foreach (var callable in _ctx.Methods.Callables.Values.Concat<MethodContext.RegisteredCallable>(
                     _ctx.Methods.ClosureSpecs))
            if (!graph.CallableDefinitions.Contains(callable.Definition.OriginalDefinition))
                throw new InvalidOperationException(
                    $"USugar internal error: callable '{callable.Definition}' was registered during "
                    + "emission but was absent from the pre-emission callable body graph.");
    }
}
