using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>The callable meaning carried directly by one Roslyn operation. Consumers add their own
/// reachability, dispatch, ABI, or export policy after this normalization step.</summary>
internal enum CallableSiteKind
{
    Method,
    Constructor,
    PropertyGet,
    PropertySet,
    EventAdd,
    EventRemove,
    Operator,
    Conversion,
}

internal readonly struct CallableSite
{
    public readonly CallableSiteKind Kind;
    public readonly IMethodSymbol Target;
    public readonly IOperation Operation;
    public readonly IOperation Receiver;

    public CallableSite(CallableSiteKind kind, IMethodSymbol target, IOperation operation,
        IOperation receiver = null)
    {
        Kind = kind;
        Target = target;
        Operation = operation;
        Receiver = receiver;
    }
}

internal static class CallableSites
{
    /// <summary>Normalize explicit callable-bearing operation shapes. Property references yield both
    /// accessors conservatively because a standalone Roslyn node does not encode its read/write context;
    /// consumers that know the context select the appropriate kind.</summary>
    public static IEnumerable<CallableSite> FromOperation(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation invocation when invocation.TargetMethod != null:
                yield return new CallableSite(
                    invocation.TargetMethod.MethodKind == MethodKind.Constructor
                        ? CallableSiteKind.Constructor : CallableSiteKind.Method,
                    invocation.TargetMethod, operation, invocation.Instance);
                yield break;
            case IObjectCreationOperation creation when creation.Constructor != null:
                yield return new CallableSite(
                    CallableSiteKind.Constructor, creation.Constructor, operation);
                yield break;
            case IMethodReferenceOperation methodReference when methodReference.Method != null:
                yield return new CallableSite(
                    CallableSiteKind.Method, methodReference.Method, operation, methodReference.Instance);
                yield break;
            case IPropertyReferenceOperation property:
                if (property.Property.GetMethod != null)
                    yield return new CallableSite(
                        CallableSiteKind.PropertyGet, property.Property.GetMethod, operation, property.Instance);
                if (property.Property.SetMethod != null)
                    yield return new CallableSite(
                        CallableSiteKind.PropertySet, property.Property.SetMethod, operation, property.Instance);
                yield break;
            case IEventAssignmentOperation assignment
                when assignment.EventReference is IEventReferenceOperation eventReference:
            {
                var target = assignment.Adds
                    ? eventReference.Event.AddMethod : eventReference.Event.RemoveMethod;
                if (target != null)
                    yield return new CallableSite(
                        assignment.Adds ? CallableSiteKind.EventAdd : CallableSiteKind.EventRemove,
                        target, operation, eventReference.Instance);
                yield break;
            }
        }

        var operatorMethod = OperatorMethod(operation);
        if (operatorMethod != null)
            yield return new CallableSite(
                operatorMethod.MethodKind == MethodKind.Conversion
                    ? CallableSiteKind.Conversion : CallableSiteKind.Operator,
                operatorMethod, operation);
    }

    public static IMethodSymbol OperatorMethod(IOperation operation)
        => (operation as IBinaryOperation)?.OperatorMethod
           ?? (operation as IUnaryOperation)?.OperatorMethod
           ?? (operation as ICompoundAssignmentOperation)?.OperatorMethod
           ?? (operation as IIncrementOrDecrementOperation)?.OperatorMethod
           ?? (operation as IConversionOperation)?.OperatorMethod;
}
