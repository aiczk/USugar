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

    public void Plan(IOperation operation)
    {
        if (operation == null) return;
        try
        {
            switch (operation)
            {
                case IInvocationOperation invocation:
                    PlanInvocation(invocation);
                    break;
                case IConversionOperation conversion:
                    PlanConversion(conversion);
                    break;
                case IFieldReferenceOperation field:
                    PlanField(field);
                    break;
                case IPropertyReferenceOperation property:
                    PlanProperty(property);
                    break;
            }
        }
        catch (NotSupportedException)
        {
            // The census deliberately visits internal/intrinsic shapes too.
            // If body lowering selects one of them as an extern, the absent
            // decision remains a loud compiler error.
        }
    }

    void PlanInvocation(IInvocationOperation operation)
    {
        var method = _lowering.CloseMethodForPlanning(
            operation.TargetMethod);
        if (_lowering.ResolveType(operation.Instance?.Type)
                is INamedTypeSymbol receiver
            && TypeClassifier.IsObjectArrayEmulated(receiver))
            return;
        if (method.ContainingType is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;

        var request = _lowering.DescribeExternMethodAbi(
            method, operation.Instance?.Type);
        _abi.TryBindMethod(
            request.Method, request.Owner, TypeName,
            request.ParameterOverride, out _);

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
        _abi.TryBindMethod(
            request.Method, request.Owner, TypeName,
            request.ParameterOverride, out _);
    }

    void PlanConversion(IConversionOperation operation)
    {
        if (operation.Operand.Type == null || operation.Type == null)
            return;
        var source = _lowering.ResolveType(operation.Operand.Type);
        var destination = _lowering.ResolveType(operation.Type);
        var rawMethod = operation.OperatorMethod;
        if (rawMethod == null)
        {
            var carrier = operation.Operand;
            while (carrier is IConversionOperation conversion
                   && conversion.OperatorMethod == null)
                carrier = conversion.Operand;
            if (carrier.Type == null)
                return;
            source = _lowering.ResolveType(carrier.Type);
            rawMethod = (_lowering.Compilation as
                    Microsoft.CodeAnalysis.CSharp.CSharpCompilation)
                ?.ClassifyConversion(source, destination)
                .MethodSymbol;
        }
        if (rawMethod == null) return;
        var method = _lowering.CloseMethodForPlanning(rawMethod);
        if (method.ContainingType is INamedTypeSymbol aggregate
            && TypeClassifier.IsObjectArrayEmulated(aggregate))
            return;
        _abi.TryBind(() => _abi.BindConversion(
            method, source, destination, TypeName));
    }

    void PlanField(IFieldReferenceOperation operation)
    {
        var field = operation.Field;
        var valueType = TypeName(field.Type);
        foreach (var owner in Owners(
                     field.ContainingType,
                     operation.Instance?.Type,
                     field.Name))
        {
            var hasReceiver = !field.IsStatic;
            _abi.TryBind(() => _abi.BindPropertyGetter(
                owner, field.Name, valueType, hasReceiver));
            _abi.TryBind(() => _abi.BindFieldSetter(
                owner, field.Name, valueType,
                field.ContainingType.IsValueType, hasReceiver));
            _abi.TryBind(() => _abi.BindFieldSetter(
                owner, field.Name, valueType,
                !field.ContainingType.IsValueType, hasReceiver));
        }
    }

    void PlanProperty(IPropertyReferenceOperation operation)
    {
        var property = operation.Property;
        var valueType = TypeName(property.Type);
        var indexTypes = operation.Arguments
            .Select(argument => TypeName(argument.Value.Type))
            .ToArray();
        foreach (var owner in Owners(
                     property.ContainingType,
                     operation.Instance?.Type,
                     property.Name))
        {
            var hasReceiver = !property.IsStatic;
            if (property.IsIndexer)
            {
                _abi.TryBind(() => _abi.BindIndexerGetter(
                    owner, property.MetadataName, indexTypes,
                    valueType, hasReceiver));
                _abi.TryBind(() => _abi.BindIndexerSetter(
                    owner, property.MetadataName, indexTypes,
                    valueType, hasReceiver));
            }
            else
            {
                _abi.TryBind(() => _abi.BindPropertyGetter(
                    owner, property.Name, valueType, hasReceiver));
                _abi.TryBind(() => _abi.BindPropertySetter(
                    owner, property.Name, valueType, hasReceiver));
            }
        }
    }

    IEnumerable<string> Owners(
        ITypeSymbol declaringType,
        ITypeSymbol instanceType,
        string memberName)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        void Add(ITypeSymbol type)
        {
            if (type != null) owners.Add(TypeName(type));
        }

        Add(declaringType);
        Add(instanceType);
        if (instanceType != null)
            Add(_lowering.ResolveExternOwnerType(
                declaringType, instanceType, memberName));
        if (instanceType is IArrayTypeSymbol)
            owners.Add("SystemArray");
        return owners;
    }

    string TypeName(ITypeSymbol type)
        => _lowering.GetStorageTypeName(type);
}
