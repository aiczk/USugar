using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

public enum BridgeReceiverKind { None, Environment, Payload }
public enum BridgeDispatchKind { Direct, Runtime, DelegatePayload }
public enum BridgeReturnKind { None, Convention, Field }

public sealed class BridgeArgumentAdapter
{
    public string SourceField { get; }
    public StorageType Type { get; }
    public bool Materialize { get; }

    public BridgeArgumentAdapter(string sourceField, StorageType type, bool materialize = false)
    {
        SourceField = !string.IsNullOrEmpty(sourceField)
            ? sourceField : throw new ArgumentException("Bridge argument field is required.", nameof(sourceField));
        Type = type;
        Materialize = materialize;
    }
}

public sealed class BridgeReturnAdapter
{
    public static readonly BridgeReturnAdapter None = new BridgeReturnAdapter(BridgeReturnKind.None, null);

    public BridgeReturnKind Kind { get; }
    public string DestinationField { get; }

    public BridgeReturnAdapter(BridgeReturnKind kind, string destinationField)
    {
        if (kind == BridgeReturnKind.None && destinationField != null)
            throw new ArgumentException("A void bridge cannot have a return field.", nameof(destinationField));
        if (kind != BridgeReturnKind.None && string.IsNullOrEmpty(destinationField))
            throw new ArgumentException("A non-void bridge requires a return field.", nameof(destinationField));
        Kind = kind;
        DestinationField = destinationField;
    }
}

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
    public IReadOnlyList<BridgeArgumentAdapter> Arguments { get; }
    public BridgeReturnAdapter Return { get; }

    public BridgePlan(string functionName, string exportName, IMethodSymbol signatureMethod,
        CFunction directTarget, BridgeReceiverKind receiver, BridgeDispatchKind dispatch,
        IReadOnlyList<BridgeArgumentAdapter> arguments, BridgeReturnAdapter returnAdapter)
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
        if (arguments == null) throw new ArgumentNullException(nameof(arguments));
        var frozenArguments = new List<BridgeArgumentAdapter>(arguments.Count);
        foreach (var argument in arguments)
            frozenArguments.Add(argument ?? throw new ArgumentException(
                "Bridge arguments cannot contain null adapters.", nameof(arguments)));
        Arguments = frozenArguments.AsReadOnly();
        Return = returnAdapter ?? throw new ArgumentNullException(nameof(returnAdapter));
        if (dispatch == BridgeDispatchKind.Direct && directTarget == null)
            throw new ArgumentException("A direct bridge requires a target function.", nameof(directTarget));
    }
}
