using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class NullableHandler : HandlerBase, IExpressionHandler
{
    public NullableHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IConditionalAccessOperation
            or ICoalesceOperation
            or IConditionalAccessInstanceOperation
            or ICoalesceAssignmentOperation;

    public HExpr Handle(IOperation expression) => expression switch
    {
        IConditionalAccessOperation op => VisitConditionalAccess(op),
        ICoalesceOperation op => VisitCoalesce(op),
        IConditionalAccessInstanceOperation => _conditionalAccessTargets.Peek(),
        ICoalesceAssignmentOperation op => VisitCoalesceAssignment(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    HExpr VisitConditionalAccess(IConditionalAccessOperation op)
    {
        bool isVoid = op.Type == null || op.Type.SpecialType == SpecialType.System_Void;

        int resultSlot = -1;
        string resultType = null;
        if (!isVoid)
        {
            resultType = GetUdonType(op.Type);
            resultSlot = _ctx.AllocTemp(resultType);
            var defaultConst = Const(null, resultType);
            EmitAssign(resultSlot, defaultConst);
        }

        var targetVal = VisitExpression(op.Operation);

        // Store in temp slot to avoid double evaluation of impure expressions (e.g., method calls)
        var targetType = GetUdonType(op.Operation.Type ?? op.Type);
        var targetSlot = _ctx.AllocTemp(targetType);
        EmitAssign(targetSlot, targetVal);
        var targetRef = SlotRef(targetSlot);

        var nullConst = Const(null, "SystemObject");

        // condVal = (target != null); if true → evaluate WhenNotNull, else skip
        var condVal = ExternCall(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { targetRef, nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            // target is not null → evaluate WhenNotNull with target as the instance
            _conditionalAccessTargets.Push(targetRef);
            var accessVal = VisitExpression(op.WhenNotNull);
            _conditionalAccessTargets.Pop();

            if (!isVoid && accessVal != null)
                EmitAssign(resultSlot, accessVal);
        });

        return resultSlot >= 0 ? SlotRef(resultSlot) : null;
    }

    HExpr VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = GetUdonType(op.Type);
        var resultSlot = _ctx.AllocTemp(resultType);
        var leftVal = VisitExpression(op.Value);
        EmitAssign(resultSlot, leftVal);

        var nullConst = Const(null, "SystemObject");

        // Use SlotRef for null check to avoid double evaluation of impure left-hand side
        var condVal = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { SlotRef(resultSlot), nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            // left IS null → use right
            var rightVal = VisitExpression(op.WhenNull);
            EmitAssign(resultSlot, rightVal);
        });

        return SlotRef(resultSlot);
    }

    HExpr VisitCoalesceAssignment(ICoalesceAssignmentOperation op)
    {
        // x ??= expr → if (x == null) x = expr; return x
        // Capture lvalue sub-expressions once to avoid double evaluation
        HExpr targetVal;
        int targetSlot;
        HExpr cachedArrayVal = null, cachedIndexVal = null, cachedInstanceVal = null;

        if (op.Target is IArrayElementReferenceOperation arrayElemTarget)
        {
            cachedArrayVal = VisitExpression(arrayElemTarget.ArrayReference);
            cachedIndexVal = VisitExpression(arrayElemTarget.Indices[0]);
            var arrSym = arrayElemTarget.ArrayReference.Type as IArrayTypeSymbol;
            var arrType = GetArrayType(arrSym);
            var elemType = GetArrayElemType(arrSym);
            var targetType = GetUdonType(arrayElemTarget.Type);
            targetSlot = _ctx.AllocTemp(targetType);
            targetVal = ExternCall(
                $"{arrType}.__Get__SystemInt32__{elemType}",
                new List<HExpr> { cachedArrayVal, cachedIndexVal },
                elemType);
            EmitAssign(targetSlot, targetVal);
        }
        else if (op.Target is IPropertyReferenceOperation propTarget)
        {
            if (propTarget.Instance is IInstanceReferenceOperation)
            {
                var thisType = GetUdonType(propTarget.Property.ContainingType);
                cachedInstanceVal = LoadField(_ctx.DeclareThisOnce(thisType), thisType);
            }
            else if (propTarget.Instance != null)
                cachedInstanceVal = VisitExpression(propTarget.Instance);
            targetVal = VisitExpression(op.Target);
            var targetType = GetUdonType(op.Target.Type);
            targetSlot = _ctx.AllocTemp(targetType);
            EmitAssign(targetSlot, targetVal);
        }
        else
        {
            targetVal = VisitExpression(op.Target);
            var targetType = GetUdonType(op.Target.Type);
            targetSlot = _ctx.AllocTemp(targetType);
            EmitAssign(targetSlot, targetVal);
        }

        var nullConst = Const(null, "SystemObject");

        // condVal = (target == null); if true → assign
        var condVal = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { SlotRef(targetSlot), nullConst },
            "SystemBoolean");

        // Capture values for use inside the closure
        var capturedArrayVal = cachedArrayVal;
        var capturedIndexVal = cachedIndexVal;
        var capturedInstanceVal = cachedInstanceVal;
        var capturedTargetSlot = targetSlot;

        _builder.EmitIf(condVal, b =>
        {
            var rightVal = VisitExpression(op.Value);
            EmitAssign(capturedTargetSlot, rightVal);

            // Write-back for non-local targets using cached sub-expressions
            if (op.Target is IArrayElementReferenceOperation arrayElem)
            {
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                    new List<HExpr> { capturedArrayVal, capturedIndexVal, rightVal });
            }
            else if (op.Target is IPropertyReferenceOperation propRef && propRef.Property.SetMethod != null)
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                var valueType = GetUdonType(propRef.Property.Type);
                var sig = $"{containingType}.__set_{propRef.Property.Name}__{valueType}__SystemVoid";
                if (capturedInstanceVal != null)
                    EmitExternVoid(sig, new List<HExpr> { capturedInstanceVal, rightVal });
                else
                    EmitExternVoid(sig, new List<HExpr> { rightVal });
            }
        });

        return SlotRef(targetSlot);
    }
}
