using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class NullableHandler : AssignmentHandlerBase, IExpressionHandler
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
        IConditionalAccessInstanceOperation => _conditionalAccessStack.Peek(),
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

        // targetVal is a single-assignment scratch leaf under ANF — re-readable for the null check and
        // as the conditional-access instance without a snapshot. A delegate-typed receiver is its BUNDLE
        // reference (design §2.6: `d?.Invoke()` null-guards the bundle leaf itself, so any
        // delegate-valued expression — field, local, param, element, call result — is a legal receiver).
        // The null check is type-agnostic (SystemObject), so no retype is needed.
        CLeaf targetVal = VisitExpression(op.Operation);

        var nullConst = Const(null, "SystemObject");

        // condVal = (target != null); if true → evaluate WhenNotNull, else skip
        var condVal = ExternCall(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { targetVal, nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            // target is not null → evaluate WhenNotNull with target as the instance
            _conditionalAccessStack.Push(targetVal);
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
        // x ??= expr  →  if (x == null) x = expr;  return x
        // Route the conditional store through the shared lvalue machinery (the same CaptureLValue/EmitWriteBack
        // that CompoundAssignmentHandler uses for its read-modify-write): CaptureLValue reads the current value
        // once — re-used for the null check — and EmitWriteBack stores exactly like a plain `x = expr` for every
        // lvalue form. The old inline if/else-if chain silently dropped cross-behaviour field/property and
        // aggregate-member (tuple/struct) write-backs and built a bogus __set_X extern for user auto-properties.
        var lv = CaptureLValue(op.Target);
        var targetType = GetUdonType(op.Target.Type);
        var targetSlot = _ctx.AllocTemp(targetType);
        EmitAssign(targetSlot, lv.Value);

        var nullConst = Const(null, "SystemObject");
        var condVal = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { SlotRef(targetSlot), nullConst },
            "SystemBoolean");

        _builder.EmitIf(condVal, b =>
        {
            var rightVal = VisitExpression(op.Value);
            EmitAssign(targetSlot, rightVal);
            EmitWriteBack(op.Target, rightVal, lv);
        });

        return SlotRef(targetSlot);
    }
}
