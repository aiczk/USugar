using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Nullable<T> ABI over a boxed SystemObject value: null means no value; any non-null boxed value means present.
/// </summary>
public static class NullableAbi
{
    public const string StorageType = "SystemObject";

    public static CLeaf IsNull(CoreBuilder builder, CLeaf nullableValue)
        => builder.ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { nullableValue, builder.Const(null, StorageType) },
            "SystemBoolean");

    public static CLeaf HasValue(CoreBuilder builder, CLeaf nullableValue)
        => builder.ExternCall(
            "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            new List<CLeaf> { IsNull(builder, nullableValue) },
            "SystemBoolean");

    public static CLeaf IsNotNull(CoreBuilder builder, CLeaf nullableValue)
        => builder.ExternCall(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { nullableValue, builder.Const(null, StorageType) },
            "SystemBoolean");

    public static (CLeaf Value, ITypeSymbol EffectiveType) PromoteBoxedToInt32(CoreBuilder builder,
        CLeaf boxed, ITypeSymbol underlying, ITypeSymbol int32Type, Func<ITypeSymbol, string> getUdonType)
    {
        if (ExternResolver.IsSmallIntOrChar(getUdonType(underlying)))
        {
            return (builder.ExternCall("SystemConvert.__ToInt32__SystemObject__SystemInt32",
                new List<CLeaf> { boxed }, "SystemInt32"), int32Type);
        }
        return (boxed, underlying);
    }

    public static CLeaf EmitGetValueOrDefault(CoreBuilder builder, CLeaf nullableValue, string resultType,
        CLeaf fallbackValue, Func<CLeaf, CLeaf> presentValue)
    {
        var resultSlot = builder.AllocScratch(resultType);
        builder.EmitAssign(resultSlot, fallbackValue);
        builder.EmitIf(HasValue(builder, nullableValue),
            _ => builder.EmitAssign(resultSlot, presentValue(nullableValue)));
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitLiftedNumericConversion(CoreBuilder builder, CLeaf sourceValue,
        string destinationUdonType, string convertMethodName, bool integerToInteger,
        Func<CLeaf, string, string, CLeaf> narrowConvert)
    {
        var resultSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(resultSlot, builder.Const(null, StorageType));
        builder.EmitIf(HasValue(builder, sourceValue), _ =>
        {
            CValue converted = integerToInteger
                ? narrowConvert(
                    builder.ExternCall("SystemConvert.__ToInt64__SystemObject__SystemInt64",
                        new List<CLeaf> { sourceValue }, "SystemInt64"),
                    "SystemInt64", destinationUdonType)
                : builder.ExternCall($"SystemConvert.__{convertMethodName}__SystemObject__{destinationUdonType}",
                    new List<CLeaf> { sourceValue }, destinationUdonType);
            builder.EmitAssign(resultSlot, converted);
        });
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitCoalesce(CoreBuilder builder, CValue leftValue, string resultType,
        Func<CLeaf> whenNullValue, Func<CLeaf, CLeaf> presentValue)
    {
        var resultSlot = builder.AllocScratch(resultType);
        builder.EmitAssign(resultSlot, leftValue);
        Action<CoreBuilder> elseBranch = presentValue == null
            ? null
            : _ => builder.EmitAssign(resultSlot, presentValue(builder.SlotRef(resultSlot)));
        builder.EmitIf(IsNull(builder, builder.SlotRef(resultSlot)),
            _ => builder.EmitAssign(resultSlot, whenNullValue()),
            elseBranch);
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitCoalesceAssignment(CoreBuilder builder, CValue currentValue, string resultType,
        Func<CLeaf> whenNullValue, Action<CLeaf> writeBack)
    {
        var resultSlot = builder.AllocScratch(resultType);
        builder.EmitAssign(resultSlot, currentValue);
        builder.EmitIf(IsNull(builder, builder.SlotRef(resultSlot)), _ =>
        {
            var rightValue = whenNullValue();
            builder.EmitAssign(resultSlot, rightValue);
            writeBack(rightValue);
        });
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitConditionalAccess(CoreBuilder builder, CLeaf targetValue, bool isVoid, string resultType,
        Func<CLeaf, CLeaf> whenNotNull)
    {
        int resultSlot = -1;
        if (!isVoid)
        {
            resultSlot = builder.AllocScratch(resultType);
            builder.EmitAssign(resultSlot, builder.Const(null, resultType));
        }

        builder.EmitIf(IsNotNull(builder, targetValue), _ =>
        {
            var accessValue = whenNotNull(targetValue);
            if (!isVoid && accessValue != null)
                builder.EmitAssign(resultSlot, accessValue);
        });

        return resultSlot >= 0 ? builder.SlotRef(resultSlot) : null;
    }

    public static CLeaf EmitPatternCheck(CoreBuilder builder, CValue value, ITypeSymbol underlyingType,
        IPatternOperation pattern, Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> matchUnderlying)
    {
        var nullableSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(nullableSlot, value);
        if (pattern is IConstantPatternOperation cpn && cpn.Value.ConstantValue is { HasValue: true, Value: null })
            return IsNull(builder, builder.SlotRef(nullableSlot));

        var matchSlot = builder.AllocScratch("SystemBoolean");
        builder.EmitAssign(matchSlot, builder.Const(false, "SystemBoolean"));
        builder.EmitIf(HasValue(builder, builder.SlotRef(nullableSlot)),
            _ => builder.EmitAssign(matchSlot, matchUnderlying(builder.SlotRef(nullableSlot), underlyingType, pattern)));
        return builder.SlotRef(matchSlot);
    }

    public static CLeaf EmitLiftedBoolLogic(CoreBuilder builder, CValue leftValue, CValue rightValue,
        BinaryOperatorKind kind)
    {
        var aSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(aSlot, leftValue);
        var bSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(bSlot, rightValue);

        void IfBool(int slot, bool wantTrue, Action<CoreBuilder> body)
        {
            CLeaf boolCond = wantTrue
                ? builder.SlotRef(slot)
                : builder.ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CLeaf> { builder.SlotRef(slot) }, "SystemBoolean");
            builder.EmitIf(HasValue(builder, builder.SlotRef(slot)), _ => builder.EmitIf(boolCond, body));
        }

        var resultSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(resultSlot, builder.Const(null, StorageType));
        bool isAnd = kind == BinaryOperatorKind.And;
        // Dominating value: false for &, true for |.
        IfBool(aSlot, !isAnd, _ => builder.EmitAssign(resultSlot, builder.Const(!isAnd, "SystemBoolean")));
        IfBool(bSlot, !isAnd, _ => builder.EmitAssign(resultSlot, builder.Const(!isAnd, "SystemBoolean")));
        // Both non-dominating values: both true for &, both false for |.
        IfBool(aSlot, isAnd, _ => IfBool(bSlot, isAnd, __ => builder.EmitAssign(resultSlot, builder.Const(isAnd, "SystemBoolean"))));
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitLiftedBinaryCore(CoreBuilder builder,
        CValue leftValue, bool leftNullable, ITypeSymbol leftUnderlying,
        CValue rightValue, bool rightNullable, ITypeSymbol rightUnderlying,
        BinaryOperatorKind kind, IMethodSymbol operatorMethod, ITypeSymbol resultType, ITypeSymbol int32Type,
        Func<ITypeSymbol, string> getUdonType, Func<ITypeSymbol, ITypeSymbol> resolveType,
        Func<CLeaf, ITypeSymbol, (CLeaf Value, ITypeSymbol EffectiveType)> promoteBoxed,
        Func<CLeaf, string, string, CLeaf> narrowConvert)
    {
        var resultNullable = EmitPolicy.IsNullableT(resultType, out var resultUnderlying);

        var aSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(aSlot, leftValue);
        var bSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(bSlot, rightValue);

        void IfBothPresent(Action<CoreBuilder> body)
        {
            Action<CoreBuilder> inner = rightNullable
                ? _ => builder.EmitIf(HasValue(builder, builder.SlotRef(bSlot)), body)
                : body;
            if (leftNullable) builder.EmitIf(HasValue(builder, builder.SlotRef(aSlot)), inner);
            else inner(builder);
        }

        CValue ValueOp(BinaryOperatorKind opKind)
        {
            var resultUnder = resultNullable ? resultUnderlying : resultType;
            var (leftOperand, leftEffective) = promoteBoxed(builder.SlotRef(aSlot), leftUnderlying);
            var (rightOperand, rightEffective) = promoteBoxed(builder.SlotRef(bSlot), rightUnderlying);
            bool resultPromotes = ExternResolver.IsSmallIntOrChar(getUdonType(resultUnder));
            var resultEffective = resultPromotes ? int32Type : resultUnder;
            var raw = builder.ExternCall(
                ExternResolver.ResolveBinaryExtern(opKind, operatorMethod,
                    resolveType(leftEffective), resolveType(rightEffective), resolveType(resultEffective)),
                new List<CLeaf> { leftOperand, rightOperand }, getUdonType(resultEffective));
            return resultPromotes && getUdonType(resultUnder) != "SystemInt32"
                ? narrowConvert(raw, "SystemInt32", getUdonType(resultUnder))
                : raw;
        }

        if (resultNullable)
        {
            var resultSlot = builder.AllocScratch(StorageType);
            builder.EmitAssign(resultSlot, builder.Const(null, StorageType));
            IfBothPresent(_ => builder.EmitAssign(resultSlot, ValueOp(kind)));
            return builder.SlotRef(resultSlot);
        }

        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var eqSlot = builder.AllocScratch("SystemBoolean");
            builder.EmitAssign(eqSlot, builder.Const(false, "SystemBoolean"));
            if (leftNullable && rightNullable)
                builder.EmitIf(IsNull(builder, builder.SlotRef(aSlot)),
                    _ => builder.EmitIf(IsNull(builder, builder.SlotRef(bSlot)),
                        __ => builder.EmitAssign(eqSlot, builder.Const(true, "SystemBoolean"))));
            IfBothPresent(_ => builder.EmitAssign(eqSlot, ValueOp(BinaryOperatorKind.Equals)));
            if (kind == BinaryOperatorKind.NotEquals)
                return builder.ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CLeaf> { builder.SlotRef(eqSlot) }, "SystemBoolean");
            return builder.SlotRef(eqSlot);
        }

        var relSlot = builder.AllocScratch("SystemBoolean");
        builder.EmitAssign(relSlot, builder.Const(false, "SystemBoolean"));
        IfBothPresent(_ => builder.EmitAssign(relSlot, ValueOp(kind)));
        return builder.SlotRef(relSlot);
    }

    public static CLeaf EmitLiftedUnary(CoreBuilder builder, CValue operandValue,
        ITypeSymbol operandUnderlying, ITypeSymbol resultUnderlying, UnaryOperatorKind kind,
        Func<ITypeSymbol, string> getUdonType,
        Func<CLeaf, ITypeSymbol, (CLeaf Value, ITypeSymbol EffectiveType)> promoteBoxed)
    {
        var resultUdonType = getUdonType(resultUnderlying);
        var opName = kind switch
        {
            UnaryOperatorKind.Not => "op_UnaryNegation",
            UnaryOperatorKind.Minus => resultUdonType == "SystemDecimal" ? "op_UnaryNegation" : "op_UnaryMinus",
            _ => throw new NotSupportedException($"Unsupported lifted unary operator: {kind}")
        };

        var nullableSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(nullableSlot, operandValue);
        var resultSlot = builder.AllocScratch(StorageType);
        builder.EmitAssign(resultSlot, builder.Const(null, StorageType));
        builder.EmitIf(HasValue(builder, builder.SlotRef(nullableSlot)), _ =>
        {
            var (value, _) = promoteBoxed(builder.SlotRef(nullableSlot), operandUnderlying);
            var computed = builder.ExternCall(
                ExternResolver.BuildMethodSignature(resultUdonType, $"__{opName}", new[] { resultUdonType }, resultUdonType),
                new List<CLeaf> { value }, resultUdonType);
            builder.EmitAssign(resultSlot, computed);
        });
        return builder.SlotRef(resultSlot);
    }
}
