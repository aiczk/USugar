using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

public enum ClosedConversionKind
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
/// Closed semantic conversion selected after generic specialization. It keeps
/// planning separate from IR emission and is the single authority for casts
/// whose Roslyn operation was originally obscured by an object-erasure step.
/// </summary>
public readonly struct ClosedConversionPlan
{
    public readonly ClosedConversionKind Kind;
    public readonly ITypeSymbol SourceType;
    public readonly IMethodSymbol OperatorMethod;

    public ClosedConversionPlan(ClosedConversionKind kind,
        ITypeSymbol sourceType, IMethodSymbol operatorMethod = null)
    {
        Kind = kind;
        SourceType = sourceType;
        OperatorMethod = operatorMethod;
    }
}

public sealed class ConversionLowerer
{
    readonly LoweringState _context;

    public ConversionLowerer(LoweringState context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public ClosedConversionPlan ClassifyClosedObjectCast(
        IConversionOperation conversion, LoweredValue source,
        ITypeSymbol closedDestination)
    {
        var semanticSource = source.SemanticType;
        if (conversion == null
            || conversion.OperatorMethod != null
            || semanticSource?.SpecialType != SpecialType.System_Object
            || closedDestination == null
            || closedDestination is ITypeParameterSymbol
            || EmitPolicy.IsNullableT(closedDestination, out _)
            || TypeClassifier.IsObjectArrayEmulated(closedDestination)
            || closedDestination is INamedTypeSymbol { DelegateInvokeMethod: not null })
            return default;

        var effectiveSource = source.CarrierType ?? semanticSource;
        if (effectiveSource == null
            || _context.ResolveStorageType(effectiveSource) != source.Leaf.Type)
            effectiveSource = semanticSource;

        var destinationStorage = _context.ResolveStorageType(closedDestination);
        if (source.Leaf.Type == destinationStorage)
            return new ClosedConversionPlan(
                ClosedConversionKind.Identity, effectiveSource);

        var classified = (_context.Compilation as CSharpCompilation)
            ?.ClassifyConversion(effectiveSource, closedDestination);
        if (classified is { IsUserDefined: true, MethodSymbol: { } method })
            return new ClosedConversionPlan(
                ClosedConversionKind.UserOperator, effectiveSource, method);
        if (classified is { IsEnumeration: true })
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
}
