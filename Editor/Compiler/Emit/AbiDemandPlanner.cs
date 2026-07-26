using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Materializes every semantic ABI query reachable from one closed operation
/// tree. It may conservatively probe unused accessors; only successful
/// decisions are published.
/// </summary>
internal sealed class AbiDemandPlanner
{
    readonly LoweringServices _lowering;
    readonly BoundAbiPlanBuilder _abi;

    public AbiDemandPlanner(
        LoweringServices lowering,
        BoundAbiPlanBuilder abi)
    {
        _lowering = lowering
            ?? throw new ArgumentNullException(nameof(lowering));
        _abi = abi ?? throw new ArgumentNullException(nameof(abi));
    }

    public void Plan(
        IOperation operation,
        CallSiteBindingScope scope,
        ClosedConversionPlan? conversionPlan = null)
    {
        if (operation == null) return;
        switch (operation)
        {
            case IInvocationOperation invocation:
                PlanInvocation(invocation, scope);
                break;
            case IConversionOperation conversion:
                PlanConversion(conversion, scope, conversionPlan);
                break;
            case IFieldReferenceOperation field:
                PlanField(field, scope);
                break;
            case IPropertyReferenceOperation property:
                PlanProperty(property, scope);
                break;
            case IBinaryOperation binary:
                PlanOperator(
                    binary, binary.OperatorMethod, scope);
                break;
            case IUnaryOperation unary:
                PlanOperator(
                    unary, unary.OperatorMethod, scope);
                break;
            case ICompoundAssignmentOperation compound:
                PlanOperator(
                    compound, compound.OperatorMethod, scope);
                break;
        }
    }

