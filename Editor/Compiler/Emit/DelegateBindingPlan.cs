using System;
using Microsoft.CodeAnalysis;

public enum DelegateBindingKind
{
    Direct,
    Receiver,
    CrossProgram,
    Closure,
    SignatureAdapter,
    Wrapper,
}

/// <summary>Immutable symbolic result of binding one delegate creation site.</summary>
public sealed class DelegateBindingPlan
{
    public DelegateBindingKind Kind { get; }
    public IMethodSymbol TargetMethod { get; }
    public string BridgeName { get; }

    public DelegateBindingPlan(DelegateBindingKind kind, IMethodSymbol targetMethod, string bridgeName)
    {
        Kind = kind;
        TargetMethod = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
        BridgeName = !string.IsNullOrEmpty(bridgeName)
            ? bridgeName : throw new ArgumentException("Delegate bridge name is required.", nameof(bridgeName));
    }
}

/// <summary>Emission-time values for a preclassified delegate binding.</summary>
public sealed class MaterializedDelegateBinding
{
    public DelegateBindingPlan Plan { get; }
    public CLeaf FunctionReference { get; }
    public CLeaf TargetInstance { get; }
    public CLeaf Environment { get; }

    public MaterializedDelegateBinding(DelegateBindingPlan plan, CLeaf functionReference,
        CLeaf targetInstance, CLeaf environment)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        FunctionReference = functionReference
            ?? throw new ArgumentNullException(nameof(functionReference));
        TargetInstance = targetInstance;
        Environment = environment;
    }
}
