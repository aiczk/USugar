using System;
using Microsoft.CodeAnalysis;

/// <summary>
/// Explicit shared authority for one Roslyn compilation. Immutable compiler
/// services live here; mutable per-class emission state remains in LoweringState
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
