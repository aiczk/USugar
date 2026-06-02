using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Common lvalue handling shared by SimpleAssignmentHandler / CompoundAssignmentHandler /
/// DeconstructionAssignmentHandler. Provides LValueCapture, write-back, and target field
/// name resolution.
/// </summary>
public abstract class AssignmentHandlerBase : HandlerBase
{
    protected AssignmentHandlerBase(EmitContext ctx) : base(ctx) { }

    // ── LValue Capture ──
    // Evaluates and caches sub-expressions of an l-value (array ref, index, instance)
    // to avoid re-evaluating side-effecting expressions during write-back.

    protected struct LValueCapture
    {
        public CLeaf Value;          // The evaluated l-value value
        public CLeaf ArrayVal;       // Cached array reference (for array elements)
        public CLeaf IndexVal;       // Cached index (for array elements)
        public CLeaf InstanceVal;    // Cached instance (for cross-behaviour fields/properties)
        public List<CLeaf> IndexArgs; // Cached index args (for user indexers — avoid re-evaluating side effects)
    }

    protected LValueCapture CaptureLValue(IOperation target)
    {
        switch (target)
        {
            // User-defined indexer on this: cache the (possibly side-effecting) index args ONCE, so a
            // compound assignment (`this[Idx()] += x`) does not evaluate the index twice.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when idxRef.Property.GetMethod != null && _methodFunctions.ContainsKey(idxRef.Property.GetMethod):
            {
                // Each VisitExpression(arg) is bound to a scratch leaf once under ANF — the index side effect
                // runs exactly once and the SAME leaf is reused by the getter here and the setter in
                // EmitWriteBack (via IndexArgs), so the cache itself is load-bearing but needs no extra copy.
                var cachedArgs = new List<CLeaf>();
                foreach (var arg in idxRef.Arguments)
                    cachedArgs.Add(VisitExpression(arg.Value));
                var currentVal = EmitCallToMethod(idxRef.Property.GetMethod, new List<CLeaf>(cachedArgs));
                return new LValueCapture { Value = currentVal, IndexArgs = cachedArgs };
            }
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && aggFieldRef.Instance.Type is INamedTypeSymbol aggCapType
                && EmitContext.IsAggregateType(aggCapType):
            {
                var layout = _ctx.GetAggregateLayout(aggCapType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    var arrVal = LoadInstanceRaw(aggFieldRef.Instance);
                    var idxVal = Const(elemIdx, "SystemInt32");
                    var currentVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                        new List<CLeaf> { arrVal, idxVal }, "SystemObject");
                    return new LValueCapture { Value = currentVal, ArrayVal = arrVal, IndexVal = idxVal };
                }
                goto default;
            }
            case IArrayElementReferenceOperation arrayElem:
            {
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elemAccessorType = GetArrayElemType(arrSymbol);

                // Evaluate the array ref and index ONCE; the resulting scratch leaves are reused by
                // EmitWriteBack (compound RMW) via ArrayVal/IndexVal so a side-effecting index (`arr[Next()]
                // += v`) runs once with the read Get and the write Set targeting the SAME element. Under ANF
                // VisitExpression already binds each to a single-assignment scratch, so the capture needs no
                // extra copy slot — storing the leaves directly preserves the read↔writeback sharing.
                var arrayVal = VisitExpression(arrayElem.ArrayReference);
                var indexVal = VisitExpression(arrayElem.Indices[0]);

                // Read current value: arr[idx]
                var valResult = ExternCall(
                    $"{arrayType}.__Get__SystemInt32__{elemAccessorType}",
                    new List<CLeaf> { arrayVal, indexVal },
                    GetUdonType(arrayElem.Type));
                return new LValueCapture { Value = valResult, ArrayVal = arrayVal, IndexVal = indexVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceVal = VisitExpression(fieldRef.Instance);
                // Read via GetProgramVariable
                var nameConst = Const(fieldRef.Field.Name, "SystemString");
                var valResult = ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<CLeaf> { instanceVal, nameConst },
                    "SystemObject");
                return new LValueCapture { Value = valResult, InstanceVal = instanceVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                var instanceVal = VisitExpression(fieldRef2.Instance);
                var containingType = GetUdonType(fieldRef2.Field.ContainingType);
                var valueType = GetUdonType(fieldRef2.Field.Type);
                var sig = ExternResolver.BuildPropertyGetSignature(containingType, fieldRef2.Field.Name, valueType);
                var valResult = ExternCall(sig, new List<CLeaf> { instanceVal }, valueType);
                return new LValueCapture { Value = valResult, InstanceVal = instanceVal };
            }
            default:
                // Simple l-value (local, field on this): just evaluate normally
                return new LValueCapture { Value = VisitExpression(target) };
        }
    }

