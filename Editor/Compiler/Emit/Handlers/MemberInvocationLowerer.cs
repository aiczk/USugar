using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Owns property, constructor, interpolation, and anonymous-object lowering.</summary>
internal sealed class MemberInvocationLowerer
{
    readonly InvocationHandler _owner;
    LoweringServices _lowering => _owner.Lowering;

    internal MemberInvocationLowerer(InvocationHandler owner)
        => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    // ── Property Reference ──

    internal CLeaf VisitPropertyReference(IPropertyReferenceOperation op)
    {
        var boundGetter = VirtualDispatch.FindAccessor(op.Property, getter: true);
        if (boundGetter != null && !op.Property.ContainingType.IsAnonymousType)
            _lowering.RequireBoundCallSite(
                op, CallableSiteKind.PropertyGet, boundGetter);

        // Indexer access: Type.__get_Item__IndexTypes__ReturnType
        if (op.Property.IsIndexer)
            return VisitIndexerGet(op);

        // Nullable<T> (boxed-object emulation): HasValue → null check; Value → the boxed value itself
        // (Udon unboxes transparently when the result is used as the underlying type).
        if (op.Instance != null && EmitPolicy.IsNullableT(op.Property.ContainingType, out var nblUnder))
        {
            var nv = _lowering.VisitExpression(op.Instance);
            if (op.Property.Name == "HasValue") return NullableAbi.HasValue(_lowering.Builder, nv);
            // Value of a nullable AGGREGATE (e.g. (int,int)? / V?) copies the struct out (value semantics).
            // A small-underlying box is re-tagged tolerantly (CW18 — see RetagSmallNullablePresent).
            if (op.Property.Name == "Value")
                return nblUnder is INamedTypeSymbol nblAgg && TypeClassifier.IsAggregateValue(nblAgg)
                    ? AggregateAbi.DeepClone(_lowering.Builder, nv, nblAgg, _lowering.State.Aggregates.GetLayout)
                    : _lowering.RetagSmallNullablePresent(nv, nblUnder);
        }

        // CW1 lift: a runtime-polymorphic property READ on a v1-class receiver dispatches through the
        // same typeobj machinery as the method arm (chain / devirt / empty lowering) — BEFORE the
        // static auto-slot and computed-getter arms below, which bind the receiver's STATIC symbol.
        // Fires for base-typed variables AND `this` inside an inherited base body; `base.P` keeps the
        // static binding (IsDispatchSite excludes it).
        if (VirtualDispatch.FindAccessor(op.Property, getter: true) is { } vpGetter
            && _lowering.IsAccessorDispatchSite(op, vpGetter, out var vpRecvTy))
        {
            var (vpRecv, vpIdx) = _lowering.StageAccessorDispatchLegs(op);
            return _lowering.EmitAccessorDispatch(
                op, vpRecvTy, vpGetter, vpRecv, vpIdx, null);
        }

        // Auto-property on an aggregate (struct/tuple) OR v1 class → object[] element (the backing field's
        // slot). The clone at the return stays IsAggregateValue, so a class-typed property returns by reference.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggProp && TypeClassifier.IsObjectArrayEmulated(aggProp)
            && _lowering.State.Aggregates.GetLayout(aggProp).TryGetIndex(op.Property, out var aggPropIdx))
        {
            var arrExpr = _lowering.LoadInstanceRaw(op.Instance);
            var getVal = AggregateAbi.ReadSlot(_lowering.Builder, arrExpr, aggPropIdx, StorageTypes.Object);
            // A struct-typed property returns a COPY (C# getters return by value; you cannot mutate through it).
            return op.Property.Type is INamedTypeSymbol propAgg && TypeClassifier.IsAggregateValue(propAgg)
                ? AggregateAbi.DeepClone(_lowering.Builder, getVal, propAgg, _lowering.State.Aggregates.GetLayout) : getVal;
        }

