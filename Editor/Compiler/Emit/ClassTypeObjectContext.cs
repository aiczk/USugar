using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>CA-v2b-1 (typeobj identity + is/cast): owns the per-program type-object registry for a
/// class-emit. Each MINTED concrete user class gets one per-program object[] typeobj whose REFERENCE
/// identity distinguishes the runtime type (charter #2: hop-zero ReferenceEquals; #4: constructed-spec
/// keyed). A v1 class instance's bundle slot 0 holds its concrete type's typeobj; `o is T` enumerates
/// the typeobjs of every minted class that is T-or-derived and ReferenceEquals slot 0 against them.</summary>
public sealed class ClassTypeObjectContext
{
    readonly Dictionary<INamedTypeSymbol, string> _vars = new(SymbolEqualityComparer.Default);

    /// <summary>The minted concrete user classes in this program (constructed-spec identity). Drives
    /// both the _start typeobj allocation and the is-test enumeration set.</summary>
    public IReadOnlyCollection<INamedTypeSymbol> MintedClasses => _vars.Keys;

    public void Seed(IEnumerable<INamedTypeSymbol> mintedClasses)
    {
        foreach (var c in mintedClasses)
            if (!_vars.ContainsKey(c))
                _vars[c] = "__typeobj_" + Sanitize(c);
    }

    /// <summary>The heap-var name holding this class's typeobj, or null if the class is never minted in
    /// this program (so no instance of it can exist here — an is-test against it is vacuously false).</summary>
    public string TryGetTypeObjVar(INamedTypeSymbol classTy)
        => _vars.TryGetValue(classTy, out var v) ? v : null;

    /// <summary>The typeobj vars of every minted class whose runtime type would satisfy `is targetType`
    /// (the class IS targetType or derives from it). Compile-time closed-world enumeration (charter #2).</summary>
    public IEnumerable<string> TypeObjVarsAssignableTo(INamedTypeSymbol targetType)
    {
        foreach (var kv in _vars)
            if (IsAssignable(kv.Key, targetType)) yield return kv.Value;
    }

    /// <summary>True when some MINTED class of the same generic family as `target` was registered as an
    /// OPEN spec (its type arguments still carry a type parameter). That happens when a generic user class
    /// is constructed inside a generic method body (`new Box&lt;T&gt;()` in `M&lt;T&gt;()`): the reach walk sees
    /// the open `Box&lt;T&gt;`, so every closed spec flowing through that method shares one open typeobj and the
    /// runtime type identity is NOT spec-distinct. A runtime type test against any one closed spec then cannot
    /// be answered soundly — reject at the choke point (charter: conservative-reject over silent-wrong).</summary>
    public bool HasOpenGenericSiblingOf(INamedTypeSymbol target)
    {
        foreach (var m in _vars.Keys)
            if (SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, target.OriginalDefinition)
                && m.TypeArguments.Any(ContainsTypeParameter))
                return true;
        return false;
    }

    /// <summary>True when some minted class is an OPEN spec whose base chain passes through `target`'s
    /// generic family. The dispatch-side census matches minted classes by exact constructed symbol, so an
    /// open mint in another generic context is INVISIBLE to it — an empty/partial target set is then
    /// ambiguity, not proof of unreachability, and must reject rather than fall through (Phase-A armor,
    /// same polarity as <see cref="HasOpenGenericSiblingOf"/>).</summary>
    public bool HasOpenMintedFamilyAssignableTo(INamedTypeSymbol target)
    {
        foreach (var m in _vars.Keys)
        {
            if (!m.TypeArguments.Any(ContainsTypeParameter)) continue;
            for (var t = m; t != null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, target.OriginalDefinition))
                    return true;
        }
        return false;
    }

    /// <summary>Deep type-parameter test: `Box&lt;T&gt;` nested as `Outer&lt;Box&lt;T&gt;&gt;`'s argument is just as
    /// open as a bare `T` — the shallow `is ITypeParameterSymbol` test misses it.</summary>
    public static bool ContainsTypeParameter(ITypeSymbol t) => t switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
        INamedTypeSymbol n => n.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    static bool IsAssignable(INamedTypeSymbol concrete, INamedTypeSymbol target)
    {
        for (var t = concrete; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target)
                || SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, target.OriginalDefinition)
                   && SymbolEqualityComparer.Default.Equals(t, target))
                return true;
        return false;
    }

    static string Sanitize(INamedTypeSymbol t)
    {
        var name = t.Name;
        if (t.IsGenericType)
            name += "_" + string.Join("_", t.TypeArguments.Select(a => a.Name));
        return name;
    }
}
