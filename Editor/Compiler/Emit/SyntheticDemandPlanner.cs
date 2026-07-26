using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Discovers synthetic helpers through the same policies used by body emission.</summary>
internal sealed class SyntheticDemandPlanner
{
    readonly LoweringServices _lowering;
    public SyntheticDemandPlanner(LoweringServices lowering) => _lowering = lowering;

    public DelegateBindingPlan Plan(IDelegateCreationOperation operation)
        => _lowering.PlanDelegateBridge(operation);

    public void PlanOperation(IOperation operation)
    {
        if (operation is IDelegateCreationOperation creation)
        {
            Plan(creation);
            return;
        }
        if (operation is IConversionOperation conversion
            && DelegateDemandPolicy.TryGetVariantConversion(
                _lowering.Compilation, conversion, _lowering.State.Types,
                _lowering.State.Generics.TypeParamMap,
                out var outerInvoke, out var innerInvoke))
        {
            _lowering.PlanWrapperSig(outerInvoke, innerInvoke, _lowering.State.Generics.TypeParamMap);
            return;
        }
        if (operation is ICompoundAssignmentOperation compound
            && compound.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            && _lowering.ResolveType(compound.Type) is INamedTypeSymbol delegateType
            && delegateType.DelegateInvokeMethod is { } invoke)
        {
            var signature = DelegateAbi.BuildSigPart(
                invoke, _lowering.State.Types, _lowering.State.Generics.TypeParamMap);
            _lowering.PlanMulticastSig(signature, invoke,
                compound.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine : MulticastOperations.Remove);
            return;
        }
        if (operation is IBinaryOperation binary
            && binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            && _lowering.ResolveType(binary.Type) is INamedTypeSymbol binaryDelegate
            && binaryDelegate.DelegateInvokeMethod is { } binaryInvoke)
        {
            _lowering.PlanMulticastSig(
                DelegateAbi.BuildSigPart(
                    binaryInvoke, _lowering.State.Types,
                    _lowering.State.Generics.TypeParamMap), binaryInvoke,
                binary.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine : MulticastOperations.Remove);
            return;
        }
        if (operation is IEventAssignmentOperation eventAssignment
            && eventAssignment.EventReference is IEventReferenceOperation eventReference
            && eventReference.Event.Type is { } eventType
            && _lowering.ResolveType(eventType) is INamedTypeSymbol eventDelegate
            && eventDelegate.DelegateInvokeMethod is { } eventInvoke)
        {
            _lowering.PlanMulticastSig(
                DelegateAbi.BuildSigPart(
                    eventInvoke, _lowering.State.Types,
                    _lowering.State.Generics.TypeParamMap), eventInvoke,
                eventAssignment.Adds ? MulticastOperations.Combine : MulticastOperations.Remove);
            return;
        }
        if (operation is IInvocationOperation invocation
            && invocation.TargetMethod.Name == "ToString"
            && invocation.TargetMethod.Parameters.Length == 0 && invocation.Instance != null)
        {
            var instanceType = _lowering.ResolveType(invocation.Instance.Type);
            if (instanceType is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                instanceType = nullable.TypeArguments[0];
            _lowering.PlanEnumToStringDemand(instanceType, rejectFlags: false);
            var isBase = invocation.Instance is IInstanceReferenceOperation
            {
                Syntax: Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax
            };
            var family = isBase
                         && invocation.Instance.Type?.SpecialType
                         == SpecialType.System_Object
                ? _lowering.CurrentMethod?.ContainingType
                : invocation.Instance.Type;
            _lowering.PlanClassToStringDemand(family);
        }
        else if (operation is IInterpolationOperation interpolation)
        {
            _lowering.PlanEnumToStringDemand(interpolation.Expression.Type, rejectFlags: false);
            _lowering.PlanClassToStringDemand(interpolation.Expression.Type);
        }
        else if (operation is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } concat
                 && _lowering.ResolveType(concat.Type)?.SpecialType == SpecialType.System_String)
        {
            var left = LoweringServices.UnwrapConcatOperand(concat.LeftOperand)?.Type;
            var right = LoweringServices.UnwrapConcatOperand(concat.RightOperand)?.Type;
            _lowering.PlanEnumToStringDemand(left, rejectFlags: false);
            _lowering.PlanEnumToStringDemand(right, rejectFlags: false);
            _lowering.PlanClassToStringDemand(left);
            _lowering.PlanClassToStringDemand(right);
        }
        else if (operation is ICompoundAssignmentOperation
                 { OperatorKind: BinaryOperatorKind.Add } compoundConcat
                 && _lowering.ResolveType(compoundConcat.Target.Type)?.SpecialType == SpecialType.System_String)
        {
            var value = LoweringServices.UnwrapConcatOperand(
                compoundConcat.Value)?.Type;
            _lowering.PlanEnumToStringDemand(value, rejectFlags: false);
            _lowering.PlanClassToStringDemand(value);
        }
    }

}

internal static class DelegateDemandPolicy
{
    public static bool TryGetVariantConversion(Compilation compilation,
        IConversionOperation conversion, IUdonTypeSystem types,
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
