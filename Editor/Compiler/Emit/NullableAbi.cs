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

    public static CLeaf EmitGetValueOrDefault(CoreBuilder builder, CLeaf nullableValue, string resultType,
        CLeaf fallbackValue, Func<CLeaf, CLeaf> presentValue,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        var resultSlot = allocTemp(resultType);
        emitAssign(resultSlot, fallbackValue);
        builder.EmitIf(HasValue(builder, nullableValue),
            _ => emitAssign(resultSlot, presentValue(nullableValue)));
        return slotRef(resultSlot);
    }

    public static CLeaf EmitCoalesce(CoreBuilder builder, CValue leftValue, string resultType,
        Func<CLeaf> whenNullValue, Func<CLeaf, CLeaf> presentValue,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        var resultSlot = allocTemp(resultType);
        emitAssign(resultSlot, leftValue);
        Action<CoreBuilder> elseBranch = presentValue == null
            ? null
            : _ => emitAssign(resultSlot, presentValue(slotRef(resultSlot)));
        builder.EmitIf(IsNull(builder, slotRef(resultSlot)),
            _ => emitAssign(resultSlot, whenNullValue()),
            elseBranch);
        return slotRef(resultSlot);
    }

    public static CLeaf EmitCoalesceAssignment(CoreBuilder builder, CValue currentValue, string resultType,
        Func<CLeaf> whenNullValue, Action<CLeaf> writeBack,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        var resultSlot = allocTemp(resultType);
        emitAssign(resultSlot, currentValue);
        builder.EmitIf(IsNull(builder, slotRef(resultSlot)), _ =>
        {
            var rightValue = whenNullValue();
            emitAssign(resultSlot, rightValue);
            writeBack(rightValue);
        });
        return slotRef(resultSlot);
    }

    public static CLeaf EmitConditionalAccess(CoreBuilder builder, CLeaf targetValue, bool isVoid, string resultType,
        Func<CLeaf, CLeaf> whenNotNull,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        int resultSlot = -1;
        if (!isVoid)
        {
            resultSlot = allocTemp(resultType);
            emitAssign(resultSlot, builder.Const(null, resultType));
        }

        builder.EmitIf(IsNotNull(builder, targetValue), _ =>
        {
            var accessValue = whenNotNull(targetValue);
            if (!isVoid && accessValue != null)
                emitAssign(resultSlot, accessValue);
        });

        return resultSlot >= 0 ? slotRef(resultSlot) : null;
    }

    public static CLeaf EmitPatternCheck(CoreBuilder builder, CValue value, ITypeSymbol underlyingType,
        IPatternOperation pattern, Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> matchUnderlying,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        var nullableSlot = allocTemp(StorageType);
        emitAssign(nullableSlot, value);
        if (pattern is IConstantPatternOperation cpn && cpn.Value.ConstantValue is { HasValue: true, Value: null })
            return IsNull(builder, slotRef(nullableSlot));

        var matchSlot = allocTemp("SystemBoolean");
        emitAssign(matchSlot, builder.Const(false, "SystemBoolean"));
        builder.EmitIf(HasValue(builder, slotRef(nullableSlot)),
            _ => emitAssign(matchSlot, matchUnderlying(slotRef(nullableSlot), underlyingType, pattern)));
        return slotRef(matchSlot);
    }

    public static CLeaf EmitLiftedBoolLogic(CoreBuilder builder, CValue leftValue, CValue rightValue,
        BinaryOperatorKind kind, Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        var aSlot = allocTemp(StorageType);
        emitAssign(aSlot, leftValue);
        var bSlot = allocTemp(StorageType);
        emitAssign(bSlot, rightValue);

        void IfBool(int slot, bool wantTrue, Action<CoreBuilder> body)
        {
            CLeaf boolCond = wantTrue
                ? slotRef(slot)
                : builder.ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CLeaf> { slotRef(slot) }, "SystemBoolean");
            builder.EmitIf(HasValue(builder, slotRef(slot)), _ => builder.EmitIf(boolCond, body));
        }

        var resultSlot = allocTemp(StorageType);
        emitAssign(resultSlot, builder.Const(null, StorageType));
        bool isAnd = kind == BinaryOperatorKind.And;
        // Dominating value: false for &, true for |.
        IfBool(aSlot, !isAnd, _ => emitAssign(resultSlot, builder.Const(!isAnd, "SystemBoolean")));
        IfBool(bSlot, !isAnd, _ => emitAssign(resultSlot, builder.Const(!isAnd, "SystemBoolean")));
        // Both non-dominating values: both true for &, both false for |.
        IfBool(aSlot, isAnd, _ => IfBool(bSlot, isAnd, __ => emitAssign(resultSlot, builder.Const(isAnd, "SystemBoolean"))));
        return slotRef(resultSlot);
    }

    public static CLeaf EmitLiftedBinaryCore(CoreBuilder builder,
        CValue leftValue, bool leftNullable, ITypeSymbol leftUnderlying,
        CValue rightValue, bool rightNullable, ITypeSymbol rightUnderlying,
        BinaryOperatorKind kind, IMethodSymbol operatorMethod, ITypeSymbol resultType, ITypeSymbol int32Type,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef,
        Func<ITypeSymbol, string> getUdonType, Func<ITypeSymbol, ITypeSymbol> resolveType,
        Func<CLeaf, ITypeSymbol, (CLeaf Value, ITypeSymbol EffectiveType)> promoteBoxed,
        Func<CLeaf, string, string, CLeaf> narrowConvert)
    {
        var resultNullable = EmitPolicy.IsNullableT(resultType, out var resultUnderlying);

        var aSlot = allocTemp(StorageType);
        emitAssign(aSlot, leftValue);
        var bSlot = allocTemp(StorageType);
        emitAssign(bSlot, rightValue);

        void IfBothPresent(Action<CoreBuilder> body)
        {
            Action<CoreBuilder> inner = rightNullable
                ? _ => builder.EmitIf(HasValue(builder, slotRef(bSlot)), body)
                : body;
            if (leftNullable) builder.EmitIf(HasValue(builder, slotRef(aSlot)), inner);
            else inner(builder);
        }

        CValue ValueOp(BinaryOperatorKind opKind)
        {
            var resultUnder = resultNullable ? resultUnderlying : resultType;
            var (leftOperand, leftEffective) = promoteBoxed(slotRef(aSlot), leftUnderlying);
            var (rightOperand, rightEffective) = promoteBoxed(slotRef(bSlot), rightUnderlying);
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
            var resultSlot = allocTemp(StorageType);
            emitAssign(resultSlot, builder.Const(null, StorageType));
            IfBothPresent(_ => emitAssign(resultSlot, ValueOp(kind)));
            return slotRef(resultSlot);
        }

        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var eqSlot = allocTemp("SystemBoolean");
            emitAssign(eqSlot, builder.Const(false, "SystemBoolean"));
            if (leftNullable && rightNullable)
                builder.EmitIf(IsNull(builder, slotRef(aSlot)),
                    _ => builder.EmitIf(IsNull(builder, slotRef(bSlot)),
                        __ => emitAssign(eqSlot, builder.Const(true, "SystemBoolean"))));
            IfBothPresent(_ => emitAssign(eqSlot, ValueOp(BinaryOperatorKind.Equals)));
            if (kind == BinaryOperatorKind.NotEquals)
                return builder.ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CLeaf> { slotRef(eqSlot) }, "SystemBoolean");
            return slotRef(eqSlot);
        }

        var relSlot = allocTemp("SystemBoolean");
        emitAssign(relSlot, builder.Const(false, "SystemBoolean"));
        IfBothPresent(_ => emitAssign(relSlot, ValueOp(kind)));
        return slotRef(relSlot);
    }

    public static CLeaf EmitLiftedUnary(CoreBuilder builder, CValue operandValue,
        ITypeSymbol operandUnderlying, ITypeSymbol resultUnderlying, UnaryOperatorKind kind,
        Func<ITypeSymbol, string> getUdonType,
        Func<CLeaf, ITypeSymbol, (CLeaf Value, ITypeSymbol EffectiveType)> promoteBoxed,
        Func<string, int> allocTemp, Action<int, CValue> emitAssign, Func<int, CLeaf> slotRef)
    {
        var resultUdonType = getUdonType(resultUnderlying);
        var opName = kind switch
        {
            UnaryOperatorKind.Not => "op_UnaryNegation",
            UnaryOperatorKind.Minus => resultUdonType == "SystemDecimal" ? "op_UnaryNegation" : "op_UnaryMinus",
            _ => throw new NotSupportedException($"Unsupported lifted unary operator: {kind}")
        };

        var nullableSlot = allocTemp(StorageType);
        emitAssign(nullableSlot, operandValue);
        var resultSlot = allocTemp(StorageType);
        emitAssign(resultSlot, builder.Const(null, StorageType));
        builder.EmitIf(HasValue(builder, slotRef(nullableSlot)), _ =>
        {
            var (value, _) = promoteBoxed(slotRef(nullableSlot), operandUnderlying);
            var computed = builder.ExternCall(
                ExternResolver.BuildMethodSignature(resultUdonType, $"__{opName}", new[] { resultUdonType }, resultUdonType),
                new List<CLeaf> { value }, resultUdonType);
            emitAssign(resultSlot, computed);
        });
        return slotRef(resultSlot);
    }
}
