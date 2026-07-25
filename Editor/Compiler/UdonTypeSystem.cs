using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// How one source type is represented in Udon storage. This is semantic
/// provenance, not a reconstruction from the emitted storage name.
/// </summary>
public enum UdonRepresentationKind
{
    Exact,
    NativeArray,
    FoldedEnum,
    BoxedNullable,
    ObjectArrayBundle,
    ObjectArrayBehaviourAlias,
    BehaviourReference,
    ComponentArray,
}

/// <summary>Whether a SystemType runtime test can answer the source-level type question.</summary>
public enum UdonRuntimeTypeTest
{
    Unsupported,
    Exact,
    UniversalObject,
}

/// <summary>
/// One authoritative source-to-Udon lowering decision. Heap layout, enum
/// synthesis, runtime type tests, and ABI binding consume this same value.
/// </summary>
public readonly struct UdonTypeLowering
{
    public UdonTypeId SourceType { get; }
    public StorageType Storage { get; }
    public UdonRepresentationKind Representation { get; }
    public UdonRuntimeTypeTest RuntimeTypeTest { get; }
    public UdonTypeDescriptor InstalledEvidence { get; }
    public RuntimeShape? SourceShape { get; }

    public bool IsFoldedEnum
        => Representation == UdonRepresentationKind.FoldedEnum;
    public bool IsRuntimeDistinguishable
        => RuntimeTypeTest != UdonRuntimeTypeTest.Unsupported;
    public bool HasRegisteredTypeNode
        => InstalledEvidence?.HasTypeNode == true;

    internal UdonTypeLowering(UdonTypeId sourceType,
        StorageType storage,
        UdonRepresentationKind representation,
        UdonRuntimeTypeTest runtimeTypeTest,
        UdonTypeDescriptor installedEvidence,
        RuntimeShape? sourceShape)
    {
        SourceType = sourceType;
        Storage = storage;
        Representation = representation;
        RuntimeTypeTest = runtimeTypeTest;
        InstalledEvidence = installedEvidence;
        SourceShape = sourceShape;
    }
}

/// <summary>
/// Session-bound Roslyn-to-Udon type lowering. This is the only source-symbol
/// entry point that records verifier facts; installed-SDK facts arrive with the
/// ABI catalog, while ExternResolver itself remains pure for editor reflection
/// utilities that do not produce IR.
/// </summary>
public sealed class UdonTypeSystem
{
    readonly UdonTypeFactRegistry _facts;
    readonly ObjectArrayBehaviourAliasCensus _objectArrayBehaviourAliases;
    readonly UdonAbiCatalog _abiCatalog;