        // Computed (non-auto) property on an aggregate (struct) OR v1 class: no backing-field slot, so
        // inline-call the user getter with the receiver object[] as synthetic param0 (same convention as
        // EmitStructInstanceCall). The getter only reads, so the receiver is passed uncloned. The return
        // clone stays IsAggregateValue, so a class-typed getter result is returned by reference.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggGet && TypeClassifier.IsObjectArrayEmulated(aggGet)
            && op.Property.GetMethod is { } aggGetterRaw)
        {
            var ret = _lowering.EmitCallToMethod(_lowering.RequireStructMember(aggGetterRaw),
                new List<CLeaf> { _lowering.LoadInstanceRaw(op.Instance) });
            return op.Property.Type is INamedTypeSymbol getRetAgg && TypeClassifier.IsAggregateValue(getRetAgg)
                ? AggregateAbi.DeepClone(_lowering.Builder, ret, getRetAgg, _lowering.State.Aggregates.GetLayout) : ret;
        }

        // this.gameObject / this.transform → __this_* variable (Udon VM resolves via "this" default)
        if (op.Instance is IInstanceReferenceOperation)
        {
            // Virtual dispatch through `this` (round 7): a read inside an inherited base body binds the
            // BASE accessor — resolve to the chain-leaf override; `base.P` keeps the static binding.
            var thisProp = _lowering.ResolveDispatchProperty(op);

            // User-defined property getter → internal call. A struct-typed getter result is COPIED (C#
            // getters return by value) — otherwise `read = this.Prop` aliases the backing field. (diff-fuzz w4)
            if (thisProp.GetMethod != null
                && _lowering.MethodFunctions.ContainsKey(thisProp.GetMethod))
            {
                var gv = _lowering.EmitCallToMethod(thisProp.GetMethod, new List<CLeaf>());
                return thisProp.Type is INamedTypeSymbol thisGetAgg && TypeClassifier.IsAggregateValue(thisGetAgg)
                    ? AggregateAbi.DeepClone(_lowering.Builder, gv, thisGetAgg, _lowering.State.Aggregates.GetLayout) : gv;
            }

            // Auto-property on this class → direct backing-field access (user-defined classes only). A
            // struct-typed backing field is COPIED on read (value semantics), same as a struct field.
            if (thisProp.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                && ExternResolver.IsUdonSharpBehaviour(thisProp.ContainingType)
                && thisProp.ContainingType.Name != "UdonSharpBehaviour")
            {
                var bv = _lowering.LoadField(thisProp.Name, _lowering.GetStorageType(thisProp.Type));
                return thisProp.Type is INamedTypeSymbol thisAutoAgg && TypeClassifier.IsAggregateValue(thisAutoAgg)
                    ? AggregateAbi.DeepClone(_lowering.Builder, bv, thisAutoAgg, _lowering.State.Aggregates.GetLayout) : bv;
            }

            var propName = op.Property.Name;
            if (propName == "gameObject" || propName == "transform")
            {
                var propType = _lowering.GetStorageTypeName(op.Property.Type);
                return _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(new StorageType(propType)), new StorageType(propType));
            }
            // Other this.property → extern getter with this instance
            var thisType = _lowering.GetStorageTypeName(_lowering.ClassSymbol);
            var thisVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(new StorageType(thisType)), new StorageType(thisType));
            var cType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(op.Property.ContainingType, op.Instance?.Type, op.Property.Name));
            var rType = _lowering.GetStorageTypeName(op.Property.Type);
            return _lowering.ExternCall(
                _lowering.State.BoundAbi.RequirePropertyGetter(cType, propName, rType),
                new List<CLeaf> { thisVal },
                new StorageType(rType));
        }

        var containingType = _lowering.GetStorageTypeName(op.Property.ContainingType);
        var returnType = _lowering.GetStorageTypeName(op.Property.Type);

        // Static property: no instance
        if (op.Instance == null)
        {
            // B47 (wave-14 r6): a STATIC COMPUTED property on a user struct/class (StaticPropHelper<T>.Doubled)
            // is a foreign-static accessor CALL, not a real extern — inline-call its getter (the B46
            // static-method pattern, one node kind over). Without this the fall-through emits a bogus
            // SystemObjectArray.__get_Doubled__ extern. The getter is pre-registered when its call site was
            // reachable with a CLOSED containing type (collection layer); a closed spec first seen at a
            // generic call site inside a struct/generic body is registered on demand here (mirrors the
            // foreign-static-on-generic method arm). Auto/BCL/const-foldable statics are excluded: the
            // const-fold arm below owns the BCL foldables, and IsUserComputedStaticProperty gates out autos.
            if (op.Property.IsStatic && op.Property.GetMethod is { } sPropGetter
                && sPropGetter.DeclaringSyntaxReferences.Length > 0
                && !USugarCompilerHelper.IsFrameworkNamespace(sPropGetter.ContainingNamespace)
                && IsUserComputedStaticProperty(op.Property))
            {
                // ResolveStructMember substitutes the containing type's type args from the current map
                // (SP<T>.get_Doubled → SP<int>.get_Doubled inside a Box<int> spec) and registers the closed
                // spec on demand; an already-closed getter (a class-body call site) is returned unchanged
                // and was pre-registered by the collection layer.
                var sgv = _lowering.EmitCallToMethod(_lowering.RequireStructMember(sPropGetter), new List<CLeaf>());
                return op.Property.Type is INamedTypeSymbol sgAgg && TypeClassifier.IsAggregateValue(sgAgg)
                    ? AggregateAbi.DeepClone(_lowering.Builder, sgv, sgAgg, _lowering.State.Aggregates.GetLayout) : sgv;
            }
            if (op.Property.GetMethod?.DeclaringSyntaxReferences.Length > 0
                && !USugarCompilerHelper.IsFrameworkNamespace(op.Property.ContainingNamespace)
                && !IsUserComputedStaticProperty(op.Property))
            {
                var owner = _lowering.ResolveType(op.Property.ContainingType) as INamedTypeSymbol
                            ?? op.Property.ContainingType;
                return _lowering.LoadField(StaticOwnerAbi.PropertyName(op.Property, owner),
                    _lowering.GetStorageType(op.Property.Type));
            }

            // Constant folding: static properties on foldable struct types (e.g., Vector3.zero)
            if (op.Property.IsStatic && ConstFoldableStructTypes.Contains(containingType))
            {
                var value = TryGetStaticPropertyValue(containingType, op.Property.Name);
                if (value != null)
                    return _lowering.LoadField(_lowering.State.Storage.DeclareStructConst(new StorageType(returnType), value), new StorageType(returnType));
            }

            // Armor: a user-struct static property reaching here (the B47 on-demand arm above did not
            // register its accessor) would mint a bogus SystemObjectArray.__get_<Name>__ extern — fail
            // with a diagnosis instead (collector-scope drift, roadmap B47 family).
            _lowering.GuardUserStructMemberReachedExtern(op.Property.ContainingType, op.Property.Name);

            return _lowering.ExternCall(
                _lowering.State.BoundAbi.RequirePropertyGetter(
                    containingType, op.Property.Name, returnType, hasReceiver: false),
                new List<CLeaf>(),
                new StorageType(returnType));
        }

        // Cross-behaviour property get
        if (op.Instance != null && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
            && !(op.Instance is IInstanceReferenceOperation))
        {
            _lowering.RejectProgramLocalCrossBehaviourPropertyRead(op.Property); // CW22 (field-read twin)
            var instanceVal = _lowering.VisitExpression(op.Instance);
            // Wave-12 [V2]: non-public autos read the declared backing symbol directly (their
            // accessors are never exported); see IsNonPublicAutoCrossProperty.
            var isAuto = LoweringServices.IsNonPublicAutoCrossProperty(op.Property.GetMethod, op.Property);

            if (isAuto)
            {
                // Auto-property: direct GetProgramVariable("PropertyName")
                return _lowering.LoadProgramVariable(
                    instanceVal, op.Property.Name, new StorageType(returnType));
            }
            else
            {
                // Non-auto property getter: a single-return cross-behaviour call. CrossCall binds it to a
                // scratch slot at this point (A-normal form), so the SendCustomEvent fires exactly once in
                // program order — inside the branch block when this getter is a ternary arm.
                var (getExportName, _, getRetId) = _lowering.GetCalleeLayout(op.Property.GetMethod);
                var getReturns = getRetId != null
                    ? new[] { new ReturnSlot(getRetId, new StorageType(returnType)) }
                    : System.Array.Empty<ReturnSlot>();
                return _lowering.CrossCall(instanceVal, getExportName,
                    System.Array.Empty<CrossCallParameter>(), getReturns, new StorageType(returnType),
                    _lowering.TryMarkReentrantCrossDispatch(op, op.Property.GetMethod)); // wave-12 r2 [V1]
            }
        }

        // Interface property get → dispatch the getter through its interface bridge (SendCustomEvent),
        // like an interface method call. Without this, GetStorageTypeName(interface) yields IUdonEventReceiver and
        // the fall-through emits a non-existent __get_Value extern on it.
        if (op.Property.GetMethod is { } ifaceGetter
            && ExternResolver.IsUserInterface(op.Property.ContainingType)
            && !_lowering.IsResolvedConcreteNonBehaviour(op.Instance.Type)
            && _lowering.Planner.GetLayout(op.Property.ContainingType).Methods.TryGetValue(ifaceGetter, out var ifaceGetterMl))
        {
            _lowering.GuardInterfaceDispatchRepresentation(op.Property.ContainingType, op.Property.Name);
            _lowering.RejectProgramLocalCrossBehaviourPropertyRead(op.Property); // CW22
            var ifaceInst = _lowering.VisitExpression(op.Instance);
            return _lowering.CrossCall(ifaceInst, LayoutPlanBuilder.InterfaceDispatchName(ifaceGetter, ifaceGetterMl),
                System.Array.Empty<CrossCallParameter>(), ifaceGetterMl.Returns.ToArray(),
                new StorageType(returnType),
                _lowering.TryMarkReentrantCrossDispatch(op, ifaceGetter)); // wave-12 r2 [V1]
        }

        // N-dim array (design 2026-07-04 §2/N-R4): Rank>1 array VALUE is an object[] bundle whose Udon
        // type tag (SystemObjectArray) happens to have REAL, valid Rank/Length externs registered — MUST
        // intercept before the generic extern-getter path below, or ".Rank"/".Length" would silently
        // read the bundle wrapper's own shape (rank always 1, length always 1+r) instead of the logical
        // array's. Length reads the FLAT BACKING's length (§2); Rank is a compile-time constant.
        if (op.Instance != null && NdimArrayAbi.IsNdimArray(op.Instance.Type))
        {
            var ndimPropType = (IArrayTypeSymbol)op.Instance.Type;
            if (!NdimArrayAbi.TryGetProperty(op.Property.Name, out var propertyKind))
            {
                NdimArrayAbi.RejectMember(op.Property.Name);
                return null; // unreachable
            }
            switch (propertyKind)
            {
                case NdimArrayAbi.PropertyKind.Length: return _lowering.Ndim.EmitNdimLength(_lowering.VisitExpression(op.Instance), ndimPropType);
                case NdimArrayAbi.PropertyKind.Rank: return _lowering.Ndim.EmitNdimRank(ndimPropType);
                default: throw new System.InvalidOperationException($"Unknown N-dim array property kind: {propertyKind}");
            }
        }

        // Other instance.property → extern getter
        var instVal = _lowering.VisitExpression(op.Instance);
        // Array .Length → use SystemArray (not the concrete array type) to match UdonSharp
        if (op.Instance.Type is IArrayTypeSymbol && op.Property.Name != "Length")
            containingType = _lowering.GetStorageTypeName((IArrayTypeSymbol)op.Instance.Type);
        // An inherited member (incl. Behaviour/MonoBehaviour/BCL-base) registers under the RECEIVER's
        // static type, not the declaring base — resolve through the shared owner choke point (B55/B59).
        // Array receivers keep the fixup above (SystemArray for .Length, element-typed otherwise).
        else if (op.Instance.Type is not IArrayTypeSymbol)
            containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(op.Property.ContainingType, op.Instance.Type, op.Property.Name));
        var sig = _lowering.State.BoundAbi.RequirePropertyGetter(
            containingType, op.Property.Name, returnType);
        return _lowering.ExternCall(sig, new List<CLeaf> { instVal }, new StorageType(returnType));
    }

    // ── Indexer Get ──

    CLeaf VisitIndexerGet(IPropertyReferenceOperation op)
    {
        // CW1 lift: a runtime-polymorphic indexer READ on a v1-class receiver dispatches through the
        // typeobj machinery (see the property-read twin above); the static arms below bind the
        // receiver's STATIC accessor. `base[i]` and struct receivers keep their static arms.
        if (VirtualDispatch.FindAccessor(op.Property, getter: true) is { } viGetter
            && _lowering.IsAccessorDispatchSite(op, viGetter, out var viRecvTy))
        {
            var (viRecv, viIdx) = _lowering.StageAccessorDispatchLegs(op);
            return _lowering.EmitAccessorDispatch(
                op, viRecvTy, viGetter, viRecv, viIdx, null);
        }

        // User-defined indexer on this/base BEHAVIOUR → internal getter call (`this[i]` reads this-fields
        // directly, no receiver param). ResolveDispatchProperty (round 7): `this[i]` inside an inherited
        // base body binds the BASE indexer — dispatch the chain-leaf override; `base[i]` keeps the static
        // binding. B48/WjR3: an object[]-emulated containing type (user struct OR v1 class) must NOT take
        // this behaviour-only arm — its accessor is a StructuredFunction expecting the receiver object[] as param0,
        // and calling it with only the index args skews the arity (`base[i]` / non-virtual `this[i]` in a
        // v1-class body hit the CInternalCall arity gate on legal C#). It falls through to the
        // object[]-emulated arm below, which passes LoadInstanceRaw(this/base) = the receiver param —
        // mirroring how a struct's `this.Method()` self-call routes through EmitStructInstanceCall.
        if (op.Instance is IInstanceReferenceOperation
            && !TypeClassifier.IsObjectArrayEmulated(op.Property.ContainingType)
            && _lowering.ResolveDispatchProperty(op) is { GetMethod: { } idxDispatchGetter }
            && _lowering.MethodFunctions.ContainsKey(idxDispatchGetter))
        {
            // Wave-9 round-4: index args slotted by parameter ordinal (named/reordered index args
            // bind by name) — the textual foreach bound `this[col: 2, row: 1]` positionally.
            return _lowering.EmitCallToMethod(idxDispatchGetter, _lowering.EvaluateIndexerArgs(op));
        }

        // User-defined indexer on a user STRUCT or v1-class instance (`s[i]`) → call the getter with the
        // receiver (object[]) as param0 plus the index args, like a computed property. Without this it falls
        // to a bogus SystemObjectArray.__get_Item extern the validator rejects. (diff-fuzz wave 4) The return
        // clone stays IsAggregateValue, so a class-typed indexer result is returned by reference.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggIdx && TypeClassifier.IsObjectArrayEmulated(aggIdx)
            && op.Property.GetMethod is { } idxGetterRaw)
        {
            var sargs = new List<CLeaf> { _lowering.LoadInstanceRaw(op.Instance) };
            sargs.AddRange(_lowering.EvaluateIndexerArgs(op)); // wave-9 round-4: named index args bind by ordinal
            var ret = _lowering.EmitCallToMethod(_lowering.RequireStructMember(idxGetterRaw), sargs);
            return op.Property.Type is INamedTypeSymbol idxRetAgg && TypeClassifier.IsAggregateValue(idxRetAgg)
                ? AggregateAbi.DeepClone(_lowering.Builder, ret, idxRetAgg, _lowering.State.Aggregates.GetLayout) : ret;
        }

        // Wave-9 round-2 [W6]: user indexer read through a VARIABLE receiver (own-typed copy /
        // base-typed / another behaviour) → cross-program getter dispatch (see EmitCrossIndexerCall).
        // Pre-fix this fell through to the extern arm below and emitted a nonexistent
        // IUdonEventReceiver.__get_Item the validator/assembler crashes on.
        if (LoweringServices.IsVariableReceiverBehaviourIndexer(op) && op.Property.GetMethod is { } recvIdxGetter)
        {
            var recvVal = _lowering.VisitExpression(op.Instance);
            return _lowering.EmitCrossIndexerCall(recvIdxGetter, recvVal, _lowering.EvaluateIndexerArgs(op),
                _lowering.TryMarkReentrantCrossDispatch(op, recvIdxGetter)); // wave-12 r2 [V1]
        }

        // Wave-9 round-4 [X4]/[X9]: user indexer read through an INTERFACE-typed receiver → dispatch
        // the getter through its interface bridge, like an interface method/property. The [W6] gate
        // tests IsUdonSharpBehaviour(ContainingType), which is the INTERFACE here, so this fell
        // through to extern resolution and emitted a nonexistent IUdonEventReceiver.__get_Item the
        // validator crashes on (loud crash on legal C#).
        if (_lowering.TryGetInterfaceAccessorLayout(op, op.Property.GetMethod, out var ifaceIdxGetMl))
        {
            var ifaceIdxInst = _lowering.VisitExpression(op.Instance);
            return _lowering.EmitInterfaceAccessorCall(op.Property.GetMethod, ifaceIdxGetMl, ifaceIdxInst,
                _lowering.EvaluateIndexerArgs(op),
                _lowering.TryMarkReentrantCrossDispatch(op, op.Property.GetMethod)); // wave-12 r2 [V1]
        }

        var cType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(op.Property.ContainingType, op.Instance?.Type, op.Property.Name));
        var rType = _lowering.GetStorageTypeName(op.Property.Type);

        // string[i] → str.ToCharArray(i, 1)[0]
        // Udon VM has no string indexer; mirror UdonSharp's BoundStringAccessExpression
        if (cType == "SystemString")
        {
            CLeaf inst = op.Instance is IInstanceReferenceOperation
                ? _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(_lowering.GetStorageType(_lowering.ClassSymbol)), _lowering.GetStorageType(_lowering.ClassSymbol))
                : _lowering.VisitExpression(op.Instance);
            var indexVal = _lowering.VisitExpression(op.Arguments[0].Value);
            var oneConst = _lowering.Const(1, StorageTypes.Int32);
            var charArr = _lowering.ExternCall(
                UdonAbiKey.Method("SystemString", "ToCharArray", new[] { "SystemInt32", "SystemInt32" }, "SystemCharArray"),
                new List<CLeaf> { inst, indexVal, oneConst },
                StorageTypes.CharArray);
            var zeroConst = _lowering.Const(0, StorageTypes.Int32);
            return _lowering.ExternCall(
                UdonAbi.ArrayGet("SystemCharArray", "SystemChar"),
                new List<CLeaf> { charArr, zeroConst },
                StorageTypes.Char);
        }

        CLeaf instVal;
        if (op.Instance is IInstanceReferenceOperation)
            instVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(_lowering.GetStorageType(_lowering.ClassSymbol)), _lowering.GetStorageType(_lowering.ClassSymbol));
        else
            instVal = _lowering.VisitExpression(op.Instance);

        var externArgs = new List<CLeaf>();
        externArgs.Add(instVal);
        var idxTypes = new List<string>();
        foreach (var arg in op.Arguments)
        {
            externArgs.Add(_lowering.VisitExpression(arg.Value));
            idxTypes.Add(_lowering.GetStorageTypeName(arg.Value.Type));
        }
        // Use the indexer's metadata name, not a hardcoded "Item": most indexers are "Item", but a type with
        // [IndexerName(...)] differs (e.g. StringBuilder's indexer is "Chars" → __get_Chars__, not __get_Item__).
        return _lowering.ExternCall(
            _lowering.State.BoundAbi.RequireIndexerGetter(
                cType, op.Property.MetadataName, idxTypes, rType),
            externArgs,
            new StorageType(rType));
    }

    // ── Interpolated String ──

    internal CLeaf VisitInterpolatedString(IInterpolatedStringOperation op)
    {
        var formatParts = new List<string>();
        var argVals = new List<CLeaf>();
        int argIndex = 0;

        foreach (var part in op.Parts)
        {
            switch (part)
            {
                case IInterpolatedStringTextOperation text:
                    if (text.Text is ILiteralOperation lit && lit.ConstantValue.HasValue)
                        formatParts.Add(lit.ConstantValue.Value?.ToString() ?? "");
                    break;
                case IInterpolationOperation interpolation:
                    var placeholder = new System.Text.StringBuilder();
                    placeholder.Append('{');
                    placeholder.Append(argIndex);
                    if (interpolation.Alignment != null)
                    {
                        var alignVal = interpolation.Alignment.ConstantValue;
                        if (alignVal.HasValue)
                        {
                            placeholder.Append(',');
                            placeholder.Append(alignVal.Value);
                        }
                    }
                    if (interpolation.FormatString != null)
                    {
                        var fmtVal = interpolation.FormatString.ConstantValue;
                        if (fmtVal.HasValue)
                        {
                            placeholder.Append(':');
                            placeholder.Append(fmtVal.Value);
                        }
                    }
                    placeholder.Append('}');
                    formatParts.Add(placeholder.ToString());
                    // M4b: a v1 class hole stringifies through the object.ToString dispatch slot —
                    // override arm = direct call, no-override arm = the runtime type-name constant,
                    // null = "" (C# Format semantics). Replaces the M3 sealed-only fast path (a
                    // sealed/singleton set devirtualizes inside the dispatch helper).
                    if (_lowering.ResolveType(interpolation.Expression.Type) is INamedTypeSymbol interpCls
                        && TypeClassifier.IsUserClass(interpCls))
                    {
                        var recv = _lowering.VisitExpression(interpolation.Expression);
                        argVals.Add(_lowering.EmitClassToStringDispatch(interpCls, recv,
                            nullIsError: false, useOverrides: true));
                        argIndex++;
                        break;
                    }
                    // B67: a user enum in an interpolation hole would be boxed and Format-ToString'd to its
                    // underlying number — pre-convert it to the C#-correct name string instead.
                    ClassAbi.RejectImplicitToString(interpolation.Expression.Type);
                    var interpVal = _lowering.VisitExpression(interpolation.Expression);
                    argVals.Add(_lowering.TryEmitEnumToString(interpVal, interpolation.Expression.Type) ?? interpVal);
                    argIndex++;
                    break;
            }
        }

        var formatStr = string.Join("", formatParts);
        var formatConst = _lowering.Const(formatStr, StorageTypes.String);

        if (argVals.Count == 0)
        {
            // No interpolation: just return the literal
            return formatConst;
        }

        if (argVals.Count <= 3)
        {
            var externArgs = new List<CLeaf>();
            externArgs.Add(formatConst);
            externArgs.AddRange(argVals);
            return _lowering.ExternCall(
                UdonAbiKey.Method("SystemString", "Format",
                    new[] { "SystemString" }
                        .Concat(argVals.Select(_ => "SystemObject")),
                    "SystemString"),
                externArgs,
                StorageTypes.String);
        }
        else
        {
            // 4+ args: pack into SystemObjectArray, use Format(string, object[])
            var sizeConst = _lowering.Const(argVals.Count, StorageTypes.Int32);
            var arrVal = _lowering.ExternCall(
                UdonAbi.ArrayConstructor("SystemObjectArray"),
                new List<CLeaf> { sizeConst },
                StorageTypes.ObjectArray);
            for (int i = 0; i < argVals.Count; i++)
            {
                var idxConst = _lowering.Const(i, StorageTypes.Int32);
                _lowering.EmitExternVoid(UdonAbi.ArraySet("SystemObjectArray", "SystemObject"),
                    new List<CLeaf> { arrVal, idxConst, argVals[i] });
            }
            return _lowering.ExternCall(
                UdonAbiKey.Method("SystemString", "Format", new[] { "SystemString", "SystemObjectArray" }, "SystemString"),
                new List<CLeaf> { formatConst, arrVal },
                StorageTypes.String);
        }
    }

    // ── Object Creation ──

    static readonly HashSet<string> ConstFoldableStructTypes = new()
    {
        "UnityEngineVector2", "UnityEngineVector3", "UnityEngineVector4",
        "UnityEngineQuaternion", "UnityEngineColor", "UnityEngineColor32",
        "UnityEngineMatrix4x4", "UnityEngineRect",
    };

    /// <summary>Class ABI v1 (CA-M1): THE single mint sequence for a supported user class — a
    /// reference-semantics object[1+F] bundle (slot 0 reserved for the future type-object reference). Kept in
    /// one place (the DelegateAbi.EmitBundleMint analogue) so the future slot-0 flip is a one-line change here.
    /// Order (C# semantics): allocate object[SlotCount] → default-init fields (slot 0 stays the ctor's null) →
    /// run instance field initializers in declaration order → run the ctor body (ctor-as-StructuredFunction, receiver =
    /// param0) → apply any object-initializer. NO defensive copies — the same bundle reference flows through.</summary>
    /// <summary>CA-v2 M1 ctor prologue (charter #6): before a v1 class ctor's body runs, either
    /// redirect to a `: this(...)` target (suppressing own field inits) or run own field inits then the
    /// base ctor (explicit `: base(...)` function, or the implicit base chain). Emitted from EmitMethod
    /// which owns the ctor function; this handler owns EmitCallToMethod/field-init helpers.</summary>
    public void EmitClassCtorPrologue(IMethodSymbol ctor, IConstructorBodyOperation body, string receiverParamId)
    {
        var classTy = (INamedTypeSymbol)ctor.ContainingType;

        // Roslyn wraps `: base(...)` / `: this(...)` as an IExpressionStatementOperation over the
        // ctor IInvocationOperation.
        var initInv = body.Initializer switch
        {
            IInvocationOperation d => d,
            IExpressionStatementOperation { Operation: IInvocationOperation w } => w,
            _ => null,
        };

        // Zero-work fast path (byte-identical to pre-M1 for a plain single class): nothing to do unless
        // this class has field initializers, a user-class base chain, or an EXPLICIT this/base call.
        bool baseIsUserClass = classTy.BaseType is INamedTypeSymbol bt0 && TypeClassifier.IsUserClass(bt0);
        bool explicitChainCall = initInv != null && !initInv.TargetMethod.IsImplicitlyDeclared;
        if (_lowering.State.Program.ClassInitializers.Require(classTy).Count == 0
            && !baseIsUserClass
            && !explicitChainCall)
            return;

        var inst = _lowering.LoadField(receiverParamId, new StorageType(AggregateAbi.ArrayType));
        if (initInv is { } init
            && init.TargetMethod is { MethodKind: MethodKind.Constructor } target)
        {
            bool isThisChain = SymbolEqualityComparer.Default.Equals(target.ContainingType, classTy);
            if (!isThisChain)
                EmitClassInitializers(inst, classTy);
            if (target.IsImplicitlyDeclared)
            {
                if (target.ContainingType is INamedTypeSymbol implBase && TypeClassifier.IsUserClass(implBase))
                    ClassAbi.EmitImplicitCtorChain(
                        _lowering.Builder, inst, implBase,
                        _lowering.State.Program.ClassInitializers.Require,
                        VisitClassInitializerExpression, CallBaseCtor);
            }
            else
            {
                // CW4: same by-ordinal + ref/out discipline as the mint arm (EmitClassInstanceMint).
                var chainCtor = _lowering.SubstituteMethodTypeArgs(target);
            _owner.Externs.GuardRefOutArguments(init.Arguments, chainCtor);
                var chainArgs = new List<CLeaf> { inst };
            var chainPrepared = _owner.Externs.MarshalArgumentsByOrdinal(init.Arguments, chainCtor, chainArgs);
                _lowering.EmitExprStmt(_lowering.EmitCallToMethod(_lowering.RequireStructMember(target), chainArgs));
            _owner.Externs.EmitRefOutCopyBack(init.Arguments, chainCtor, 0, chainPrepared);
            }
            return;
        }

        // No initializer node = implicit `: base()`: own field inits then the implicit base chain.
        EmitClassInitializers(inst, classTy);
        if (classTy.BaseType is INamedTypeSymbol cbt && TypeClassifier.IsUserClass(cbt))
            ClassAbi.EmitImplicitCtorChain(
                _lowering.Builder, inst, cbt,
                _lowering.State.Program.ClassInitializers.Require,
                VisitClassInitializerExpression, CallBaseCtor);
    }

    /// <summary>CA-v2b-2: emit a direct call to an explicit parameterless base ctor from an implicit derived
    /// ctor chain — the base ctor runs its own field inits, base chain, and body (needed for a base ctor with
    /// side effects, e.g. a virtual call under charter #6).</summary>
    void CallBaseCtor(IMethodSymbol ctorSym, CLeaf inst)
        => _lowering.EmitExprStmt(_lowering.EmitCallToMethod(_lowering.RequireStructMember(ctorSym), new List<CLeaf> { inst }));

    void EmitClassInitializers(CLeaf instance, INamedTypeSymbol owner)
        => ClassAbi.EmitInstanceFieldInitializers(
            _lowering.Builder,
            instance,
            _lowering.State.Program.ClassInitializers.Require(owner),
            VisitClassInitializerExpression);

    CLeaf VisitClassInitializerExpression(
        BoundClassFieldInitializer initializer)
    {
        using var bindingScope = _lowering.State.EnterBindingScope(
            initializer.Binding.Scope);
        using var genericScope =
            initializer.Binding.TypeParameterMap != null
            ? _lowering.State.Generics.EnterOverlayScope(
                initializer.Binding.TypeParameterMap)
            : null;
        return _lowering.VisitExpression(initializer.Operation);
    }

    CLeaf EmitClassInstanceMint(
        IObjectCreationOperation op,
        INamedTypeSymbol classTy,
        IMethodSymbol constructor)
    {
        var layout = _lowering.State.Aggregates.GetLayout(classTy);
        return ClassAbi.EmitMint(
            _lowering.Builder, layout,
            instance => AggregateAbi.DefaultInitialize(_lowering.Builder, instance, layout, _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName),
            instance =>
            {
                // CA-v2 M1: an explicit ctor runs the full chain (field inits + base call + body) inside
                // its own function; a class with no explicit ctor runs the implicit chain (field inits
                // derived->base) inline here.
                if (constructor == null || constructor.IsImplicitlyDeclared)
                {
                    ClassAbi.EmitImplicitCtorChain(
                        _lowering.Builder, instance, classTy,
                        _lowering.State.Program.ClassInitializers.Require,
                        VisitClassInitializerExpression, CallBaseCtor);
                    return;
                }
                // CW4: ctor args were staged positionally (IObjectCreationOperation.Arguments arrives in
                // SOURCE order for named args, the same Roslyn fact behind the w4 invocation fix — so
                // `new C(b: 2, a: 1)` silently swapped fields) with no ref/out guard or copy-back. Same
                // by-ordinal + guard + copy-back discipline as EmitUserMethodCall; the substituted symbol
                // equals ResolveStructMember's result minus its on-demand registration side effect, which
                // stays at the call (after argument evaluation, as before).
                var ctor = constructor;
        _owner.Externs.GuardRefOutArguments(op.Arguments, ctor);
                var ctorArgs = new List<CLeaf> { instance };
        var ctorPrepared = _owner.Externs.MarshalArgumentsByOrdinal(op.Arguments, ctor, ctorArgs);
                _lowering.EmitExprStmt(_lowering.EmitCallToMethod(
                    _lowering.RequireStructMember(constructor), ctorArgs));
        _owner.Externs.EmitRefOutCopyBack(op.Arguments, ctor, 0, ctorPrepared);
            },
            instance => _lowering.EmitAggregateObjectInitializer(instance, layout, op.Initializer),
            TypeObjWrite(classTy));
    }

    /// <summary>CA-v2b-1: the bundle[0]=typeobj write action for a minted concrete class. We are AT a mint
    /// site, so the class IS minted — a missing registry entry means the Phase-1 minted-class census and
    /// the emitter disagree (typically a `new T()` whose concrete type is never constructed directly).
    /// Skipping the write would leave bundle[0] null and every downstream `is`/`as`/virtual dispatch
    /// silently mis-answering (the GenBoxFactoryIdentity family) — throw instead (Phase-A census-hole
    /// sensor, loud over silent).</summary>
    System.Action<CLeaf> TypeObjWrite(INamedTypeSymbol classTy)
    {
        var tv = _lowering.State.ClassTypes.TryGetTypeObjVar(classTy);
        if (tv == null)
            throw new System.NotSupportedException(
                $"'{classTy.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is minted here but is not in the compile-time minted-class census "
                + "even though the closed-specialization census must contain every live mint. This is a compiler bug; report the "
                + "instantiation chain and source location. Emission stopped before producing a class bundle without runtime type identity.");
        return inst => AggregateAbi.WriteSlot(_lowering.Builder, inst, 0, _lowering.LoadField(tv, StorageTypes.String));
    }

    /// <summary>`new T()` (kind-level census gap, 2026-07-11): monomorphization has substituted T to a
    /// concrete type in the type-param map, so this mints exactly as `new ConcreteType()` would — a v1
    /// class bundle (implicit parameterless ctor chain), a default-initialized struct aggregate, or a
    /// primitive/SDK default. `new()` is always parameterless; only an object initializer can follow.</summary>
    internal CLeaf VisitTypeParameterObjectCreation(ITypeParameterObjectCreationOperation op)
    {
        var concrete = _lowering.ResolveType(op.Type);
        var udon = _lowering.GetStorageTypeName(concrete);
        if (concrete is INamedTypeSymbol classTy && TypeClassifier.IsUserClass(classTy))
        {
            var layout = _lowering.State.Aggregates.GetLayout(classTy);
            return ClassAbi.EmitMint(_lowering.Builder, layout,
                inst => AggregateAbi.DefaultInitialize(_lowering.Builder, inst, layout, _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName),
                inst => ClassAbi.EmitImplicitCtorChain(
                    _lowering.Builder, inst, classTy,
                    _lowering.State.Program.ClassInitializers.Require,
                    VisitClassInitializerExpression, CallBaseCtor),
                inst => _lowering.EmitAggregateObjectInitializer(inst, layout, op.Initializer),
                TypeObjWrite(classTy));
        }
        if (concrete is INamedTypeSymbol structTy && TypeClassifier.IsAggregateValue(structTy))
        {
            var inst = AggregateAbi.MintDefault(_lowering.Builder, _lowering.State.Aggregates.GetLayout(structTy),
                _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName);
            _lowering.EmitAggregateObjectInitializer(inst, _lowering.State.Aggregates.GetLayout(structTy), op.Initializer);
            return inst;
        }
        // Primitive / SDK value type: `new T()` is the type's default value.
        return DelegateInvocationLowerer.DefaultConst(_lowering.Builder, new StorageType(udon));
    }

    /// <summary>`new { X = a, Y = b }` (kind-level census gap, 2026-07-11): an anonymous type is an
    /// immutable value-shaped aggregate — allocate the object[] and write each initializer value to its
    /// property's slot (declaration order = layout order). Member reads route through the ordinary
    /// aggregate property path once IsAggregateValue admits anonymous types.</summary>
    internal CLeaf VisitAnonymousObjectCreation(IAnonymousObjectCreationOperation op)
    {
        var anonTy = (INamedTypeSymbol)op.Type;
        var layout = _lowering.State.Aggregates.GetLayout(anonTy);
        var inst = AggregateAbi.Allocate(_lowering.Builder, layout.SlotCount);
        foreach (var init in op.Initializers)
        {
            if (init is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation propRef } sa
                && layout.TryGetIndex(propRef.Property, out var idx))
                AggregateAbi.WriteSlot(_lowering.Builder, inst, idx, _lowering.VisitExpression(sa.Value));
        }
        return inst;
    }

    internal CLeaf VisitObjectCreation(IObjectCreationOperation op)
    {
        var constructor = op.Constructor;
        if (constructor != null)
            constructor = _lowering.RequireBoundCallSite(
                op, CallableSiteKind.Constructor, constructor).Callable.Site.Target;

        var resultType = _lowering.GetStorageTypeName(op.Type);
        var concreteType = _lowering.ResolveType(op.Type);

        // UdonSharpBehaviour subclasses cannot be instantiated at runtime —
        // Udon VM has no heap allocation for user-defined types.
        // Emit a diagnostic error instead of generating invalid UASM.
        if (!concreteType.IsValueType
            && concreteType is INamedTypeSymbol namedCtor
            && ExternResolver.IsUdonSharpBehaviour(namedCtor))
        {
            var loc = op.Syntax.GetLocation();
            var lineSpan = loc.GetLineSpan();
            _lowering.Diagnostics.Add(new EmitDiagnostic
            {
                Severity = "Error",
                Message = $"Cannot instantiate user-defined type '{op.Type.Name}' with 'new'. "
                        + "Udon VM does not support runtime object allocation for user-defined types. "
                        + "UdonSharpBehaviour instances must be placed in the scene.",
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Character = lineSpan.StartLinePosition.Character + 1,
            });
            return _lowering.Const(null, new StorageType(resultType));
        }

        // Class ABI v1 (CA-M1): a supported user class mints via the single ClassAbi bundle sequence. An
        // unsupported class (record / non-Object base / extern-backed foreign) already threw at the resultType
        // GetStorageTypeName above (B79); nothing unsupported lands here.
        if (concreteType is INamedTypeSymbol classTy && TypeClassifier.IsUserClass(classTy))
            return EmitClassInstanceMint(op, classTy, constructor);

        // Parameterless struct ctor. A user struct used AS A VALUE (e.g. `_field = new V()`, `Foo(new V())`)
        // must allocate + default-init a fresh object[]; the local-declaration path already does this, but
        // other contexts reach here. SDK value types fall through to the null placeholder.
        if (op.Arguments.Length == 0 && concreteType.IsValueType && op.Initializer == null)
            return concreteType is INamedTypeSymbol structTy && TypeClassifier.IsAggregateValue(structTy)
                ? AggregateAbi.MintDefault(_lowering.Builder, _lowering.State.Aggregates.GetLayout(structTy),
                    _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName)
                : _lowering.Const(null, new StorageType(resultType));

        // Constant folding: struct ctor with all-constant args
        if (op.Type.IsValueType && op.Initializer == null && op.Arguments.Length > 0
            && op.Arguments.All(a => a.Value.ConstantValue.HasValue)
            && ConstFoldableStructTypes.Contains(resultType))
        {
            var value = TryConstructAtCompileTime(resultType, op.Arguments);
            if (value != null)
                return _lowering.LoadField(_lowering.State.Storage.DeclareStructConst(new StorageType(resultType), value), new StorageType(resultType));
        }

        // User struct with a user-defined ctor, used AS A VALUE (e.g. an operator body `return new V(x,y)`):
        // allocate + default-init the object[] and run the registered ctor on it, like the local-declaration
        // path. The SDK extern-ctor path below is only for SDK value types (Vector3, …) — for a user struct it
        // would emit a bogus SystemObjectArray.__ctor__<args>__ extern that the validator rejects. (diff-fuzz w3)
        if (op.Type.IsValueType && op.Arguments.Length > 0
            && op.Type is INamedTypeSymbol userStruct && TypeClassifier.IsUserStruct(userStruct)
            && constructor != null)
        {
            var layout = _lowering.State.Aggregates.GetLayout(userStruct);
            var slot = _lowering.State.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
            _lowering.EmitAssign(slot, AggregateAbi.Allocate(_lowering.Builder, layout.Count));
            AggregateAbi.DefaultInitialize(_lowering.Builder, _lowering.SlotRef(slot), layout, _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName);
            // CW4: same by-ordinal + ref/out discipline as the class mint arm (EmitClassInstanceMint).
            var structCtor = constructor;
            _owner.Externs.GuardRefOutArguments(op.Arguments, structCtor);
            var ctorArgs = new List<CLeaf> { _lowering.SlotRef(slot) };
            var ctorPrepared = _owner.Externs.MarshalArgumentsByOrdinal(op.Arguments, structCtor, ctorArgs);
            _lowering.EmitExprStmt(_lowering.EmitCallToMethod(
                _lowering.RequireStructMember(constructor), ctorArgs));
            _owner.Externs.EmitRefOutCopyBack(op.Arguments, structCtor, 0, ctorPrepared);
            // ctor + object-initializer combo (`new V(1,2) { Y = 3 }`): apply the initializer AFTER the
            // ctor runs, same order C# gives the fields (roadmap B41 (d)).
            _lowering.EmitAggregateObjectInitializer(_lowering.SlotRef(slot), layout, op.Initializer);
            return _lowering.SlotRef(slot);
        }

        // User struct / tuple object initializer with NO ctor args (`new V { X = 1 }`): allocate +
        // default-init the object[] like the local-declaration path, then apply the initializer via
        // layout-INDEX writes. The SDK extern initializer path below assumes a native per-field setter extern (SDK
        // value types like Vector3), which object[]-emulated aggregates don't have (roadmap B41).
        if (op.Arguments.Length == 0 && op.Type.IsValueType && op.Initializer != null
            && op.Type is INamedTypeSymbol aggInitType && TypeClassifier.IsAggregateValue(aggInitType))
        {
            var layout = _lowering.State.Aggregates.GetLayout(aggInitType);
            var aggVal = AggregateAbi.MintDefault(_lowering.Builder, layout, _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName);
            _lowering.EmitAggregateObjectInitializer(aggVal, layout, op.Initializer);
            return aggVal;
        }

        CLeaf resultVal;
        if (op.Arguments.Length == 0 && op.Type.IsValueType)
        {
            // Struct with initializer but no ctor args: need a mutable temp
            var resultSlot = _lowering.State.Builder.AllocScratch(new StorageType(resultType));
            _lowering.EmitAssign(resultSlot, _lowering.Const(null, new StorageType(resultType)));
            resultVal = _lowering.SlotRef(resultSlot);
        }
        else
        {
            // Evaluate all args first. CW13: this is the same extern boundary as EmitExternMethodCall's
            // argument loop — mirror its N-R1 per-argument check (an ndim bundle bound to an object-typed
            // ctor param would silently become the wrapper payload, e.g. new DataToken(ndim)).
            var argVals = new List<CLeaf>();
            for (int i = 0; i < op.Arguments.Length; i++)
            {
                if (NdimArrayAbi.IsNdimArray(LoweringServices.UnwrapConversions(op.Arguments[i].Value).Type))
                    throw new System.NotSupportedException(ExternResolver.MultidimExternArgMessage);
                argVals.Add(_lowering.VisitExpression(op.Arguments[i].Value));
            }
            var paramTypes = op.Arguments.Select(a => _lowering.GetStorageTypeName(a.Value.Type)).ToArray();
            resultVal = _lowering.ExternCall(
                UdonAbiKey.Constructor(resultType, paramTypes, resultType),
                argVals,
                new StorageType(resultType));
        }

        // Object initializer: new T { Prop = val, ... }
        if (op.Initializer != null)
        {
            foreach (var init in op.Initializer.Initializers)
            {
                // CW27 polarity: the SDK path lowers members to native setter externs — anything else
                // (nested member initializers, collection adds) has no extern lowering; loud, not dropped.
                if (init is not ISimpleAssignmentOperation assign)
                    throw new System.NotSupportedException(
                        $"Object initializer member '{init.Syntax}' ({init.Kind}) on '{op.Type.Name}' is not "
                        + "supported: an SDK type lowers only simple member assignments (a nested member "
                        + "initializer has no per-field extern path). Assign the member a whole value instead.");
                var valueVal = _lowering.VisitExpression(assign.Value);
                EmitMemberSet(resultVal, assign.Target, valueVal);
            }
        }

        return resultVal;
    }

    void EmitMemberSet(CLeaf instanceVal, IOperation target, CLeaf valueVal)
    {
        if (target is IFieldReferenceOperation fieldRef && fieldRef.Field.ContainingType.IsValueType)
        {
            var containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(fieldRef.Field.ContainingType, fieldRef.Instance?.Type, fieldRef.Field.Name));
            var valueType = _lowering.GetStorageTypeName(fieldRef.Field.Type);
            var sig = _lowering.State.BoundAbi.RequireFieldSetter(
                containingType, fieldRef.Field.Name, valueType);
            _lowering.EmitExternVoid(sig, new List<CLeaf> { instanceVal, valueVal });
        }
        else if (target is IPropertyReferenceOperation propRef)
        {
            var containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(propRef.Property.ContainingType, propRef.Instance?.Type, propRef.Property.Name));
            var valueType = _lowering.GetStorageTypeName(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                var externArgs = new List<CLeaf>();
                externArgs.Add(instanceVal);
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    externArgs.Add(_lowering.VisitExpression(arg.Value));
                    indexTypes.Add(_lowering.GetStorageTypeName(arg.Value.Type));
                }
                externArgs.Add(valueVal);
                // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                _lowering.EmitExternVoid(_lowering.State.BoundAbi.RequireIndexerSetter(
                        containingType, propRef.Property.MetadataName, indexTypes, valueType),
                    externArgs);
            }
            else
            {
                _lowering.EmitExternVoid(
                    _lowering.State.BoundAbi.RequirePropertySetter(
                        containingType, propRef.Property.Name, valueType),
                    new List<CLeaf> { instanceVal, valueVal });
            }
        }
        else if (target is IFieldReferenceOperation fieldRef2)
        {
            // Non-struct field assignment (class fields via SetProgramVariable or direct)
            _lowering.EmitStoreField(_lowering.State.SourceStorageName(fieldRef2.Field), valueVal);
        }
        else
        {
            // CW27 polarity: an initializer target this method cannot lower must be loud, not a no-op.
            throw new System.NotSupportedException(
                $"Object initializer target '{target.Syntax}' ({target.Kind}) cannot be lowered to an "
                + "extern member set. Assign the member in a separate statement after construction.");
        }
    }

    // ── Constant Folding Helpers ──

    static readonly Dictionary<string, string> UdonToClrTypeName = new()
    {
        ["UnityEngineVector2"] = "UnityEngine.Vector2, UnityEngine.CoreModule",
        ["UnityEngineVector3"] = "UnityEngine.Vector3, UnityEngine.CoreModule",
        ["UnityEngineVector4"] = "UnityEngine.Vector4, UnityEngine.CoreModule",
        ["UnityEngineQuaternion"] = "UnityEngine.Quaternion, UnityEngine.CoreModule",
        ["UnityEngineColor"] = "UnityEngine.Color, UnityEngine.CoreModule",
        ["UnityEngineColor32"] = "UnityEngine.Color32, UnityEngine.CoreModule",
        ["UnityEngineMatrix4x4"] = "UnityEngine.Matrix4x4, UnityEngine.CoreModule",
        ["UnityEngineRect"] = "UnityEngine.Rect, UnityEngine.CoreModule",
    };

    static Type ResolveClrType(string udonType)
    {
        if (!UdonToClrTypeName.TryGetValue(udonType, out var typeName))
            return null;
        return Type.GetType(typeName);
    }

    static object TryConstructAtCompileTime(string udonType, ImmutableArray<IArgumentOperation> args)
    {
        try
        {
            var clrType = ResolveClrType(udonType);
            if (clrType == null) return null;
            var ctorArgs = args.Select(a => Convert.ChangeType(
                a.Value.ConstantValue.Value, typeof(float))).ToArray();
            var ctorArgTypes = ctorArgs.Select(a => a.GetType()).ToArray();
            var ctor = clrType.GetConstructor(ctorArgTypes);
            return ctor?.Invoke(ctorArgs);
        }
        catch { return null; }
    }

    // B47: a user-defined COMPUTED (non-auto) static property — its getter has a real body to inline-call,
    // vs an auto-property whose getter reads a backing field. Mirrors UasmEmitter.IsComputedProperty (no
    // field on the containing type is associated with the property).
    static bool IsUserComputedStaticProperty(IPropertySymbol prop)
        => !prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));

    static object TryGetStaticPropertyValue(string udonType, string propertyName)
    {
        try
        {
            var clrType = ResolveClrType(udonType);
            if (clrType == null) return null;
            var prop = clrType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null);
        }
        catch { return null; }
    }

}
