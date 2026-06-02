using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a += b`, `a -= b`, `a++`, `++a`, etc.</summary>
public class CompoundAssignmentHandler : AssignmentHandlerBase, IExpressionHandler
{
    public CompoundAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation op) => op is ICompoundAssignmentOperation or IIncrementOrDecrementOperation;

    public CValue Handle(IOperation op) => op switch
    {
        ICompoundAssignmentOperation compound => VisitCompoundAssignment(compound),
        IIncrementOrDecrementOperation incDec => VisitIncrementDecrement(incDec),
        _ => throw new System.NotSupportedException(op.GetType().Name),
    };

    CValue VisitCompoundAssignment(ICompoundAssignmentOperation op)
    {
        // Block += / -= on delegate fields — Udon VM does not support Delegate.Combine/Remove
        if (op.Target is IFieldReferenceOperation fr
            && fr.Field.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
            throw new System.NotSupportedException("Multicast delegates (+=/-=) are not supported. Udon VM does not support Delegate.Combine/Remove.");

        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var leftVal = lv.Value;
        var rightVal = VisitExpression(op.Value);

        // Nullable (lifted) compound assignment: x += v  →  x = lifted(x, v) (null-propagating).
        if (EmitContext.IsNullableT(op.Target.Type, out var tUnderlying))
        {
            var rNullable = EmitContext.IsNullableT(op.Value.Type, out var vUnderlying);
            var lifted = EmitLiftedBinaryCore(
                leftVal, true, tUnderlying,
                rightVal, rNullable, rNullable ? vUnderlying : op.Value.Type,
                op.OperatorKind, op.OperatorMethod, op.Type);
            EmitWriteBack(op.Target, lifted, lv);
            return lifted;
        }

        var resultType = GetUdonType(op.Type);

        // long %= / ulong %= : no Udon op_Remainder extern; polyfill a - (a/b)*b (shared with the binary path).
        if (op.OperatorKind == BinaryOperatorKind.Remainder && Is64BitInt(resultType))
        {
            var rem = EmitInt64Remainder(leftVal, rightVal, resultType);
            EmitWriteBack(op.Target, rem, lv);
            return rem;
        }

        // Promote small integers for the operation temp.
        // Udon VM has no byte/sbyte/short/ushort operators — operations go through int.
        var opResultType = resultType;
        if (IsSmallInteger(resultType))
            opResultType = "SystemInt32";

        // Explicit operand promotion: byte slot pushed to int extern requires ToInt32 conversion
        // (matches ExpressionHandler.VisitConversion behaviour for byte+byte). Without this we
        // rely on Udon VM's implicit boxed-value coercion, which is fragile across SDK updates.
        var leftType = GetUdonType(op.Target.Type);
        if (IsSmallInteger(leftType))
            leftVal = PromoteToInt32(leftVal, leftType);
        var rightType = GetUdonType(op.Value.Type);
        if (IsSmallInteger(rightType))
            rightVal = PromoteToInt32(rightVal, rightType);

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        CValue resultVal = ExternCall(sig, new List<CValue> { leftVal, rightVal }, opResultType);

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opResultType != resultType)
            resultVal = EmitNarrowingConvert(resultVal, opResultType, resultType);

        EmitWriteBack(op.Target, resultVal, lv);
        return resultVal;
    }

    // char promotes to int for arithmetic just like byte/short: Udon has no SystemChar +/-
    // operator returning char (its op_Addition returns SystemInt32), so we promote to int,
    // operate, then narrow back via SystemConvert.ToChar. Excluding char emitted a non-existent
    // SystemChar.__op_Addition__SystemChar_SystemChar__SystemChar (caught only at runtime).
    static bool IsSmallInteger(string udonType)
        => udonType is "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16" or "SystemChar";

    CValue PromoteToInt32(CValue value, string srcUdonType)
        => ExternCall($"SystemConvert.__ToInt32__{srcUdonType}__SystemInt32",
            new List<CValue> { value }, "SystemInt32");

    CValue VisitIncrementDecrement(IIncrementOrDecrementOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var targetVal = lv.Value;

        // Nullable (lifted) increment/decrement: x++  →  x = lifted(x, 1) (null-propagating).
        if (EmitContext.IsNullableT(op.Type, out var incUnderlying))
        {
            CValue saved = null;
            if (op.IsPostfix)
            {
                var s = _ctx.AllocTemp("SystemObject");
                EmitAssign(s, targetVal);
                saved = SlotRef(s);
            }
            var kind = op.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
            var lifted = EmitLiftedBinaryCore(
                targetVal, true, incUnderlying,
                Const(1, GetUdonType(incUnderlying)), false, incUnderlying,
                kind, null, op.Type);
            EmitWriteBack(op.Target, lifted, lv);
            return op.IsPostfix && saved != null ? saved : lifted;
        }

        var udonType = GetUdonType(op.Type);

        // Promote small integers: Udon VM has no byte/sbyte/short/ushort operators
        var opType = udonType;
        if (IsSmallInteger(opType))
            opType = "SystemInt32";

        var oneConst = Const(1, opType);

        // For postfix, save old value before modifying target (only if result is used).
        // Save the un-promoted value so postfix returns the original byte (not the int promotion).
        CValue savedVal = null;
        if (op.IsPostfix)
        {
            var resultUsed = op.Parent is not IExpressionStatementOperation
                             && op.Parent is not IForLoopOperation;
            if (op.Parent == null || resultUsed)
            {
                var savedSlot = _ctx.AllocTemp(udonType);
                EmitAssign(savedSlot, targetVal);
                savedVal = SlotRef(savedSlot);
            }
        }

        // Explicit operand promotion to match the int extern signature.
        if (IsSmallInteger(udonType))
            targetVal = PromoteToInt32(targetVal, udonType);

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        CValue resultVal = ExternCall(sig, new List<CValue> { targetVal, oneConst }, opType);

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opType != udonType)
            resultVal = EmitNarrowingConvert(resultVal, opType, udonType);

        // resultVal is already a materialized (single-assignment) slot leaf under A-normal form, so it is
        // stable across the write-back and the return — no extra snapshot needed.
        EmitWriteBack(op.Target, resultVal, lv);

        return op.IsPostfix ? savedVal : resultVal;
    }
}
