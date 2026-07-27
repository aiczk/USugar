using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

internal enum ClosedConversionKind
{
    None,
    Identity,
    UserOperator,
    Enumeration,
    ObjectNumeric,
    Numeric,
    Representation,
}

/// <summary>
/// A conversion decision made while the bound program is materialized.
/// Body lowering consumes this value and never asks Roslyn to classify the
/// closed pair again.
/// </summary>
internal readonly struct ClosedConversionPlan
{
    public readonly ClosedConversionKind Kind;
    public readonly ITypeSymbol SourceType;
    public readonly IMethodSymbol OperatorMethod;
    public readonly string DelegateWrapperName;

    public ClosedConversionPlan(
        ClosedConversionKind kind,
        ITypeSymbol sourceType,
        IMethodSymbol operatorMethod = null,
        string delegateWrapperName = null)
    {
        if (delegateWrapperName != null
            && kind != ClosedConversionKind.None)
            throw new ArgumentException(
                "Delegate wrapper conversions must remain representation-preserving.",
                nameof(kind));
        Kind = kind;
        SourceType = sourceType;
        OperatorMethod = operatorMethod;
        DelegateWrapperName = delegateWrapperName;
    }
}

internal readonly struct BoundConversionKey
    : IEquatable<BoundConversionKey>
{
    readonly IConversionOperation _operation;
    readonly CallSiteBindingScope _scope;

    public BoundConversionKey(
        IConversionOperation operation,
        CallSiteBindingScope scope)
    {
        _operation = operation
            ?? throw new ArgumentNullException(nameof(operation));
        _scope = scope;
    }

    public bool Equals(BoundConversionKey other)
        => ReferenceEquals(_operation, other._operation)
           && _scope.Equals(other._scope);

    public override bool Equals(object obj)
        => obj is BoundConversionKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return _operation.GetHashCode() * 31 + _scope.GetHashCode();
        }
    }
}

/// <summary>
/// Roslyn-facing conversion authority. This object exists only while the
/// bound program is being built and is never retained by it.
/// </summary>
internal sealed class ConversionSemanticPlanner
{
    readonly LoweringServices _lowering;
    readonly CSharpCompilation _compilation;

    public ConversionSemanticPlanner(LoweringServices lowering)
    {
        _lowering = lowering
            ?? throw new ArgumentNullException(nameof(lowering));
        _compilation = lowering.Compilation as CSharpCompilation
            ?? throw new InvalidOperationException(
                "Closed conversion planning requires a C# compilation.");
    }

    public ClosedConversionPlan Plan(IConversionOperation conversion)
    {
        if (conversion == null)
            throw new ArgumentNullException(nameof(conversion));

        var semanticSource = _lowering.ResolveType(conversion.Operand.Type);
        var closedDestination = _lowering.ResolveType(conversion.Type);
        if (DelegateDemandPolicy.TryGetVariantConversion(
                _lowering.Compilation,
                conversion,
                _lowering.State.Types,
                _lowering.State.TypeParamMap,
                out var outerInvoke,
                out var innerInvoke))
            return new ClosedConversionPlan(
                ClosedConversionKind.None,
                semanticSource,
                delegateWrapperName: _lowering.PlanWrapperSig(
                    outerInvoke,
                    innerInvoke,
                    _lowering.State.TypeParamMap));
        if (conversion.OperatorMethod != null
            || semanticSource?.SpecialType != SpecialType.System_Object
            || closedDestination == null
            || closedDestination is ITypeParameterSymbol
            || EmitPolicy.IsNullableT(closedDestination, out _)
            || _lowering.IsObjectArrayEmulated(closedDestination)
            || closedDestination
                is INamedTypeSymbol { DelegateInvokeMethod: not null })
            return new ClosedConversionPlan(
                ClosedConversionKind.None,
                semanticSource);

        var carrier = FindCarrier(conversion.Operand);
        var effectiveSource = _lowering.ResolveType(
            carrier?.Type ?? conversion.Operand.Type) ?? semanticSource;
        var destinationStorage =
            _lowering.GetStorageTypeName(closedDestination);
        if (_lowering.GetStorageTypeName(effectiveSource)
            == destinationStorage)
            return new ClosedConversionPlan(
                ClosedConversionKind.Identity, effectiveSource);

        var classified = _compilation.ClassifyConversion(
            effectiveSource, closedDestination);
        if (classified is { IsUserDefined: true, MethodSymbol: { } method })
            return new ClosedConversionPlan(
                ClosedConversionKind.UserOperator,
                effectiveSource,
                _lowering.CloseMethodForPlanning(method));
        if (classified.IsEnumeration)
            return new ClosedConversionPlan(
                ClosedConversionKind.Enumeration, effectiveSource);

        if (ExternResolver.IsConvertibleNumericType(closedDestination))
        {
            if (effectiveSource.SpecialType == SpecialType.System_Object)
                return new ClosedConversionPlan(
                    ClosedConversionKind.ObjectNumeric, effectiveSource);
            if (ExternResolver.IsConvertibleNumericType(effectiveSource))
                return new ClosedConversionPlan(
                    ClosedConversionKind.Numeric, effectiveSource);
        }

        return new ClosedConversionPlan(
            ClosedConversionKind.Representation, effectiveSource);
    }

    IOperation FindCarrier(IOperation operation)
    {
        while (operation is IConversionOperation conversion
               && conversion.OperatorMethod == null)
        {
            var destination = _lowering.ResolveType(conversion.Type);
            var semantics = conversion.Conversion;
            var preservesCarrier =
                destination?.SpecialType == SpecialType.System_Object
                || semantics.IsIdentity
                || semantics.IsReference;
            if (!preservesCarrier) break;
            operation = conversion.Operand;
        }
        return operation;
    }
}
