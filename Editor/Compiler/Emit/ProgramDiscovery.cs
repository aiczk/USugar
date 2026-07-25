using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Immutable closed-world discovery result. It contains no body-lowering products.
/// </summary>
sealed class ProgramDiscovery
{
    public readonly CallableDefinitionPlan Callables;
    public readonly ReachabilityPlan Reach;
    public readonly IReadOnlyList<IMethodSymbol> CaptureRoots;
    public readonly IReadOnlyList<IOperation> FieldInitOps;
    public readonly FieldDiscoveryPlan Fields;

    public ProgramDiscovery(
        CallableDefinitionPlan callables,
        ReachabilityPlan reach,
        IEnumerable<IMethodSymbol> captureRoots,
        IEnumerable<IOperation> fieldInitOps,
        FieldDiscoveryPlan fields)
    {
        Callables = callables ?? throw new ArgumentNullException(nameof(callables));
        Reach = reach ?? throw new ArgumentNullException(nameof(reach));
        CaptureRoots = Array.AsReadOnly(captureRoots.ToArray());
        FieldInitOps = Array.AsReadOnly(fieldInitOps.ToArray());
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }
}
