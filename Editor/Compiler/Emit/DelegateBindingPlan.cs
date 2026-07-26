using System;
using System.Collections.Generic;
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
    public string InnerBridgeName { get; }

    public DelegateBindingPlan(
        DelegateBindingKind kind,
        IMethodSymbol targetMethod,
        string bridgeName,
        string innerBridgeName = null)
    {
        Kind = kind;
        TargetMethod = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
        BridgeName = !string.IsNullOrEmpty(bridgeName)
            ? bridgeName : throw new ArgumentException("Delegate bridge name is required.", nameof(bridgeName));
        InnerBridgeName = innerBridgeName;
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

public sealed class DelegateBridgeDemand
{
    public DelegateBindingPlan Binding { get; }
    public IMethodSymbol SignatureMethod { get; }
    public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParameterMap { get; }

    public DelegateBridgeDemand(DelegateBindingPlan binding, IMethodSymbol signatureMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        SignatureMethod = signatureMethod ?? binding.TargetMethod;
        TypeParameterMap = typeParameterMap;
    }
}

public sealed class DelegateWrapperDemand
{
    public DelegateBindingPlan Binding { get; }
    public IMethodSymbol OuterInvoke { get; }
    public IMethodSymbol InnerInvoke { get; }
    public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParameterMap { get; }

    public DelegateWrapperDemand(DelegateBindingPlan binding, IMethodSymbol outerInvoke,
        IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        OuterInvoke = outerInvoke ?? throw new ArgumentNullException(nameof(outerInvoke));
        InnerInvoke = innerInvoke ?? throw new ArgumentNullException(nameof(innerInvoke));
        TypeParameterMap = typeParameterMap;
    }
}
