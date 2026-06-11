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

        // Aggregate field/auto-property write: point.x = 5, result.Item1 = 42, v.X = 7 (struct auto-property).
        // Triggered by the containing type being aggregate, regardless of instance kind.
        if (TryGetAggregateMemberTarget(assign.Target, out var aggInstance, out var aggMemberName)
            && aggInstance.Type is INamedTypeSymbol aggContaining && EmitContext.IsAggregateType(aggContaining)
            && _ctx.GetAggregateLayout(aggContaining).TryGetIndex(aggMemberName, out var fieldIndex))
        {
            var srcVal = VisitExpression(assign.Value);
            var arrExpr = LoadInstanceRaw(aggInstance);
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CLeaf> { arrExpr, Const(fieldIndex, "SystemInt32"), srcVal });
            return srcVal;
        }

        // Computed (non-auto) struct property setter: p.Both = v → call the user setter with the receiver
        // object[] as synthetic param0 (mutates this-fields through the shared backing array). Auto-properties
        // are handled by the aggregate fast path above (their backing field is in the layout).
        if (assign.Target is IPropertyReferenceOperation { Property: { IsIndexer: false, SetMethod: { } aggSetter } } aggSetRef
            && aggSetRef.Instance?.Type is INamedTypeSymbol aggSetType && EmitContext.IsAggregateType(aggSetType)
            && _methodFunctions.ContainsKey(aggSetter.OriginalDefinition))
        {
            var srcVal = VisitExpression(assign.Value);
            EmitExprStmt(EmitCallToMethod(aggSetter.OriginalDefinition,
                new List<CLeaf> { LoadInstanceRaw(aggSetRef.Instance), srcVal }));
            return srcVal;
        }

        // User-defined indexer on a user STRUCT instance (`s[i] = v`) → call the setter with the struct
        // receiver object[] as param0, the index args, then the value. Mirrors the GET routing in
        // VisitIndexerGet; without it this falls to a bogus SystemObjectArray.__set_Item extern. (diff-fuzz wave 4)
        if (assign.Target is IPropertyReferenceOperation { Property: { IsIndexer: true, SetMethod: { } aggIdxSetter } } aggIdxSetRef
            && aggIdxSetRef.Instance?.Type is INamedTypeSymbol aggIdxSetType && EmitContext.IsAggregateType(aggIdxSetType)
            && _methodFunctions.ContainsKey(aggIdxSetter.OriginalDefinition))
        {
            var setterArgs = new List<CLeaf> { LoadInstanceRaw(aggIdxSetRef.Instance) };
            setterArgs.AddRange(EvaluateIndexerArgs(aggIdxSetRef)); // wave-9 r4: named index args bind by ordinal
            var srcVal = VisitExpression(assign.Value);
            setterArgs.Add(srcVal);
            EmitExprStmt(EmitCallToMethod(aggIdxSetter.OriginalDefinition, setterArgs));
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

        if (assign.Target is IPropertyReferenceOperation propRef)
        {
            var srcVal = VisitExpression(assign.Value);
            // Virtual dispatch through `this` (round 7): a write inside an inherited base body binds
            // the BASE accessor — resolve to the chain-leaf override for the this-path setter lookups
            // below; `base.P` (and every non-this receiver) keeps the static binding.
            var dispatchProp = ResolveDispatchProperty(propRef);
            var propContainingUdon = GetUdonType(propRef.Property.ContainingType);

            // User-defined indexer on this/base → internal setter call (index args followed by the value).
            if (dispatchProp.IsIndexer && propRef.Instance is IInstanceReferenceOperation
                && dispatchProp.SetMethod != null && _methodFunctions.ContainsKey(dispatchProp.SetMethod))
            {
                // Wave-9 round-4: index args slotted by parameter ordinal (named/reordered index args
                // bind by name; the base[...] flavor rides this same arm via the base-instance copy).
                var setterArgs = EvaluateIndexerArgs(propRef);
                setterArgs.Add(srcVal);
                EmitExprStmt(EmitCallToMethod(dispatchProp.SetMethod, setterArgs));
                return srcVal;
            }

            // Static property setter (no instance) — e.g. Time.timeScale = 1.0f
            if (propRef.Instance == null)
            {
                var staticValType = GetUdonType(propRef.Property.Type);
                EmitExternVoid(ExternResolver.BuildPropertySetSignature(propContainingUdon, propRef.Property.Name, staticValType), new List<CLeaf> { srcVal });
                return srcVal;
            }

            // Behaviour/MonoBehaviour have no Udon externs; resolve to actual type
            if (propContainingUdon is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            {
                propContainingUdon = propRef.Instance is IInstanceReferenceOperation
                    ? GetUdonType(_classSymbol)
                    : GetUdonType(propRef.Instance.Type);
            }
            var instanceVal = propRef.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(propContainingUdon), propContainingUdon)
                : VisitExpression(propRef.Instance);
            var containingType = propContainingUdon;
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                // Wave-9 round-2 [W6]: user indexer WRITE through a VARIABLE receiver → cross-program
                // setter dispatch (index args + the value as the setter's LAST parameter). Pre-fix this
                // fell to the extern arm below and emitted a nonexistent IUdonEventReceiver.__set_Item.
                if (IsVariableReceiverBehaviourIndexer(propRef) && propRef.Property.SetMethod is { } recvIdxSetter)
                {
                    var orderedIdx = EvaluateIndexerArgs(propRef);
                    orderedIdx.Add(srcVal);
                    EmitCrossIndexerCall(recvIdxSetter, instanceVal, orderedIdx); // void: self-emitting
                    return srcVal;
                }
                // Wave-9 round-4 [X4]/[X9]: user indexer WRITE through an INTERFACE-typed receiver →
                // dispatch the setter through its interface bridge (index args + the value as the
                // setter's LAST parameter). Pre-fix this fell to the extern arm below and emitted a
                // nonexistent IUdonEventReceiver.__set_Item (loud validator crash on legal C#).
                if (TryGetInterfaceAccessorLayout(propRef, propRef.Property.SetMethod, out var ifaceIdxSetMl))
                {
                    var ifaceOrderedIdx = EvaluateIndexerArgs(propRef);
                    ifaceOrderedIdx.Add(srcVal);
                    EmitInterfaceAccessorCall(propRef.Property.SetMethod, ifaceIdxSetMl, instanceVal,
                        ifaceOrderedIdx); // void: self-emitting
                    return srcVal;
                }
                var indexArgs = new List<CLeaf> { instanceVal };
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    indexArgs.Add(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                indexArgs.Add(srcVal);
                var indexParamStr = string.Join("_", indexTypes);
                // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                EmitExternVoid($"{containingType}.__set_{propRef.Property.MetadataName}__{indexParamStr}_{valueType}__SystemVoid", indexArgs);
            }
            else switch (propRef.Instance)
            {
                case IInstanceReferenceOperation
                    when dispatchProp.SetMethod != null && _methodFunctions.TryGetValue(dispatchProp.SetMethod, out _):
                    // User-defined property setter on this → internal call
                    EmitExprStmt(EmitCallToMethod(dispatchProp.SetMethod, new List<CLeaf> { srcVal }));
                    break;
                case IInstanceReferenceOperation
                    when dispatchProp.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                         && ExternResolver.IsUdonSharpBehaviour(dispatchProp.ContainingType)
                         && dispatchProp.ContainingType.Name != "UdonSharpBehaviour":
                    // Auto-property set on this → direct variable assignment (user-defined classes only)
                    EmitStoreField(dispatchProp.Name, srcVal);
                    break;
                default:
                {
                    // Interface property set → dispatch the setter through its interface bridge (SetProgramVariable
                    // the value, SendCustomEvent the setter), like an interface method call. Without this the
                    // fall-through emits a non-existent __set_Value extern on IUdonEventReceiver.
                    if (propRef.Property.SetMethod is { } ifaceSetter
                        && propRef.Property.ContainingType.TypeKind == TypeKind.Interface
                        && propRef.Property.ContainingType.SpecialType == SpecialType.None
                        && propRef.Instance is not IInstanceReferenceOperation
                        && _planner.GetLayout(propRef.Property.ContainingType).Methods.TryGetValue(ifaceSetter, out var ifaceSetterMl))
                    {
                        var paramNameConst = Const(ifaceSetterMl.ParamIds[0], "SystemString");
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, srcVal });
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
                            new List<CLeaf> { instanceVal, Const(LayoutPlanner.InterfaceDispatchName(ifaceSetter, ifaceSetterMl), "SystemString") });
                    }
                    else if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                    {
                        if (propRef.Property.Type is INamedTypeSymbol dlgPropType && dlgPropType.DelegateInvokeMethod != null)
                            throw new System.NotSupportedException("Delegate properties are not supported in v2.1. Use delegate fields instead.");

                        var isAutoSet = propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
                        if (isAutoSet || propRef.Property.SetMethod == null)
                        {
                            // Auto-property or read-only: direct SetProgramVariable("PropertyName")
                            var nameConst = Const(propRef.Property.Name, "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, nameConst, srcVal });
                        }
                        else
                        {
                            // Non-auto property setter: call via SendCustomEvent
                            var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);

                            // SetProgramVariable for the value parameter
                            var paramNameConst = Const(setParamIds[0], "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, srcVal });

                            // SendCustomEvent to invoke setter
                            var eventConst = Const(exportName, "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<CLeaf> { instanceVal, eventConst });
                        }
                    }
                    else
                    {
                        EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<CLeaf> { instanceVal, srcVal });
                    }

                    break;
                }
            }
            return srcVal;
        }

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
