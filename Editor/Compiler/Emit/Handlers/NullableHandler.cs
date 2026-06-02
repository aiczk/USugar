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

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IConditionalAccessOperation op => VisitConditionalAccess(op),
        ICoalesceOperation op => VisitCoalesce(op),
        IConditionalAccessInstanceOperation => _conditionalAccessStack.Peek().Target,
        ICoalesceAssignmentOperation op => VisitCoalesceAssignment(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    CLeaf VisitConditionalAccess(IConditionalAccessOperation op)
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

        // Detect delegate field conditional access (e.g., _callback?.Invoke(42))
        // The original field variable doesn't exist — it's been expanded to a DelegateBundle.
        string delegateFieldName = null;
        if (op.Operation is IFieldReferenceOperation fieldRef
            && fieldRef.Field.Type is INamedTypeSymbol dlgType
            && dlgType.DelegateInvokeMethod != null
            && _delegateFields.Contains(fieldRef.Field.Name))
        {
            delegateFieldName = fieldRef.Field.Name;
        }

        // targetVal is a single-assignment scratch leaf under ANF (LoadField for the delegate-bundle Target,
        // else VisitExpression for the receiver) — re-readable for the null check and as the conditional-access
        // instance without a snapshot. The null check is type-agnostic (SystemObject), so no retype is needed.
        CLeaf targetVal = delegateFieldName != null
            ? LoadField(new DelegateBundle(delegateFieldName).Target, "VRCUdonCommonInterfacesIUdonEventReceiver")
            : VisitExpression(op.Operation);

        var nullConst = Const(null, "SystemObject");

        // condVal = (target != null); if true → evaluate WhenNotNull, else skip
        var condVal = ExternCall(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { targetVal, nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            // target is not null → evaluate WhenNotNull with target as the instance
            _conditionalAccessStack.Push((targetVal, delegateFieldName));
            try
            {
                var accessVal = VisitExpression(op.WhenNotNull);

                if (!isVoid && accessVal != null)
                    EmitAssign(resultSlot, accessVal);
            }
            finally
            {
                _conditionalAccessStack.Pop();
            }
        });

        return resultSlot >= 0 ? SlotRef(resultSlot) : null;
    }

    CLeaf VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = GetUdonType(op.Type);
        var resultSlot = _ctx.AllocTemp(resultType);
        // For an aggregate (struct/tuple) result both branches yield a boxed object[] that aliases the
        // nullable's internal storage; deep-clone so the copied-out value has independent value semantics.
        // When op.Type is the aggregate, the right side has the non-nullable aggregate type → always non-null,
        // and the non-null left is cloned in the else branch, so EmitDeepCloneAggregate never sees null.
        var aggType = ResolveType(op.Type) as INamedTypeSymbol;
        bool aggResult = aggType != null && EmitContext.IsAggregateType(aggType);
        var leftVal = VisitExpression(op.Value);
        EmitAssign(resultSlot, leftVal);

        var nullConst = Const(null, "SystemObject");

        // Use SlotRef for null check to avoid double evaluation of impure left-hand side
        var condVal = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { SlotRef(resultSlot), nullConst },
            "SystemBoolean");

        System.Action<CoreBuilder> elseB = null;
        if (aggResult)
            elseB = b => EmitAssign(resultSlot, EmitDeepCloneAggregate(SlotRef(resultSlot), aggType));

        _builder.EmitIf(condVal, b =>
        {
            // left IS null → use right
            var rightVal = VisitExpression(op.WhenNull);
            EmitAssign(resultSlot, aggResult ? EmitDeepCloneAggregate(rightVal, aggType) : rightVal);
        }, elseB);

        return SlotRef(resultSlot);
    }

    CLeaf VisitCoalesceAssignment(ICoalesceAssignmentOperation op)
    {
        // x ??= expr → if (x == null) x = expr; return x
        // Capture lvalue sub-expressions once to avoid double evaluation
        CLeaf targetVal;
        int targetSlot;
        CLeaf cachedArrayVal = null, cachedIndexVal = null, cachedInstanceVal = null;

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
                new List<CLeaf> { cachedArrayVal, cachedIndexVal },
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
            new List<CLeaf> { SlotRef(targetSlot), nullConst },
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
                    new List<CLeaf> { capturedArrayVal, capturedIndexVal, rightVal });
            }
            else if (op.Target is IPropertyReferenceOperation propRef && propRef.Property.SetMethod != null)
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                var valueType = GetUdonType(propRef.Property.Type);
                var sig = $"{containingType}.__set_{propRef.Property.Name}__{valueType}__SystemVoid";
                if (capturedInstanceVal != null)
                    EmitExternVoid(sig, new List<CLeaf> { capturedInstanceVal, rightVal });
                else
                    EmitExternVoid(sig, new List<CLeaf> { rightVal });
            }
            else if (op.Target is ILocalReferenceOperation localTarget
                     && _localBindings.TryGetValue(localTarget.Local, out var lb))
            {
                EmitStoreField(lb.Id, rightVal);
            }
            else if (op.Target is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldTarget)
            {
                EmitStoreField(fieldTarget.Field.Name, rightVal);
            }
        });

        return SlotRef(targetSlot);
    }
}
