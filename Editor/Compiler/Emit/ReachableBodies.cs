using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Design §1: the single per-class reach fixpoint's first-class result. Every reachable method
/// DEFINITION's body is fetched EXACTLY ONCE (during the walk) and retained in <see cref="BodyByDef"/>;
/// every consumer — Phase-1 registration, BuildRecursionInfo, CaptureScopeAnalysis — reads bodies from
/// here instead of re-issuing GetOperation for the same definitions.
///
/// The regime arrays are the provenance projection of the fixpoint (which registration regime a
/// constructed symbol belongs to): <see cref="ForeignStatics"/> / <see cref="StructMembers"/> /
/// <see cref="BaseCopies"/> carry the gated CONSTRUCTED symbols each registration loop consumes;
/// <see cref="StructMemberDefs"/> is the ungated DEFINITION projection consumed by the recursion-graph
/// and capture-scope root sets. (Own/accessor/field-init provenance needs no separate list — those bodies
/// are the seeds and are covered by <see cref="BodyByDef"/> keyed by their own definition.)</summary>
sealed class ReachableBodies
{
    /// <summary>Reachable method DEFINITION → its body IOperation, fetched once during the walk. Keyed by
    /// OriginalDefinition. Field-initializer bodies are NOT method definitions and are not keyed here.</summary>
    public readonly Dictionary<IMethodSymbol, IOperation> BodyByDef = new(SymbolEqualityComparer.Default);

    /// <summary>Design 2026-07-10 v3 SS2A (B89 leg A): supplementary capture roots - definitions of
    /// generic (method-generic or open-container) foreign statics, which the REGISTRATION projections
    /// deliberately exclude (phantom-open / on-demand-spec rules) but whose bodies still host closures
    /// that must be capture-analyzed (else they fall to flat capture wiring - VM-proven activation
    /// aliasing, B89). Populated by a registration-free fixpoint: these bodies feed ONLY this map,
    /// never ForeignStatics/StructMembers/BodyByDef, so Phase-1 registration ordinals are untouched
    /// (F1/F2 implementation guard - do NOT fold this into another projection).</summary>
    public readonly Dictionary<IMethodSymbol, IOperation> GenericForeignStaticBodies = new(SymbolEqualityComparer.Default);

    public IMethodSymbol[] ForeignStatics = Array.Empty<IMethodSymbol>();
    public IMethodSymbol[] StructMembers = Array.Empty<IMethodSymbol>();
    public IMethodSymbol[] BaseCopies = Array.Empty<IMethodSymbol>();
    public readonly HashSet<IMethodSymbol> StructMemberDefs = new(SymbolEqualityComparer.Default);

    // CA-v2b-1: concrete user classes instantiated (new C()) in this program — each needs a per-program
    // typeobj (its instances carry it in bundle slot 0; is/cast enumerates the assignable subset).
    public readonly HashSet<INamedTypeSymbol> MintedClasses = new(SymbolEqualityComparer.Default);

    // CA rewrite (M5a): the open-constructed generic base-instance definitions (the legacy
    // UasmEmitter._openGenericBaseDefs side effect). The resolver-driven worklist populates this so the swap
    // can reproduce that side effect for BuildRecursionInfo; the legacy builder writes the field directly and
    // leaves this empty.
    public readonly HashSet<IMethodSymbol> OpenGenericBaseDefs = new(SymbolEqualityComparer.Default);
}
