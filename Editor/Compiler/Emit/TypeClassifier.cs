using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Centralized semantic type classification used by emit-time policy. This intentionally sits above
/// raw Udon type names: several ABI values share SystemObjectArray at runtime but have different
/// compiler semantics (class bundle, aggregate bundle, delegate bundle, env record).
/// </summary>
public readonly struct TypeClassifierContext
{
    public readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;

    public TypeClassifierContext(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
        => TypeParamMap = typeParamMap;
}

public static class TypeClassifier
{
    public static bool ContainsProgramLocalPayload(ITypeSymbol type, TypeClassifierContext ctx)
        => EmitPolicy.ContainsUserClassType(type, ctx.TypeParamMap);

    public static bool IsUserClass(ITypeSymbol type)
        => EmitPolicy.IsUserClassType(type);

    public static bool IsAggregateValue(ITypeSymbol type)
        => EmitPolicy.IsAggregateType(type);

    public static bool IsObjectArrayEmulated(ITypeSymbol type)
        => type is INamedTypeSymbol named && EmitPolicy.IsObjectArrayEmulated(named);
}
