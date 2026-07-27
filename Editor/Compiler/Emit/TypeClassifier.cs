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

public enum RuntimeBundleKind
{
    None,
    Class,
    Aggregate,
    Delegate,
}

[System.Flags]
public enum TransportCapabilities
{
    None = 0,
    TypedProgramChannel = 1 << 0,
    ExternCall = 1 << 1,
}

[System.Flags]
public enum TypeContents
{
    None = 0,
    UserClass = 1 << 0,
    Delegate = 1 << 1,
    OpaqueObject = 1 << 2,
}

/// <summary>
/// Authoritative compiler-side description of a source type. Several source types erase to
/// SystemObjectArray, so storage, boundary, capture, and lowering policy consume these facts rather
/// than independently walking the symbol or rediscovering meaning from the emitted Udon type name.
/// </summary>
public readonly struct RuntimeShape
{
    public readonly RuntimeBundleKind Bundle;
    public readonly TransportCapabilities Transport;
    public readonly TypeContents Contents;

    internal RuntimeShape(RuntimeBundleKind bundle, TransportCapabilities transport,
        TypeContents contents)
    {
        Bundle = bundle;
        Transport = transport;
        Contents = contents;
    }

    public bool IsBundle => Bundle != RuntimeBundleKind.None;
    public bool ContainsUserClassPayload => Contains(TypeContents.UserClass);
    public bool ContainsDelegate => Contains(TypeContents.Delegate);
    public bool ContainsOpaqueObject => Contains(TypeContents.OpaqueObject);
    public bool Contains(TypeContents contents) => (Contents & contents) == contents;
    public bool Supports(TransportCapabilities capability) => (Transport & capability) == capability;
}

public static class TypeClassifier
{
    public static RuntimeShape ShapeOf(ITypeSymbol type, TypeClassifierContext ctx)
    {
        type = Resolve(type, ctx);
        RequireSupportedArrayRank(type);
        var contents = ContentsOf(type, ctx.TypeParamMap,
            new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
        var bundle = IsUserClassLeaf(type)
            ? RuntimeBundleKind.Class
            : type is INamedTypeSymbol { DelegateInvokeMethod: not null }
                    ? RuntimeBundleKind.Delegate
                    : IsAggregateValueLeaf(type)
                        ? RuntimeBundleKind.Aggregate
                        : RuntimeBundleKind.None;
        var containsUserClass = (contents & TypeContents.UserClass) != 0;
        var transport = bundle switch
        {
            RuntimeBundleKind.Class
                or RuntimeBundleKind.Aggregate
                => TransportCapabilities.TypedProgramChannel,
            RuntimeBundleKind.Delegate
                => containsUserClass
                    ? TransportCapabilities.None
                    : TransportCapabilities.TypedProgramChannel,
            _ => containsUserClass
                ? TransportCapabilities.ExternCall
                : TransportCapabilities.TypedProgramChannel | TransportCapabilities.ExternCall,
        };
        return new RuntimeShape(bundle, transport, contents);
    }

    public static bool ContainsUserClassPayload(ITypeSymbol type, TypeClassifierContext ctx)
        => ShapeOf(type, ctx).ContainsUserClassPayload;

    public static void RequireSupportedArrayRank(ITypeSymbol type)
    {
        if (type is not IArrayTypeSymbol { Rank: > 1 } array)
            return;
        throw new System.NotSupportedException(
            $"Multidimensional array '{array.ToDisplayString()}' is not supported: "
            + "Udon has no native rank-greater-than-one array representation. "
            + "Use a one-dimensional or jagged array.");
    }

    public static bool IsUserClass(ITypeSymbol type)
        => ShapeOf(type, new TypeClassifierContext(null)).Bundle == RuntimeBundleKind.Class;

    internal static bool IsUserClassLeaf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.IsAnonymousType
            || !ExternResolver.IsPlainUserClass(named))
            return false;
        if (named.BaseType != null && named.BaseType.SpecialType != SpecialType.System_Object)
        {
            var baseType = named.BaseType;
            if (!ExternResolver.IsPlainUserClass(baseType)
                || !IsUserClassLeaf(baseType)) return false;
        }
        return true;
    }

    public static bool IsAggregateValue(ITypeSymbol type)
        => ShapeOf(type, new TypeClassifierContext(null)).Bundle == RuntimeBundleKind.Aggregate;

    internal static bool IsAggregateValueLeaf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Delegate) return false;
        return named.IsTupleType || named.IsAnonymousType || IsUserStruct(named);
    }

    public static bool IsUserStruct(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return false;
        if (type.DeclaringSyntaxReferences.Length == 0) return false;
        return !ExternResolver.IsSdkNamespace(type.ContainingNamespace);
    }

    public static bool IsObjectArrayEmulated(ITypeSymbol type)
    {
        var bundle = ShapeOf(type, new TypeClassifierContext(null)).Bundle;
        return bundle is RuntimeBundleKind.Aggregate or RuntimeBundleKind.Class;
    }

    static ITypeSymbol Resolve(ITypeSymbol type, TypeClassifierContext ctx)
    {
        while (type is ITypeParameterSymbol parameter
               && ctx.TypeParamMap != null
               && ctx.TypeParamMap.TryGetValue(parameter, out var resolved))
        {
            if (SymbolEqualityComparer.Default.Equals(parameter, resolved)) break;
            type = resolved;
        }
        return type;
    }

    static TypeContents ContentsOf(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        HashSet<ITypeSymbol> visited)
    {
        type = Resolve(type, new TypeClassifierContext(typeParameterMap));
        if (type == null) return TypeContents.None;

        var contents = TypeContents.None;
        if (IsUserClassLeaf(type)) contents |= TypeContents.UserClass;
        if (type.SpecialType == SpecialType.System_Object) contents |= TypeContents.OpaqueObject;
        if (type is IArrayTypeSymbol array)
            return contents | ContentsOf(array.ElementType, typeParameterMap, visited);
        if (type is not INamedTypeSymbol named || !visited.Add(named)) return contents;

        if (named.DelegateInvokeMethod is { } invoke)
        {
            contents |= TypeContents.Delegate;
            foreach (var parameter in invoke.Parameters)
                contents |= ContentsOf(parameter.Type, typeParameterMap, visited);
            contents |= ContentsOf(invoke.ReturnType, typeParameterMap, visited);
        }
        if (IsAggregateValueLeaf(named))
            foreach (var member in named.GetMembers())
                if (member is IFieldSymbol { IsStatic: false } field)
                    contents |= ContentsOf(field.Type, typeParameterMap, visited);
        foreach (var argument in named.TypeArguments)
            contents |= ContentsOf(argument, typeParameterMap, visited);
        return contents;
    }
}
