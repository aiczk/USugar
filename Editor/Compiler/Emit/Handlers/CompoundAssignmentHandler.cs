using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a += b`, `a -= b`, `a++`, `++a`, etc.</summary>
public class CompoundAssignmentHandler : AssignmentHandlerBase, IExpressionHandler
{
    public CompoundAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation op) => op is ICompoundAssignmentOperation or IIncrementOrDecrementOperation;

    public CLeaf Handle(IOperation op) => op switch
    {
        ICompoundAssignmentOperation compound => VisitCompoundAssignment(compound),
        IIncrementOrDecrementOperation incDec => VisitIncrementDecrement(incDec),
        _ => throw new System.NotSupportedException(op.GetType().Name),
    };

    CLeaf VisitCompoundAssignment(ICompoundAssignmentOperation op)
    {
        // Block += / -= on ANY delegate-typed target (field, local, param, property, array element, …) —
        // Udon VM does not support Delegate.Combine/Remove (predicate widened per design §5.2; message unchanged).
        if (op.Target.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
            throw new System.NotSupportedException("Multicast delegates (+=/-=) are not supported. Udon VM does not support Delegate.Combine/Remove.");

        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var leftVal = lv.Value;
        var rightVal = VisitExpression(op.Value);

        // User-defined struct operator (s += t uses the struct's operator +): static method call, then write
        // back. The struct's Udon type is SystemObjectArray, so ResolveBinaryExtern would build a bogus extern.
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } cuOpM
            && cuOpM.ContainingType is INamedTypeSymbol cuOpCt && EmitContext.IsUserStruct(cuOpCt)
            && _methodFunctions.ContainsKey(cuOpM.OriginalDefinition))
        {
            var res = EmitCallToMethod(cuOpM.OriginalDefinition, new List<CLeaf> { leftVal, rightVal });
            EmitWriteBack(op.Target, res, lv);
            return res;
        }

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

        // long/ulong/uint %= : no Udon op_Remainder extern; polyfill a - (a/b)*b (shared with the binary path).
        if (op.OperatorKind == BinaryOperatorKind.Remainder && RemainderNeedsPolyfill(resultType))
        {
            var rem = EmitRemainderViaDivision(leftVal, rightVal, resultType);
            EmitWriteBack(op.Target, rem, lv);
            return rem;
        }

        // Promote small integers for the operation temp.
        // Udon VM has no byte/sbyte/short/ushort operators — operations go through int.
        var opResultType = resultType;
        if (ExternResolver.IsSmallIntOrChar(resultType))
            opResultType = "SystemInt32";

        // Explicit operand promotion: byte slot pushed to int extern requires ToInt32 conversion
        // (matches ExpressionHandler.VisitConversion behaviour for byte+byte). Without this we
        // rely on Udon VM's implicit boxed-value coercion, which is fragile across SDK updates.
        var leftType = GetUdonType(op.Target.Type);
        if (ExternResolver.IsSmallIntOrChar(leftType))
            leftVal = PromoteToInt32(leftVal, leftType);
        var rightType = GetUdonType(op.Value.Type);
        if (ExternResolver.IsSmallIntOrChar(rightType))
            rightVal = PromoteToInt32(rightVal, rightType);

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        CLeaf resultVal = ExternCall(sig, new List<CLeaf> { leftVal, rightVal }, opResultType);

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opResultType != resultType)
            resultVal = EmitNarrowingConvert(resultVal, opResultType, resultType);

        EmitWriteBack(op.Target, resultVal, lv);
        return resultVal;
    }

    CLeaf PromoteToInt32(CLeaf value, string srcUdonType)
        => ExternCall($"SystemConvert.__ToInt32__{srcUdonType}__SystemInt32",
            new List<CLeaf> { value }, "SystemInt32");

    CLeaf VisitIncrementDecrement(IIncrementOrDecrementOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var targetVal = lv.Value;

        // User-defined struct operator ++/-- (a single-operand static method returning the new struct), then
        // write back. Postfix returns the captured OLD value; the built-in op_Addition path below would build
        // a bogus extern on the struct's SystemObjectArray type and use the wrong (value, 1) shape.
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } iuOpM
            && iuOpM.ContainingType is INamedTypeSymbol iuOpCt && EmitContext.IsUserStruct(iuOpCt)
            && _methodFunctions.ContainsKey(iuOpM.OriginalDefinition))
        {
            var res = EmitCallToMethod(iuOpM.OriginalDefinition, new List<CLeaf> { targetVal });
            EmitWriteBack(op.Target, res, lv);
            return op.IsPostfix ? lv.Value : res;
        }

        // Nullable (lifted) increment/decrement: x++  →  x = lifted(x, 1) (null-propagating).
        if (EmitContext.IsNullableT(op.Type, out var incUnderlying))
        {
            var kind = op.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
            var lifted = EmitLiftedBinaryCore(
                targetVal, true, incUnderlying,
                Const(1, GetUdonType(incUnderlying)), false, incUnderlying,
                kind, null, op.Type);
            EmitWriteBack(op.Target, lifted, lv);
            // Postfix returns the OLD value: targetVal (= lv.Value) is a single-assignment scratch leaf bound
            // before the write-back, which stores to the target's heap id and never touches this scratch.
            return op.IsPostfix ? targetVal : lifted;
        }

        var udonType = GetUdonType(op.Type);

        // Promote small integers: Udon VM has no byte/sbyte/short/ushort operators
        var opType = udonType;
        if (ExternResolver.IsSmallIntOrChar(opType))
            opType = "SystemInt32";

        var oneConst = Const(1, opType);

        // Explicit operand promotion to match the int extern signature.
        if (ExternResolver.IsSmallIntOrChar(udonType))
            targetVal = PromoteToInt32(targetVal, udonType);

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        CLeaf resultVal = ExternCall(sig, new List<CLeaf> { targetVal, oneConst }, opType);

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opType != udonType)
            resultVal = EmitNarrowingConvert(resultVal, opType, udonType);

        EmitWriteBack(op.Target, resultVal, lv);

        // Postfix returns the OLD un-promoted value. lv.Value is the single-assignment scratch leaf bound by
        // CaptureLValue (NOT `targetVal`, which PromoteToInt32 may have overwritten above); the write-back
        // stores resultVal to the target's heap id and never touches that scratch, so it still holds the old value.
        return op.IsPostfix ? lv.Value : resultVal;
    }
}
