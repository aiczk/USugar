using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// How one source type is represented in Udon storage. This is semantic
/// provenance, not a reconstruction from the emitted storage name.
/// </summary>
internal enum UdonRepresentationKind
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
internal enum UdonRuntimeTypeTest
{
    Unsupported,
    Exact,
    UniversalObject,
}

/// <summary>
/// One authoritative source-to-Udon lowering decision. Heap layout, enum
/// synthesis, runtime type tests, and ABI binding consume this same value.
/// </summary>
internal readonly struct UdonTypeLowering
{
    public UdonTypeId SourceType { get; }
    public StorageType Storage { get; }
    public UdonRepresentationKind Representation { get; }
    public UdonRuntimeTypeTest RuntimeTypeTest { get; }
    public UdonTypeDescriptor InstalledEvidence { get; }

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
        UdonTypeDescriptor installedEvidence)
    {
        SourceType = sourceType;
        Storage = storage;
        Representation = representation;
        RuntimeTypeTest = runtimeTypeTest;
        InstalledEvidence = installedEvidence;
    }
}

internal interface IUdonTypeSystem
{
    RuntimeShape SourceShape(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null);

    UdonTypeLowering Describe(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null);

    bool IsFoldedEnum(ITypeSymbol type);

    bool IsRuntimeDistinguishable(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null);

    string GetUdonTypeName(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null);

    StorageType GetStorageType(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null);

    ITypeSymbol Resolve(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null);
}

/// <summary>
/// Session-bound Roslyn-to-Udon type lowering. This is the only source-symbol
/// entry point that records verifier facts; installed-SDK facts arrive with the
/// ABI catalog, while ExternResolver itself remains pure for editor reflection
/// utilities that do not produce IR.
/// </summary>
internal sealed class UdonTypeSystem : IUdonTypeSystem
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

    public RuntimeShape SourceShape(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        return TypeClassifier.ShapeOf(
            Resolve(type, typeParameterMap),
            new TypeClassifierContext(typeParameterMap));
    }

    public bool IsRegisteredUdonType(UdonTypeId type)
        => _abiCatalog.IsRegisteredType(type);

    public UdonTypeLowering Describe(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        var resolved = Resolve(type, typeParameterMap);
        TypeClassifier.RequireSupportedArrayRank(resolved);
        var sourceType = UdonTypeIdentity.FromStorage(resolved);

        // Anonymous types are compiler-owned aggregate bundles. They have no
        // CLR/SDK extern name to lower, so asking ExternResolver to mint one
        // would misclassify them as unsupported user classes. Record their
        // semantic representation directly in the same type decision that
        // body lowering later consumes.
        if (resolved is INamedTypeSymbol { IsAnonymousType: true })
            return Create(
                sourceType,
                StorageTypes.ObjectArray,
                UdonRepresentationKind.ObjectArrayBundle,
                UdonRuntimeTypeTest.Unsupported);

        if (_objectArrayBehaviourAliases.UsesObjectArrayStorage(
                resolved, typeParameterMap))
            return Create(
                sourceType,
                StorageTypes.ObjectArray,
                UdonRepresentationKind.ObjectArrayBehaviourAlias,
                UdonRuntimeTypeTest.Unsupported);

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
                storage, representation));
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

    public StorageType GetStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage;

    UdonTypeLowering Create(UdonTypeId sourceType,
        StorageType storage,
        UdonRepresentationKind representation,
        UdonRuntimeTypeTest runtimeTypeTest)
    {
        _abiCatalog.TryGetType(sourceType, out var evidence);
        return new UdonTypeLowering(
            sourceType,
            storage,
            representation,
            runtimeTypeTest,
            evidence);
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
            if (elementType is IArrayTypeSymbol
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

    public ITypeSymbol Resolve(ITypeSymbol type,
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
