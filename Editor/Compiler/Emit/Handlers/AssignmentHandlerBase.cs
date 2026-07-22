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

    protected LValuePlan PrepareLValue(IOperation target)
    {
        var plan = CaptureLValue(target);
        var captured = plan;
        plan.SetWriter(value => EmitWriteBack(target, value, captured));
        return plan;
    }

    LValuePlan CaptureLValue(IOperation target)
    {
        // CW1 lift: compound/inc-dec READ of a runtime-polymorphic accessor on a v1-class receiver —
        // dispatch the getter through the typeobj machinery and cache the STAGED legs so
        // EmitWriteBack's dispatch twin stores through the same cell (receiver/index side effects run
        // exactly once). The static arms below bind the receiver's STATIC accessor.
        if (target is IPropertyReferenceOperation vCapRef
            && VirtualDispatch.FindAccessor(vCapRef.Property, getter: true) is { } vCapGetter
            && IsAccessorDispatchSite(vCapRef, vCapGetter, out var vCapRecvTy))
        {
            var (vCapRecv, vCapIdx) = StageAccessorDispatchLegs(vCapRef);
            var current = EmitAccessorDispatch(vCapRef.Property, vCapRecvTy, vCapGetter, vCapRecv, vCapIdx, null);
            return new LValuePlan { Value = current, ArrayVal = vCapRecv, IndexArgs = vCapIdx };
        }

        switch (target)
        {
            // User-defined indexer on this/base BEHAVIOUR: cache the (possibly side-effecting) index args
            // ONCE, so a compound assignment (`this[Idx()] += x`) does not evaluate the index twice.
            // ResolveDispatchProperty (round 7): `this[i]` inside an inherited base body binds the BASE
            // indexer — read through the chain-leaf override's getter; `base[i]` keeps the static binding.
            // WjR3 (B48 twin): an object[]-emulated containing type (user struct OR v1 class) must NOT
            // take this behaviour-only no-receiver arm — its accessor expects the receiver object[] as
            // param0 (CInternalCall arity skew); it falls through to the receiver-as-param0 arm below.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when !TypeClassifier.IsObjectArrayEmulated(idxRef.Property.ContainingType)
                && ResolveDispatchProperty(idxRef).GetMethod is { } idxDispatchGetter
                && _methodFunctions.ContainsKey(idxDispatchGetter):
            {
                // Each VisitExpression(arg) is bound to a scratch leaf once under ANF — the index side effect
                // runs exactly once and the SAME leaf is reused by the getter here and the setter in
                // EmitWriteBack (via IndexArgs), so the cache itself is load-bearing but needs no extra copy.
                // Wave-9 round-4: slotted by parameter ordinal (named/reordered index args bind by name).
                var cachedArgs = EvaluateIndexerArgs(idxRef);
                var currentVal = EmitCallToMethod(idxDispatchGetter, new List<CLeaf>(cachedArgs));
                return new LValuePlan { Value = currentVal, IndexArgs = cachedArgs };
            }
            // User-defined indexer on an object[]-emulated instance (`s[i] += x`, and this/base inside a
            // struct or v1-class body): cache the receiver and the (possibly side-effecting) index args
            // ONCE, then read via the getter with the receiver as param0. The same receiver/args are
            // reused by the setter in EmitWriteBack. Mirrors VisitIndexerGet. WjR3: gate on
            // IsObjectArrayEmulated, not IsAggregateValue — the CW6 polarity of the property arm below.
            case IPropertyReferenceOperation { Property: { IsIndexer: true } } sIdxRef
                when sIdxRef.Instance?.Type is INamedTypeSymbol sIdxType && TypeClassifier.IsObjectArrayEmulated(sIdxType)
                && sIdxRef.Property.GetMethod is { } sIdxGetterRaw:
            {
                var recv = LoadInstanceRaw(sIdxRef.Instance);
                var cachedArgs = EvaluateIndexerArgs(sIdxRef); // wave-9 r4: named index args bind by ordinal
                var getterArgs = new List<CLeaf> { recv };
                getterArgs.AddRange(cachedArgs);
                var currentVal = EmitCallToMethod(ResolveStructMember(sIdxGetterRaw), getterArgs);
                return new LValuePlan { Value = currentVal, ArrayVal = recv, IndexArgs = cachedArgs };
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
                return new LValuePlan { Value = currentVal, InstanceVal = recvVal, IndexArgs = cachedArgs };
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
                return new LValuePlan { Value = currentVal, InstanceVal = recvVal, IndexArgs = cachedArgs };
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
                && TypeClassifier.IsObjectArrayEmulated(aggCapPropType):
            {
                if (_ctx.Aggregates.GetLayout(aggCapPropType).TryGetIndex(aggCapPropRef.Property.Name, out var capSlotIdx))
                {
                    var recv = LoadInstanceRaw(aggCapPropRef.Instance);
                    CLeaf slotVal = AggregateAbi.ReadSlot(_builder, recv, capSlotIdx, "SystemObject");
                    if (aggCapPropRef.Property.Type is INamedTypeSymbol capSlotAgg && TypeClassifier.IsAggregateValue(capSlotAgg))
                        slotVal = AggregateAbi.DeepClone(_builder, slotVal, capSlotAgg, _ctx.Aggregates.GetLayout);
                    return new LValuePlan { Value = slotVal, ArrayVal = recv, IndexVal = Const(capSlotIdx, "SystemInt32") };
                }
                if (aggCapPropRef.Property.GetMethod is { } capGetterRaw)
                {
                    var recv = LoadInstanceRaw(aggCapPropRef.Instance);
                    CLeaf getVal = EmitCallToMethod(ResolveStructMember(capGetterRaw), new List<CLeaf> { recv });
                    if (aggCapPropRef.Property.Type is INamedTypeSymbol capGetAgg && TypeClassifier.IsAggregateValue(capGetAgg))
                        getVal = AggregateAbi.DeepClone(_builder, getVal, capGetAgg, _ctx.Aggregates.GetLayout);
                    return new LValuePlan { Value = getVal, ArrayVal = recv };
                }
                goto default;
            }
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && ResolveType(aggFieldRef.Instance.Type) is INamedTypeSymbol aggCapType
                && TypeClassifier.IsObjectArrayEmulated(aggCapType):
            {
                var layout = _ctx.Aggregates.GetLayout(aggCapType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    RejectStaticReadonlyWriteThrough(aggFieldRef.Instance); // §3.3, R5 (compound/inc-dec write-back)
                    var arrVal = LoadInstanceRaw(aggFieldRef.Instance);
                    var idxVal = Const(elemIdx, "SystemInt32");
                    var currentVal = AggregateAbi.ReadSlot(_builder, arrVal, elemIdx, "SystemObject");
                    return new LValuePlan { Value = currentVal, ArrayVal = arrVal, IndexVal = idxVal };
                }
                goto default;
            }
            case IArrayElementReferenceOperation ndimCapElem when ndimCapElem.Indices.Length > 1:
            {
                RejectStaticReadonlyWriteThrough(ndimCapElem.ArrayReference); // §3.3, R5 (compound/inc-dec write-back)
                var ndimType = (IArrayTypeSymbol)ndimCapElem.ArrayReference.Type;
                var elemUdonType = GetStorageTypeName(ndimType.ElementType);
                var plan = PrepareNdimAccess(ndimCapElem.ArrayReference, ndimCapElem.Indices, ndimType);
                var ndimCurrentVal = EmitNdimReadFromPlan(ndimCapElem, plan, elemUdonType);
                return new LValuePlan { Value = ndimCurrentVal, NdimPlan = plan };
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
                    GetStorageTypeName(arrayElem.Type));
                return new LValuePlan { Value = valResult, ArrayVal = arrayVal, IndexVal = indexVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceVal = VisitExpression(fieldRef.Instance);
                // Read via GetProgramVariable
                var nameConst = Const(fieldRef.Field.Name, "SystemString");
                var valResult = ExternCall(
                    ExternResolver.EventReceiverGetProgramVariable,
                    new List<CLeaf> { instanceVal, nameConst },
                    "SystemObject");
                return new LValuePlan { Value = valResult, InstanceVal = instanceVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                var instanceVal = VisitExpression(fieldRef2.Instance);
                var containingType = GetStorageTypeName(ResolveExternOwnerType(fieldRef2.Field.ContainingType, fieldRef2.Instance?.Type, fieldRef2.Field.Name));
                var valueType = GetStorageTypeName(fieldRef2.Field.Type);
                var sig = ExternResolver.BuildPropertyGetSignature(containingType, fieldRef2.Field.Name, valueType);
                var valResult = ExternCall(sig, new List<CLeaf> { instanceVal }, valueType);
                return new LValuePlan { Value = valResult, InstanceVal = instanceVal };
            }
            default:
                // Simple l-value (local, field on this): just evaluate normally
                return new LValuePlan { Value = VisitExpression(target) };
        }
    }

    // ── EmitWriteBack ──
    // Write back a computed value to non-trivial l-value targets (array elements, properties).
    // For local/field variables, also writes back via EmitStoreField.

    void EmitWriteBack(IOperation target, CLeaf valueVal, LValuePlan lv)
    {
        // CW1 lift: compound/inc-dec WRITE-BACK of a runtime-polymorphic accessor on a v1-class
        // receiver — dispatch the setter, reusing the legs CaptureLValue's dispatch twin staged
        // (fresh legs only on the capture-less paths, mirroring the static arms' `??` fallbacks).
        if (target is IPropertyReferenceOperation vWbRef
            && VirtualDispatch.FindAccessor(vWbRef.Property, getter: false) is { } vWbSetter
            && IsAccessorDispatchSite(vWbRef, vWbSetter, out var vWbRecvTy))
        {
            var vWbRecv = lv.ArrayVal;
            var vWbIdx = lv.IndexArgs;
            if (vWbRecv == null)
                (vWbRecv, vWbIdx) = StageAccessorDispatchLegs(vWbRef);
            var vWbSlot = _ctx.Builder.AllocScratch(GetStorageTypeName(vWbRef.Property.Type));
            EmitAssign(vWbSlot, valueVal);
            EmitAccessorDispatch(vWbRef.Property, vWbRecvTy, vWbSetter, vWbRecv,
                vWbIdx ?? new List<CLeaf>(), SlotRef(vWbSlot));
            return;
        }

        switch (target)
        {
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && ResolveType(aggFieldRef.Instance.Type) is INamedTypeSymbol aggWbType
                && TypeClassifier.IsObjectArrayEmulated(aggWbType):
            {
                var layout = _ctx.Aggregates.GetLayout(aggWbType);
                if (layout.TryGetIndex(aggFieldRef.Field, out var elemIdx))
                {
                    var arrVal = lv.ArrayVal ?? VisitExpression(aggFieldRef.Instance);
                    AggregateAbi.WriteSlot(_builder, arrVal, elemIdx, valueVal);
                    return;
                }
                break;
            }
            case IArrayElementReferenceOperation ndimWbElem when ndimWbElem.Indices.Length > 1:
            {
                // Reuse CaptureLValue's plan when available (avoid re-evaluating indices/bundle);
                // the uncached path is defensive (mirrors the rank-1 arm's ?? default).
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
            // User-defined indexer on this/base BEHAVIOUR → call setter with the index args followed by
            // the value (no receiver param). Reuse the index args cached by CaptureLValue (compound
            // assignment) to avoid re-evaluating them. WjR3 (B48 twin): an object[]-emulated containing
            // type must NOT take this no-receiver arm — it falls through to the receiver-as-param0
            // object[]-emulated indexer arm below.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: true } } idxRef
                when !TypeClassifier.IsObjectArrayEmulated(idxRef.Property.ContainingType)
                && ResolveDispatchProperty(idxRef).SetMethod is { } idxDispatchSetter
                && _methodFunctions.TryGetValue(idxDispatchSetter, out _):
            {
                // Wave-9 round-4: the uncached path slots by parameter ordinal too (named args).
                var setterArgs = lv.IndexArgs != null ? new List<CLeaf>(lv.IndexArgs) : EvaluateIndexerArgs(idxRef);
                setterArgs.Add(valueVal);
                EmitExprStmt(EmitCallToMethod(idxDispatchSetter, setterArgs));
                return;
            }
            // User-defined property on this/base BEHAVIOUR → call setter (value only, no receiver
            // param). WjR3: an object[]-emulated containing type must NOT take this arm — its setter
            // expects the receiver object[] as param0 (a v1-class/struct `this.P += 1` skewed the
            // CInternalCall arity); it falls through to the object[]-emulated property arm below. An
            // INDEXER never belongs here either (the value-only call drops the index legs) — it rides
            // the indexer arms above/below.
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { IsIndexer: false } } propRef
                when !TypeClassifier.IsObjectArrayEmulated(propRef.Property.ContainingType)
                && ResolveDispatchProperty(propRef).SetMethod is { } dispatchSetter
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
                RejectProgramLocalCrossBehaviourPropertyWrite(propRef.Property); // CW22 (compound/`??=` leg)
                var instanceVal = VisitExpression(propRef.Instance);
                // Wave-12 [V2]: non-public autos write the declared backing symbol directly (their
                // accessors are never exported); see IsNonPublicAutoCrossProperty.
                var isAutoSet = propRef.Property.SetMethod == null
                    || IsNonPublicAutoCrossProperty(propRef.Property.SetMethod, propRef.Property);
                if (isAutoSet)
                {
                    var nameConst = Const(propRef.Property.Name, "SystemString");
                    EmitExternVoid(ExternResolver.EventReceiverSetProgramVariable, new List<CLeaf> { instanceVal, nameConst, valueVal });
                }
                else
                {
                    RejectNonPublicCrossAccessor(propRef.Property.SetMethod, propRef.Property); // wave-12 [V2]
                    // Wave-12 r2 [V1]: reentrant setter — value copy-in inside the spill window.
                    bool wbReentrant = TryMarkReentrantCrossDispatch(propRef, propRef.Property.SetMethod);
                    var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                    var paramNameConst = Const(setParamIds[0], "SystemString");
                    EmitExternVoid(ExternResolver.EventReceiverSetProgramVariable, new List<CLeaf> { instanceVal, paramNameConst, valueVal });
                    var eventConst = Const(exportName, "SystemString");
                    EmitExternVoid(ExternResolver.EventReceiverSendCustomEvent, new List<CLeaf> { instanceVal, eventConst },
                        wbReentrant, wbReentrant ? 1 : 0);
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
                when aggPropRef.Instance?.Type is INamedTypeSymbol aggPropType && TypeClassifier.IsObjectArrayEmulated(aggPropType):
            {
                if (_ctx.Aggregates.GetLayout(aggPropType).TryGetIndex(aggPropRef.Property.Name, out var propIdx))
                {
                    var arrVal = lv.ArrayVal ?? LoadInstanceRaw(aggPropRef.Instance);
                    AggregateAbi.WriteSlot(_builder, arrVal, propIdx, valueVal);
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
            // User-defined indexer on an object[]-emulated instance (`s[i] = v` / `s[i] += v`) → call the
            // setter with the receiver (object[]) as param0, the index args, then the value. Reuse the
            // receiver/args cached by CaptureLValue (compound assignment); without this it falls to a bogus
            // __set_Item extern. CW6: IsObjectArrayEmulated so class receivers ride the same arm.
            case IPropertyReferenceOperation { Property: { IsIndexer: true, SetMethod: { } aggIdxSetter } } aggIdxRef
                when aggIdxRef.Instance?.Type is INamedTypeSymbol aggIdxType && TypeClassifier.IsObjectArrayEmulated(aggIdxType)
                && _methodFunctions.ContainsKey(aggIdxSetter):
            {
                var setterArgs = new List<CLeaf> { lv.ArrayVal ?? LoadInstanceRaw(aggIdxRef.Instance) };
                // Wave-9 round-4: the uncached path slots by parameter ordinal too (named args).
                setterArgs.AddRange(lv.IndexArgs ?? EvaluateIndexerArgs(aggIdxRef));
                setterArgs.Add(valueVal);
                EmitExprStmt(EmitCallToMethod(aggIdxSetter, setterArgs));
                return;
            }
            // Resolve containing type and instance
            case IPropertyReferenceOperation propRef:
            {
                // CW6 armor: a user-struct/class property whose write-back reaches this generic extern
                // arm was not routed by the object[]-emulated arms above — fail with the collector-drift
                // diagnosis instead of minting a bogus SystemObjectArray.__set_<Name>__ extern.
                GuardUserStructMemberReachedExtern(propRef.Property.ContainingType, propRef.Property.Name);
                // B55 setter door: the property-SET write-back resolves its extern owner through the same
                // inherited-member choke point as the getter (subsumes the former Behaviour fixup). Static
                // (Instance null) → declaring type; inherited instance member → receiver's static type.
                var containingType = GetStorageTypeName(ResolveExternOwnerType(propRef.Property.ContainingType, propRef.Instance?.Type, propRef.Property.Name));

                CLeaf wbInstanceVal;
                if (propRef.Instance is IInstanceReferenceOperation)
                    wbInstanceVal = LoadField(_ctx.Storage.DeclareThisOnce(containingType), containingType);
                else if (propRef.Instance != null)
                    wbInstanceVal = VisitExpression(propRef.Instance);
                else
                {
                    // Static property: no instance
                    var valueType = GetStorageTypeName(propRef.Property.Type);
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<CLeaf> { valueVal });
                    return;
                }

                var propValueType = GetStorageTypeName(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    var indexArgs = new List<CLeaf> { wbInstanceVal };
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        indexArgs.Add(VisitExpression(arg.Value));
                        indexTypes.Add(GetStorageTypeName(arg.Value.Type));
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
                var containingType = GetStorageTypeName(ResolveExternOwnerType(fieldRef2.Field.ContainingType, fieldRef2.Instance?.Type, fieldRef2.Field.Name));
                var valueType = GetStorageTypeName(fieldRef2.Field.Type);
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
    /// Used by direct local/this-field store paths in SimpleAssignmentHandler / CompoundAssignmentHandler.
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
