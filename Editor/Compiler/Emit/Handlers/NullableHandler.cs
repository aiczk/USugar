using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

internal sealed class NullableHandler
{
    readonly LoweringServices _lowering;
    readonly LValueLowerer _lvalues;

    public NullableHandler(LoweringServices lowering)
    {
        _lowering = lowering;
        _lvalues = new LValueLowerer(lowering);
    }

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IConditionalAccessOperation op => VisitConditionalAccess(op),
        ICoalesceOperation op => VisitCoalesce(op),
        IConditionalAccessInstanceOperation => _lowering.ConditionalAccessStack.Peek(),
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
        CLeaf targetVal = _lowering.VisitExpression(op.Operation);

        return NullableAbi.EmitConditionalAccess(_lowering.Builder, targetVal, isVoid,
            isVoid ? (StorageType?)null : _lowering.GetStorageType(op.Type),
            target =>
        {
            // target is not null → evaluate WhenNotNull with target as the instance
            _lowering.ConditionalAccessStack.Push(target);
            try
            {
                return _lowering.VisitExpression(op.WhenNotNull);
            }
            finally
            {
                _lowering.ConditionalAccessStack.Pop();
            }
        });
    }

    CLeaf VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = _lowering.GetStorageTypeName(op.Type);
        // For an aggregate (struct/tuple) result both branches yield a boxed object[] that aliases the
        // nullable's internal storage; deep-clone so the copied-out value has independent value semantics.
        // When op.Type is the aggregate, the right side has the non-nullable aggregate type → always non-null,
        // and the non-null left is cloned in the else branch, so AggregateAbi.DeepClone never sees null.
        var aggType = _lowering.ResolveType(op.Type) as INamedTypeSymbol;
        bool aggResult = aggType != null && TypeClassifier.IsAggregateValue(aggType);
        var leftVal = _lowering.VisitExpression(op.Value);
        System.Func<CLeaf, CLeaf> presentValue = null;
        if (aggResult)
            presentValue = present => AggregateAbi.DeepClone(_lowering.Builder, present, aggType, _lowering.State.Aggregates.GetLayout);
        // CW18: a small-underlying nullable left coalescing into a strict underlying-typed slot — the
        // present box may carry a plain-int tag (the drift the lifted-operator/pattern consumers already
        // tolerate), and the raw copy left a mistyped value that faults the next strict extern read.
        else if (EmitPolicy.IsNullableT(op.Value.Type, out _) && ExternResolver.IsSmallIntOrChar(resultType))
            presentValue = present => _lowering.RetagSmallNullablePresent(present, op.Type);
        return NullableAbi.EmitCoalesce(_lowering.Builder, leftVal, new StorageType(resultType),
            () =>
            {
                var rightVal = _lowering.VisitExpression(op.WhenNull);
                return aggResult ? AggregateAbi.DeepClone(_lowering.Builder, rightVal, aggType, _lowering.State.Aggregates.GetLayout) : rightVal;
            },
            presentValue);
    }

    CLeaf VisitCoalesceAssignment(ICoalesceAssignmentOperation op)
    {
        // x ??= expr  →  if (x == null) x = expr;  return x
        // Route the conditional store through the shared lvalue machinery (the same CaptureLValue/EmitWriteBack
        // that CompoundAssignmentHandler uses for its read-modify-write): CaptureLValue reads the current value
        // once — re-used for the null check — and EmitWriteBack stores exactly like a plain `x = expr` for every
        // lvalue form. The old inline if/else-if chain silently dropped cross-behaviour field/property and
        // aggregate-member (tuple/struct) write-backs and built a bogus __set_X extern for user auto-properties.
        _lowering.RejectUnsafeCrossProgramDelegateWrite(op.Target, _lowering.State.Boundary.ClassifyValue(op.Value));
        var lv = _lvalues.PrepareLValue(op.Target);
        var targetType = _lowering.GetStorageTypeName(op.Target.Type);
        return NullableAbi.EmitCoalesceAssignment(_lowering.Builder, lv.Value, new StorageType(targetType),
            () => _lowering.VisitExpression(op.Value),
            rightVal => lv.Write(rightVal));
    }
}
