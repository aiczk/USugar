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

        string resultField = null;
        string resultType = null;
        if (!isVoid)
        {
            resultType = GetUdonType(op.Type);
            resultField = _ctx.DeclareTemp(resultType);
            var defaultConst = Const(null, resultType);
            EmitStoreField(resultField, defaultConst);
        }

        var targetVal = VisitExpression(op.Operation);

        var nullConst = Const(null, "SystemObject");

        // condVal = (target != null); if true → evaluate WhenNotNull, else skip
        var condVal = ExternCall(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { targetVal, nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            // target is not null → evaluate WhenNotNull with target as the instance
            _conditionalAccessTargets.Push(targetVal);
            var accessVal = VisitExpression(op.WhenNotNull);
            _conditionalAccessTargets.Pop();

            if (!isVoid && accessVal != null)
                EmitStoreField(resultField, accessVal);
        });

        return resultField != null ? LoadField(resultField, resultType) : null;
    }

    HExpr VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = GetUdonType(op.Type);
        var resultField = _ctx.DeclareTemp(resultType);
        var leftVal = VisitExpression(op.Value);
        EmitStoreField(resultField, leftVal);

        var nullConst = Const(null, "SystemObject");

        // condVal = (left == null); if true → use right
        var condVal = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { leftVal, nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            // left IS null → use right
            var rightVal = VisitExpression(op.WhenNull);
            EmitStoreField(resultField, rightVal);
        });

        return LoadField(resultField, resultType);
    }

    HExpr VisitCoalesceAssignment(ICoalesceAssignmentOperation op)
    {
        // x ??= expr → if (x == null) x = expr; return x
        // Capture lvalue sub-expressions once to avoid double evaluation
        HExpr targetVal;
        string targetField = null;
        HExpr cachedArrayVal = null, cachedIndexVal = null, cachedInstanceVal = null;

        if (op.Target is IArrayElementReferenceOperation arrayElemTarget)
        {
            cachedArrayVal = VisitExpression(arrayElemTarget.ArrayReference);
            cachedIndexVal = VisitExpression(arrayElemTarget.Indices[0]);
            var arrSym = arrayElemTarget.ArrayReference.Type as IArrayTypeSymbol;
            var arrType = GetArrayType(arrSym);
            var elemType = GetArrayElemType(arrSym);
            var targetType = GetUdonType(arrayElemTarget.Type);
            targetField = _ctx.DeclareTemp(targetType);
            targetVal = ExternCall(
                $"{arrType}.__Get__SystemInt32__{elemType}",
                new List<HExpr> { cachedArrayVal, cachedIndexVal },
                elemType);
            EmitStoreField(targetField, targetVal);
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
            targetField = _ctx.DeclareTemp(targetType);
            EmitStoreField(targetField, targetVal);
        }
        else
        {
            targetVal = VisitExpression(op.Target);
            var targetType = GetUdonType(op.Target.Type);
            targetField = _ctx.DeclareTemp(targetType);
            EmitStoreField(targetField, targetVal);
        }

        var nullConst = Const(null, "SystemObject");

        // condVal = (target == null); if true → assign
        var condVal = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<HExpr> { LoadField(targetField, GetUdonType(op.Target.Type)), nullConst },
            "SystemBoolean");

        // Capture values for use inside the closure
        var capturedArrayVal = cachedArrayVal;
        var capturedIndexVal = cachedIndexVal;
        var capturedInstanceVal = cachedInstanceVal;
        var capturedTargetField = targetField;

        _builder.EmitIf(condVal, b =>
        {
            var rightVal = VisitExpression(op.Value);
            EmitStoreField(capturedTargetField, rightVal);

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

        return LoadField(targetField, GetUdonType(op.Target.Type));
    }
}
