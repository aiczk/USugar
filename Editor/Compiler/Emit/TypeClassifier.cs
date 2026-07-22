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

public enum RuntimeStorageKind { Native, ObjectArrayBundle }
public enum RuntimeBundleKind { None, Class, Aggregate, Delegate, MultiDimensionalArray }
public enum RuntimeIdentityKind { Value, Reference }
public enum RuntimeScopeKind { Portable, ProgramLocal }
public enum RuntimeTypeIdentityKind { Exact, Tagged, Collapsed }

/// <summary>Compiler-side description of a value's Udon representation. Several source types erase to
/// SystemObjectArray, so policy must consume these semantic facts rather than rediscovering meaning from
/// the emitted Udon type name.</summary>
public readonly struct RuntimeShape
{
    public readonly RuntimeStorageKind Storage;
    public readonly RuntimeBundleKind Bundle;
    public readonly RuntimeIdentityKind Identity;
    public readonly RuntimeScopeKind Scope;
    public readonly RuntimeTypeIdentityKind RuntimeTypeIdentity;
    public readonly bool ContainsUserClassPayload;

    RuntimeShape(RuntimeStorageKind storage, RuntimeBundleKind bundle, RuntimeIdentityKind identity,
        RuntimeScopeKind scope, RuntimeTypeIdentityKind runtimeTypeIdentity, bool containsUserClassPayload)
    {
        Storage = storage;
        Bundle = bundle;
        Identity = identity;
        Scope = scope;
        RuntimeTypeIdentity = runtimeTypeIdentity;
        ContainsUserClassPayload = containsUserClassPayload;
    }

    public bool IsBundle => Storage == RuntimeStorageKind.ObjectArrayBundle;
    public bool CanCrossProgram => Scope == RuntimeScopeKind.Portable;
    public bool CanEraseToObject => !ContainsUserClassPayload
        && (!IsBundle || RuntimeTypeIdentity != RuntimeTypeIdentityKind.Collapsed);

    public static RuntimeShape Class(bool containsPayload = true)
        => new(RuntimeStorageKind.ObjectArrayBundle, RuntimeBundleKind.Class, RuntimeIdentityKind.Reference,
            RuntimeScopeKind.Portable, RuntimeTypeIdentityKind.Tagged, containsPayload);
    public static RuntimeShape Aggregate(bool containsPayload)
        => new(RuntimeStorageKind.ObjectArrayBundle, RuntimeBundleKind.Aggregate, RuntimeIdentityKind.Value,
            RuntimeScopeKind.Portable,
            RuntimeTypeIdentityKind.Collapsed, containsPayload);
    public static RuntimeShape Delegate(bool containsPayload)
        => new(RuntimeStorageKind.ObjectArrayBundle, RuntimeBundleKind.Delegate, RuntimeIdentityKind.Reference,
            containsPayload ? RuntimeScopeKind.ProgramLocal : RuntimeScopeKind.Portable,
            RuntimeTypeIdentityKind.Collapsed, containsPayload);
    public static RuntimeShape MultiDimensionalArray(bool containsPayload)
        => new(RuntimeStorageKind.ObjectArrayBundle, RuntimeBundleKind.MultiDimensionalArray,
            RuntimeIdentityKind.Reference, RuntimeScopeKind.Portable,
            RuntimeTypeIdentityKind.Collapsed, containsPayload);
    public static RuntimeShape Native(bool containsPayload)
        => new(RuntimeStorageKind.Native, RuntimeBundleKind.None, RuntimeIdentityKind.Value,
            containsPayload ? RuntimeScopeKind.ProgramLocal : RuntimeScopeKind.Portable,
            RuntimeTypeIdentityKind.Exact, containsPayload);
}

public static class TypeClassifier
{
    public static RuntimeShape ShapeOf(ITypeSymbol type, TypeClassifierContext ctx)
    {
        type = Resolve(type, ctx);
        var containsPayload = EmitPolicy.ContainsUserClassType(type, ctx.TypeParamMap);
        if (IsUserClassLeaf(type)) return RuntimeShape.Class();
        if (NdimArrayAbi.IsNdimArray(type)) return RuntimeShape.MultiDimensionalArray(containsPayload);
        if (type is INamedTypeSymbol { DelegateInvokeMethod: not null }) return RuntimeShape.Delegate(containsPayload);
        if (IsAggregateValueLeaf(type)) return RuntimeShape.Aggregate(containsPayload);
        return RuntimeShape.Native(containsPayload);
    }

    public static bool ContainsUserClassPayload(ITypeSymbol type, TypeClassifierContext ctx)
        => ShapeOf(type, ctx).ContainsUserClassPayload;

    public static bool IsUserClass(ITypeSymbol type)
        => ShapeOf(type, new TypeClassifierContext(null)).Bundle == RuntimeBundleKind.Class;

    internal static bool IsUserClassLeaf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.IsAnonymousType || !ExternResolver.IsPlainUserClass(named) || named.IsRecord) return false;
        if (named.BaseType != null && named.BaseType.SpecialType != SpecialType.System_Object)
        {
            var baseType = named.BaseType;
            if (!ExternResolver.IsPlainUserClass(baseType) || baseType.IsRecord
                || ExternResolver.ClassHasRegisteredExterns(baseType) || !IsUserClassLeaf(baseType)) return false;
        }
        return !ExternResolver.ClassHasRegisteredExterns(named);
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
        => type is ITypeParameterSymbol tp && ctx.TypeParamMap != null
            && ctx.TypeParamMap.TryGetValue(tp, out var resolved) ? resolved : type;
}
