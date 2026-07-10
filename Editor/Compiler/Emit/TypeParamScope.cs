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
        // TypeParamId comparer (design 2026-07-10 symbol-intern v2, T1): per-walk fresh twins of one
        // declared type parameter ([Y8]) hash/compare onto one key, so a body-walk reference hits the
        // call-site walk's binding directly — the EmitMethod rekey block this replaces is retired.
        var dict = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(TypeParamIdComparer.Instance);
        if (baseMap != null)
            foreach (var kv in baseMap) dict[kv.Key] = kv.Value;
        foreach (var (parms, args) in bindings)
            for (int i = 0; i < parms.Count; i++)
            {
                // B70 armor: an identity binding (T→T) is a no-op for lookup but turns
                // GetUdonTypeName's resolve-then-recurse into an infinite self-reference (process-killing
                // stack overflow). Such a binding only arises from an UNCLOSED containing-type spec (the
                // bug being fixed). Never install it: skip so the key keeps its real base binding (or stays
                // unmapped) instead of becoming a self-cycle. Widened by the comparer: a FRESH TWIN of the
                // same declared parameter is equally an identity binding (it would self-cycle through the
                // twin under TypeParamId equality).
                if (args[i] is ITypeParameterSymbol argTp && TypeParamIdComparer.Instance.Equals(argTp, parms[i])) continue;
                if (newWins || !dict.ContainsKey(parms[i]))
                    dict[parms[i]] = args[i];
            }
        return dict;
    }
}
