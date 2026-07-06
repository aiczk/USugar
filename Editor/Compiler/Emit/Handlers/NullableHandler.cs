using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class NullableHandler : AssignmentHandlerBase, IExpressionHandler
{
    public NullableHandler(EmitContext ctx) : base(ctx) { }

    public OperationKind[] HandledKinds { get; } = new[]
    {
        OperationKind.ConditionalAccess, OperationKind.Coalesce, OperationKind.ConditionalAccessInstance, OperationKind.CoalesceAssignment,
    };

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

        // targetVal is a single-assignment scratch leaf under ANF — re-readable for the null check and
        // as the conditional-access instance without a snapshot. A delegate-typed receiver is its BUNDLE
        // reference (design §2.6: `d?.Invoke()` null-guards the bundle leaf itself, so any
        // delegate-valued expression — field, local, param, element, call result — is a legal receiver).
        // The null check is type-agnostic (SystemObject), so no retype is needed.
        CLeaf targetVal = VisitExpression(op.Operation);

        return NullableAbi.EmitConditionalAccess(_builder, targetVal, isVoid, isVoid ? null : GetUdonType(op.Type),
            target =>
        {
            // target is not null → evaluate WhenNotNull with target as the instance
            _conditionalAccessStack.Push(target);
            try
            {
                return VisitExpression(op.WhenNotNull);
            }
            finally
            {
                _conditionalAccessStack.Pop();
            }
        }, _ctx.AllocTemp, EmitAssign, SlotRef);
    }

    CLeaf VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = GetUdonType(op.Type);
        // For an aggregate (struct/tuple) result both branches yield a boxed object[] that aliases the
        // nullable's internal storage; deep-clone so the copied-out value has independent value semantics.
        // When op.Type is the aggregate, the right side has the non-nullable aggregate type → always non-null,
        // and the non-null left is cloned in the else branch, so EmitDeepCloneAggregate never sees null.
        var aggType = ResolveType(op.Type) as INamedTypeSymbol;
        bool aggResult = aggType != null && EmitPolicy.IsAggregateType(aggType);
        var leftVal = VisitExpression(op.Value);
        return NullableAbi.EmitCoalesce(_builder, leftVal, resultType,
            () =>
            {
                var rightVal = VisitExpression(op.WhenNull);
                return aggResult ? EmitDeepCloneAggregate(rightVal, aggType) : rightVal;
            },
            aggResult ? present => EmitDeepCloneAggregate(present, aggType) : null,
            _ctx.AllocTemp, EmitAssign, SlotRef);
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
        return NullableAbi.EmitCoalesceAssignment(_builder, lv.Value, targetType,
            () => VisitExpression(op.Value),
            rightVal => EmitWriteBack(op.Target, rightVal, lv),
            _ctx.AllocTemp, EmitAssign, SlotRef);
    }
}
