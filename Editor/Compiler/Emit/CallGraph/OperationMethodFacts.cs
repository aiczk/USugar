using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Roslyn-symbol facts carried directly by an operation, before any consumer projection.</summary>
internal static class OperationMethodFacts
{
    public static IEnumerable<IMethodSymbol> ConstructedTargets(IOperation op)
    {
        switch (op)
        {
            case IInvocationOperation invocation when invocation.TargetMethod != null:
                yield return invocation.TargetMethod;
                break;
            case IObjectCreationOperation creation when creation.Constructor != null:
                yield return creation.Constructor;
                break;
            case IMethodReferenceOperation methodReference when methodReference.Method != null:
                yield return methodReference.Method;
                break;
            case IPropertyReferenceOperation property:
                if (property.Property.GetMethod != null) yield return property.Property.GetMethod;
                if (property.Property.SetMethod != null) yield return property.Property.SetMethod;
                break;
        }
        if (OperatorMethod(op) is { } opMethod) yield return opMethod;
    }

    public static IMethodSymbol OperatorMethod(IOperation op)
        => (op as IBinaryOperation)?.OperatorMethod
           ?? (op as IUnaryOperation)?.OperatorMethod
           ?? (op as ICompoundAssignmentOperation)?.OperatorMethod
           ?? (op as IIncrementOrDecrementOperation)?.OperatorMethod
           ?? (op as IConversionOperation)?.OperatorMethod;
}