    void PlanInvocation(
        IInvocationOperation operation,
        CallSiteBindingScope scope)
    {
        var method = _lowering.CloseMethodForPlanning(
            operation.TargetMethod);
        if (method.ContainingType is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;
        (
            IMethodSymbol Method,
            string Owner,
            string[] ParameterOverride) request;
        try
        {
            request = _lowering.DescribeExternMethodAbi(
                method, operation.Instance?.Type);
        }
        catch (Exception ex) when (
            ex is NotSupportedException
            or InvalidOperationException)
        {
            _abi.RecordOperationFailure(
                operation, scope, BoundAbiRole.Invocation,
                ex.Message);
            return;
        }
        if (_abi.TryBindMethod(
                request.Method, request.Owner, TypeName,
                request.ParameterOverride, out var standard))
            _abi.RecordOperation(
                operation, scope, BoundAbiRole.Invocation, standard);
        else
            _abi.RecordOperationFailure(
                operation, scope, BoundAbiRole.Invocation,
                $"No registered Udon extern implements method "
                + $"'{request.Method.ToDisplayString()}' for ABI owner "
                + $"'{request.Owner}'.");

        var last = method.Parameters.Length - 1;
        if (last < 0
            || !method.Parameters[last].IsParams
            || operation.Arguments.Length != method.Parameters.Length
            || operation.Arguments[last].ArgumentKind
            != ArgumentKind.ParamArray)
            return;

        var value = operation.Arguments[last].Value;
        while (value is IConversionOperation conversion)
            value = conversion.Operand;
        if (value is not IArrayCreationOperation array)
            return;
        var count = array.Initializer?.ElementValues.Length ?? 0;
        var parameters = new List<string>();
        for (var index = 0; index < last; index++)
            parameters.Add(TypeName(method.Parameters[index].Type));
        for (var index = 0; index < count; index++)
            parameters.Add("SystemObject");
        request = _lowering.DescribeExternMethodAbi(
            method, operation.Instance?.Type, parameters.ToArray());
        if (_abi.TryBindMethod(
                request.Method, request.Owner, TypeName,
                request.ParameterOverride, out var expanded))
            _abi.RecordOperation(
                operation, scope,
                BoundAbiRole.ExpandedParamsInvocation,
                expanded);
        else
            _abi.RecordOperationFailure(
                operation, scope,
                BoundAbiRole.ExpandedParamsInvocation,
                $"No expanded params extern is registered for "
                + $"'{request.Method.ToDisplayString()}'.");
    }

    void PlanConversion(
        IConversionOperation operation,
        CallSiteBindingScope scope,
        ClosedConversionPlan? conversionPlan)
    {
        if (operation.Operand.Type == null || operation.Type == null)
            return;
        var plan = conversionPlan;
        var source = plan?.SourceType
                     ?? _lowering.ResolveType(operation.Operand.Type);
        var destination = _lowering.ResolveType(operation.Type);
        var rawMethod = plan?.OperatorMethod
                        ?? operation.OperatorMethod;
        if (rawMethod == null) return;
        var method = _lowering.CloseMethodForPlanning(rawMethod);
        if (method.ContainingType is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;
        Bind(
            operation,
            scope,
            BoundAbiRole.Conversion,
            () => _abi.BindConversion(
                method, source, destination, TypeName));
    }

    void PlanField(
        IFieldReferenceOperation operation,
        CallSiteBindingScope scope)
    {
        var field = operation.Field;
        var valueType = TypeName(field.Type);
        string owner;
        try
        {
            owner = Owner(
                field.ContainingType,
                operation.Instance?.Type,
                field.Name);
        }
        catch (Exception ex) when (
            ex is NotSupportedException
            or InvalidOperationException)
        {
            foreach (var role in new[]
                     {
                         BoundAbiRole.FieldGet,
                         BoundAbiRole.FieldSetValue,
                         BoundAbiRole.FieldSetReference,
                     })
                _abi.RecordOperationFailure(
                    operation, scope, role, ex.Message);
            return;
        }
        var hasReceiver = !field.IsStatic;
        Bind(
            operation, scope, BoundAbiRole.FieldGet,
            () => _abi.BindPropertyGetter(
                owner, field.Name, valueType, hasReceiver));
        Bind(
            operation, scope, BoundAbiRole.FieldSetValue,
            () => _abi.BindFieldSetter(
                owner, field.Name, valueType,
                isValueType: true, hasReceiver));
        Bind(
            operation, scope, BoundAbiRole.FieldSetReference,
            () => _abi.BindFieldSetter(
                owner, field.Name, valueType,
                isValueType: false, hasReceiver));
    }

    void PlanProperty(
        IPropertyReferenceOperation operation,
        CallSiteBindingScope scope)
    {
        var property = operation.Property;
        var valueType = TypeName(property.Type);
        var indexTypes = operation.Arguments
            .Select(argument => TypeName(argument.Value.Type))
            .ToArray();
        string owner;
        try
        {
            owner = PropertyOwner(operation);
        }
        catch (Exception ex) when (
            ex is NotSupportedException
            or InvalidOperationException)
        {
            foreach (var role in property.IsIndexer
                         ? new[]
                         {
                             BoundAbiRole.IndexerGet,
                             BoundAbiRole.IndexerSet,
                         }
                         : new[]
                         {
                             BoundAbiRole.PropertyGet,
                             BoundAbiRole.PropertySet,
                         })
                _abi.RecordOperationFailure(
                    operation, scope, role, ex.Message);
            return;
        }
        var hasReceiver = !property.IsStatic;
        if (property.IsIndexer)
        {
            Bind(
                operation, scope, BoundAbiRole.IndexerGet,
                () => _abi.BindIndexerGetter(
                    owner, property.MetadataName, indexTypes,
                    valueType, hasReceiver));
            Bind(
                operation, scope, BoundAbiRole.IndexerSet,
                () => _abi.BindIndexerSetter(
                    owner, property.MetadataName, indexTypes,
                    valueType, hasReceiver));
        }
        else
        {
            Bind(
                operation, scope, BoundAbiRole.PropertyGet,
                () => _abi.BindPropertyGetter(
                    owner, property.Name, valueType, hasReceiver));
            Bind(
                operation, scope, BoundAbiRole.PropertySet,
                () => _abi.BindPropertySetter(
                    owner, property.Name, valueType, hasReceiver));
        }
    }

    void PlanOperator(
        IOperation operation,
        IMethodSymbol rawMethod,
        CallSiteBindingScope scope)
    {
        if (rawMethod == null) return;
        var method = _lowering.CloseMethodForPlanning(rawMethod);
        if (method.ContainingType is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;
        Bind(
            operation, scope, BoundAbiRole.Operator,
            () => _abi.BindExact(
                AbiDecisionKey.Operator(method, TypeName)));
    }

    string Owner(
        ITypeSymbol declaringType,
        ITypeSymbol instanceType,
        string memberName)
        => TypeName(
            instanceType == null
                ? declaringType
                : _lowering.ResolveExternOwnerType(
                    declaringType, instanceType, memberName));

    string PropertyOwner(IPropertyReferenceOperation operation)
    {
        var property = operation.Property;
        if (operation.Instance?.Type is IArrayTypeSymbol array
            && property.Name != "Length")
            return TypeName(array);
        return Owner(
            property.ContainingType,
            operation.Instance?.Type,
            property.Name);
    }

    void Bind(
        IOperation operation,
        CallSiteBindingScope scope,
        BoundAbiRole role,
        Func<BoundExtern> bind)
    {
        try
        {
            _abi.RecordOperation(
                operation, scope, role, bind());
        }
        catch (NotSupportedException ex)
        {
            // Internal/intrinsic shapes may never consume this role. If body
            // lowering does select it, the captured SDK diagnosis stays loud.
            _abi.RecordOperationFailure(
                operation, scope, role, ex.Message);
        }
    }

    string TypeName(ITypeSymbol type)
        => _lowering.GetStorageTypeName(type);
}
