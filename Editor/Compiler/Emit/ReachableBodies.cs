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

    public IMethodSymbol[] ForeignStatics = Array.Empty<IMethodSymbol>();
    public IMethodSymbol[] StructMembers = Array.Empty<IMethodSymbol>();
    public IMethodSymbol[] BaseCopies = Array.Empty<IMethodSymbol>();
    public readonly HashSet<IMethodSymbol> StructMemberDefs = new(SymbolEqualityComparer.Default);
}
