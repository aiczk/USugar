using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Walk-stable identity for a type parameter (design 2026-07-10 symbol-intern v2, T1).
/// Roslyn freshens a LOCAL FUNCTION's type-parameter symbols per operation-tree walk ([Y8]) — raw
/// symbol identity is unusable as a map key across walks, which is what the retired EmitMethod
/// rekey block compensated for. The declaration site is walk-stable (one parse per compilation), so
/// identity = (owner declaration site, owner symbol kind, type-param kind, ordinal). Pure function
/// of the symbol — no intern table, no reverse map.</summary>
public readonly struct TypeParamId : IEquatable<TypeParamId>
{
    readonly string _ownerSite; // decl file path; metadata owners fall back to their display string
    readonly int _ownerSpan;    // decl span start; -1 for the metadata fallback
    readonly byte _ownerKind;   // SymbolKind — guards same-span symbol pairs (soundness finding 3)
    readonly byte _paramKind;   // TypeParameterKind — method vs containing-type dimension
    readonly int _ordinal;

    TypeParamId(string ownerSite, int ownerSpan, byte ownerKind, byte paramKind, int ordinal)
    {
        _ownerSite = ownerSite; _ownerSpan = ownerSpan; _ownerKind = ownerKind;
        _paramKind = paramKind; _ordinal = ordinal;
    }

    public static TypeParamId For(ITypeParameterSymbol tp)
    {
        ISymbol owner = tp.TypeParameterKind == TypeParameterKind.Method
            ? tp.DeclaringMethod : (ISymbol)tp.DeclaringType;
        // Canonicalize before taking the site: constructed→definition, reduced extension→original,
        // partial definition-part→implementation-part (soundness findings 6/7).
        owner = owner.OriginalDefinition;
        if (owner is IMethodSymbol m)
            owner = (m.ReducedFrom ?? m.PartialImplementationPart ?? m).OriginalDefinition;
        var syntaxRef = owner.DeclaringSyntaxReferences.Length > 0
            ? owner.DeclaringSyntaxReferences[0] : null;
        return syntaxRef != null
            ? new TypeParamId(syntaxRef.SyntaxTree.FilePath, syntaxRef.Span.Start,
                (byte)owner.Kind, (byte)tp.TypeParameterKind, tp.Ordinal)
            : new TypeParamId(owner.ToDisplayString(), -1,
                (byte)owner.Kind, (byte)tp.TypeParameterKind, tp.Ordinal);
    }

    public bool Equals(TypeParamId other)
        => _ownerSpan == other._ownerSpan && _ownerKind == other._ownerKind
        && _paramKind == other._paramKind && _ordinal == other._ordinal
        && string.Equals(_ownerSite, other._ownerSite, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is TypeParamId other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = _ownerSite?.GetHashCode() ?? 0;
            h = h * 31 + _ownerSpan;
            h = h * 31 + (_ownerKind << 8 | _paramKind);
            h = h * 31 + _ordinal;
            return h;
        }
    }
}

/// <summary>Adapts <see cref="TypeParamId"/> identity to the existing
/// <c>IReadOnlyDictionary&lt;ITypeParameterSymbol, ITypeSymbol&gt;</c> map shape: consumers keep
/// their signatures and raw-symbol lookups, but fresh per-walk twins hash/compare onto one key.</summary>
public sealed class TypeParamIdComparer : IEqualityComparer<ITypeParameterSymbol>
{
    public static readonly TypeParamIdComparer Instance = new();
    public bool Equals(ITypeParameterSymbol a, ITypeParameterSymbol b)
        => ReferenceEquals(a, b) || (a != null && b != null && TypeParamId.For(a).Equals(TypeParamId.For(b)));
    public int GetHashCode(ITypeParameterSymbol tp) => TypeParamId.For(tp).GetHashCode();
}
