using System;
using Microsoft.CodeAnalysis;

public enum BridgeReceiverKind { None, Environment, Payload }
public enum BridgeDispatchKind { Direct, Runtime, DelegatePayload }
public enum BridgeReturnKind { None, Convention, Field }

/// <summary>Immutable identity and ABI strategy for one synthetic bridge. Emitters describe their
/// differences as data; SyntheticBridgeBuilder owns the shared function lifecycle.</summary>
public sealed class BridgePlan
{
    public string FunctionName { get; }
    public string ExportName { get; }
    public IMethodSymbol SignatureMethod { get; }
    public CFunction DirectTarget { get; }
    public BridgeReceiverKind Receiver { get; }
    public BridgeDispatchKind Dispatch { get; }
    public BridgeReturnKind Return { get; }

    public BridgePlan(string functionName, string exportName, IMethodSymbol signatureMethod,
        CFunction directTarget, BridgeReceiverKind receiver, BridgeDispatchKind dispatch,
        BridgeReturnKind returnKind)
    {
        FunctionName = !string.IsNullOrEmpty(functionName)
            ? functionName : throw new ArgumentException("Bridge function name is required.", nameof(functionName));
        ExportName = !string.IsNullOrEmpty(exportName)
            ? exportName : throw new ArgumentException("Bridge export name is required.", nameof(exportName));
        SignatureMethod = signatureMethod
            ?? throw new ArgumentNullException(nameof(signatureMethod));
        DirectTarget = directTarget;
        Receiver = receiver;
        Dispatch = dispatch;
        Return = returnKind;
        if (dispatch == BridgeDispatchKind.Direct && directTarget == null)
            throw new ArgumentException("A direct bridge requires a target function.", nameof(directTarget));
    }
}
