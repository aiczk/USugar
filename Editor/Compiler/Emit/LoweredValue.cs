using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// A lowered expression whose C# meaning remains distinct from its physical
/// Udon storage. CarrierType survives representation-preserving erasure such
/// as T -> object, so a later closed generic cast never has to reverse-walk
/// syntax to rediscover the value actually being carried.
/// </summary>
public readonly struct LoweredValue
{
    public readonly CLeaf Leaf;
    public readonly ValueInfo Info;
    public readonly ITypeSymbol SemanticType;
    public readonly ITypeSymbol CarrierType;
    public readonly RuntimeShape Shape;

    public LoweredValue(CLeaf leaf, ValueInfo info,
        ITypeSymbol semanticType, ITypeSymbol carrierType, RuntimeShape shape)
    {
        Leaf = leaf;
        Info = info;
        SemanticType = semanticType;
        CarrierType = carrierType;
        Shape = shape;
    }

    public ITypeSymbol StaticType => SemanticType ?? Info.StaticType;

    public static LoweredValue Create(EmitContext context, IOperation operation, CLeaf leaf)
    {
        if (context == null) throw new System.ArgumentNullException(nameof(context));
        if (operation == null) throw new System.ArgumentNullException(nameof(operation));
        var semanticType = TypeEnvironment.CloseType(
            context.Compilation, operation.Type, context.Generics.TypeParamMap);
        var carrierOperation = FindCarrier(operation, context);
        var carrierType = TypeEnvironment.CloseType(
            context.Compilation, carrierOperation?.Type ?? operation.Type,
            context.Generics.TypeParamMap);
        var shapeType = semanticType ?? carrierType;
        var shape = shapeType == null
            ? RuntimeShape.Native(false)
            : TypeClassifier.ShapeOf(
                shapeType, new TypeClassifierContext(context.Generics.TypeParamMap));
        return new LoweredValue(
            leaf, context.Boundary.ClassifyValue(operation),
            semanticType, carrierType, shape);
    }

    static IOperation FindCarrier(IOperation operation, EmitContext context)
    {
        while (operation is IConversionOperation conversion
               && conversion.OperatorMethod == null)
        {
            var destination = TypeEnvironment.CloseType(
                context.Compilation, conversion.Type, context.Generics.TypeParamMap);
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
