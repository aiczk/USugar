using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a = b` simple assignments across all lvalue targets
/// (locals, fields, array elements, properties, cross-behaviour, delegates, struct fields).</summary>
internal sealed class SimpleAssignmentHandler
{
    readonly LoweringServices _lowering;
    readonly LValueLowerer _lvalues;

    public SimpleAssignmentHandler(LoweringServices lowering)
    {
        _lowering = lowering;
        _lvalues = new LValueLowerer(lowering);
    }

    public CLeaf Handle(IOperation op)
        => op is ISimpleAssignmentOperation assign
            ? VisitAssignment(assign)
            : throw new System.NotSupportedException(op.GetType().Name);

    CLeaf VisitAssignment(ISimpleAssignmentOperation assign)
    {
        // ref reassignment `r = ref y` (round 7, §8-3 loud): the declaration reject in
        // VisitVariableDeclaration already makes ref locals unreachable, but keep the assignment
        // form loud too so no future lvalue kind re-opens the alias-as-value-copy hole.
        if (assign.IsRef)
            throw new System.NotSupportedException(
                "ref local reassignment ('r = ref y') is not supported: the flat-heap Udon VM has "
                + "no variable aliases. Use the referenced variable directly.");

        // Wave-9 round-7 [Y1]: `_ = expr;` discard assignment — legal C#: evaluate the RHS for its
        // side effects and drop the value. A discard has no storage and can never be read back, so
        // no escape channel opens (the guards below are storage guards).
        if (assign.Target is IDiscardOperation)
            return _lowering.VisitExpression(assign.Value);

        // Field lvalue with receiver legs (aggregate member `point.x` / `arr[i].v`, cross-behaviour
        // field, extern value-type / reference-type field) — the shared legs-now/store-later path,
        // also consumed by the deconstruction lvalue arm. Wave-9 round-7 [Y2]: C# evaluates the
        // target's component expressions BEFORE the RHS; the old arms evaluated the RHS first, so
        // `arr[idx].v = Mut()` with Mut() bumping idx wrote the WRONG element (VM-proven ref=701
        // vs 71). Behaviour this-fields ride the fallback below (no receiver legs).
        if (_lowering.TryPrepareWriteLValue(assign.Target) is { } writePlan)
        {
            var srcValue = _lowering.VisitLoweredExpression(assign.Value);
            _lowering.RejectUnsafeCrossProgramDelegateWrite(assign.Target, srcValue.Info);
            writePlan.Write(srcValue.Leaf);
            return srcValue.Leaf;
        }

        // Direct store: local variable or this.field. Delegate assignments (field and local, including
        // `d = null` and `a = b` reference copy) ride this path now: VisitExpression yields the
        // bundle reference (or null const) and the store is a single reference copy (design §2.3).

        // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
        var directValue = _lowering.VisitLoweredExpression(assign.Value);
        if (assign.Target is IFieldReferenceOperation dlgFieldTarget)
            _lowering.RejectUnsafeCrossProgramDelegateWrite(dlgFieldTarget, directValue.Info);
        var srcLeaf = directValue.Leaf;
        // Stage 2 §4.1: captured local/param target → env cell store (value read-back contract kept:
        // re-read the cell, clone aggregates when the assignment is used as a value).
        if (_lowering.TryEmitEnvStore(assign.Target, srcLeaf))
        {
            if (assign.Parent is IExpressionStatementOperation) return srcLeaf;
            ISymbol envSym = assign.Target is ILocalReferenceOperation elr
                ? elr.Local
                : ((IParameterReferenceOperation)assign.Target).Parameter;
            var envLoaded = EnvEmit.Read(_lowering.Builder, _lowering.State, envSym, _lowering.GetStorageType(assign.Target.Type));
            return assign.Target.Type is INamedTypeSymbol eAgg && _lowering.IsAggregateValue(eAgg)
                ? AggregateAbi.DeepClone(_lowering.Builder, envLoaded, eAgg, _lowering.State.Aggregates.GetLayout) : envLoaded;
        }
        var targetFieldName = _lvalues.GetAssignTargetFieldName(assign.Target);
        _lowering.EmitStoreField(targetFieldName, srcLeaf);
        // The assignment's VALUE is the stored value. Return a fresh read of the target rather than the
        // RHS expression tree: re-emitting the tree (when the assignment is used as an expression, e.g.
        // `G(n = n - 1)`) would re-evaluate it after the store already mutated its inputs. A dead read in
        // statement form is harmless and simply remains (the optimizer has no DCE pass).
        var targetFieldType = _lowering.State.Storage.GetFieldType(targetFieldName);
        if (targetFieldType == null) return srcLeaf;
        var loaded = _lowering.LoadField(targetFieldName, targetFieldType.Value);
        // When the assignment is USED AS A VALUE (e.g. chained `z = y = x`) and the target is an aggregate,
        // that value must be an independent COPY (struct value semantics) — otherwise z aliases y. (diff-fuzz w4)
        return assign.Parent is not IExpressionStatementOperation
               && assign.Target.Type is INamedTypeSymbol tAgg && _lowering.IsAggregateValue(tAgg)
            ? AggregateAbi.DeepClone(_lowering.Builder, loaded, tAgg, _lowering.State.Aggregates.GetLayout) : loaded;
    }

}
