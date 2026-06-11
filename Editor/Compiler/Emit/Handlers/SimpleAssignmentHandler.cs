using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a = b` simple assignments across all lvalue targets
/// (locals, fields, array elements, properties, cross-behaviour, delegates, struct fields).</summary>
public class SimpleAssignmentHandler : AssignmentHandlerBase, IExpressionHandler
{
    public SimpleAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation op) => op is ISimpleAssignmentOperation;

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

        // §2.8(a): capturing lambdas stored long-lived (delegate field / auto-property / struct member,
        // self or cross) feed the post-emit aliasing detector. §2.8(b): escaping stores (array element,
        // object/object[] target, tainted-local read into a member) are loud compile errors in Stage 1.
        RecordLongLivedLambdaStore(assign.Target, assign.Value);
        GuardCaptureEscapeStore(assign.Target, assign.Value);

        // Aggregate FIELD write: point.x = 5, result.Item1 = 42. Triggered by the containing type
        // being aggregate, regardless of instance kind. (Aggregate auto-PROPERTY writes ride the
        // shared EmitPropertySet path below — wave-9 round-5 [X2] factoring.)
        if (assign.Target is IFieldReferenceOperation && TryGetAggregateMemberTarget(assign.Target, out var aggInstance, out var aggMemberName)
            && aggInstance.Type is INamedTypeSymbol aggContaining && EmitContext.IsAggregateType(aggContaining)
            && _ctx.GetAggregateLayout(aggContaining).TryGetIndex(aggMemberName, out var fieldIndex))
        {
            var srcVal = VisitExpression(assign.Value);
            var arrExpr = LoadInstanceRaw(aggInstance);
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CLeaf> { arrExpr, Const(fieldIndex, "SystemInt32"), srcVal });
            return srcVal;
        }

        if (assign.Target is IArrayElementReferenceOperation arrayElem)
        {
            var arrayVal = VisitExpression(arrayElem.ArrayReference);
            var indexVal = VisitExpression(arrayElem.Indices[0]);
            var srcVal = VisitExpression(assign.Value);
            var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
            var arrayType = GetArrayType(arrSymbol);
            var elementType = GetArrayElemType(arrSymbol);
            EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<CLeaf> { arrayVal, indexVal, srcVal });
            return srcVal;
        }

        // cross-behaviour field write → SetProgramVariable
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } ubTarget && ExternResolver.IsUdonSharpBehaviour(ubTarget.Field.ContainingType))
        {
            // Cross-behaviour delegate field: the generic arm below ships the BUNDLE REFERENCE in ONE
            // SetProgramVariable (design §2.3 — one object[] shared by both heaps; a creation RHS carries
            // the REAL funcaddr even cross-Behaviour, the invoke-side target-identity guard is the gate).
            // Only the tuple-return reject stays special (design §3.4-3 KEEP).
            if (ubTarget.Field.Type is INamedTypeSymbol dlgType && dlgType.DelegateInvokeMethod != null
                && dlgType.DelegateInvokeMethod.ReturnType.IsTupleType)
                throw new System.NotSupportedException($"Tuple-return delegate field '{ubTarget.Field.Name}' is not supported.");

            var srcVal = VisitExpression(assign.Value);
            var instanceVal2 = VisitExpression(ubTarget.Instance);
            var nameConst = Const(ubTarget.Field.Name, "SystemString");
            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal2, nameConst, srcVal });
            return srcVal;
        }

        if (assign.Target is IFieldReferenceOperation { Instance: not null } fieldTarget
            && fieldTarget.Field.ContainingType.IsValueType)
        {
            var srcVal = VisitExpression(assign.Value);
            var containingType = GetUdonType(fieldTarget.Field.ContainingType);
            var instanceVal = fieldTarget.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(containingType), containingType)
                : VisitExpression(fieldTarget.Instance);
            var valueType = GetUdonType(fieldTarget.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldTarget.Field.Name, valueType);
            EmitExternVoid(sig, new List<CLeaf> { instanceVal, srcVal });
            return srcVal;
        }

        // Property/indexer SET (struct, this/base, static, variable-receiver, interface, extern) —
        // shared with the deconstruction lvalue path. Wave-9 round-5 [X2]: evaluation order is
        // receiver → index args → value (C# order); the old inline arm evaluated the RHS first.
        if (assign.Target is IPropertyReferenceOperation propRef)
            return EmitPropertySet(propRef, () => VisitExpression(assign.Value));

        // Non-this reference-type field assignment → extern field setter
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } refFieldTarget
            && !refFieldTarget.Field.ContainingType.IsValueType
            && !ExternResolver.IsUdonSharpBehaviour(refFieldTarget.Field.ContainingType))
        {
            var srcVal = VisitExpression(assign.Value);
            var instanceVal = VisitExpression(refFieldTarget.Instance);
            var containingType = GetUdonType(refFieldTarget.Field.ContainingType);
            var valueType = GetUdonType(refFieldTarget.Field.Type);
            EmitExternVoid(ExternResolver.BuildFieldSetSignature(containingType, refFieldTarget.Field.Name, valueType, isValueType: false), new List<CLeaf> { instanceVal, srcVal });
            return srcVal;
        }

        // Fallback: local variable or this.field. Delegate assignments (field and local, including
        // `d = null` and `a = b` reference copy) ride this generic path now: VisitExpression yields the
        // bundle reference (or null const) and the store is a single reference copy (design §2.3).

        // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
        var srcFallback = VisitExpression(assign.Value);
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
               && assign.Target.Type is INamedTypeSymbol tAgg && EmitContext.IsAggregateType(tAgg)
            ? EmitDeepCloneAggregate(loaded, tAgg) : loaded;
    }

}
