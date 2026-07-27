using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Base for the assignment-family handlers (SimpleAssignmentHandler, CompoundAssignmentHandler,
/// DeconstructionAssignmentHandler, NullableHandler). LValuePlan captures an l-value's
/// sub-expressions once, then writes back after computing the new value. It is shared by simple,
/// compound, coalesce, and deconstruction assignment.
/// GetAssignTargetFieldName is used by SimpleAssignmentHandler. EmitWriteBack's array-element and
/// cross-behaviour-field arms reuse CaptureLValue's cached receiver/index legs
/// ones) — the actual Set emission is LoweringServices.EmitArrayElementSet / EmitCrossBehaviourFieldSet,
/// shared with LoweringServices's single-write path (PrepareArrayElementSet / TryPrepareFieldSet) so the
/// two mechanisms can't drift on the emitted extern.
/// </summary>
internal sealed class LValueLowerer
{
    readonly LoweringServices _lowering;
    public LValueLowerer(LoweringServices lowering)
        => _lowering = lowering ?? throw new System.ArgumentNullException(nameof(lowering));

    // ── LValue Capture ──
    // Evaluates and caches sub-expressions of an l-value (array ref, index, instance)
    // to avoid re-evaluating side-effecting expressions during write-back.

    public LoweringServices.LValuePlan PrepareLValue(IOperation target)
    {
        var plan = CaptureLValue(target);
        var captured = plan;
        plan.SetWriter(value => EmitWriteBack(target, value, captured));
        return plan;
    }

    LoweringServices.LValuePlan CaptureLValue(IOperation target)
    {
        var getterSite = target is IPropertyReferenceOperation property
            ? _lowering.RequireBoundCallSite(
                property, CallableSiteKind.PropertyGet)
            : null;
        // CW1 lift: compound/inc-dec READ of a runtime-polymorphic accessor on a v1-class receiver —
        // dispatch the getter through the typeobj machinery and cache the STAGED legs so
        // EmitWriteBack's dispatch twin stores through the same cell (receiver/index side effects run
        // exactly once). The static arms below bind the receiver's STATIC accessor.
        if (target is IPropertyReferenceOperation vCapRef
            && _lowering.IsAccessorDispatchSite(
                getterSite, out var vCapRecvTy))
        {
            var (vCapRecv, vCapIdx) = _lowering.StageAccessorDispatchLegs(vCapRef);
            var current = _lowering.EmitAccessorDispatch(
                vCapRef,
                vCapRecvTy,
                getterSite.Callable.Site.Target,
                vCapRecv,
                vCapIdx,
                null,
                getterSite);
            return new LoweringServices.LValuePlan { Value = current, ArrayVal = vCapRecv, IndexArgs = vCapIdx };
        }

        switch (target)
        {
            // User-defined indexer on this/base BEHAVIOUR: cache the (possibly side-effecting) index args
            // ONCE, so a compound assignment (`this[Idx()] += x`) does not evaluate the index twice.
            // BoundProgram maps `this[i]` inside an inherited base body from the BASE
            // indexer — read through the chain-leaf override's getter; `base[i]` keeps the static binding.
            // WjR3 (B48 twin): an object[]-emulated containing type (user struct OR v1 class) must NOT
            // take this behaviour-only no-receiver arm — its accessor expects the receiver object[] as
            // param0 (CInternalCall arity skew); it falls through to the receiver-as-param0 arm below.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when !_lowering.IsObjectArrayEmulated(idxRef.Property.ContainingType)
                && getterSite.Target is { } idxDispatchGetter
                && _lowering.MethodFunctions.ContainsKey(idxDispatchGetter):
            {
                // Each VisitExpression(arg) is bound to a scratch leaf once under ANF — the index side effect
                // runs exactly once and the SAME leaf is reused by the getter here and the setter in
                // EmitWriteBack (via IndexArgs), so the cache itself is load-bearing but needs no extra copy.
                // Wave-9 round-4: slotted by parameter ordinal (named/reordered index args bind by name).
                var cachedArgs = _lowering.EvaluateIndexerArgs(idxRef);
                var currentVal = _lowering.EmitCallToMethod(idxDispatchGetter, new List<CLeaf>(cachedArgs));
                return new LoweringServices.LValuePlan { Value = currentVal, IndexArgs = cachedArgs };
            }
            // User-defined indexer on an object[]-emulated instance (`s[i] += x`, and this/base inside a
            // struct or v1-class body): cache the receiver and the (possibly side-effecting) index args
            // ONCE, then read via the getter with the receiver as param0. The same receiver/args are
            // reused by the setter in EmitWriteBack. Mirrors VisitIndexerGet. WjR3: gate on
            // IsObjectArrayEmulated, not IsAggregateValue — the CW6 polarity of the property arm below.
            case IPropertyReferenceOperation { Property: { IsIndexer: true } } sIdxRef
                when sIdxRef.Instance?.Type is INamedTypeSymbol sIdxType && _lowering.IsObjectArrayEmulated(sIdxType)
                && sIdxRef.Property.GetMethod is { } sIdxGetterRaw:
            {
                var recv = _lowering.LoadInstanceRaw(sIdxRef.Instance);
                var cachedArgs = _lowering.EvaluateIndexerArgs(sIdxRef); // wave-9 r4: named index args bind by ordinal
                var getterArgs = new List<CLeaf> { recv };
                getterArgs.AddRange(cachedArgs);
                var currentVal = _lowering.EmitCallToMethod(
                    _lowering.RequireBoundCallable(
                        sIdxRef, CallableSiteKind.PropertyGet),
                    getterArgs);
                return new LoweringServices.LValuePlan { Value = currentVal, ArrayVal = recv, IndexArgs = cachedArgs };
            }
            // Wave-9 round-2 [W6]: user indexer COMPOUND assignment through a VARIABLE receiver
            // (`s[i] += x` where s is an own-typed copy / base-typed ref / another behaviour): read via
            // the cross-program getter; the receiver and the ordinal-ordered index args are cached so
            // EmitWriteBack's setter dispatch reuses them (index side effects run exactly once).
            case IPropertyReferenceOperation vIdxRef
                when LoweringServices.IsVariableReceiverBehaviourIndexer(vIdxRef) && vIdxRef.Property.GetMethod is { } vIdxGetter:
            {
                var recvVal = _lowering.VisitExpression(vIdxRef.Instance);
                var cachedArgs = _lowering.EvaluateIndexerArgs(vIdxRef);
                var currentVal = _lowering.EmitCrossIndexerCall(vIdxGetter, recvVal, cachedArgs,
                    _lowering.TryMarkReentrantCrossDispatch(vIdxRef, vIdxGetter)); // wave-12 r2 [V1]
                return new LoweringServices.LValuePlan { Value = currentVal, InstanceVal = recvVal, IndexArgs = cachedArgs };
            }
            // Wave-9 round-4 [X4]: user indexer COMPOUND assignment (and inc-dec) through an
            // INTERFACE-typed receiver: read via the interface getter bridge; the receiver and the
            // ordinal-ordered index args are cached so EmitWriteBack's setter bridge dispatch reuses
            // them (index side effects run exactly once). Pre-fix both legs fell to extern resolution
            // and emitted nonexistent IUdonEventReceiver.__get_Item/__set_Item externs.
            case IPropertyReferenceOperation iIdxRef
                when _lowering.TryGetInterfaceAccessorLayout(iIdxRef, iIdxRef.Property.GetMethod, out var iIdxGetMl):
            {
                var recvVal = _lowering.VisitExpression(iIdxRef.Instance);
                var cachedArgs = iIdxRef.Property.IsIndexer
                    ? _lowering.EvaluateIndexerArgs(iIdxRef)
                    : new List<CLeaf>();
                var currentVal = _lowering.EmitInterfaceAccessorCall(iIdxRef.Property.GetMethod, iIdxGetMl, recvVal, cachedArgs,
                    _lowering.TryMarkReentrantCrossDispatch(iIdxRef, iIdxRef.Property.GetMethod)); // wave-12 r2 [V1]
                return new LoweringServices.LValuePlan { Value = currentVal, InstanceVal = recvVal, IndexArgs = cachedArgs };
            }
            // Wave-11 round-11 [Z1]: NON-indexer property on an aggregate (struct/tuple) receiver —
            // compound assignment and inc-dec (`ss[Ix()].P += Mut()`, `arr[i].X++`). Evaluate the
            // receiver legs ONCE and cache the raw backing object[] so EmitWriteBack's
            // aggregate-property arm stores into the SAME cell. Pre-fix this fell to the default arm
            // (ArrayVal stayed null) and the write-back re-ran side-effecting receiver legs AFTER the
            // RHS (wrong element + legs twice; VM-proven ref trace=12/result=803 vs 121/283). The read
            // mirrors VisitPropertyReference verbatim: auto-prop → layout slot; computed → user getter
            // with the receiver as param0 (struct-typed results deep-clone, value semantics).
            // CW6: gate on IsObjectArrayEmulated, not IsAggregateValue — a v1 CLASS receiver routes
            // through the same layout-slot/getter read as PreparePropertySet's set twin (the deep-clone
            // sub-conditions below gate on the PROPERTY type, so class reference semantics are kept).
            case IPropertyReferenceOperation { Property: { IsIndexer: false } } aggCapPropRef
                when aggCapPropRef.Instance?.Type is INamedTypeSymbol aggCapPropType
                && _lowering.IsObjectArrayEmulated(aggCapPropType):
            {
                if (_lowering.State.Aggregates.GetLayout(aggCapPropType).TryGetIndex(aggCapPropRef.Property, out var capSlotIdx))
                {
                    var recv = _lowering.LoadInstanceRaw(aggCapPropRef.Instance);
                    CLeaf slotVal = AggregateAbi.ReadSlot(_lowering.Builder, recv, capSlotIdx, StorageTypes.Object);
                    if (aggCapPropRef.Property.Type is INamedTypeSymbol capSlotAgg && _lowering.IsAggregateValue(capSlotAgg))
                        slotVal = AggregateAbi.DeepClone(_lowering.Builder, slotVal, capSlotAgg, _lowering.State.Aggregates.GetLayout);
                    return new LoweringServices.LValuePlan { Value = slotVal, ArrayVal = recv, IndexVal = _lowering.Const(capSlotIdx, StorageTypes.Int32) };
                }
                if (aggCapPropRef.Property.GetMethod is { } capGetterRaw)
                {
                    var recv = _lowering.LoadInstanceRaw(aggCapPropRef.Instance);
                    CLeaf getVal = _lowering.EmitCallToMethod(
                        _lowering.RequireBoundCallable(
                            aggCapPropRef,
                            CallableSiteKind.PropertyGet),
                        new List<CLeaf> { recv });
                    if (aggCapPropRef.Property.Type is INamedTypeSymbol capGetAgg && _lowering.IsAggregateValue(capGetAgg))
                        getVal = AggregateAbi.DeepClone(_lowering.Builder, getVal, capGetAgg, _lowering.State.Aggregates.GetLayout);
                    return new LoweringServices.LValuePlan { Value = getVal, ArrayVal = recv };
                }
                goto default;
            }
            case IPropertyReferenceOperation
                {
                    Property: { IsIndexer: false },
                    Instance: not null and not IInstanceReferenceOperation
                } crossPropRef
                when ExternResolver.IsUdonSharpBehaviour(crossPropRef.Property.ContainingType)
                && crossPropRef.Property.GetMethod != null:
            {
                _lowering.RejectProgramLocalCrossBehaviourPropertyRead(crossPropRef.Property);
                var crossRecv = _lowering.VisitExpression(crossPropRef.Instance);
                var crossCurrent = _lowering.EmitCrossBehaviourPropertyGet(
                    crossPropRef, crossRecv,
                    _lowering.GetStorageType(crossPropRef.Property.Type));
                return new LoweringServices.LValuePlan { Value = crossCurrent, InstanceVal = crossRecv };
            }
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && _lowering.ResolveType(aggFieldRef.Instance.Type) is INamedTypeSymbol aggCapType
                && _lowering.IsObjectArrayEmulated(aggCapType):
            {
                var layout = _lowering.State.Aggregates.GetLayout(aggCapType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    var arrVal = _lowering.LoadInstanceRaw(aggFieldRef.Instance);
                    var idxVal = _lowering.Const(elemIdx, StorageTypes.Int32);
                    var currentVal = AggregateAbi.ReadSlot(_lowering.Builder, arrVal, elemIdx, StorageTypes.Object);
                    return new LoweringServices.LValuePlan { Value = currentVal, ArrayVal = arrVal, IndexVal = idxVal };
                }
                goto default;
            }
            case IArrayElementReferenceOperation arrayElem:
            {
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = _lowering.GetArrayType(arrSymbol);
                var elemAccessorType = _lowering.GetArrayElemType(arrSymbol);

                // Evaluate the array ref and index ONCE; the resulting scratch leaves are reused by
                // EmitWriteBack (compound RMW) via ArrayVal/IndexVal so a side-effecting index (`arr[Next()]
                // += v`) runs once with the read Get and the write Set targeting the SAME element. Under ANF
                // VisitExpression already binds each to a single-assignment scratch, so the capture needs no
                // extra copy slot — storing the leaves directly preserves the read↔writeback sharing.
                var arrayVal = _lowering.VisitExpression(arrayElem.ArrayReference);
                var indexVal = _lowering.ResolveArrayIndex(arrayVal, arrayType, arrayElem.Indices[0]);

                // Read current value: arr[idx]
                var valResult = _lowering.ExternCall(
                    UdonAbi.ArrayGet(arrayType, elemAccessorType),
                    new List<CLeaf> { arrayVal, indexVal },
                    _lowering.GetStorageType(arrayElem.Type));
                return new LoweringServices.LValuePlan { Value = valResult, ArrayVal = arrayVal, IndexVal = indexVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceVal = _lowering.VisitExpression(fieldRef.Instance);
                // Read via GetProgramVariable
                var valResult = _lowering.LoadProgramVariable(
                    instanceVal, fieldRef.Field.Name, _lowering.GetStorageType(fieldRef.Field.Type));
                return new LoweringServices.LValuePlan { Value = valResult, InstanceVal = instanceVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                var instanceVal = _lowering.VisitExpression(fieldRef2.Instance);
                var containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(fieldRef2.Field.ContainingType, fieldRef2.Instance?.Type, fieldRef2.Field.Name));
                var valueType = _lowering.GetStorageTypeName(fieldRef2.Field.Type);
                var sig = _lowering.RequireBoundAbi(
                    fieldRef2, BoundAbiRole.FieldGet);
                var valResult = _lowering.ExternCall(sig, new List<CLeaf> { instanceVal }, new StorageType(valueType));
                return new LoweringServices.LValuePlan { Value = valResult, InstanceVal = instanceVal };
            }
            default:
                // Simple l-value (local, field on this): just evaluate normally
                return new LoweringServices.LValuePlan { Value = _lowering.VisitExpression(target) };
        }
    }

    // ── EmitWriteBack ──
    // Write back a computed value to non-trivial l-value targets (array elements, properties).
    // For local/field variables, also writes back via EmitStoreField.

    void EmitWriteBack(IOperation target, CLeaf valueVal, LoweringServices.LValuePlan lv)
    {
        var setterSite = target is IPropertyReferenceOperation property
            ? _lowering.RequireBoundCallSite(
                property, CallableSiteKind.PropertySet)
            : null;
        // CW1 lift: compound/inc-dec WRITE-BACK of a runtime-polymorphic accessor on a v1-class
        // receiver — dispatch the setter, reusing the legs CaptureLValue's dispatch twin staged
        // (fresh legs only on the capture-less paths, mirroring the static arms' `??` fallbacks).
        if (target is IPropertyReferenceOperation vWbRef
            && _lowering.IsAccessorDispatchSite(
                setterSite, out var vWbRecvTy))
        {
            var vWbRecv = lv.ArrayVal;
            var vWbIdx = lv.IndexArgs;
            if (vWbRecv == null)
                (vWbRecv, vWbIdx) = _lowering.StageAccessorDispatchLegs(vWbRef);
            var vWbSlot = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(vWbRef.Property.Type));
            _lowering.EmitAssign(vWbSlot, valueVal);
            _lowering.EmitAccessorDispatch(
                vWbRef,
                vWbRecvTy,
                setterSite.Callable.Site.Target,
                vWbRecv,
                vWbIdx ?? new List<CLeaf>(),
                _lowering.SlotRef(vWbSlot),
                setterSite);
            return;
        }

        switch (target)
        {
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && _lowering.ResolveType(aggFieldRef.Instance.Type) is INamedTypeSymbol aggWbType
                && _lowering.IsObjectArrayEmulated(aggWbType):
            {
                var layout = _lowering.State.Aggregates.GetLayout(aggWbType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    var arrVal = lv.ArrayVal ?? _lowering.VisitExpression(aggFieldRef.Instance);
                    AggregateAbi.WriteSlot(_lowering.Builder, arrVal, elemIdx, valueVal);
                    return;
                }
                throw new System.InvalidOperationException(
                    $"Object[] field write-back lost layout slot for "
                    + $"'{aggFieldRef.Field.ToDisplayString()}' in '{aggWbType.ToDisplayString()}'.");
            }
            case IArrayElementReferenceOperation arrayElem:
            {
                // Use captured array/index if available (avoid double evaluation)
                var arrayVal = lv.ArrayVal ?? _lowering.VisitExpression(arrayElem.ArrayReference);
                var indexVal = lv.IndexVal ?? _lowering.VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                _lowering.EmitArrayElementSet(arrSymbol, arrayVal, indexVal, valueVal);
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                // Cross-behaviour field write-back → SetProgramVariable
                var instanceVal = lv.InstanceVal ?? _lowering.VisitExpression(fieldRef.Instance);
                _lowering.EmitCrossBehaviourFieldSet(fieldRef.Field, instanceVal, valueVal);
                break;
            }
            // Auto-property on this → backing field already handled by write-back to field (user-defined
            // classes only). BoundProgram maps an inherited base body's write from the
            // BASE accessor — all three this-path cases below dispatch the chain-leaf override instead;
            // `base.P` keeps the static binding (its base-instance copy accessors).
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } propRef
                when setterSite.Target.AssociatedSymbol
                    is IPropertySymbol autoDispatchProp
                && autoDispatchProp.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                && ExternResolver.IsUdonSharpBehaviour(autoDispatchProp.ContainingType)
                && autoDispatchProp.ContainingType.Name != "UdonSharpBehaviour":
                return;
            // User-defined indexer on this/base BEHAVIOUR → call setter with the index args followed by
            // the value (no receiver param). Reuse the index args cached by CaptureLValue (compound
            // assignment) to avoid re-evaluating them. WjR3 (B48 twin): an object[]-emulated containing
            // type must NOT take this no-receiver arm — it falls through to the receiver-as-param0
            // object[]-emulated indexer arm below.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when !_lowering.IsObjectArrayEmulated(idxRef.Property.ContainingType)
                && setterSite.Target is { } idxDispatchSetter
                && _lowering.MethodFunctions.TryGetValue(idxDispatchSetter, out _):
            {
                // Wave-9 round-4: the uncached path slots by parameter ordinal too (named args).
                var setterArgs = lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : _lowering.EvaluateIndexerArgs(idxRef);
                setterArgs.Add(valueVal);
                _lowering.EmitExprStmt(_lowering.EmitCallToMethod(idxDispatchSetter, setterArgs));
                return;
            }
            // User-defined property on this/base BEHAVIOUR → call setter (value only, no receiver
            // param). WjR3: an object[]-emulated containing type must NOT take this arm — its setter
            // expects the receiver object[] as param0 (a v1-class/struct `this.P += 1` skewed the
            // CInternalCall arity); it falls through to the object[]-emulated property arm below. An
            // INDEXER never belongs here either (the value-only call drops the index legs) — it rides
            // the indexer arms above/below.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: false } } propRef
                when !_lowering.IsObjectArrayEmulated(propRef.Property.ContainingType)
                && setterSite.Target is { } dispatchSetter
                && _lowering.MethodFunctions.TryGetValue(dispatchSetter, out _):
                _lowering.EmitExprStmt(_lowering.EmitCallToMethod(dispatchSetter, new List<CLeaf> { valueVal }));
                return;
            // Wave-9 round-2 [W6]: user indexer write-back through a VARIABLE receiver → cross-program
            // setter dispatch, reusing the receiver/index leaves cached by CaptureLValue. MUST sit
            // before the generic cross-behaviour property arm below, which would SetProgramVariable
            // only the setter's FIRST param (an index) and drop the value.
            case IPropertyReferenceOperation vIdxRef
                when LoweringServices.IsVariableReceiverBehaviourIndexer(vIdxRef) && vIdxRef.Property.SetMethod is { } vIdxSetter:
            {
                var recvVal = lv.InstanceVal ?? _lowering.VisitExpression(vIdxRef.Instance);
                var ordered = lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : _lowering.EvaluateIndexerArgs(vIdxRef);
                ordered.Add(valueVal);
                _lowering.EmitCrossIndexerCall(vIdxSetter, recvVal, ordered,
                    _lowering.TryMarkReentrantCrossDispatch(vIdxRef, vIdxSetter)); // wave-12 r2 [V1]; void: self-emitting
                return;
            }
            // Wave-9 round-4 [X4]/[X5]: property/indexer COMPOUND (and inc-dec) write-back through an
            // INTERFACE-typed receiver → dispatch the setter through its interface bridge, reusing the
            // receiver/index leaves cached by CaptureLValue. The simple-set path already routed through
            // the bridge; only this write-back arm fell to the generic property case below and emitted a
            // nonexistent IUdonEventReceiver.__set_P / __set_Item (loud validator crash on legal C#).
            case IPropertyReferenceOperation ifaceWbRef
                when _lowering.TryGetInterfaceAccessorLayout(ifaceWbRef, ifaceWbRef.Property.SetMethod, out var ifaceWbMl):
            {
                var recvVal = lv.InstanceVal ?? _lowering.VisitExpression(ifaceWbRef.Instance);
                var ordered = ifaceWbRef.Property.IsIndexer
                    ? (lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : _lowering.EvaluateIndexerArgs(ifaceWbRef))
                    : new List<CLeaf>();
                ordered.Add(valueVal);
                _lowering.EmitInterfaceAccessorCall(ifaceWbRef.Property.SetMethod, ifaceWbMl, recvVal, ordered,
                    _lowering.TryMarkReentrantCrossDispatch(ifaceWbRef, ifaceWbRef.Property.SetMethod)); // wave-12 r2 [V1]; void: self-emitting
                return;
            }
            // Cross-behaviour UdonSharpBehaviour property → SetProgramVariable / SendCustomEvent
            case IPropertyReferenceOperation propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation:
            {
                _lowering.RejectProgramLocalCrossBehaviourPropertyWrite(propRef.Property); // CW22 (compound/`??=` leg)
                var instanceVal = lv.InstanceVal ?? _lowering.VisitExpression(propRef.Instance);
                // Wave-12 [V2]: non-public autos write the declared backing symbol directly (their
                // accessors are never exported); see IsNonPublicAutoCrossProperty.
                var isAutoSet = propRef.Property.SetMethod == null
                    || LoweringServices.IsNonPublicAutoCrossProperty(propRef.Property.SetMethod, propRef.Property);
                if (isAutoSet)
                {
                    _lowering.StoreProgramVariable(instanceVal, propRef.Property.Name,
                        _lowering.GetStorageType(propRef.Property.Type), valueVal);
                }
                else
                {
                    // Wave-12 r2 [V1]: reentrant setter — value copy-in inside the spill window.
                    bool wbReentrant = _lowering.TryMarkReentrantCrossDispatch(propRef, propRef.Property.SetMethod);
                    var (exportName, setParamIds, _) = _lowering.GetCalleeLayout(propRef.Property.SetMethod);
                    _lowering.CrossCall(instanceVal, exportName,
                        _lowering.CrossCallParameters(propRef.Property.SetMethod, setParamIds, new[] { valueVal }),
                        System.Array.Empty<ReturnSlot>(), StorageTypes.Void, wbReentrant);
                }
                return;
            }
            // Property on an object[]-emulated (struct/tuple/v1-class) instance — e.g. compound `p.X += 1`
            // / `c.Computed += 1`, which routes through CaptureLValue + EmitWriteBack. Auto-property →
            // write the backing-field slot by layout index; computed (non-auto) → call the user setter
            // with the receiver as param0. CW6: gated on IsObjectArrayEmulated so a class receiver takes
            // the setter-call path PreparePropertySet already implements for the simple set — the old
            // IsAggregateValue gate dropped classes through to the generic extern arm below (bogus
            // SystemObjectArray.__set_P__ extern, loud validator crash on legal C#).
            case IPropertyReferenceOperation { Property: { IsIndexer: false } } aggPropRef
                when aggPropRef.Instance?.Type is INamedTypeSymbol aggPropType && _lowering.IsObjectArrayEmulated(aggPropType):
            {
                if (_lowering.State.Aggregates.GetLayout(aggPropType).TryGetIndex(aggPropRef.Property, out var propIdx))
                {
                    var arrVal = lv.ArrayVal ?? _lowering.LoadInstanceRaw(aggPropRef.Instance);
                    AggregateAbi.WriteSlot(_lowering.Builder, arrVal, propIdx, valueVal);
                    return;
                }
                if (aggPropRef.Property.SetMethod is { } aggSetter && _lowering.MethodFunctions.ContainsKey(aggSetter))
                {
                    // Wave-11 round-11 [Z1]: reuse the receiver cached by CaptureLValue — the
                    // unconditional LoadInstanceRaw here re-ran side-effecting legs at store time.
                    _lowering.EmitExprStmt(_lowering.EmitCallToMethod(aggSetter,
                        new List<CLeaf> { lv.ArrayVal ?? _lowering.LoadInstanceRaw(aggPropRef.Instance), valueVal }));
                    return;
                }
                throw new System.InvalidOperationException(
                    $"Object[] property write-back resolved neither a layout slot nor a registered setter "
                    + $"for '{aggPropRef.Property.ToDisplayString()}' in '{aggPropType.ToDisplayString()}'.");
            }
            // User-defined indexer on an object[]-emulated instance (`s[i] = v` / `s[i] += v`) → call the
            // setter with the receiver (object[]) as param0, the index args, then the value. Reuse the
            // receiver/args cached by CaptureLValue (compound assignment); without this it falls to a bogus
            // __set_Item extern. CW6: IsObjectArrayEmulated so class receivers ride the same arm.
            case IPropertyReferenceOperation { Property: { IsIndexer: true, SetMethod: { } aggIdxSetter } } aggIdxRef
                when aggIdxRef.Instance?.Type is INamedTypeSymbol aggIdxType && _lowering.IsObjectArrayEmulated(aggIdxType)
                && _lowering.MethodFunctions.ContainsKey(aggIdxSetter):
            {
                var setterArgs = new List<CLeaf> { lv.ArrayVal ?? _lowering.LoadInstanceRaw(aggIdxRef.Instance) };
                // Wave-9 round-4: the uncached path slots by parameter ordinal too (named args).
                setterArgs.AddRange(lv.IndexArgs ?? _lowering.EvaluateIndexerArgs(aggIdxRef));
                setterArgs.Add(valueVal);
                _lowering.EmitExprStmt(_lowering.EmitCallToMethod(aggIdxSetter, setterArgs));
                return;
            }
            // Resolve containing type and instance
            case IPropertyReferenceOperation propRef:
            {
                if (propRef.Instance == null
                    && propRef.Property.SetMethod?.DeclaringSyntaxReferences.Length > 0
                    && !USugarCompilerHelper.IsFrameworkNamespace(propRef.Property.ContainingNamespace))
                {
                    if (!UasmEmitter.IsComputedProperty(propRef.Property))
                        throw ClassAbiPolicy.UnsupportedStaticStorage(
                            propRef.Property);
                    var setter = _lowering.RequireBoundCallable(
                        propRef, CallableSiteKind.PropertySet);
                    _lowering.EmitExprStmt(_lowering.EmitCallToMethod(setter, new List<CLeaf> { valueVal }));
                    return;
                }
                // CW6 armor: a user-struct/class property whose write-back reaches this generic extern
                // arm was not routed by the object[]-emulated arms above — fail with the collector-drift
                // diagnosis instead of minting a bogus SystemObjectArray.__set_<Name>__ extern.
                _lowering.GuardUserStructMemberReachedExtern(propRef.Property.ContainingType, propRef.Property.Name);
                // B55 setter door: the property-SET write-back resolves its extern owner through the same
                // inherited-member choke point as the getter (subsumes the former Behaviour fixup). Static
                // (Instance null) → declaring type; inherited instance member → receiver's static type.
                var containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(propRef.Property.ContainingType, propRef.Instance?.Type, propRef.Property.Name));

                CLeaf wbInstanceVal;
                if (propRef.Instance is IInstanceReferenceOperation)
                    wbInstanceVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(new StorageType(containingType)), new StorageType(containingType));
                else if (propRef.Instance != null)
                    wbInstanceVal = _lowering.VisitExpression(propRef.Instance);
                else
                {
                    // Static property: no instance
                    var valueType = _lowering.GetStorageTypeName(propRef.Property.Type);
                    _lowering.EmitExternVoid(
                        _lowering.RequireBoundAbi(
                            propRef, BoundAbiRole.PropertySet),
                        new List<CLeaf> { valueVal });
                    return;
                }

                var propValueType = _lowering.GetStorageTypeName(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    var indexArgs = new List<CLeaf> { wbInstanceVal };
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        indexArgs.Add(_lowering.VisitExpression(arg.Value));
                        indexTypes.Add(_lowering.GetStorageTypeName(arg.Value.Type));
                    }
                    indexArgs.Add(valueVal);
                    // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                    _lowering.EmitExternVoid(
                        _lowering.RequireBoundAbi(
                            propRef, BoundAbiRole.IndexerSet),
                        indexArgs);
                }
                else
                {
                    _lowering.EmitExternVoid(
                        _lowering.RequireBoundAbi(
                            propRef, BoundAbiRole.PropertySet),
                        new List<CLeaf> { wbInstanceVal, valueVal });
                }
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                // Struct field setter (e.g., vec.y += 3f where vec is an array element)
                var instanceVal = lv.InstanceVal ?? _lowering.VisitExpression(fieldRef2.Instance);
                var containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(fieldRef2.Field.ContainingType, fieldRef2.Instance?.Type, fieldRef2.Field.Name));
                var valueType = _lowering.GetStorageTypeName(fieldRef2.Field.Type);
                var sig = _lowering.RequireBoundAbi(
                    fieldRef2, BoundAbiRole.FieldSetValue);
                _lowering.EmitExternVoid(sig, new List<CLeaf> { instanceVal, valueVal });
                break;
            }
            default:
            {
                // Stage 2 §4.1: captured local/param → env cell write-back.
                if (_lowering.TryEmitEnvStore(target, valueVal)) break;
                // Simple l-value (local, field on this): write back via EmitStoreField
                var fieldName = GetAssignTargetFieldName(target);
                _lowering.EmitStoreField(fieldName, valueVal);
                break;
            }
        }
    }

    /// <summary>
    /// Resolve the field name (lvalue) for a simple assignment target.
    /// Used by direct local/this-field store paths in SimpleAssignmentHandler / CompoundAssignmentHandler.
    /// </summary>
    public string GetAssignTargetFieldName(IOperation target)
    {
        switch (target)
        {
            case ILocalReferenceOperation localRef:
                if (_lowering.LocalBindings.TryGetValue(localRef.Local, out var lb))
                    return lb.Id;
                throw new System.InvalidOperationException(
                    $"Cannot resolve local variable '{localRef.Local.Name}' for assignment.");
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                return _lowering.State.SourceStorageName(fieldRef.Field);
            case IFieldReferenceOperation { Instance: null } staticField
                when staticField.Field.DeclaringSyntaxReferences.Length > 0
                     && !USugarCompilerHelper.IsFrameworkNamespace(
                         staticField.Field.ContainingNamespace):
                throw ClassAbiPolicy.UnsupportedStaticStorage(
                    staticField.Field);
            case IParameterReferenceOperation paramRef:
                return _lowering.GetParamVarId(paramRef.Parameter);
            default:
                throw new System.NotSupportedException(
                    $"Unsupported simple assignment target: {target.GetType().Name}");
        }
    }
}
