using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Recursion/reentrancy analysis results for one class, populated once (before body
/// emission) by <c>UasmEmitter.BuildRecursionInfo</c> and consulted throughout emit. Each field is
/// null until BuildRecursionInfo runs; callers check for null exactly as they did when these lived
/// directly on LoweringState (grouped here purely to give the concept its own name/home — no field was
/// renamed or re-typed). WRITE-ONCE: the properties are externally read-only and set only by the
/// single <see cref="Populate"/> call at BuildRecursionInfo's tail, which throws on a second call — so
/// a future emit-restructure cannot silently mutate a frozen analysis artifact.</summary>
public class RecursionInfo
{
    /// <summary>For each internal method, the set of callees that lie in the same strongly-connected
    /// component (i.e. calls that can re-enter the caller). Calls along these edges must spill the
    /// caller's live values to the software stack, because Udon's flat heap shares param/local slots
    /// across call frames.</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> RecursiveCallees { get; private set; }

    /// <summary>Wave-9 round-8 [Y3]: per internal method, ALL same-SCC callees — the UNFILTERED twin
    /// of <see cref="RecursiveCallees"/> (which keeps only edges carrying a non-tail call, because it
    /// drives frame SPILLS). The ref/out re-chain guard must fire on every recursion-cycle edge
    /// regardless of tail position: a re-chained ref in pure RETURN position (`return M(m-1, ref w);`)
    /// is a tail call (no spill needed) yet still threads every frame's write through the ONE shared
    /// param heap var and corrupts the outer frame's copy-back (VM-proven 21021 vs CLR 9021).</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> CycleCallees { get; private set; }

    /// <summary>Round-7 follow-up [Q5]: per internal method (keyed by OriginalDefinition), the
    /// this-FIELDS the method touches — directly (field reference through an implicit/explicit
    /// this/base receiver anywhere in its body) or transitively (closed over the internal call
    /// graph, including this-property accessor edges and the synthetic dispatch edges —
    /// conservative, §8-3). A ref/out argument rooted at a this-field hands the callee an alias
    /// of storage it can also reach directly; the caller-side copy-in/copy-back convention
    /// snapshots it (callee param reads go stale, callee direct field writes are reverted by the
    /// stale copy-back — VM-proven 19 vs CLR 59). Consulted by EmitCallToMethod's ref/out-argument
    /// guard. Non-touching callees (Inc(ref field) / Swap(ref a, ref b)) stay legal.</summary>
    public Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> ThisFieldTouches { get; private set; }

    /// <summary>Syntax nodes of delegate-dispatch invocations that can re-enter their containing
    /// function: the containing function lies on a synthetic-edge-inclusive SCC cycle AND the dispatch
    /// is non-tail (design §4.2/§4.3). Keyed by the invocation's red SYNTAX node because operation trees
    /// are NOT shared between the analysis and emit walks (each GetSemanticModel call builds a fresh
    /// operation tree) while red syntax nodes ARE shared. MEMBERSHIP-ONLY — never enumerated (§1.5
    /// determinism).</summary>
    public HashSet<SyntaxNode> ReentrantDispatchSites { get; private set; }

    /// <summary>Wave-9 round-9 [Y3]: direct-call invocation sites on a RECURSIVE edge that are in
    /// TAIL position (statement-form or return-form) — the frame reads nothing after them, so
    /// EmitCallToMethod flags the instruction TailSpared and InsertRecursionSpills skips the wrap.
    /// Without this, ONE non-tail site put the callee in RecursiveCalleeNames and EVERY site of
    /// that callee spilled (per-callee gating), overflowing the 8192-entry __recurStack on deep
    /// mixed tail/non-tail recursion while the dispatch arm (per-site Reentrant marking) survived
    /// the identical shape. Keyed by red SYNTAX node like ReentrantDispatchSites (operation trees
    /// are not shared between analysis and emit walks). MEMBERSHIP-ONLY (§1.5).</summary>
    public HashSet<SyntaxNode> TailSparedDirectCallSites { get; private set; }

    /// <summary>Stage 2 M3 (§5.5, graft #2): the definition-keyed set of every function that got a
    /// recursion-graph node in BuildRecursionInfo (roots + local functions + lambda nodes). Consumed
    /// by <c>UasmEmitter.VerifyBridgeTargetsAreNodes</c> AFTER emission to assert every capturing
    /// delegate bridge target is a graph node — a capturing bridge with no node has its reentrancy
    /// protection silently missing (wave-10 [Z1] class). MEMBERSHIP-ONLY (§1.5).</summary>
    public HashSet<IMethodSymbol> RecursionGraphNodes { get; private set; }

    // Write-once assignment of all analysis artifacts, once, at BuildRecursionInfo's tail.
    public void Populate(
        Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> recursiveCallees,
        Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> cycleCallees,
        Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> thisFieldTouches,
        HashSet<SyntaxNode> reentrantDispatchSites,
        HashSet<SyntaxNode> tailSparedDirectCallSites,
        HashSet<IMethodSymbol> recursionGraphNodes)
    {
        if (RecursiveCallees != null)
            throw new InvalidOperationException("RecursionInfo is write-once and was already populated.");
        RecursiveCallees = recursiveCallees;
        CycleCallees = cycleCallees;
        ThisFieldTouches = thisFieldTouches;
        ReentrantDispatchSites = reentrantDispatchSites;
        TailSparedDirectCallSites = tailSparedDirectCallSites;
        RecursionGraphNodes = recursionGraphNodes;
    }
}
