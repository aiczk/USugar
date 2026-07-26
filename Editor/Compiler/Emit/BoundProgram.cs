using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Complete immutable semantic input to body lowering. Every callable specialization, closure,
/// capture, recursion edge, synthetic helper, and field has been resolved before this is created.
/// </summary>
internal sealed class BoundProgram
{
    public readonly ProgramDiscovery Discovery;
    public readonly ClosureIdentityPlan ClosureIdentities;
    public readonly CaptureScopeAnalysis Captures;
    public readonly CallableBodyGraph Bodies;
    public readonly RecursionInfo Recursion;
    public readonly SyntheticDemandPlan SyntheticDemands;
    public readonly BoundCallSiteTable CallSites;
    public readonly BoundInitializerTable Initializers;
    public readonly BoundClassInitializationTable ClassInitializers;
    public readonly BoundDeconstructionTable Deconstructions;
    public readonly BoundConversionTable Conversions;
    public readonly BoundConstantTable Constants;
    public readonly BoundMethodBodyTable MethodBodies;
    public readonly BoundValueTable Values;
    public readonly BoundSyntheticDispatchTable SyntheticDispatch;
    public readonly BoundAbiPlan Abi;
    public readonly BoundUdonTypeSystem Types;
    public readonly UdonTypeFactRegistry TypeFacts;
    public readonly AggregateLayoutTable Aggregates;
    public readonly ClassTypeObjectContext ClassTypes;

    public CallableDefinitionPlan Callables => Discovery.Callables;
    public ReachabilityPlan Reach => Discovery.Reach;
    public FieldDiscoveryPlan Fields => Discovery.Fields;
    public IReadOnlyList<IOperation> FieldInitOps => Discovery.FieldInitOps;
    public IReadOnlyList<IMethodSymbol> CaptureRoots => Discovery.CaptureRoots;

    public BoundProgram(
        ProgramDiscovery discovery,
        ClosureIdentityPlan closureIdentities,
        CaptureScopeAnalysis captures,
        CallableBodyGraph bodies,
        RecursionInfo recursion,
        SyntheticDemandPlan syntheticDemands,
        BoundCallSiteTable callSites,
        BoundInitializerTable initializers,
        BoundClassInitializationTable classInitializers,
        BoundDeconstructionTable deconstructions,
        BoundConversionTable conversions,
        BoundConstantTable constants,
        BoundMethodBodyTable methodBodies,
        BoundValueTable values,
        BoundSyntheticDispatchTable syntheticDispatch,
        BoundAbiPlan abi,
        BoundUdonTypeSystem types,
        UdonTypeFactRegistry typeFacts,
        AggregateLayoutTable aggregates,
        ClassTypeObjectContext classTypes)
    {
        Discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        ClosureIdentities = closureIdentities
            ?? throw new ArgumentNullException(nameof(closureIdentities));
        Captures = captures ?? throw new ArgumentNullException(nameof(captures));
        Bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
        Recursion = recursion ?? throw new ArgumentNullException(nameof(recursion));
        SyntheticDemands = syntheticDemands
            ?? throw new ArgumentNullException(nameof(syntheticDemands));
        CallSites = callSites ?? throw new ArgumentNullException(nameof(callSites));
        Initializers = initializers
            ?? throw new ArgumentNullException(nameof(initializers));
        ClassInitializers = classInitializers
            ?? throw new ArgumentNullException(nameof(classInitializers));
        Deconstructions = deconstructions
            ?? throw new ArgumentNullException(nameof(deconstructions));
        Conversions = conversions
            ?? throw new ArgumentNullException(nameof(conversions));
        Constants = constants
            ?? throw new ArgumentNullException(nameof(constants));
        MethodBodies = methodBodies
            ?? throw new ArgumentNullException(nameof(methodBodies));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        SyntheticDispatch = syntheticDispatch
            ?? throw new ArgumentNullException(nameof(syntheticDispatch));
        Abi = abi ?? throw new ArgumentNullException(nameof(abi));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        TypeFacts = typeFacts ?? throw new ArgumentNullException(nameof(typeFacts));
        if (!TypeFacts.IsFrozen)
            throw new ArgumentException(
                "A bound program requires frozen type facts.",
                nameof(typeFacts));
        Aggregates = aggregates
            ?? throw new ArgumentNullException(nameof(aggregates));
        ClassTypes = classTypes
            ?? throw new ArgumentNullException(nameof(classTypes));
    }
}
