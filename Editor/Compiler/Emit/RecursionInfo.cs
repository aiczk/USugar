using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Immutable recursion and reentrancy analysis for one bound program.</summary>
public sealed class RecursionInfo
{
    public IReadOnlyDictionary<IMethodSymbol, FrozenSet<IMethodSymbol>>
        RecursiveCallees { get; }
    public IReadOnlyDictionary<IMethodSymbol, FrozenSet<IMethodSymbol>>
        CycleCallees { get; }
    public IReadOnlyDictionary<IMethodSymbol, FrozenSet<IFieldSymbol>>
        ThisFieldTouches { get; }
    public FrozenSet<SyntaxNode> ReentrantDispatchSites { get; }
    public FrozenSet<SyntaxNode> TailSparedDirectCallSites { get; }
    public FrozenSet<IMethodSymbol> RecursionGraphNodes { get; }

    public RecursionInfo(
        IReadOnlyDictionary<IMethodSymbol, HashSet<IMethodSymbol>> recursiveCallees,
        IReadOnlyDictionary<IMethodSymbol, HashSet<IMethodSymbol>> cycleCallees,
        IReadOnlyDictionary<IMethodSymbol, HashSet<IFieldSymbol>> thisFieldTouches,
        IEnumerable<SyntaxNode> reentrantDispatchSites,
        IEnumerable<SyntaxNode> tailSparedDirectCallSites,
        IEnumerable<IMethodSymbol> recursionGraphNodes)
    {
        RecursiveCallees = CopyMethodMap(recursiveCallees);
        CycleCallees = CopyMethodMap(cycleCallees);
        ThisFieldTouches = CopyFieldMap(thisFieldTouches);
        ReentrantDispatchSites = new FrozenSet<SyntaxNode>(reentrantDispatchSites);
        TailSparedDirectCallSites = new FrozenSet<SyntaxNode>(tailSparedDirectCallSites);
        RecursionGraphNodes = new FrozenSet<IMethodSymbol>(
            recursionGraphNodes, SymbolEqualityComparer.Default);
    }

    public bool IsRecursiveEdge(
        IMethodSymbol caller,
        IMethodSymbol callee)
        => caller != null
           && callee != null
           && RecursiveCallees.TryGetValue(
               caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    public bool IsCycleEdge(
        IMethodSymbol caller,
        IMethodSymbol callee)
        => caller != null
           && callee != null
           && CycleCallees.TryGetValue(
               caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    public bool CalleeTouchesThisField(
        IMethodSymbol callee,
        IFieldSymbol field)
        => callee != null
           && field != null
           && ThisFieldTouches.TryGetValue(
               callee.OriginalDefinition, out var fields)
           && fields.Contains(field.OriginalDefinition);

    static IReadOnlyDictionary<IMethodSymbol, FrozenSet<IMethodSymbol>> CopyMethodMap(
        IReadOnlyDictionary<IMethodSymbol, HashSet<IMethodSymbol>> source)
    {
        var copy = new Dictionary<IMethodSymbol, FrozenSet<IMethodSymbol>>(
            SymbolEqualityComparer.Default);
        foreach (var pair in source)
            copy.Add(pair.Key, new FrozenSet<IMethodSymbol>(
                pair.Value, SymbolEqualityComparer.Default));
        return new ReadOnlyDictionary<IMethodSymbol, FrozenSet<IMethodSymbol>>(copy);
    }

    static IReadOnlyDictionary<IMethodSymbol, FrozenSet<IFieldSymbol>> CopyFieldMap(
        IReadOnlyDictionary<IMethodSymbol, HashSet<IFieldSymbol>> source)
    {
        var copy = new Dictionary<IMethodSymbol, FrozenSet<IFieldSymbol>>(
            SymbolEqualityComparer.Default);
        foreach (var pair in source)
            copy.Add(pair.Key, new FrozenSet<IFieldSymbol>(
                pair.Value, SymbolEqualityComparer.Default));
        return new ReadOnlyDictionary<IMethodSymbol, FrozenSet<IFieldSymbol>>(copy);
    }
}

public sealed class FrozenSet<T> : IReadOnlyCollection<T>
{
    readonly HashSet<T> _items;

    public FrozenSet(IEnumerable<T> items, IEqualityComparer<T> comparer = null)
        => _items = new HashSet<T>(
            items ?? throw new ArgumentNullException(nameof(items)),
            comparer ?? EqualityComparer<T>.Default);

    public int Count => _items.Count;
    public bool Contains(T item) => _items.Contains(item);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}
