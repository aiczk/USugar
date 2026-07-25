using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Discovers synthetic helpers through the same policies used by body emission.</summary>
internal sealed class SyntheticDemandPlanner : HandlerBase
{
    public SyntheticDemandPlanner(EmitContext context) : base(context) { }

    public DelegateBindingPlan Plan(IDelegateCreationOperation operation)
        => PlanDelegateBridge(operation);

    public void PlanOperation(IOperation operation)
    {
        if (operation is IDelegateCreationOperation creation)
        {
            Plan(creation);
            return;
        }
        if (operation is IConversionOperation conversion
            && DelegateDemandPolicy.TryGetVariantConversion(
                _compilation, conversion, _ctx.Session.Types,
                _ctx.Generics.TypeParamMap,
                out var outerInvoke, out var innerInvoke))
        {
            RegisterWrapperSig(outerInvoke, innerInvoke, _ctx.Generics.TypeParamMap);
            return;
        }
        if (operation is ICompoundAssignmentOperation compound
            && compound.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            && ResolveType(compound.Type) is INamedTypeSymbol delegateType
            && delegateType.DelegateInvokeMethod is { } invoke)
        {
            var signature = DelegateAbi.BuildSigPart(
                invoke, _ctx.Session.Types, _ctx.Generics.TypeParamMap);
            RegisterMulticastSig(signature, invoke,
                compound.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine : MulticastOperations.Remove);
            return;
        }
        if (operation is IBinaryOperation binary
            && binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            && ResolveType(binary.Type) is INamedTypeSymbol binaryDelegate
            && binaryDelegate.DelegateInvokeMethod is { } binaryInvoke)
        {
            RegisterMulticastSig(
                DelegateAbi.BuildSigPart(
                    binaryInvoke, _ctx.Session.Types,
                    _ctx.Generics.TypeParamMap), binaryInvoke,
                binary.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine : MulticastOperations.Remove);
            return;
        }
        if (operation is IEventAssignmentOperation eventAssignment
            && eventAssignment.EventReference is IEventReferenceOperation eventReference
            && eventReference.Event.Type is { } eventType
            && ResolveType(eventType) is INamedTypeSymbol eventDelegate
            && eventDelegate.DelegateInvokeMethod is { } eventInvoke)
        {
            RegisterMulticastSig(
                DelegateAbi.BuildSigPart(
                    eventInvoke, _ctx.Session.Types,
                    _ctx.Generics.TypeParamMap), eventInvoke,
                eventAssignment.Adds ? MulticastOperations.Combine : MulticastOperations.Remove);
            return;
        }
        if (operation is IInvocationOperation invocation
            && invocation.TargetMethod.Name == "ToString"
            && invocation.TargetMethod.Parameters.Length == 0 && invocation.Instance != null)
        {
            var instanceType = ResolveType(invocation.Instance.Type);
            if (instanceType is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                instanceType = nullable.TypeArguments[0];
            RegisterEnumToStringDemand(instanceType, rejectFlags: false);
        }
        else if (operation is IInterpolationOperation interpolation)
            RegisterEnumToStringDemand(interpolation.Expression.Type, rejectFlags: false);
        else if (operation is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } concat
                 && ResolveType(concat.Type)?.SpecialType == SpecialType.System_String)
        {
            RegisterEnumToStringDemand(UnwrapConcatOperand(concat.LeftOperand)?.Type, rejectFlags: false);
            RegisterEnumToStringDemand(UnwrapConcatOperand(concat.RightOperand)?.Type, rejectFlags: false);
        }
        else if (operation is ICompoundAssignmentOperation
                 { OperatorKind: BinaryOperatorKind.Add } compoundConcat
                 && ResolveType(compoundConcat.Target.Type)?.SpecialType == SpecialType.System_String)
            RegisterEnumToStringDemand(UnwrapConcatOperand(compoundConcat.Value)?.Type, rejectFlags: false);
    }

}

internal static class DelegateDemandPolicy
{
    public static bool TryGetVariantConversion(Compilation compilation,
        IConversionOperation conversion, UdonTypeSystem types,
        System.Collections.Generic.IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        out IMethodSymbol outerInvoke, out IMethodSymbol innerInvoke)
    {
        if (types == null) throw new System.ArgumentNullException(nameof(types));
        outerInvoke = null;
        innerInvoke = null;
        var destination = TypeEnvironment.CloseType(compilation, conversion.Type, typeParameterMap)
            as INamedTypeSymbol;
        var source = TypeEnvironment.CloseType(compilation, conversion.Operand.Type, typeParameterMap)
            as INamedTypeSymbol;
        if (destination?.DelegateInvokeMethod is not { } destinationInvoke
            || source?.DelegateInvokeMethod is not { } sourceInvoke
            || SymbolEqualityComparer.Default.Equals(destination, source)
            || DelegateAbi.BuildSigPart(
                   destinationInvoke, types, typeParameterMap)
               == DelegateAbi.BuildSigPart(
                   sourceInvoke, types, typeParameterMap))
            return false;
        outerInvoke = destinationInvoke;
        innerInvoke = sourceInvoke;
        return true;
    }
}