    // ── EmitWriteBack ──
    // Write back a computed value to non-trivial l-value targets (array elements, properties).
    // For local/field variables, also writes back via EmitStoreField.

    protected void EmitWriteBack(IOperation target, CLeaf valueVal, LValueCapture lv = default)
    {
        switch (target)
        {
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && aggFieldRef.Instance.Type is INamedTypeSymbol aggWbType
                && EmitContext.IsAggregateType(aggWbType):
            {
                var layout = _ctx.GetAggregateLayout(aggWbType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    var arrVal = lv.ArrayVal ?? VisitExpression(aggFieldRef.Instance);
                    EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                        new List<CLeaf> { arrVal, Const(elemIdx, "SystemInt32"), valueVal });
                    return;
                }
                break;
            }
            case IArrayElementReferenceOperation arrayElem:
            {
                // Use captured array/index if available (avoid double evaluation)
                var arrayVal = lv.ArrayVal ?? VisitExpression(arrayElem.ArrayReference);
                var indexVal = lv.IndexVal ?? VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<CLeaf> { arrayVal, indexVal, valueVal });
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                // Cross-behaviour field write-back → SetProgramVariable
                var instanceVal = lv.InstanceVal ?? VisitExpression(fieldRef.Instance);
                var nameConst = Const(fieldRef.Field.Name, "SystemString");
                EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, nameConst, valueVal });
                break;
            }
            // Auto-property on this → backing field already handled by write-back to field (user-defined classes only)
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } propRef when propRef.Property.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true && ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Property.ContainingType.Name != "UdonSharpBehaviour":
                return;
            // User-defined indexer on this → call setter with the index args followed by the value. Reuse
            // the index args cached by CaptureLValue (compound assignment) to avoid re-evaluating them.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true, SetMethod: not null } } idxRef when _methodFunctions.TryGetValue(idxRef.Property.SetMethod, out _):
            {
                var setterArgs = lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : new List<CLeaf>();
                if (lv.IndexArgs == null)
                    foreach (var arg in idxRef.Arguments) setterArgs.Add(VisitExpression(arg.Value));
                setterArgs.Add(valueVal);
                EmitExprStmt(EmitCallToMethod(idxRef.Property.SetMethod, setterArgs));
                return;
            }
            // User-defined property on this → call setter
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { SetMethod: not null } } propRef when _methodFunctions.TryGetValue(propRef.Property.SetMethod, out _):
                EmitExprStmt(EmitCallToMethod(propRef.Property.SetMethod, new List<CLeaf> { valueVal }));
                return;
            // Cross-behaviour UdonSharpBehaviour property → SetProgramVariable / SendCustomEvent
            case IPropertyReferenceOperation propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation:
            {
                var instanceVal = VisitExpression(propRef.Instance);
                var isAutoSet = propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
                if (isAutoSet || propRef.Property.SetMethod == null)
                {
                    var nameConst = Const(propRef.Property.Name, "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, nameConst, valueVal });
                }
                else
                {
                    var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                    var paramNameConst = Const(setParamIds[0], "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, valueVal });
                    var eventConst = Const(exportName, "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<CLeaf> { instanceVal, eventConst });
                }
                return;
            }
            // Property on an aggregate (struct) instance — e.g. compound `p.X += 1` / `p.Computed += 1`,
            // which routes through CaptureLValue + EmitWriteBack. Auto-property → write the backing-field
            // slot by layout index; computed (non-auto) → call the user setter with the receiver as param0.
            case IPropertyReferenceOperation { Property: { IsIndexer: false } } aggPropRef
                when aggPropRef.Instance?.Type is INamedTypeSymbol aggPropType && EmitContext.IsAggregateType(aggPropType):
            {
                if (_ctx.GetAggregateLayout(aggPropType).TryGetIndex(aggPropRef.Property.Name, out var propIdx))
                {
                    var arrVal = lv.ArrayVal ?? LoadInstanceRaw(aggPropRef.Instance);
                    EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                        new List<CLeaf> { arrVal, Const(propIdx, "SystemInt32"), valueVal });
                    return;
                }
                if (aggPropRef.Property.SetMethod is { } aggSetter && _methodFunctions.ContainsKey(aggSetter.OriginalDefinition))
                {
                    EmitExprStmt(EmitCallToMethod(aggSetter.OriginalDefinition,
                        new List<CLeaf> { LoadInstanceRaw(aggPropRef.Instance), valueVal }));
                    return;
                }
                break;
            }
            // Resolve containing type and instance
            case IPropertyReferenceOperation propRef:
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                if (containingType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
                    containingType = propRef.Instance is IInstanceReferenceOperation
                        ? GetUdonType(_classSymbol)
                        : GetUdonType(propRef.Instance.Type);

                CLeaf wbInstanceVal;
                if (propRef.Instance is IInstanceReferenceOperation)
                    wbInstanceVal = LoadField(_ctx.DeclareThisOnce(containingType), containingType);
                else if (propRef.Instance != null)
                    wbInstanceVal = VisitExpression(propRef.Instance);
                else
                {
                    // Static property: no instance
                    var valueType = GetUdonType(propRef.Property.Type);
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<CLeaf> { valueVal });
                    return;
                }

                var propValueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    var indexArgs = new List<CLeaf> { wbInstanceVal };
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        indexArgs.Add(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    indexArgs.Add(valueVal);
                    var indexParamStr = string.Join("_", indexTypes);
                    EmitExternVoid($"{containingType}.__set_Item__{indexParamStr}_{propValueType}__SystemVoid", indexArgs);
                }
                else
                {
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, propValueType), new List<CLeaf> { wbInstanceVal, valueVal });
                }
                // COW dirty: struct property setter → copy back to force heap update
                if (propRef.Property.ContainingType.IsValueType)
                {
                    var cowSlot = _ctx.AllocTemp(containingType);
                    EmitAssign(cowSlot, wbInstanceVal);
                }

                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                // Struct field setter (e.g., vec.y += 3f where vec is an array element)
                var instanceVal = lv.InstanceVal ?? VisitExpression(fieldRef2.Instance);
                var containingType = GetUdonType(fieldRef2.Field.ContainingType);
                var valueType = GetUdonType(fieldRef2.Field.Type);
                var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef2.Field.Name, valueType);
                EmitExternVoid(sig, new List<CLeaf> { instanceVal, valueVal });
                break;
            }
            default:
            {
                // Simple l-value (local, field on this): write back via EmitStoreField
                var fieldName = GetAssignTargetFieldName(target);
                EmitStoreField(fieldName, valueVal);
                break;
            }
        }
    }

    /// <summary>
    /// Resolve the field name (lvalue) for a simple assignment target.
    /// Used by the fallback paths in SimpleAssignmentHandler / CompoundAssignmentHandler.
    /// </summary>
    protected string GetAssignTargetFieldName(IOperation target)
    {
        switch (target)
        {
            case ILocalReferenceOperation localRef:
                if (_localBindings.TryGetValue(localRef.Local, out var lb))
                    return lb.Id;
                throw new System.InvalidOperationException(
                    $"Cannot resolve local variable '{localRef.Local.Name}' for assignment.");
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                return fieldRef.Field.Name;
            case IParameterReferenceOperation paramRef:
                return GetParamVarId(paramRef.Parameter);
            default:
                throw new System.NotSupportedException(
                    $"Unsupported simple assignment target: {target.GetType().Name}");
        }
    }
}
