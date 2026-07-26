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
                PlanBinary(binary, scope);
                break;
            case IUnaryOperation unary:
                PlanUnary(unary, scope);
                break;
            case ICompoundAssignmentOperation compound:
                PlanCompoundAssignment(compound, scope);
                break;
            case IIncrementOrDecrementOperation increment:
                PlanIncrementOrDecrement(increment, scope);
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
        var last = method.Parameters.Length - 1;
        IArrayCreationOperation paramsArray = null;
        if (last >= 0
            && method.Parameters[last].IsParams
            && operation.Arguments.Length == method.Parameters.Length
            && operation.Arguments[last].ArgumentKind
            == ArgumentKind.ParamArray)
        {
            var paramsValue = operation.Arguments[last].Value;
            while (paramsValue is IConversionOperation conversion)
                paramsValue = conversion.Operand;
            paramsArray = paramsValue as IArrayCreationOperation;
        }
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
            if (paramsArray != null)
                _abi.RecordOperationFailure(
                    operation, scope, BoundAbiRole.ParamsInvocation,
                    ex.Message);
            return;
        }
        BoundExtern standard = null;
        var standardFailure =
            $"No registered Udon extern implements method "
            + $"'{request.Method.ToDisplayString()}' for ABI owner "
            + $"'{request.Owner}'.";
        if (_abi.TryBindMethod(
                request.Method, request.Owner, TypeName,
                request.ParameterOverride, out standard))
            _abi.RecordOperation(
                operation, scope, BoundAbiRole.Invocation, standard);
        else
            _abi.RecordOperationFailure(
                operation, scope, BoundAbiRole.Invocation,
                standardFailure);

        if (paramsArray == null)
            return;

        var count =
            paramsArray.Initializer?.ElementValues.Length ?? 0;
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
                BoundAbiRole.ParamsInvocation,
                expanded);
        else if (standard != null)
            _abi.RecordOperation(
                operation, scope,
                BoundAbiRole.ParamsInvocation,
                standard);
        else
            _abi.RecordOperationFailure(
                operation, scope,
                BoundAbiRole.ParamsInvocation,
                standardFailure + " No expanded params extern is registered.");
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

    void PlanBinary(
        IBinaryOperation operation,
        CallSiteBindingScope scope)
    {
        if (PlanMethodOperator(
                operation, operation.OperatorMethod, scope))
            return;

        var resultName = TypeName(operation.Type);
        if (operation.OperatorKind == BinaryOperatorKind.Remainder
            && LoweringServices.RemainderNeedsPolyfill(resultName))
        {
            PlanRemainderPolyfill(operation, scope, resultName);
            return;
        }

        var leftNullable = EmitPolicy.IsNullableT(
            operation.LeftOperand.Type, out var leftUnderlying);
        var rightNullable = EmitPolicy.IsNullableT(
            operation.RightOperand.Type, out var rightUnderlying);
        if (operation.IsLifted
            && (leftNullable || rightNullable))
        {
            var left = leftNullable
                ? leftUnderlying
                : operation.LeftOperand.Type;
            var right = rightNullable
                ? rightUnderlying
                : operation.RightOperand.Type;
            var result = EmitPolicy.IsNullableT(
                operation.Type, out var resultUnderlying)
                ? resultUnderlying
                : operation.Type;
            PlanBuiltInBinary(
                operation, scope,
                operation.OperatorKind == BinaryOperatorKind.NotEquals
                    ? BinaryOperatorKind.Equals
                    : operation.OperatorKind,
                left, right, result);
            return;
        }

        PlanBuiltInBinary(
            operation, scope, operation.OperatorKind,
            operation.LeftOperand.Type,
            operation.RightOperand.Type,
            operation.Type);
    }

    void PlanUnary(
        IUnaryOperation operation,
        CallSiteBindingScope scope)
    {
        if (operation.OperatorMethod?.ContainingType
                is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;

        if (operation.OperatorKind
            == UnaryOperatorKind.BitwiseNegation)
        {
            var operand = EmitPolicy.IsNullableT(
                operation.Operand.Type, out var operandUnderlying)
                ? operandUnderlying
                : operation.Operand.Type;
            var result = EmitPolicy.IsNullableT(
                operation.Type, out var resultUnderlying)
                ? resultUnderlying
                : operation.Type;
            PlanBuiltInBinary(
                operation, scope, BinaryOperatorKind.ExclusiveOr,
                operand, operand, result);
            return;
        }

        if (operation.OperatorMethod != null
            && !ExternResolver.IsNumericType(operation.Operand.Type)
            && PlanMethodOperator(
                operation, operation.OperatorMethod, scope))
            return;

        var effectiveOperand = operation.Operand.Type;
        var effectiveResult = operation.Type;
        if (operation.IsLifted
            && EmitPolicy.IsNullableT(
                operation.Type, out var liftedResult))
        {
            effectiveOperand = liftedResult;
            effectiveResult = liftedResult;
        }
        Bind(
            operation, scope, BoundAbiRole.Operator,
            () => _abi.BindExact(
                ExternResolver.ResolveBuiltInUnaryExtern(
                    operation.OperatorKind,
                    _lowering.ResolveType(effectiveOperand),
                    _lowering.ResolveType(effectiveResult),
                    TypeName)));
    }

    void PlanCompoundAssignment(
        ICompoundAssignmentOperation operation,
        CallSiteBindingScope scope)
    {
        if (PlanMethodOperator(
                operation, operation.OperatorMethod, scope))
            return;

        if (EmitPolicy.IsNullableT(
                operation.Target.Type, out var targetUnderlying))
        {
            var right = EmitPolicy.IsNullableT(
                operation.Value.Type, out var valueUnderlying)
                ? valueUnderlying
                : operation.Value.Type;
            var result = EmitPolicy.IsNullableT(
                operation.Type, out var resultUnderlying)
                ? resultUnderlying
                : operation.Type;
            PlanBuiltInBinary(
                operation, scope, operation.OperatorKind,
                targetUnderlying, right, result);
            return;
        }

        var resultName = TypeName(operation.Type);
        if (operation.OperatorKind == BinaryOperatorKind.Remainder
            && LoweringServices.RemainderNeedsPolyfill(resultName))
        {
            PlanRemainderPolyfill(operation, scope, resultName);
            return;
        }
        PlanBuiltInBinary(
            operation, scope, operation.OperatorKind,
            operation.Target.Type,
            operation.Value.Type,
            operation.Type);
    }

    void PlanIncrementOrDecrement(
        IIncrementOrDecrementOperation operation,
        CallSiteBindingScope scope)
    {
        if (operation.OperatorMethod?.ContainingType
                is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;
        var operand = EmitPolicy.IsNullableT(
            operation.Type, out var underlying)
            ? underlying
            : operation.Type;
        PlanBuiltInBinary(
            operation, scope,
            operation.Kind == OperationKind.Increment
                ? BinaryOperatorKind.Add
                : BinaryOperatorKind.Subtract,
            operand, operand, operand);
    }

    bool PlanMethodOperator(
        IOperation operation,
        IMethodSymbol rawMethod,
        CallSiteBindingScope scope)
    {
        if (rawMethod == null) return false;
        var method = _lowering.CloseMethodForPlanning(rawMethod);
        if (method.ContainingType is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return true;
        Bind(
            operation, scope, BoundAbiRole.Operator,
            () => _abi.BindExact(
                AbiDecisionKey.Operator(method, TypeName)));
        return true;
    }

    void PlanBuiltInBinary(
        IOperation operation,
        CallSiteBindingScope scope,
        BinaryOperatorKind kind,
        ITypeSymbol left,
        ITypeSymbol right,
        ITypeSymbol result)
        => Bind(
            operation, scope, BoundAbiRole.Operator,
            () => _abi.BindExact(
                ExternResolver.ResolveBuiltInBinaryExtern(
                    kind,
                    _lowering.ResolveType(left),
                    _lowering.ResolveType(right),
                    _lowering.ResolveType(result),
                    TypeName)));

    void PlanRemainderPolyfill(
        IOperation operation,
        CallSiteBindingScope scope,
        string type)
    {
        Bind(
            operation, scope, BoundAbiRole.RemainderDivision,
            () => _abi.BindExact(UdonAbiKey.Binary(
                type, "op_Division", type, type, type)));
        Bind(
            operation, scope, BoundAbiRole.RemainderMultiplication,
            () => _abi.BindExact(UdonAbiKey.Binary(
                type, "op_Multiplication", type, type, type)));
        Bind(
            operation, scope, BoundAbiRole.RemainderSubtraction,
            () => _abi.BindExact(UdonAbiKey.Binary(
                type, "op_Subtraction", type, type, type)));
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