    internal UdonTypeSystem(UdonTypeFactRegistry facts,
        ObjectArrayBehaviourAliasCensus objectArrayBehaviourAliases,
        UdonAbiCatalog abiCatalog)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _objectArrayBehaviourAliases = objectArrayBehaviourAliases
            ?? throw new ArgumentNullException(nameof(objectArrayBehaviourAliases));
        _abiCatalog = abiCatalog ?? throw new ArgumentNullException(nameof(abiCatalog));
    }

    public bool IsRegisteredUdonType(UdonTypeId type)
        => _abiCatalog.IsRegisteredType(type);

    public UdonTypeLowering Describe(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        var resolved = Resolve(type, typeParameterMap);
        var sourceType = UdonTypeIdentity.FromStorage(resolved);
        var sourceShape = TypeClassifier.ShapeOf(
            resolved, new TypeClassifierContext(typeParameterMap));

        if (_objectArrayBehaviourAliases.UsesObjectArrayStorage(
                resolved, typeParameterMap))
            return Create(
                sourceType,
                StorageTypes.ObjectArray,
                UdonRepresentationKind.ObjectArrayBehaviourAlias,
                UdonRuntimeTypeTest.Unsupported,
                sourceShape);

        var storage = new StorageType(ExternResolver.LowerUdonStorageName(
            resolved, typeParameterMap, _abiCatalog.IsRegisteredType));
        _facts.RecordSourceLowering(storage.Id, resolved);
        var representation = Classify(
            resolved, typeParameterMap, sourceType, storage);
        return Create(
            sourceType,
            storage,
            representation,
            RuntimeTestFor(resolved, typeParameterMap,
                storage, representation),
            sourceShape);
    }

    public UdonTypeLowering Describe(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (type.IsByRef)
            type = type.GetElementType()
                ?? throw new InvalidOperationException(
                    "A by-ref CLR type has no element type.");
        var sourceType = UdonTypeIdentity.FromStorage(type);
        var storage = LowerClrStorage(type);
        var representation = Classify(type, sourceType, storage);
        return Create(
            sourceType, storage, representation,
            UdonRuntimeTypeTest.Unsupported,
            null);
    }

    public bool IsFoldedEnum(ITypeSymbol type)
        => Describe(type).IsFoldedEnum;

    public bool IsRuntimeDistinguishable(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        if (type == null || ExternResolver.IsUnsupportedUserClass(
                Resolve(type, typeParameterMap)))
            return false;
        return Describe(type, typeParameterMap).IsRuntimeDistinguishable;
    }

    public string GetUdonTypeName(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage.Name;

    public string GetUdonTypeName(Type type)
        => Describe(type).Storage.Name;

    public StorageType GetStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage;

    public StorageType GetStorageType(Type type)
        => Describe(type).Storage;

    UdonTypeLowering Create(UdonTypeId sourceType,
        StorageType storage,
        UdonRepresentationKind representation,
        UdonRuntimeTypeTest runtimeTypeTest,
        RuntimeShape? sourceShape)
    {
        _abiCatalog.TryGetType(sourceType, out var evidence);
        return new UdonTypeLowering(
            sourceType, storage, representation, runtimeTypeTest, evidence, sourceShape);
    }

    UdonRepresentationKind Classify(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        UdonTypeId sourceType, StorageType storage)
    {
        if (type is IArrayTypeSymbol array)
        {
            var elementType = Resolve(array.ElementType, typeParameterMap);
            if (storage == StorageTypes.ComponentArray)
                return UdonRepresentationKind.ComponentArray;
            if (array.Rank > 1
                || elementType is IArrayTypeSymbol
                || elementType is INamedTypeSymbol
                    { DelegateInvokeMethod: not null }
                || TypeClassifier.IsObjectArrayEmulated(elementType))
                return UdonRepresentationKind.ObjectArrayBundle;
            return UdonRepresentationKind.NativeArray;
        }
        if (type is INamedTypeSymbol { DelegateInvokeMethod: not null })
            return UdonRepresentationKind.ObjectArrayBundle;
        if (type is INamedTypeSymbol nullable
            && nullable.OriginalDefinition.SpecialType
            == SpecialType.System_Nullable_T)
            return UdonRepresentationKind.BoxedNullable;
        if (TypeClassifier.IsObjectArrayEmulated(type))
            return UdonRepresentationKind.ObjectArrayBundle;
        if (storage == StorageTypes.UdonEventReceiver)
            return UdonRepresentationKind.BehaviourReference;
        if (type.TypeKind == TypeKind.Enum
            && storage.Id != sourceType)
            return UdonRepresentationKind.FoldedEnum;
        return UdonRepresentationKind.Exact;
    }

    UdonRuntimeTypeTest RuntimeTestFor(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        StorageType storage,
        UdonRepresentationKind representation)
    {
        if (type.SpecialType == SpecialType.System_Object)
            return UdonRuntimeTypeTest.UniversalObject;
        if (representation == UdonRepresentationKind.NativeArray
            && type is IArrayTypeSymbol array
            && Describe(array.ElementType, typeParameterMap).IsFoldedEnum)
            return UdonRuntimeTypeTest.Unsupported;
        if ((representation is UdonRepresentationKind.Exact
                or UdonRepresentationKind.NativeArray)
            && storage != StorageTypes.ObjectArray
            && storage != StorageTypes.UdonEventReceiver
            && storage != StorageTypes.ComponentArray)
            return UdonRuntimeTypeTest.Exact;
        return UdonRuntimeTypeTest.Unsupported;
    }

    static UdonRepresentationKind Classify(
        Type type, UdonTypeId sourceType, StorageType storage)
    {
        if (type.IsArray)
        {
            if (storage == StorageTypes.ComponentArray)
                return UdonRepresentationKind.ComponentArray;
            if (storage == StorageTypes.ObjectArray
                && (type.GetArrayRank() > 1
                    || type.GetElementType()?.IsArray == true
                    || typeof(Delegate).IsAssignableFrom(
                        type.GetElementType())))
                return UdonRepresentationKind.ObjectArrayBundle;
            return UdonRepresentationKind.NativeArray;
        }
        if (typeof(Delegate).IsAssignableFrom(type))
            return UdonRepresentationKind.ObjectArrayBundle;
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            return UdonRepresentationKind.BoxedNullable;
        if (storage == StorageTypes.UdonEventReceiver)
            return UdonRepresentationKind.BehaviourReference;
        if (type.IsEnum && storage.Id != sourceType)
            return UdonRepresentationKind.FoldedEnum;
        return storage == StorageTypes.ObjectArray
            ? UdonRepresentationKind.ObjectArrayBundle
            : UdonRepresentationKind.Exact;
    }

    StorageType LowerClrStorage(Type type)
    {
        if (type.IsByRef)
            return LowerClrStorage(type.GetElementType()
                ?? throw new InvalidOperationException(
                    "A by-ref CLR type has no element type."));
        if (type == typeof(void)) return StorageTypes.Void;

        if (type.IsArray)
        {
            if (type.GetArrayRank() > 1)
                return StorageTypes.ObjectArray;
            var element = type.GetElementType();
            if (element == null || element.IsArray
                || typeof(Delegate).IsAssignableFrom(element))
                return StorageTypes.ObjectArray;
            var elementStorage = LowerClrStorage(element);
            if (elementStorage == StorageTypes.UdonEventReceiver)
                return StorageTypes.ComponentArray;
            if (elementStorage == StorageTypes.ObjectArray)
                return StorageTypes.ObjectArray;
            return new StorageType(
                UdonTypeIdentity.FromCanonicalStorageName(
                    elementStorage.Name + "Array"));
        }

        if (typeof(Delegate).IsAssignableFrom(type))
            return StorageTypes.ObjectArray;
        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            return StorageTypes.Object;

        var exactType = UdonTypeIdentity.FromStorage(type);
        if (_abiCatalog.IsRegisteredType(exactType))
            return new StorageType(exactType);
        if (type.IsEnum)
            return LowerClrStorage(Enum.GetUnderlyingType(type));
        if (IsClrUdonSharpBehaviour(type) || type.IsInterface)
            return StorageTypes.UdonEventReceiver;
        return StorageTypes.ObjectArray;
    }

    static bool IsClrUdonSharpBehaviour(Type type)
    {
        for (var current = type; current != null;
             current = current.BaseType)
            if (current.Name == "UdonSharpBehaviour")
                return true;
        return false;
    }

    static ITypeSymbol Resolve(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        while (type is ITypeParameterSymbol parameter
               && typeParameterMap != null
               && typeParameterMap.TryGetValue(parameter, out var resolved))
        {
            if (SymbolEqualityComparer.Default.Equals(parameter, resolved))
                throw new NotSupportedException(
                    $"Type parameter '{parameter.Name}' resolves to itself in "
                    + "the monomorphization map.");
            type = resolved;
        }
        return type;
    }
}
