using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;

/// <summary>
/// Complete immutable semantic input to body lowering. Every callable specialization, closure,
/// capture, recursion edge, synthetic helper, and field has been resolved before this is created.
/// </summary>
internal sealed class BoundProgram
{
    public readonly CallableDefinitionPlan Callables;
    public readonly FieldDiscoveryPlan Fields;
    public readonly ClosureIdentityPlan ClosureIdentities;
    public readonly CaptureScopeAnalysis Captures;
    public readonly RecursionInfo Recursion;
    public readonly SyntheticDemandPlan SyntheticDemands;
    public readonly BoundCallSiteTable CallSites;
    public readonly BoundInitializerTable Initializers;
    public readonly BoundDeconstructionTable Deconstructions;
    public readonly BoundConversionTable Conversions;
    public readonly BoundConstantTable Constants;
    public readonly BoundMethodBodyTable MethodBodies;
    public readonly BoundValueTable Values;
    public readonly IReadOnlyDictionary<IFieldSymbol, string> SourceStorageNames;
    public readonly BoundSyntheticDispatchTable SyntheticDispatch;
    public readonly BoundAbiPlan Abi;
    public readonly BoundUdonTypeSystem Types;
    public readonly UdonTypeFactRegistry TypeFacts;
    public readonly FrozenLayoutPlan Layouts;
    public readonly AggregateLayoutTable Aggregates;
    public readonly ClassTypeObjectContext ClassTypes;

    public BoundProgram(
        CallableDefinitionPlan callables,
        FieldDiscoveryPlan fields,
        ClosureIdentityPlan closureIdentities,
        CaptureScopeAnalysis captures,
        RecursionInfo recursion,
        SyntheticDemandPlan syntheticDemands,
        BoundCallSiteTable callSites,
        BoundInitializerTable initializers,
        BoundDeconstructionTable deconstructions,
        BoundConversionTable conversions,
        BoundConstantTable constants,
        BoundMethodBodyTable methodBodies,
        BoundValueTable values,
        IReadOnlyDictionary<IFieldSymbol, string> sourceStorageNames,
        BoundSyntheticDispatchTable syntheticDispatch,
        BoundAbiPlan abi,
        BoundUdonTypeSystem types,
        UdonTypeFactRegistry typeFacts,
        FrozenLayoutPlan layouts,
        AggregateLayoutTable aggregates,
        ClassTypeObjectContext classTypes)
    {
        Callables = callables
            ?? throw new ArgumentNullException(nameof(callables));
        Fields = fields
            ?? throw new ArgumentNullException(nameof(fields));
        ClosureIdentities = closureIdentities
            ?? throw new ArgumentNullException(nameof(closureIdentities));
        Captures = captures ?? throw new ArgumentNullException(nameof(captures));
        Recursion = recursion ?? throw new ArgumentNullException(nameof(recursion));
        SyntheticDemands = syntheticDemands
            ?? throw new ArgumentNullException(nameof(syntheticDemands));
        CallSites = callSites ?? throw new ArgumentNullException(nameof(callSites));
        Initializers = initializers
            ?? throw new ArgumentNullException(nameof(initializers));
        Deconstructions = deconstructions
            ?? throw new ArgumentNullException(nameof(deconstructions));
        Conversions = conversions
            ?? throw new ArgumentNullException(nameof(conversions));
        Constants = constants
            ?? throw new ArgumentNullException(nameof(constants));
        MethodBodies = methodBodies
            ?? throw new ArgumentNullException(nameof(methodBodies));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        SourceStorageNames = new ReadOnlyDictionary<IFieldSymbol, string>(
            new Dictionary<IFieldSymbol, string>(
                sourceStorageNames
                ?? throw new ArgumentNullException(nameof(sourceStorageNames)),
                SymbolEqualityComparer.Default));
        SyntheticDispatch = syntheticDispatch
            ?? throw new ArgumentNullException(nameof(syntheticDispatch));
        Abi = abi ?? throw new ArgumentNullException(nameof(abi));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        TypeFacts = typeFacts ?? throw new ArgumentNullException(nameof(typeFacts));
        if (!TypeFacts.IsFrozen)
            throw new ArgumentException(
                "A bound program requires frozen type facts.",
                nameof(typeFacts));
        Layouts = layouts
            ?? throw new ArgumentNullException(nameof(layouts));
        Aggregates = aggregates
            ?? throw new ArgumentNullException(nameof(aggregates));
        ClassTypes = classTypes
            ?? throw new ArgumentNullException(nameof(classTypes));
    }

    public string RequireSourceStorageName(IFieldSymbol field)
    {
        if (field != null
            && SourceStorageNames.TryGetValue(field, out var name))
            return name;
        throw new InvalidOperationException(
            $"Source storage name for '{field?.ToDisplayString()}' was not bound.");
    }
}
