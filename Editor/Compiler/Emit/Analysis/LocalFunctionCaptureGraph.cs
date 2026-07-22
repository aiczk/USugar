using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Builds the transitive capture set for every local function in a body set.</summary>
public static class LocalFunctionCaptureGraph
{
    public static Dictionary<IMethodSymbol, ImmutableArray<ISymbol>> Build(
        IEnumerable<IOperation> roots)
    {
        var captures = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var declaredInside = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var references = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var localFunctions = new List<ILocalFunctionOperation>();

        void Collect(IOperation operation)
        {
            if (operation == null) return;
            if (operation is ILocalFunctionOperation { Body: not null } local)
                localFunctions.Add(local);
            foreach (var child in operation.ChildOps()) Collect(child);
        }

        foreach (var root in roots) Collect(root);
        foreach (var local in localFunctions)
        {
            var direct = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var inside = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var refs = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            LambdaCaptureAnalyzer.AnalyzeLocalFunction(local.Symbol, local.Body, direct, inside, refs);
            captures[local.Symbol] = direct;
            declaredInside[local.Symbol] = inside;
            references[local.Symbol] = refs;
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var entry in references)
            {
                var target = captures[entry.Key];
                var inside = declaredInside[entry.Key];
                foreach (var callee in entry.Value)
                {
                    if (!captures.TryGetValue(callee, out var inherited)
                        || ReferenceEquals(inherited, target)) continue;
                    foreach (var symbol in inherited)
                        if (!inside.Contains(symbol) && target.Add(symbol)) changed = true;
                }
            }
        } while (changed);

        var result = new Dictionary<IMethodSymbol, ImmutableArray<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var entry in captures)
            if (entry.Value.Count > 0) result.Add(entry.Key, entry.Value.ToImmutableArray());
        return result;
    }
}
