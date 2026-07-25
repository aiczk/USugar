using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Explicit shared authority for one Roslyn compilation. Immutable compiler
/// services live here; mutable per-class emission state remains in EmitContext
/// so parallel behaviour emission cannot leak state between classes.
/// </summary>
public sealed class CompilationSession
{
    public Compilation Compilation { get; }
    public UdonAbiCatalog AbiCatalog { get; }
    public UdonTypeFactRegistry TypeFacts { get; }
    public UdonTypeSystem Types { get; }
    internal ObjectArrayBehaviourAliasCensus ObjectArrayBehaviourAliases { get; }

    public CompilationSession(Compilation compilation, UdonAbiCatalog abiCatalog)
        : this(compilation, abiCatalog, new UdonTypeFactRegistry())
    {
    }

    internal CompilationSession(Compilation compilation, UdonAbiCatalog abiCatalog,
        UdonTypeFactRegistry typeFacts)
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        AbiCatalog = abiCatalog
            ?? throw new ArgumentNullException(nameof(abiCatalog));
        TypeFacts = typeFacts ?? throw new ArgumentNullException(nameof(typeFacts));
        AbiCatalog.SeedTypeFacts(TypeFacts);
        ObjectArrayBehaviourAliases = ObjectArrayBehaviourAliasCensus.For(Compilation);
        Types = new UdonTypeSystem(
            TypeFacts, ObjectArrayBehaviourAliases, AbiCatalog);
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

    public bool IsRegisteredUdonTypeName(string udonTypeName)
        => _abiCatalog.IsRegisteredType(udonTypeName);

    public bool IsUserEnum(ITypeSymbol type)
        => ExternResolver.IsUserEnum(type, IsRegisteredUdonTypeName);

    public bool IsRuntimeDistinguishable(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => ExternResolver.IsRuntimeDistinguishable(
            type, typeParameterMap, IsRegisteredUdonTypeName);

    public string GetUdonTypeName(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => _objectArrayBehaviourAliases.UsesObjectArrayStorage(type, typeParameterMap)
            ? StorageTypes.ObjectArray.Name
            : ExternResolver.GetUdonTypeName(
                type, typeParameterMap, _facts, IsRegisteredUdonTypeName);

    public StorageType GetStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => new StorageType(GetUdonTypeName(type, typeParameterMap));
}
