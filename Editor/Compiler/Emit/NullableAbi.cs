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

    public static bool IsNullLiteral(IOperation operation)
    {
        var unwrapped = operation;
        while (unwrapped is IConversionOperation conversion) unwrapped = conversion.Operand;
        return unwrapped is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
    }

    public static bool IsNullLiteralComparison(
        IBinaryOperation operation, out IOperation nullableOperand)
    {
        nullableOperand = null;
        if (operation.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
            return false;
        if (EmitPolicy.IsNullableT(operation.LeftOperand.Type, out _)
            && IsNullLiteral(operation.RightOperand))
            nullableOperand = operation.LeftOperand;
        else if (EmitPolicy.IsNullableT(operation.RightOperand.Type, out _)
            && IsNullLiteral(operation.LeftOperand))
            nullableOperand = operation.RightOperand;
        return nullableOperand != null;
    }

    public static CLeaf IsNull(CoreBuilder builder, CLeaf nullableValue)
        => builder.ExternCall(
            UdonAbi.ObjectEquality,
            new List<CLeaf> { nullableValue, builder.Const(null, new StorageType(StorageType)) },
            StorageTypes.Boolean);

    public static CLeaf HasValue(CoreBuilder builder, CLeaf nullableValue)
        => builder.ExternCall(
            UdonAbi.BooleanNot,
            new List<CLeaf> { IsNull(builder, nullableValue) },
            StorageTypes.Boolean);

    public static CLeaf IsNotNull(CoreBuilder builder, CLeaf nullableValue)
        => builder.ExternCall(
            UdonAbi.ObjectInequality,
            new List<CLeaf> { nullableValue, builder.Const(null, new StorageType(StorageType)) },
            StorageTypes.Boolean);

    public static (CLeaf Value, ITypeSymbol EffectiveType) PromoteBoxedToInt32(CoreBuilder builder,
        CLeaf boxed, ITypeSymbol underlying, ITypeSymbol int32Type, Func<ITypeSymbol, string> getUdonType)
    {
        if (ExternResolver.IsSmallIntOrChar(getUdonType(underlying)))
        {
            return (builder.ExternCall(UdonAbiKey.Method("SystemConvert", "ToInt32", new[] { "SystemObject" }, "SystemInt32"),
                new List<CLeaf> { boxed }, StorageTypes.Int32), int32Type);
        }
        return (boxed, underlying);
    }

    public static CLeaf EmitGetValueOrDefault(CoreBuilder builder, CLeaf nullableValue, StorageType resultType,
        CLeaf fallbackValue, Func<CLeaf, CLeaf> presentValue)
    {
        var resultSlot = builder.AllocScratch(resultType);
        builder.EmitAssign(resultSlot, fallbackValue);
        builder.EmitIf(HasValue(builder, nullableValue),
            _ => builder.EmitAssign(resultSlot, presentValue(nullableValue)));
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitLiftedNumericConversion(
        CoreBuilder builder,
        CLeaf sourceValue,
        Func<CLeaf, CLeaf> convertPresent)
    {
        var resultSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(resultSlot, builder.Const(null, new StorageType(StorageType)));
        builder.EmitIf(
            HasValue(builder, sourceValue),
            _ => builder.EmitAssign(
                resultSlot,
                convertPresent(sourceValue)));
        return builder.SlotRef(resultSlot);
    }

    public static CLeaf EmitCoalesce(CoreBuilder builder, CValue leftValue, StorageType resultType,
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

    public static CLeaf EmitCoalesceAssignment(CoreBuilder builder, CValue currentValue, StorageType resultType,
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

    public static CLeaf EmitConditionalAccess(CoreBuilder builder, CLeaf targetValue, bool isVoid, StorageType? resultType,
        Func<CLeaf, CLeaf> whenNotNull)
    {
        int resultSlot = -1;
        if (!isVoid)
        {
            var valueType = resultType ?? throw new ArgumentNullException(nameof(resultType));
            resultSlot = builder.AllocScratch(valueType);
            builder.EmitAssign(resultSlot, builder.Const(null, valueType));
        }

        builder.EmitIf(IsNotNull(builder, targetValue), _ =>
        {
            var accessValue = whenNotNull(targetValue);
            if (!isVoid && accessValue != null)
                builder.EmitAssign(resultSlot, accessValue);
        });

        return resultSlot >= 0 ? builder.SlotRef(resultSlot) : null;
    }

    /// <summary>CW16/CW19: null-gated match over the boxed nullable ABI. Seeds the result with the
    /// statically-known null answer, then overwrites it with the present-value match inside the
    /// HasValue gate. Shared by the pattern lowering below and the switch single-value clause.</summary>
    public static CLeaf EmitNullGatedMatch(CoreBuilder builder, CValue value, bool matchesNull,
        Func<CLeaf, CLeaf> matchPresent)
    {
        var nullableSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(nullableSlot, value);
        var matchSlot = builder.AllocScratch(StorageTypes.Boolean);
        builder.EmitAssign(matchSlot, builder.Const(matchesNull, StorageTypes.Boolean));
        builder.EmitIf(HasValue(builder, builder.SlotRef(nullableSlot)),
            _ => builder.EmitAssign(matchSlot, matchPresent(builder.SlotRef(nullableSlot))));
        return builder.SlotRef(matchSlot);
    }

    /// <summary>CW16: C#'s static null answer of a pattern over a nullable scrutinee. Only a null
    /// constant, var/discard, and the not/and/or combinators over those can match the null
    /// representation; every other shape (type / relational / recursive / non-null constant) answers
    /// false on null. Total over the pattern kinds Roslyn binds on a Nullable&lt;T&gt; scrutinee, so
    /// no shape needs a reject.</summary>
    public static bool PatternMatchesNull(IPatternOperation pattern) => pattern switch
    {
        IConstantPatternOperation cp => cp.Value.ConstantValue is { HasValue: true, Value: null },
        INegatedPatternOperation neg => !PatternMatchesNull(neg.Pattern),
        IBinaryPatternOperation bin => bin.OperatorKind == BinaryOperatorKind.Or
            ? PatternMatchesNull(bin.LeftPattern) || PatternMatchesNull(bin.RightPattern)
            : PatternMatchesNull(bin.LeftPattern) && PatternMatchesNull(bin.RightPattern),
        IDeclarationPatternOperation decl => decl.MatchedType == null || decl.MatchesNull,
        IDiscardPatternOperation => true,
        _ => false
    };

    public static CLeaf EmitPatternCheck(CoreBuilder builder, CValue value, ITypeSymbol underlyingType,
        IPatternOperation pattern, Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> matchUnderlying)
    {
        if (pattern is IConstantPatternOperation cpn && cpn.Value.ConstantValue is { HasValue: true, Value: null })
        {
            var nullableSlot = builder.AllocScratch(new StorageType(StorageType));
            builder.EmitAssign(nullableSlot, value);
            return IsNull(builder, builder.SlotRef(nullableSlot));
        }
        // CW16: a top-level var/discard matches the null representation and (for var) binds the raw
        // box — run it UNGATED so a null scrutinee still answers true and REBINDS (C#: `x is var y`
        // → true, y = null; a HasValue-gated bind would leave a stale value from a previous pass).
        if (pattern is IDiscardPatternOperation
            || (pattern is IDeclarationPatternOperation dpn && (dpn.MatchedType == null || dpn.MatchesNull)))
        {
            var nullableSlot = builder.AllocScratch(new StorageType(StorageType));
            builder.EmitAssign(nullableSlot, value);
            return matchUnderlying(builder.SlotRef(nullableSlot), underlyingType, pattern);
        }
        // CW16: the seed is the pattern's statically-computed null answer — presetting FALSE silently
        // flipped every null-matching shape (`x is not 5` / `x is null or 5` are TRUE on null in C#);
        // the HasValue-gated matchUnderlying only answers the present-value case.
        return EmitNullGatedMatch(builder, value, PatternMatchesNull(pattern),
            boxed => matchUnderlying(boxed, underlyingType, pattern));
    }

    public static CLeaf EmitLiftedBoolLogic(CoreBuilder builder, CValue leftValue, CValue rightValue,
        BinaryOperatorKind kind)
    {
        var aSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(aSlot, leftValue);
        var bSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(bSlot, rightValue);

        void IfBool(int slot, bool wantTrue, Action<CoreBuilder> body)
        {
            CLeaf boolCond = wantTrue
                ? builder.SlotRef(slot)
                : builder.ExternCall(UdonAbi.BooleanNot,
                    new List<CLeaf> { builder.SlotRef(slot) }, StorageTypes.Boolean);
            builder.EmitIf(HasValue(builder, builder.SlotRef(slot)), _ => builder.EmitIf(boolCond, body));
        }

        var resultSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(resultSlot, builder.Const(null, new StorageType(StorageType)));
        bool isAnd = kind == BinaryOperatorKind.And;
        // Dominating value: false for &, true for |.
        IfBool(aSlot, !isAnd, _ => builder.EmitAssign(resultSlot, builder.Const(!isAnd, StorageTypes.Boolean)));
        IfBool(bSlot, !isAnd, _ => builder.EmitAssign(resultSlot, builder.Const(!isAnd, StorageTypes.Boolean)));
        // Both non-dominating values: both true for &, both false for |.
        IfBool(aSlot, isAnd, _ => IfBool(bSlot, isAnd, __ => builder.EmitAssign(resultSlot, builder.Const(isAnd, StorageTypes.Boolean))));
        return builder.SlotRef(resultSlot);
    }

    internal static CLeaf EmitLiftedBinaryCore(CoreBuilder builder,
        CValue leftValue, bool leftNullable, ITypeSymbol leftUnderlying,
        CValue rightValue, bool rightNullable, ITypeSymbol rightUnderlying,
        BinaryOperatorKind kind,
        BoundExtern operatorExtern,
        ITypeSymbol resultType, ITypeSymbol int32Type,
        Func<ITypeSymbol, string> getUdonType,
        Func<CLeaf, ITypeSymbol, (CLeaf Value, ITypeSymbol EffectiveType)> promoteBoxed,
        Func<CLeaf, string, string, CLeaf> narrowConvert)
    {
        var resultNullable = EmitPolicy.IsNullableT(resultType, out var resultUnderlying);

        var aSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(aSlot, leftValue);
        var bSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(bSlot, rightValue);

        void IfBothPresent(Action<CoreBuilder> body)
        {
            Action<CoreBuilder> inner = rightNullable
                ? _ => builder.EmitIf(HasValue(builder, builder.SlotRef(bSlot)), body)
                : body;
            if (leftNullable) builder.EmitIf(HasValue(builder, builder.SlotRef(aSlot)), inner);
            else inner(builder);
        }

        CValue ValueOp()
        {
            var resultUnder = resultNullable ? resultUnderlying : resultType;
            var (leftOperand, _) = promoteBoxed(
                builder.SlotRef(aSlot), leftUnderlying);
            var (rightOperand, _) = promoteBoxed(
                builder.SlotRef(bSlot), rightUnderlying);
            bool resultPromotes = ExternResolver.IsSmallIntOrChar(getUdonType(resultUnder));
            var resultEffective = resultPromotes ? int32Type : resultUnder;
            var raw = builder.ExternCall(
                operatorExtern
                ?? throw new InvalidOperationException(
                    "Lifted operator has no bound extern."),
                new List<CLeaf> { leftOperand, rightOperand }, new StorageType(getUdonType(resultEffective)));
            return resultPromotes && getUdonType(resultUnder) != "SystemInt32"
                ? narrowConvert(raw, "SystemInt32", getUdonType(resultUnder))
                : raw;
        }

        if (resultNullable)
        {
            var resultSlot = builder.AllocScratch(new StorageType(StorageType));
            builder.EmitAssign(resultSlot, builder.Const(null, new StorageType(StorageType)));
            IfBothPresent(_ => builder.EmitAssign(resultSlot, ValueOp()));
            return builder.SlotRef(resultSlot);
        }

        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var eqSlot = builder.AllocScratch(StorageTypes.Boolean);
            builder.EmitAssign(eqSlot, builder.Const(false, StorageTypes.Boolean));
            if (leftNullable && rightNullable)
                builder.EmitIf(IsNull(builder, builder.SlotRef(aSlot)),
                    _ => builder.EmitIf(IsNull(builder, builder.SlotRef(bSlot)),
                        __ => builder.EmitAssign(eqSlot, builder.Const(true, StorageTypes.Boolean))));
            IfBothPresent(_ => builder.EmitAssign(eqSlot, ValueOp()));
            if (kind == BinaryOperatorKind.NotEquals)
                return builder.ExternCall(UdonAbi.BooleanNot,
                    new List<CLeaf> { builder.SlotRef(eqSlot) }, StorageTypes.Boolean);
            return builder.SlotRef(eqSlot);
        }

        var relSlot = builder.AllocScratch(StorageTypes.Boolean);
        builder.EmitAssign(relSlot, builder.Const(false, StorageTypes.Boolean));
        IfBothPresent(_ => builder.EmitAssign(relSlot, ValueOp()));
        return builder.SlotRef(relSlot);
    }

    public static CLeaf EmitLiftedUnary(CoreBuilder builder, CValue operandValue,
        ITypeSymbol operandUnderlying, ITypeSymbol resultUnderlying,
        BoundExtern operatorExtern,
        Func<ITypeSymbol, string> getUdonType,
        Func<CLeaf, ITypeSymbol, (CLeaf Value, ITypeSymbol EffectiveType)> promoteBoxed)
    {
        var resultUdonType = getUdonType(resultUnderlying);

        var nullableSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(nullableSlot, operandValue);
        var resultSlot = builder.AllocScratch(new StorageType(StorageType));
        builder.EmitAssign(resultSlot, builder.Const(null, new StorageType(StorageType)));
        builder.EmitIf(HasValue(builder, builder.SlotRef(nullableSlot)), _ =>
        {
            var (value, _) = promoteBoxed(builder.SlotRef(nullableSlot), operandUnderlying);
            var computed = builder.ExternCall(
                operatorExtern
                ?? throw new InvalidOperationException(
                    "Lifted unary operator has no bound extern."),
                new List<CLeaf> { value }, new StorageType(resultUdonType));
            builder.EmitAssign(resultSlot, computed);
        });
        return builder.SlotRef(resultSlot);
    }
}
