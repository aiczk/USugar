using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a = b` simple assignments across all lvalue targets
/// (locals, fields, array elements, properties, cross-behaviour, delegates, struct fields).</summary>
public class SimpleAssignmentHandler : AssignmentHandlerBase, IExpressionHandler
{
    public SimpleAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public OperationKind[] HandledKinds { get; } = new[] { OperationKind.SimpleAssignment };

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
            return VisitExpression(assign.Value);

        // Field lvalue with receiver legs (aggregate member `point.x` / `arr[i].v`, cross-behaviour
        // field, extern value-type / reference-type field) — the shared legs-now/store-later path,
        // also consumed by the deconstruction lvalue arm. Wave-9 round-7 [Y2]: C# evaluates the
        // target's component expressions BEFORE the RHS; the old arms evaluated the RHS first, so
        // `arr[idx].v = Mut()` with Mut() bumping idx wrote the WRONG element (VM-proven ref=701
        // vs 71). When BOTH sides are emission-order inert (pure reads/operators — neither can
        // perturb the other), keep the legacy value-first order so pinned UASM stays byte-identical
        // (struct_ref_param sentinel: the receiver-leg COPY position is pinned). Behaviour
        // this-fields ride the fallback below (no legs, byte-identical).
        if (assign.Target is IFieldReferenceOperation fieldLValue && IsPreparableFieldSetTarget(fieldLValue))
        {
            if (IsEmissionOrderInert(fieldLValue) && IsEmissionOrderInert(assign.Value))
            {
                var inertValue = VisitEmittedValue(assign.Value);
                RejectUnsafeCrossProgramDelegateWrite(fieldLValue, inertValue.Info);
                TryPrepareFieldSet(fieldLValue)(inertValue.Leaf);
                return inertValue.Leaf;
            }
            var fieldStore = TryPrepareFieldSet(fieldLValue);
            var srcValue = VisitEmittedValue(assign.Value);
            RejectUnsafeCrossProgramDelegateWrite(fieldLValue, srcValue.Info);
            fieldStore(srcValue.Leaf);
            return srcValue.Leaf;
        }

        if (assign.Target is IArrayElementReferenceOperation arrayElem)
        {
            RejectStaticReadonlyWriteThrough(arrayElem.ArrayReference); // §3.3, R5
            if (arrayElem.Indices.Length > 1)
            {
                var ndimStore = PrepareNdimElementSet(arrayElem);
                var ndimSrcVal = VisitExpression(assign.Value);
                ndimStore(ndimSrcVal);
                return ndimSrcVal;
            }
            var arrayVal = VisitExpression(arrayElem.ArrayReference);
            var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
            var indexVal = ResolveArrayIndex(arrayVal, GetArrayType(arrSymbol), arrayElem.Indices[0]);
            var srcVal = VisitExpression(assign.Value);
            EmitArrayElementSet(arrSymbol, arrayVal, indexVal, srcVal);
            return srcVal;
        }

        // Property/indexer SET (struct, this/base, static, variable-receiver, interface, extern) —
        // shared with the deconstruction lvalue path. Wave-9 round-5 [X2]: evaluation order is
        // receiver → index args → value (C# order); the old inline arm evaluated the RHS first.
        if (assign.Target is IPropertyReferenceOperation propRef)
            return EmitPropertySet(propRef, () => VisitExpression(assign.Value));

        // Fallback: local variable or this.field. Delegate assignments (field and local, including
        // `d = null` and `a = b` reference copy) ride this generic path now: VisitExpression yields the
        // bundle reference (or null const) and the store is a single reference copy (design §2.3).

        // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
        var srcValueFallback = VisitEmittedValue(assign.Value);
        if (assign.Target is IFieldReferenceOperation dlgFieldTarget)
            RejectUnsafeCrossProgramDelegateWrite(dlgFieldTarget, srcValueFallback.Info);
        var srcFallback = srcValueFallback.Leaf;
        // Stage 2 §4.1: captured local/param target → env cell store (value read-back contract kept:
        // re-read the cell, clone aggregates when the assignment is used as a value).
        if (TryEmitEnvStore(assign.Target, srcFallback))
        {
            if (assign.Parent is IExpressionStatementOperation) return srcFallback;
            ISymbol envSym = assign.Target is ILocalReferenceOperation elr
                ? elr.Local
                : ((IParameterReferenceOperation)assign.Target).Parameter;
            var envLoaded = EnvEmit.Read(_builder, _ctx, envSym, GetUdonType(assign.Target.Type));
            return assign.Target.Type is INamedTypeSymbol eAgg && EmitPolicy.IsAggregateType(eAgg)
                ? EmitDeepCloneAggregate(envLoaded, eAgg) : envLoaded;
        }
        var targetFieldName = GetAssignTargetFieldName(assign.Target);
        EmitStoreField(targetFieldName, srcFallback);
        // The assignment's VALUE is the stored value. Return a fresh read of the target rather than the
        // RHS expression tree: re-emitting the tree (when the assignment is used as an expression, e.g.
        // `G(n = n - 1)`) would re-evaluate it after the store already mutated its inputs. A dead read in
        // statement form is harmless and simply remains (the optimizer has no DCE pass).
        var targetFieldType = _ctx.GetFieldType(targetFieldName);
        if (targetFieldType == null) return srcFallback;
        var loaded = LoadField(targetFieldName, targetFieldType);
        // When the assignment is USED AS A VALUE (e.g. chained `z = y = x`) and the target is an aggregate,
        // that value must be an independent COPY (struct value semantics) — otherwise z aliases y. (diff-fuzz w4)
        return assign.Parent is not IExpressionStatementOperation
               && assign.Target.Type is INamedTypeSymbol tAgg && EmitPolicy.IsAggregateType(tAgg)
            ? EmitDeepCloneAggregate(loaded, tAgg) : loaded;
    }

}
