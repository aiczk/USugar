using Microsoft.CodeAnalysis;

/// <summary>
/// Owns recursion/reentrancy analysis results and the query helpers emit code uses while lowering calls.
/// </summary>
public sealed class RecursionContext
{
    // Populated in place by UasmEmitter.BuildRecursionInfo before body emission; each product
    // field is null until then.
    public readonly RecursionInfo Info = new RecursionInfo();

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> is a
    /// recursion-cycle edge (callee in caller's non-trivial SCC, including direct self-recursion).</summary>
    public bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && Info.RecursiveCallees != null
           // Reduce BOTH ends to OriginalDefinition: RecursiveCallees is keyed by definition, but a
           // monomorphized generic specialization (e.g. Fact<int>) emits with the constructed symbol as
           // _currentMethod/target - without this its self-edge would be missed and the frame not spilled.
           && Info.RecursiveCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> lies in
    /// a recursion cycle (same non-trivial SCC or direct self-loop), tail or not ([Y3]).</summary>
    public bool IsCycleEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && Info.CycleCallees != null
           && Info.CycleCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>[Q5] True when <paramref name="callee"/>'s transitive touch set contains the
    /// this-field <paramref name="field"/> (both compared by OriginalDefinition).</summary>
    public bool CalleeTouchesThisField(IMethodSymbol callee, IFieldSymbol field)
        => callee != null && field != null && Info.ThisFieldTouches != null
           && Info.ThisFieldTouches.TryGetValue(callee.OriginalDefinition, out var set)
           && set.Contains(field.OriginalDefinition);
}
