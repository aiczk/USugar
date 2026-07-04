using System.Collections.Generic;
using Microsoft.CodeAnalysis;

// The one place a type-param map is built. Every generic-emit composition (spec install, closure
// inheritance, LF body re-key) routes through Compose, so the map escapes only as IReadOnlyDictionary
// and nothing outside this factory allocates or mutates it — immutability enforced by structure, not
// convention. A composition NEVER drops a key it was given (Y8: proving a retained key is dead is an
// unbounded obligation on fresh-per-walk type-parameter symbols); it only adds. `newWins` decides who
// keeps a colliding key: the incoming binding (true, e.g. LF re-key `old ∪ rekeyed`) or the base map
// (false, e.g. a closure inheriting its generic owner's args without clobbering its own).
public static class TypeParamScope
{
    public static IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> Compose(
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> baseMap,
        bool newWins,
        IEnumerable<(IReadOnlyList<ITypeParameterSymbol> parms, IReadOnlyList<ITypeSymbol> args)> bindings)
    {
        var dict = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        if (baseMap != null)
            foreach (var kv in baseMap) dict[kv.Key] = kv.Value;
        foreach (var (parms, args) in bindings)
            for (int i = 0; i < parms.Count; i++)
                if (newWins || !dict.ContainsKey(parms[i]))
                    dict[parms[i]] = args[i];
        return dict;
    }
}
