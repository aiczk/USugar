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
        Types = new UdonTypeSystem(TypeFacts);
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

    internal UdonTypeSystem(UdonTypeFactRegistry facts)
        => _facts = facts ?? throw new ArgumentNullException(nameof(facts));

    public string GetUdonTypeName(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => ExternResolver.GetUdonTypeName(type, typeParameterMap, _facts);

    public StorageType GetStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => ExternResolver.GetStorageType(
            new RuntimeType(type), typeParameterMap, _facts);
}
