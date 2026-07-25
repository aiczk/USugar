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
        BoundCallSiteTable callSites)
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
    }
}
