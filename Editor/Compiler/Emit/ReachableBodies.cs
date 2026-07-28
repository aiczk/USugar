using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

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
    /// <summary>Reachable method DEFINITION → its shared source-body snapshot, materialized once
    /// during the walk and keyed by OriginalDefinition. Field-initializer bodies are not method
    /// definitions and are not keyed here.</summary>
    public readonly Dictionary<IMethodSymbol, BoundMethodBody> BodyByDef =
        new(SymbolEqualityComparer.Default);

    /// <summary>Design 2026-07-10 v3 SS2A (B89 leg A): supplementary capture roots - definitions of
    /// generic (method-generic or open-container) foreign statics, which the REGISTRATION projections
    /// deliberately exclude (phantom-open / on-demand-spec rules) but whose bodies still host closures
    /// that must be capture-analyzed (else they fall to flat capture wiring - VM-proven activation
    /// aliasing, B89). Populated by a registration-free fixpoint: these bodies feed ONLY this map,
    /// never ForeignStatics/StructMembers/BodyByDef, so Phase-1 registration ordinals are untouched
    /// (F1/F2 implementation guard - do NOT fold this into another projection).</summary>
    public readonly Dictionary<IMethodSymbol, BoundMethodBody>
        GenericForeignStaticBodies =
            new(SymbolEqualityComparer.Default);

    public IMethodSymbol[] ForeignStatics = Array.Empty<IMethodSymbol>();
    public IMethodSymbol[] StructMembers = Array.Empty<IMethodSymbol>();
    public IMethodSymbol[] BaseCopies = Array.Empty<IMethodSymbol>();
    public readonly HashSet<IMethodSymbol> StructMemberDefs = new(SymbolEqualityComparer.Default);

    // CA-v2b-1: concrete user classes instantiated (new C()) in this program — each needs a per-program
    // typeobj (its instances carry it in bundle slot 0; is/cast enumerates the assignable subset).
    // Only these classes execute their instance initializers in this program.
    public readonly HashSet<INamedTypeSymbol> ConstructedClasses =
        new(SymbolEqualityComparer.Default);
    // These classes need runtime identity and dispatch coverage, but were constructed elsewhere.
    public readonly HashSet<INamedTypeSymbol> ImportedRuntimeClasses =
        new(SymbolEqualityComparer.Default);

    /// <summary>Registration-free definitions whose bodies remain roots for capture and recursion analysis.</summary>
    public readonly HashSet<IMethodSymbol> OpenGenericBaseRoots = new(SymbolEqualityComparer.Default);

    public ReachabilityPlan Freeze(
        IEnumerable<INamedTypeSymbol> additionalConstructedClasses)
    {
        var constructed = new HashSet<INamedTypeSymbol>(
            ConstructedClasses.Where(
                type => !ClassTypeObjectContext.ContainsTypeParameter(type)),
            SymbolEqualityComparer.Default);
        constructed.UnionWith(
            additionalConstructedClasses
            ?? throw new ArgumentNullException(
                nameof(additionalConstructedClasses)));
        var runtimeTypes = new HashSet<INamedTypeSymbol>(
            constructed, SymbolEqualityComparer.Default);
        runtimeTypes.UnionWith(
            ImportedRuntimeClasses.Where(
                type => !ClassTypeObjectContext
                    .ContainsTypeParameter(type)));
        return new ReachabilityPlan(
            BodyByDef,
            GenericForeignStaticBodies,
            ForeignStatics,
            StructMembers,
            BaseCopies,
            StructMemberDefs,
            constructed,
            runtimeTypes,
            OpenGenericBaseRoots);
    }
}

/// <summary>
/// Read-only snapshot of the single reachability fixpoint. Lowering receives this type, never the
/// mutable worklist product used to build it.
/// </summary>
sealed class ReachabilityPlan
{
    public readonly IReadOnlyDictionary<IMethodSymbol, BoundMethodBody>
        BodyByDef;
    public readonly IReadOnlyDictionary<IMethodSymbol, BoundMethodBody>
        GenericForeignStaticBodies;
    public readonly IReadOnlyList<IMethodSymbol> ForeignStatics;
    public readonly IReadOnlyList<IMethodSymbol> StructMembers;
    public readonly IReadOnlyList<IMethodSymbol> BaseCopies;
    public readonly IReadOnlyCollection<IMethodSymbol> StructMemberDefs;
    public readonly IReadOnlyCollection<INamedTypeSymbol> ConstructedClasses;
    public readonly IReadOnlyCollection<INamedTypeSymbol> RuntimeClassTypes;
    public readonly IReadOnlyCollection<IMethodSymbol> OpenGenericBaseRoots;

    public ReachabilityPlan(
        IDictionary<IMethodSymbol, BoundMethodBody> bodyByDef,
        IDictionary<IMethodSymbol, BoundMethodBody>
            genericForeignStaticBodies,
        IEnumerable<IMethodSymbol> foreignStatics,
        IEnumerable<IMethodSymbol> structMembers,
        IEnumerable<IMethodSymbol> baseCopies,
        IEnumerable<IMethodSymbol> structMemberDefs,
        IEnumerable<INamedTypeSymbol> constructedClasses,
        IEnumerable<INamedTypeSymbol> runtimeClassTypes,
        IEnumerable<IMethodSymbol> openGenericBaseRoots)
    {
        BodyByDef = CopyMap(bodyByDef);
        GenericForeignStaticBodies = CopyMap(genericForeignStaticBodies);
        ForeignStatics = Array.AsReadOnly(foreignStatics.ToArray());
        StructMembers = Array.AsReadOnly(structMembers.ToArray());
        BaseCopies = Array.AsReadOnly(baseCopies.ToArray());
        StructMemberDefs = Array.AsReadOnly(structMemberDefs.ToArray());
        ConstructedClasses =
            Array.AsReadOnly(constructedClasses.ToArray());
        RuntimeClassTypes =
            Array.AsReadOnly(runtimeClassTypes.ToArray());
        OpenGenericBaseRoots = Array.AsReadOnly(openGenericBaseRoots.ToArray());
    }

    static IReadOnlyDictionary<IMethodSymbol, BoundMethodBody> CopyMap(
        IDictionary<IMethodSymbol, BoundMethodBody> source)
        => new ReadOnlyDictionary<IMethodSymbol, BoundMethodBody>(
            new Dictionary<IMethodSymbol, BoundMethodBody>(
                source, SymbolEqualityComparer.Default));
}
