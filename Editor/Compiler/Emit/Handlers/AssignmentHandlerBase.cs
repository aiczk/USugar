using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Base for the assignment-family handlers (SimpleAssignmentHandler, CompoundAssignmentHandler,
/// DeconstructionAssignmentHandler, NullableHandler). LValueCapture/CaptureLValue and EmitWriteBack
/// — capture an l-value's sub-expressions once, then write back after computing the new value — are
/// used by CompoundAssignmentHandler and NullableHandler for their read-modify-write targets;
/// GetAssignTargetFieldName is used by SimpleAssignmentHandler. EmitWriteBack's array-element and
/// cross-behaviour-field arms only evaluate the receiver/index legs (or reuse CaptureLValue's cached
/// ones) — the actual Set emission is HandlerBase.EmitArrayElementSet / EmitCrossBehaviourFieldSet,
/// shared with HandlerBase's single-write path (PrepareArrayElementSet / TryPrepareFieldSet) so the
/// two mechanisms can't drift on the emitted extern.
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
        public NdimAccessPlan? NdimPlan; // Cached N-dim bounds/backing/flat-index plan (rank>1 array elements)
    }

    protected LValueCapture CaptureLValue(IOperation target)
    {
        switch (target)
        {
            // User-defined indexer on this: cache the (possibly side-effecting) index args ONCE, so a
            // compound assignment (`this[Idx()] += x`) does not evaluate the index twice.
            // ResolveDispatchProperty (round 7): `this[i]` inside an inherited base body binds the BASE
            // indexer — read through the chain-leaf override's getter; `base[i]` keeps the static binding.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when ResolveDispatchProperty(idxRef).GetMethod is { } idxDispatchGetter
                && _methodFunctions.ContainsKey(idxDispatchGetter):
            {
                // Each VisitExpression(arg) is bound to a scratch leaf once under ANF — the index side effect
                // runs exactly once and the SAME leaf is reused by the getter here and the setter in
                // EmitWriteBack (via IndexArgs), so the cache itself is load-bearing but needs no extra copy.
                // Wave-9 round-4: slotted by parameter ordinal (named/reordered index args bind by name).
                var cachedArgs = EvaluateIndexerArgs(idxRef);
                var currentVal = EmitCallToMethod(idxDispatchGetter, new List<CLeaf>(cachedArgs));
                return new LValueCapture { Value = currentVal, IndexArgs = cachedArgs };
            }
            // User-defined indexer on a user STRUCT instance (`s[i] += x`): cache the struct receiver and the
            // (possibly side-effecting) index args ONCE, then read via the getter with the receiver as param0.
            // The same receiver/args are reused by the setter in EmitWriteBack. Mirrors VisitIndexerGet.
            case IPropertyReferenceOperation { Property: { IsIndexer: true } } sIdxRef
                when sIdxRef.Instance?.Type is INamedTypeSymbol sIdxType && EmitContext.IsAggregateType(sIdxType)
                && sIdxRef.Property.GetMethod is { } sIdxGetterRaw:
            {
                var recv = LoadInstanceRaw(sIdxRef.Instance);
                var cachedArgs = EvaluateIndexerArgs(sIdxRef); // wave-9 r4: named index args bind by ordinal
                var getterArgs = new List<CLeaf> { recv };
                getterArgs.AddRange(cachedArgs);
                var currentVal = EmitCallToMethod(ResolveStructMember(sIdxGetterRaw), getterArgs);
                return new LValueCapture { Value = currentVal, ArrayVal = recv, IndexArgs = cachedArgs };
            }
            // Wave-9 round-2 [W6]: user indexer COMPOUND assignment through a VARIABLE receiver
            // (`s[i] += x` where s is an own-typed copy / base-typed ref / another behaviour): read via
            // the cross-program getter; the receiver and the ordinal-ordered index args are cached so
            // EmitWriteBack's setter dispatch reuses them (index side effects run exactly once).
            case IPropertyReferenceOperation vIdxRef
                when IsVariableReceiverBehaviourIndexer(vIdxRef) && vIdxRef.Property.GetMethod is { } vIdxGetter:
            {
                var recvVal = VisitExpression(vIdxRef.Instance);
                var cachedArgs = EvaluateIndexerArgs(vIdxRef);
                var currentVal = EmitCrossIndexerCall(vIdxGetter, recvVal, cachedArgs,
                    TryMarkReentrantCrossDispatch(vIdxRef, vIdxGetter)); // wave-12 r2 [V1]
                return new LValueCapture { Value = currentVal, InstanceVal = recvVal, IndexArgs = cachedArgs };
            }
            // Wave-9 round-4 [X4]: user indexer COMPOUND assignment (and inc-dec) through an
            // INTERFACE-typed receiver: read via the interface getter bridge; the receiver and the
            // ordinal-ordered index args are cached so EmitWriteBack's setter bridge dispatch reuses
            // them (index side effects run exactly once). Pre-fix both legs fell to extern resolution
            // and emitted nonexistent IUdonEventReceiver.__get_Item/__set_Item externs.
            case IPropertyReferenceOperation iIdxRef
                when iIdxRef.Property.IsIndexer
                && TryGetInterfaceAccessorLayout(iIdxRef, iIdxRef.Property.GetMethod, out var iIdxGetMl):
            {
                var recvVal = VisitExpression(iIdxRef.Instance);
                var cachedArgs = EvaluateIndexerArgs(iIdxRef);
                var currentVal = EmitInterfaceAccessorCall(iIdxRef.Property.GetMethod, iIdxGetMl, recvVal, cachedArgs,
                    TryMarkReentrantCrossDispatch(iIdxRef, iIdxRef.Property.GetMethod)); // wave-12 r2 [V1]
                return new LValueCapture { Value = currentVal, InstanceVal = recvVal, IndexArgs = cachedArgs };
            }
            // Wave-11 round-11 [Z1]: NON-indexer property on an aggregate (struct/tuple) receiver —
            // compound assignment and inc-dec (`ss[Ix()].P += Mut()`, `arr[i].X++`). Evaluate the
            // receiver legs ONCE and cache the raw backing object[] so EmitWriteBack's
            // aggregate-property arm stores into the SAME cell. Pre-fix this fell to the default arm
            // (ArrayVal stayed null) and the write-back re-ran side-effecting receiver legs AFTER the
            // RHS (wrong element + legs twice; VM-proven ref trace=12/result=803 vs 121/283). The read
            // mirrors VisitPropertyReference verbatim: auto-prop → layout slot; computed → user getter
            // with the receiver as param0 (struct-typed results deep-clone, value semantics).
            case IPropertyReferenceOperation { Property: { IsIndexer: false } } aggCapPropRef
                when aggCapPropRef.Instance?.Type is INamedTypeSymbol aggCapPropType
                && EmitContext.IsAggregateType(aggCapPropType):
            {
                if (_ctx.GetAggregateLayout(aggCapPropType).TryGetIndex(aggCapPropRef.Property.Name, out var capSlotIdx))
                {
                    var recv = LoadInstanceRaw(aggCapPropRef.Instance);
                    var slotIdxVal = Const(capSlotIdx, "SystemInt32");
                    CLeaf slotVal = ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                        new List<CLeaf> { recv, slotIdxVal }, "SystemObject");
                    if (aggCapPropRef.Property.Type is INamedTypeSymbol capSlotAgg && EmitContext.IsAggregateType(capSlotAgg))
                        slotVal = EmitDeepCloneAggregate(slotVal, capSlotAgg);
                    return new LValueCapture { Value = slotVal, ArrayVal = recv, IndexVal = slotIdxVal };
                }
                if (aggCapPropRef.Property.GetMethod is { } capGetterRaw)
                {
                    var recv = LoadInstanceRaw(aggCapPropRef.Instance);
                    CLeaf getVal = EmitCallToMethod(ResolveStructMember(capGetterRaw), new List<CLeaf> { recv });
                    if (aggCapPropRef.Property.Type is INamedTypeSymbol capGetAgg && EmitContext.IsAggregateType(capGetAgg))
                        getVal = EmitDeepCloneAggregate(getVal, capGetAgg);
                    return new LValueCapture { Value = getVal, ArrayVal = recv };
                }
                goto default;
            }
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && aggFieldRef.Instance.Type is INamedTypeSymbol aggCapType
                && EmitContext.IsAggregateType(aggCapType):
            {
                var layout = _ctx.GetAggregateLayout(aggCapType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    RejectStaticReadonlyWriteThrough(aggFieldRef.Instance); // §3.3, R5 (compound/inc-dec write-back)
                    var arrVal = LoadInstanceRaw(aggFieldRef.Instance);
                    var idxVal = Const(elemIdx, "SystemInt32");
                    var currentVal = ExternCall(ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                        new List<CLeaf> { arrVal, idxVal }, "SystemObject");
                    return new LValueCapture { Value = currentVal, ArrayVal = arrVal, IndexVal = idxVal };
                }
                goto default;
            }
            case IArrayElementReferenceOperation ndimCapElem when ndimCapElem.Indices.Length > 1:
            {
                RejectStaticReadonlyWriteThrough(ndimCapElem.ArrayReference); // §3.3, R5 (compound/inc-dec write-back)
                var ndimType = (IArrayTypeSymbol)ndimCapElem.ArrayReference.Type;
                var elemUdonType = GetUdonType(ndimType.ElementType);
                var plan = PrepareNdimAccess(ndimCapElem.ArrayReference, ndimCapElem.Indices, ndimType);
                var ndimCurrentVal = EmitNdimReadFromPlan(ndimCapElem, plan, elemUdonType);
                return new LValueCapture { Value = ndimCurrentVal, NdimPlan = plan };
            }
            case IArrayElementReferenceOperation arrayElem:
            {
                RejectStaticReadonlyWriteThrough(arrayElem.ArrayReference); // §3.3, R5 (compound/inc-dec write-back)
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elemAccessorType = GetArrayElemType(arrSymbol);

                // Evaluate the array ref and index ONCE; the resulting scratch leaves are reused by
                // EmitWriteBack (compound RMW) via ArrayVal/IndexVal so a side-effecting index (`arr[Next()]
                // += v`) runs once with the read Get and the write Set targeting the SAME element. Under ANF
                // VisitExpression already binds each to a single-assignment scratch, so the capture needs no
                // extra copy slot — storing the leaves directly preserves the read↔writeback sharing.
                var arrayVal = VisitExpression(arrayElem.ArrayReference);
                var indexVal = ResolveArrayIndex(arrayVal, arrayType, arrayElem.Indices[0]);

                // Read current value: arr[idx]
                var valResult = ExternCall(
                    ExternResolver.BuildArrayGetSignature(arrayType, elemAccessorType),
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
                    EmitExternVoid(ExternResolver.BuildArraySetSignature("SystemObjectArray", "SystemObject"),
                        new List<CLeaf> { arrVal, Const(elemIdx, "SystemInt32"), valueVal });
                    return;
                }
                break;
            }
            case IArrayElementReferenceOperation ndimWbElem when ndimWbElem.Indices.Length > 1:
            {
                // Reuse CaptureLValue's plan when available (avoid re-evaluating indices/bundle);
                // the read-only fallback path is defensive (mirrors the rank-1 arm's ?? default).
                var plan = lv.NdimPlan
                    ?? PrepareNdimAccess(ndimWbElem.ArrayReference, ndimWbElem.Indices, (IArrayTypeSymbol)ndimWbElem.ArrayReference.Type);
                EmitNdimWriteFromPlan(ndimWbElem, plan, valueVal);
                break;
            }
            case IArrayElementReferenceOperation arrayElem:
            {
                // Use captured array/index if available (avoid double evaluation)
                var arrayVal = lv.ArrayVal ?? VisitExpression(arrayElem.ArrayReference);
                var indexVal = lv.IndexVal ?? VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                EmitArrayElementSet(arrSymbol, arrayVal, indexVal, valueVal);
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                // Cross-behaviour field write-back → SetProgramVariable
                var instanceVal = lv.InstanceVal ?? VisitExpression(fieldRef.Instance);
                EmitCrossBehaviourFieldSet(fieldRef.Field, instanceVal, valueVal);
                break;
            }
            // Auto-property on this → backing field already handled by write-back to field (user-defined
            // classes only). ResolveDispatchProperty (round 7): an inherited base body's write binds the
            // BASE accessor — all three this-path cases below dispatch the chain-leaf override instead;
            // `base.P` keeps the static binding (its base-instance copy accessors).
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } propRef
                when ResolveDispatchProperty(propRef) is { } autoDispatchProp
                && autoDispatchProp.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                && ExternResolver.IsUdonSharpBehaviour(autoDispatchProp.ContainingType)
                && autoDispatchProp.ContainingType.Name != "UdonSharpBehaviour":
                return;
            // User-defined indexer on this → call setter with the index args followed by the value. Reuse
            // the index args cached by CaptureLValue (compound assignment) to avoid re-evaluating them.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when ResolveDispatchProperty(idxRef).SetMethod is { } idxDispatchSetter
                && _methodFunctions.TryGetValue(idxDispatchSetter, out _):
            {
                // Wave-9 round-4: the no-cache fallback slots by parameter ordinal too (named args).
                var setterArgs = lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : EvaluateIndexerArgs(idxRef);
                setterArgs.Add(valueVal);
                EmitExprStmt(EmitCallToMethod(idxDispatchSetter, setterArgs));
                return;
            }
            // User-defined property on this → call setter
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } propRef
                when ResolveDispatchProperty(propRef).SetMethod is { } dispatchSetter
                && _methodFunctions.TryGetValue(dispatchSetter, out _):
                EmitExprStmt(EmitCallToMethod(dispatchSetter, new List<CLeaf> { valueVal }));
                return;
            // Wave-9 round-2 [W6]: user indexer write-back through a VARIABLE receiver → cross-program
            // setter dispatch, reusing the receiver/index leaves cached by CaptureLValue. MUST sit
            // before the generic cross-behaviour property arm below, which would SetProgramVariable
            // only the setter's FIRST param (an index) and drop the value.
            case IPropertyReferenceOperation vIdxRef
                when IsVariableReceiverBehaviourIndexer(vIdxRef) && vIdxRef.Property.SetMethod is { } vIdxSetter:
            {
                var recvVal = lv.InstanceVal ?? VisitExpression(vIdxRef.Instance);
                var ordered = lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : EvaluateIndexerArgs(vIdxRef);
                ordered.Add(valueVal);
                EmitCrossIndexerCall(vIdxSetter, recvVal, ordered,
                    TryMarkReentrantCrossDispatch(vIdxRef, vIdxSetter)); // wave-12 r2 [V1]; void: self-emitting
                return;
            }
            // Wave-9 round-4 [X4]/[X5]: property/indexer COMPOUND (and inc-dec) write-back through an
            // INTERFACE-typed receiver → dispatch the setter through its interface bridge, reusing the
            // receiver/index leaves cached by CaptureLValue. The simple-set path already routed through
            // the bridge; only this write-back arm fell to the generic property case below and emitted a
            // nonexistent IUdonEventReceiver.__set_P / __set_Item (loud validator crash on legal C#).
            case IPropertyReferenceOperation ifaceWbRef
                when TryGetInterfaceAccessorLayout(ifaceWbRef, ifaceWbRef.Property.SetMethod, out var ifaceWbMl):
            {
                var recvVal = lv.InstanceVal ?? VisitExpression(ifaceWbRef.Instance);
                var ordered = ifaceWbRef.Property.IsIndexer
                    ? (lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : EvaluateIndexerArgs(ifaceWbRef))
                    : new List<CLeaf>();
                ordered.Add(valueVal);
                EmitInterfaceAccessorCall(ifaceWbRef.Property.SetMethod, ifaceWbMl, recvVal, ordered,
                    TryMarkReentrantCrossDispatch(ifaceWbRef, ifaceWbRef.Property.SetMethod)); // wave-12 r2 [V1]; void: self-emitting
                return;
            }
            // Cross-behaviour UdonSharpBehaviour property → SetProgramVariable / SendCustomEvent
            case IPropertyReferenceOperation propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation:
            {
                var instanceVal = VisitExpression(propRef.Instance);
                // Wave-12 [V2]: non-public autos write the declared backing symbol directly (their
                // accessors are never exported); see IsNonPublicAutoCrossProperty.
                var isAutoSet = propRef.Property.SetMethod == null
                    || IsNonPublicAutoCrossProperty(propRef.Property.SetMethod, propRef.Property);
                if (isAutoSet)
                {
                    var nameConst = Const(propRef.Property.Name, "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, nameConst, valueVal });
                }
                else
                {
                    RejectNonPublicCrossAccessor(propRef.Property.SetMethod, propRef.Property); // wave-12 [V2]
                    // Wave-12 r2 [V1]: reentrant setter — value copy-in inside the spill window.
                    bool wbReentrant = TryMarkReentrantCrossDispatch(propRef, propRef.Property.SetMethod);
                    var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                    var paramNameConst = Const(setParamIds[0], "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, valueVal });
                    var eventConst = Const(exportName, "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<CLeaf> { instanceVal, eventConst },
                        wbReentrant, wbReentrant ? 1 : 0);
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
                    EmitExternVoid(ExternResolver.BuildArraySetSignature("SystemObjectArray", "SystemObject"),
                        new List<CLeaf> { arrVal, Const(propIdx, "SystemInt32"), valueVal });
                    return;
                }
                if (aggPropRef.Property.SetMethod is { } aggSetter && _methodFunctions.ContainsKey(aggSetter))
                {
                    // Wave-11 round-11 [Z1]: reuse the receiver cached by CaptureLValue — the
                    // unconditional LoadInstanceRaw here re-ran side-effecting legs at store time.
                    EmitExprStmt(EmitCallToMethod(aggSetter,
                        new List<CLeaf> { lv.ArrayVal ?? LoadInstanceRaw(aggPropRef.Instance), valueVal }));
                    return;
                }
                break;
            }
            // User-defined indexer on a user STRUCT instance (`s[i] = v` / `s[i] += v`) → call the setter with
            // the struct receiver (object[]) as param0, the index args, then the value. Reuse the receiver/args
            // cached by CaptureLValue (compound assignment); without this it falls to a bogus __set_Item extern.
            case IPropertyReferenceOperation { Property: { IsIndexer: true, SetMethod: { } aggIdxSetter } } aggIdxRef
                when aggIdxRef.Instance?.Type is INamedTypeSymbol aggIdxType && EmitContext.IsAggregateType(aggIdxType)
                && _methodFunctions.ContainsKey(aggIdxSetter):
            {
                var setterArgs = new List<CLeaf> { lv.ArrayVal ?? LoadInstanceRaw(aggIdxRef.Instance) };
                // Wave-9 round-4: the no-cache fallback slots by parameter ordinal too (named args).
                setterArgs.AddRange(lv.IndexArgs ?? EvaluateIndexerArgs(aggIdxRef));
                setterArgs.Add(valueVal);
                EmitExprStmt(EmitCallToMethod(aggIdxSetter, setterArgs));
                return;
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
                    // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                    EmitExternVoid($"{containingType}.__set_{propRef.Property.MetadataName}__{indexParamStr}_{propValueType}__SystemVoid", indexArgs);
                }
                else
                {
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, propValueType), new List<CLeaf> { wbInstanceVal, valueVal });
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
                // Stage 2 §4.1: captured local/param → env cell write-back.
                if (TryEmitEnvStore(target, valueVal)) break;
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
