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

/// <summary>Immutable result of binding one delegate creation site. Demand discovery records the
/// symbolic identity; emission supplies the already-evaluated receiver and environment leaves.</summary>
public sealed class DelegateBindingPlan
{
    public DelegateBindingKind Kind { get; }
    public IMethodSymbol TargetMethod { get; }
    public string BridgeName { get; }
    public CLeaf FunctionReference { get; }
    public CLeaf TargetInstance { get; }
    public CLeaf Environment { get; }

    public DelegateBindingPlan(DelegateBindingKind kind, IMethodSymbol targetMethod,
        string bridgeName, CLeaf functionReference, CLeaf targetInstance, CLeaf environment)
    {
        Kind = kind;
        TargetMethod = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
        BridgeName = !string.IsNullOrEmpty(bridgeName)
            ? bridgeName : throw new ArgumentException("Delegate bridge name is required.", nameof(bridgeName));
        FunctionReference = functionReference
            ?? throw new ArgumentNullException(nameof(functionReference));
        TargetInstance = targetInstance;
        Environment = environment;
    }
}
