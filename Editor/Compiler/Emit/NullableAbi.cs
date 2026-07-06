using System;
using System.Collections.Generic;
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
}
