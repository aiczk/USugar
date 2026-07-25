using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Immutable semantic plan for one emitted Udon program. Every closed-world discovery pass finishes
/// before this value is published to lowering.
/// </summary>
sealed class ProgramPlan
{
    public readonly CallableDefinitionPlan Callables;
    public readonly ReachabilityPlan Reach;
    public readonly IReadOnlyList<IMethodSymbol> CaptureRoots;
    public readonly IReadOnlyList<IOperation> FieldInitOps;
    public readonly FieldDiscoveryPlan Fields;
    public readonly SyntheticDemandPlan SyntheticDemands;

    public ProgramPlan(
        CallableDefinitionPlan callables,
        ReachabilityPlan reach,
        IEnumerable<IMethodSymbol> captureRoots,
        IEnumerable<IOperation> fieldInitOps,
        FieldDiscoveryPlan fields,
        SyntheticDemandPlan syntheticDemands = null)
    {
        Callables = callables ?? throw new ArgumentNullException(nameof(callables));
        Reach = reach ?? throw new ArgumentNullException(nameof(reach));
        CaptureRoots = Array.AsReadOnly(captureRoots.ToArray());
        FieldInitOps = Array.AsReadOnly(fieldInitOps.ToArray());
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        SyntheticDemands = syntheticDemands;
    }

    public ProgramPlan WithSyntheticDemands(SyntheticDemandPlan syntheticDemands)
    {
        if (SyntheticDemands != null)
            throw new InvalidOperationException("Synthetic demand plan was published twice.");
        return new ProgramPlan(Callables, Reach, CaptureRoots, FieldInitOps, Fields,
            syntheticDemands ?? throw new ArgumentNullException(nameof(syntheticDemands)));
    }
}
