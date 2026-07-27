using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// N-dimensional array support (design 2026-07-04, "N 次元配列（int[,] 系）"). §0: T[d0..dr-1] is an
/// object[1+r] bundle — [0] = typed flat backing (T[], row-major, length = Πdᵢ), [1..r] = boxed
/// dimension lengths. Every access Horner-flattens the N indices to one flat index and converges on
/// the EXISTING 1-D choke points (ResolveArrayIndex / EmitArrayElementSet / the Get/Set externs) via
/// the flat backing. §1: bounds are checked per-dimension; a violation LogErrors and reads
/// default(T) / skips the write (D-N1 deviation — C# throws per-dimension, Udon has no exceptions).
/// </summary>
/// <summary>Owns all lowering for the object-array-backed multidimensional array ABI.</summary>
internal sealed class NdimArrayLowerer
{
    readonly LoweringServices _lowering;

    internal NdimArrayLowerer(LoweringServices lowering)
        => _lowering = lowering ?? throw new System.ArgumentNullException(nameof(lowering));

    CoreBuilder _builder => _lowering.Builder;
    Compilation _compilation => _lowering.Compilation;
    LoweringState _state => _lowering.State;
    string GetArrayType(IArrayTypeSymbol type) => _lowering.GetArrayType(type);
    string GetArrayElemType(IArrayTypeSymbol type) => _lowering.GetArrayElemType(type);
    string GetStorageTypeName(ITypeSymbol type) => _lowering.GetStorageTypeName(type);
    CLeaf VisitExpression(IOperation operation) => _lowering.VisitExpression(operation);
    CLeaf EmitArrayDimension(IOperation operation) => _lowering.EmitArrayDimension(operation);
    void EmitAssign(int slot, CValue value) => _lowering.EmitAssign(slot, value);
    CSlotRef SlotRef(int slot) => _lowering.SlotRef(slot);
    CConst Const(object value, StorageType type) => _lowering.Const(value, type);
    CSlotRef ExternCall(UdonAbiKey key, List<CLeaf> args, StorageType type)
        => _lowering.ExternCall(key, args, type);
    void EmitExternVoid(UdonAbiKey key, List<CLeaf> args)
        => _lowering.EmitExternVoid(key, args);

    /// <summary>Fetch bundle[0] (the flat backing) as a value of its real rank-1 array type. The
    /// bundle stores it boxed as SystemObject; Udon unboxes on the typed COPY into a backing-typed
    /// scratch slot (same mechanism as the recursion-stack reload, CoreFlatOptimizer.ReloadValue).
    /// <paramref name="bundleVal"/> must already be a stable (single-assignment or slot-ref) leaf.</summary>
    internal CLeaf EmitNdimGetBacking(CLeaf bundleVal, IArrayTypeSymbol backingType)
    {
        var backingUdonType = GetArrayType(backingType);
        return NdimArrayAbi.ReadBacking(_builder, bundleVal, new StorageType(backingUdonType));
    }

    /// <summary>A fully-prepared N-dim element access: every index expression evaluated EXACTLY ONCE
    /// (B38 — a side-effecting index must not re-run for bounds-check vs. Horner-flatten vs.
    /// message), every dimension length fetched once, the AND-ed per-dimension in-bounds flag, and
    /// the Horner-flattened index (computed unconditionally — cheap arithmetic, only USED when
    /// in-bounds). Shared verbatim by read, write, and the ref/out prepare leg so a violation is
    /// detected identically regardless of access shape.</summary>
    internal NdimArrayAbi.AccessPlan PrepareNdimAccess(IOperation arrayRefOp, IReadOnlyList<IOperation> indexOps, IArrayTypeSymbol ndimType)
    {
        var indexes = new CLeaf[indexOps.Count];
        var bundle = VisitExpression(arrayRefOp);
        for (var dimension = 0;
             dimension < indexOps.Count;
             dimension++)
            indexes[dimension] =
                VisitExpression(indexOps[dimension]);
        return PrepareNdimAccess(
            bundle, indexes, ndimType);
    }

    internal NdimArrayAbi.AccessPlan PrepareNdimAccess(
        CLeaf bundle,
        IReadOnlyList<CLeaf> indexes,
        IArrayTypeSymbol ndimType)
    {
        int rank = ndimType.Rank;
        if (indexes.Count != rank)
            throw new System.NotSupportedException(
                $"A rank-{rank} array requires {rank} indices, "
                + $"but {indexes.Count} were supplied.");
        var backingType =
            NdimArrayAbi.BackingType(_compilation, ndimType);

        var bundleSlot = _state.Builder.AllocScratch(
            new StorageType(NdimArrayAbi.BundleUdonType));
        EmitAssign(bundleSlot, bundle);
        var bundleVal = SlotRef(bundleSlot);

        var idxSlots = new int[rank];
        for (int d = 0; d < rank; d++)
        {
            if (indexes[d].Type != StorageTypes.Int32)
                throw new System.NotSupportedException(
                    "Multi-dimensional array indices must lower "
                    + $"to SystemInt32, not '{indexes[d].Type}'.");
            idxSlots[d] = _state.Builder.AllocScratch(
                StorageTypes.Int32);
            EmitAssign(idxSlots[d], indexes[d]);
        }

        var dimSlots = new int[rank];
        for (int d = 0; d < rank; d++)
        {
            dimSlots[d] = _state.Builder.AllocScratch(StorageTypes.Int32);
            EmitAssign(dimSlots[d], NdimArrayAbi.ReadDimLength(_builder, bundleVal,
                Const(NdimArrayAbi.DimSlotIndex(d), StorageTypes.Int32)));
        }

        var inBounds = NdimArrayAbi.BuildInBounds(_builder, idxSlots, dimSlots);

        // Horner: ((i0*d1+i1)*d2+i2)...  — always computed; only USED when inBounds (an OOB flat
        // index is never fed to a Get/Set, so a garbage value here is harmless).
        var flatIndex = NdimArrayAbi.BuildFlatIndex(_builder, idxSlots, dimSlots);

        return new NdimArrayAbi.AccessPlan(bundleVal, inBounds, flatIndex, backingType, idxSlots, dimSlots);
    }

    /// <summary>Shared in-bounds Get from an already-prepared plan: default(T) pre-init, in-bounds
    /// branch does the real Horner-flattened Get on the flat backing (the EXISTING 1-D Get choke
    /// point); else branch LogErrors (D-N1). Used by both the plain element read and the ref/out
    /// read leg so a violation is reported identically either way.</summary>
    internal CLeaf EmitNdimReadFromPlan(IArrayElementReferenceOperation ae, NdimArrayAbi.AccessPlan plan, string elemUdonType)
    {
        var backingUdonType = GetArrayType(plan.BackingType);
        var backingElemUdonType = GetArrayElemType(plan.BackingType);
        return NdimArrayAbi.ReadFromPlan(_builder, plan, new StorageType(elemUdonType), new StorageType(backingUdonType), new StorageType(backingElemUdonType),
            ae.Syntax.ToString(), type => InvocationHandler.DefaultConst(_builder, new StorageType(type)));
    }

    /// <summary>Shared in-bounds Set from an already-prepared plan: in-bounds branch Sets on the flat
    /// backing (the EXISTING 1-D Set choke point); else branch LogErrors and SKIPS the write (D-N1 —
    /// no partial state, the write either fully happens or not at all).</summary>
    internal void EmitNdimWriteFromPlan(IArrayElementReferenceOperation ae, NdimArrayAbi.AccessPlan plan, CLeaf value)
    {
        var backingUdonType = GetArrayType(plan.BackingType);
        var backingElemUdonType = GetArrayElemType(plan.BackingType);
        NdimArrayAbi.WriteFromPlan(_builder, plan, value, new StorageType(backingUdonType), new StorageType(backingElemUdonType),
            ae.Syntax.ToString());
    }

    /// <summary>N-dim element READ (§1/§2). Struct/tuple elements are deep-cloned on the way out,
    /// mirroring ArrayHandler.VisitArrayElementReference's rank-1 value-copy semantics.</summary>
    internal CLeaf EmitNdimElementRead(IArrayElementReferenceOperation ae)
    {
        var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
        var elemUdonType = GetStorageTypeName(ndimType.ElementType);
        var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
        var resultLeaf = EmitNdimReadFromPlan(ae, plan, elemUdonType);
        return ndimType.ElementType is INamedTypeSymbol elemAggT && _lowering.IsAggregateValue(elemAggT)
            ? AggregateAbi.DeepClone(_builder, resultLeaf, elemAggT, _state.Aggregates.GetLayout) : resultLeaf;
    }

    /// <summary>N-dim element WRITE prepare (mirrors LoweringServices.PrepareArrayElementSet's rank-1
    /// legs-now/value-later split): the array reference and every index are evaluated NOW (C#
    /// evaluates an lvalue's component expressions before the RHS), the store itself deferred to the
    /// returned closure so callers control exactly when the value is materialized relative to other
    /// evaluation (matches the existing array-element write-site ordering).</summary>
    internal System.Action<CLeaf> PrepareNdimElementSet(IArrayElementReferenceOperation ae)
    {
        var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
        var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
        return value => EmitNdimWriteFromPlan(ae, plan, value);
    }

    /// <summary>N-dim ref/out prepare leg (lifts TryPrepareRefOutArg's multi-index exclusion): the
    /// read/store closures share ONE NdimAccessPlan (indices/dims/bounds/backing evaluated once),
    /// mirroring the rank-1 array-element arm's (arrayVal, indexVal) caching.</summary>
    internal (System.Func<CLeaf> read, System.Action<CLeaf> store) PrepareNdimRefOutArg(IArrayElementReferenceOperation ae)
    {
        var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
        var elemUdonType = GetStorageTypeName(ndimType.ElementType);
        var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
        return (() => EmitNdimReadFromPlan(ae, plan, elemUdonType), value => EmitNdimWriteFromPlan(ae, plan, value));
    }

    // ── Creation + initializer (§2) ──

    /// <summary>`new T[a,b,…]` (+ optional `{{1,2},{3,4}}` initializer): mint the object[1+r] bundle
    /// — flat backing allocated at total size (Πdᵢ), boxed dimension lengths at [1..r]. A nested
    /// initializer is recursively flattened to one row-major leaf list and written sequentially onto
    /// the flat backing (Horner order falls out of Roslyn's own nesting order — no flattening math
    /// needed here, unlike element access).</summary>
    internal CLeaf EmitNdimArrayCreation(IArrayCreationOperation op)
    {
        var ndimType = (IArrayTypeSymbol)op.Type;
        int rank = ndimType.Rank;
        var backingType = NdimArrayAbi.BackingType(_compilation, ndimType);
        var backingUdonType = GetArrayType(backingType);
        var elemUdonType = GetArrayElemType(backingType);
        var elemSym = ndimType.ElementType;
        bool aggElem = elemSym is INamedTypeSymbol && _lowering.IsAggregateValue(elemSym);

        var dimSlots = new int[rank];
        for (int d = 0; d < rank; d++)
        {
            dimSlots[d] = _state.Builder.AllocScratch(StorageTypes.Int32);
            EmitAssign(dimSlots[d], EmitArrayDimension(op.DimensionSizes[d]));
        }

        var totalSize = NdimArrayAbi.BuildTotalElementCount(_builder, dimSlots);
        var totalSlot = _state.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(totalSlot, totalSize);

        var backingSlot = _state.Builder.AllocScratch(new StorageType(backingUdonType));
        EmitAssign(backingSlot, ExternCall(UdonAbi.ArrayConstructor(backingUdonType),
            new List<CLeaf> { SlotRef(totalSlot) }, new StorageType(backingUdonType)));

        var bundleSlot = _state.Builder.AllocScratch(new StorageType(NdimArrayAbi.BundleUdonType));
        NdimArrayAbi.MintBundleToSlot(
            _builder, bundleSlot, backingSlot, dimSlots,
            BundleAbi.RuntimeTypeId(ndimType));

        if (op.Initializer != null)
        {
            var leaves = new List<IOperation>();
            NdimArrayAbi.FlattenInitializer(op.Initializer, leaves);
            for (int i = 0; i < leaves.Count; i++)
                EmitExternVoid(UdonAbi.ArraySet(backingUdonType, elemUdonType),
                    new List<CLeaf> { SlotRef(backingSlot), Const(i, StorageTypes.Int32), VisitExpression(leaves[i]) });
        }
        else if (aggElem)
        {
            // struct[]/tuple[] zero-init: each flat slot gets a fresh default struct (mirrors the rank-1 path
            // in ArrayHandler.VisitArrayCreation — `arr[i,j].field = x` must work on a freshly allocated array).
            var iSlot = _state.Builder.AllocScratch(StorageTypes.Int32);
            EmitAssign(iSlot, Const(0, StorageTypes.Int32));
            _builder.EmitWhile(
                () => ExternCall(UdonAbi.Int32LessThan,
                    new List<CLeaf> { SlotRef(iSlot), SlotRef(totalSlot) }, StorageTypes.Boolean),
                _ =>
                {
                    EmitExternVoid(UdonAbi.ArraySet(backingUdonType, elemUdonType),
                        new List<CLeaf> { SlotRef(backingSlot), SlotRef(iSlot),
                            AggregateAbi.MintDefault(_builder, _state.Aggregates.GetLayout((INamedTypeSymbol)elemSym),
                                _state.Aggregates.GetLayout, GetStorageTypeName) });
                    EmitAssign(iSlot, ExternCall(UdonAbi.Int32Add,
                        new List<CLeaf> { SlotRef(iSlot), Const(1, StorageTypes.Int32) }, StorageTypes.Int32));
                });
        }

        return SlotRef(bundleSlot);
    }

    // ── Length / GetLength / Rank / GetUpperBound (§2) ──

    /// <summary>`ndimArr.Length` — the FLAT BACKING's length (total element count), not the bundle
    /// wrapper's own length (1+r). Must be intercepted before the generic property-getter extern
    /// path: SystemObjectArray.__get_Length__SystemInt32 is a REAL, valid extern (the bundle IS a
    /// real object[]), so without this it would silently return 1+r instead of Πdᵢ.</summary>
    internal CLeaf EmitNdimLength(CLeaf bundleVal, IArrayTypeSymbol ndimType)
    {
        var backing = EmitNdimGetBacking(bundleVal, NdimArrayAbi.BackingType(_compilation, ndimType));
        return ExternCall(UdonAbi.SystemArrayLength, new List<CLeaf> { backing }, StorageTypes.Int32);
    }

    internal CLeaf EmitNdimLongLength(
        CLeaf bundleVal, IArrayTypeSymbol ndimType)
        => ExternCall(
            UdonAbiKey.Method(
                "SystemConvert", "ToInt64",
                new[] { "SystemInt32" }, "SystemInt64"),
            new List<CLeaf>
            {
                EmitNdimLength(bundleVal, ndimType)
            },
            StorageTypes.Int64);

    /// <summary>`ndimArr.GetLength(d)` — bundle[1+d] unboxed. <paramref name="dimArg"/> need not be a
    /// compile-time constant (design §2 allows an expression); when it is a Roslyn constant we still
    /// go through the same runtime bundle-index math (no special-cased fast path — a compile-time
    /// bounds proof / constant-fold optimization is explicitly out of scope, §1).</summary>
    internal CLeaf EmitNdimGetLength(
        CLeaf bundleVal, CLeaf dimArg,
        IArrayTypeSymbol ndimType)
        => EmitDimensionQuery(
            bundleVal, dimArg, ndimType,
            StorageTypes.Int32, length => length);

    /// <summary>`ndimArr.Rank` — the static rank is known at compile time from the declared type; no
    /// runtime code at all.</summary>
    internal CLeaf EmitNdimRank(IArrayTypeSymbol ndimType) => Const(ndimType.Rank, StorageTypes.Int32);

    /// <summary>`ndimArr.GetUpperBound(d)` = `GetLength(d) - 1`.</summary>
    internal CLeaf EmitNdimGetUpperBound(
        CLeaf bundleVal, CLeaf dimArg,
        IArrayTypeSymbol ndimType)
        => EmitDimensionQuery(
            bundleVal, dimArg, ndimType,
            StorageTypes.Int32,
            length => NdimArrayAbi.BuildUpperBound(
                _builder, length));

    internal CLeaf EmitNdimGetLongLength(
        CLeaf bundleVal, CLeaf dimArg,
        IArrayTypeSymbol ndimType)
        => EmitDimensionQuery(
            bundleVal, dimArg, ndimType,
            StorageTypes.Int64,
            length => ExternCall(
                UdonAbiKey.Method(
                    "SystemConvert", "ToInt64",
                    new[] { "SystemInt32" },
                    "SystemInt64"),
                new List<CLeaf> { length },
                StorageTypes.Int64));

    internal CLeaf EmitNdimGetLowerBound(
        CLeaf dimArg, IArrayTypeSymbol ndimType)
        => EmitDimensionQuery(
            null, dimArg, ndimType,
            StorageTypes.Int32,
            _ => Const(0, StorageTypes.Int32));

    CLeaf EmitDimensionQuery(
        CLeaf bundleVal, CLeaf dimArg,
        IArrayTypeSymbol ndimType,
        StorageType resultType,
        System.Func<CLeaf, CLeaf> emitResult)
    {
        var dimension = _builder.AllocScratch(
            StorageTypes.Int32);
        EmitAssign(dimension, dimArg);
        var nonNegative = ExternCall(
            UdonAbiKey.Method(
                "SystemInt32", "op_GreaterThanOrEqual",
                new[] { "SystemInt32", "SystemInt32" },
                "SystemBoolean"),
            new List<CLeaf>
            {
                SlotRef(dimension),
                Const(0, StorageTypes.Int32)
            },
            StorageTypes.Boolean);
        var belowRank = ExternCall(
            UdonAbi.Int32LessThan,
            new List<CLeaf>
            {
                SlotRef(dimension),
                Const(ndimType.Rank, StorageTypes.Int32)
            },
            StorageTypes.Boolean);
        var valid = ExternCall(
            UdonAbi.BooleanConditionalAnd,
            new List<CLeaf>
            {
                nonNegative, belowRank
            },
            StorageTypes.Boolean);
        var result = _builder.AllocScratch(resultType);
        EmitAssign(
            result,
            InvocationHandler.DefaultConst(
                _builder, resultType));
        _builder.EmitIf(
            valid,
            _ =>
            {
                CLeaf length =
                    Const(0, StorageTypes.Int32);
                if (bundleVal != null)
                {
                    var slotIndex =
                        NdimArrayAbi
                            .BuildRuntimeDimSlotIndex(
                                _builder,
                                SlotRef(dimension));
                    length = NdimArrayAbi.ReadDimLength(
                        _builder, bundleVal, slotIndex);
                }
                EmitAssign(
                    result, emitResult(length));
            },
            _ => EmitExternVoid(
                UdonAbi.DebugLogError,
                new List<CLeaf>
                {
                    Const(
                        $"USugar: dimension is outside "
                        + $"rank-{ndimType.Rank} array.",
                        StorageTypes.String)
                }));
        return SlotRef(result);
    }

    internal CLeaf EmitNdimGetValue(
        CLeaf bundle,
        IReadOnlyList<CLeaf> indexes,
        IArrayTypeSymbol ndimType)
    {
        var elementType = GetStorageTypeName(
            ndimType.ElementType);
        var plan = PrepareNdimAccess(
            bundle, indexes, ndimType);
        CLeaf value = NdimArrayAbi.ReadFromPlan(
            _builder, plan,
            new StorageType(elementType),
            new StorageType(GetArrayType(plan.BackingType)),
            new StorageType(GetArrayElemType(plan.BackingType)),
            "Array.GetValue", type =>
                InvocationHandler.DefaultConst(
                    _builder, new StorageType(type)));
        if (ndimType.ElementType is INamedTypeSymbol aggregate
            && _lowering.IsAggregateValue(aggregate))
            value = AggregateAbi.DeepClone(
                _builder, value, aggregate,
                _state.Aggregates.GetLayout);
        var boxed = _builder.AllocScratch(
            StorageTypes.Object);
        EmitAssign(boxed, value);
        return SlotRef(boxed);
    }

    internal void EmitNdimSetValue(
        CLeaf bundle,
        CLeaf value,
        IReadOnlyList<CLeaf> indexes,
        IArrayTypeSymbol ndimType)
    {
        var plan = PrepareNdimAccess(
            bundle, indexes, ndimType);
        NdimArrayAbi.WriteFromPlan(
            _builder, plan, value,
            new StorageType(GetArrayType(plan.BackingType)),
            new StorageType(GetArrayElemType(plan.BackingType)),
            "Array.SetValue");
    }

    internal void EmitLinearCopy(
        CLeaf source,
        IArrayTypeSymbol sourceType,
        CLeaf sourceIndex,
        CLeaf destination,
        IArrayTypeSymbol destinationType,
        CLeaf destinationIndex,
        CLeaf length)
    {
        var sourceLinear = LinearArray(
            source, sourceType);
        var destinationLinear = LinearArray(
            destination, destinationType);
        if (sourceLinear.ElementType
                != destinationLinear.ElementType
            && destinationLinear.ElementType
                != StorageTypes.Object)
            throw new System.NotSupportedException(
                $"Array.Copy from '{sourceType}' to "
                + $"'{destinationType}' requires element "
                + "conversion, which the Udon array ABI "
                + "cannot perform atomically.");

        var sourceLength = ExternCall(
            UdonAbi.SystemArrayLength,
            new List<CLeaf>
                { sourceLinear.Array },
            StorageTypes.Int32);
        var destinationLength = ExternCall(
            UdonAbi.SystemArrayLength,
            new List<CLeaf>
                { destinationLinear.Array },
            StorageTypes.Int32);
        var sourceEnd = ExternCall(
            UdonAbi.Int32Add,
            new List<CLeaf>
                { sourceIndex, length },
            StorageTypes.Int32);
        var destinationEnd = ExternCall(
            UdonAbi.Int32Add,
            new List<CLeaf>
                { destinationIndex, length },
            StorageTypes.Int32);

        CLeaf NonNegative(CLeaf value)
            => ExternCall(
                UdonAbiKey.Method(
                    "SystemInt32",
                    "op_GreaterThanOrEqual",
                    new[]
                    {
                        "SystemInt32",
                        "SystemInt32"
                    },
                    "SystemBoolean"),
                new List<CLeaf>
                {
                    value,
                    Const(0, StorageTypes.Int32)
                },
                StorageTypes.Boolean);

        CLeaf AtMost(CLeaf value, CLeaf maximum)
            => ExternCall(
                UdonAbiKey.Method(
                    "SystemInt32",
                    "op_LessThanOrEqual",
                    new[]
                    {
                        "SystemInt32",
                        "SystemInt32"
                    },
                    "SystemBoolean"),
                new List<CLeaf> { value, maximum },
                StorageTypes.Boolean);

        CLeaf valid = NonNegative(sourceIndex);
        foreach (var condition in new[]
                 {
                     NonNegative(destinationIndex),
                     NonNegative(length),
                     AtMost(sourceEnd, sourceLength),
                     AtMost(destinationEnd,
                         destinationLength)
                 })
            valid = ExternCall(
                UdonAbi.BooleanConditionalAnd,
                new List<CLeaf>
                    { valid, condition },
                StorageTypes.Boolean);

        _builder.EmitIf(
            valid,
            _ =>
            {
                var temporary = ExternCall(
                    UdonAbi.ArrayConstructor(
                        sourceLinear.ArrayType.Name),
                    new List<CLeaf> { length },
                    sourceLinear.ArrayType);
                var index = _builder.AllocScratch(
                    StorageTypes.Int32);
                _builder.EmitFor(
                    __ => EmitAssign(
                        index,
                        Const(0, StorageTypes.Int32)),
                    () => ExternCall(
                        UdonAbi.Int32LessThan,
                        new List<CLeaf>
                        {
                            SlotRef(index), length
                        },
                        StorageTypes.Boolean),
                    __ => EmitAssign(
                        index,
                        ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf>
                            {
                                SlotRef(index),
                                Const(
                                    1,
                                    StorageTypes.Int32)
                            },
                            StorageTypes.Int32)),
                    __ =>
                    {
                        var readIndex = ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf>
                            {
                                sourceIndex,
                                SlotRef(index)
                            },
                            StorageTypes.Int32);
                        CLeaf item = ExternCall(
                            UdonAbi.ArrayGet(
                                sourceLinear.ArrayType.Name,
                                sourceLinear.ElementType.Name),
                            new List<CLeaf>
                            {
                                sourceLinear.Array,
                                readIndex
                            },
                            sourceLinear.ElementType);
                        if (sourceType.ElementType
                                is INamedTypeSymbol aggregate
                            && _lowering
                                .IsAggregateValue(aggregate))
                            item = AggregateAbi.DeepClone(
                                _builder, item, aggregate,
                                _state.Aggregates.GetLayout);
                        EmitExternVoid(
                            UdonAbi.ArraySet(
                                sourceLinear.ArrayType.Name,
                                sourceLinear.ElementType.Name),
                            new List<CLeaf>
                            {
                                temporary,
                                SlotRef(index), item
                            });
                    });
                _builder.EmitFor(
                    __ => EmitAssign(
                        index,
                        Const(0, StorageTypes.Int32)),
                    () => ExternCall(
                        UdonAbi.Int32LessThan,
                        new List<CLeaf>
                        {
                            SlotRef(index), length
                        },
                        StorageTypes.Boolean),
                    __ => EmitAssign(
                        index,
                        ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf>
                            {
                                SlotRef(index),
                                Const(
                                    1,
                                    StorageTypes.Int32)
                            },
                            StorageTypes.Int32)),
                    __ =>
                    {
                        var writeIndex = ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf>
                            {
                                destinationIndex,
                                SlotRef(index)
                            },
                            StorageTypes.Int32);
                        var item = ExternCall(
                            UdonAbi.ArrayGet(
                                sourceLinear.ArrayType.Name,
                                sourceLinear.ElementType.Name),
                            new List<CLeaf>
                            {
                                temporary,
                                SlotRef(index)
                            },
                            sourceLinear.ElementType);
                        EmitExternVoid(
                            UdonAbi.ArraySet(
                                destinationLinear.ArrayType.Name,
                                destinationLinear
                                    .ElementType.Name),
                            new List<CLeaf>
                            {
                                destinationLinear.Array,
                                writeIndex, item
                            });
                    });
            },
            _ => EmitExternVoid(
                UdonAbi.DebugLogError,
                new List<CLeaf>
                {
                    Const(
                        "USugar: Array.Copy range is "
                        + "outside the logical array.",
                        StorageTypes.String)
                }));
    }

    (CLeaf Array, StorageType ArrayType,
        StorageType ElementType) LinearArray(
        CLeaf value, IArrayTypeSymbol type)
    {
        var linearType = NdimArrayAbi.IsNdimArray(type)
            ? NdimArrayAbi.BackingType(
                _compilation, type)
            : type;
        var arrayStorage = new StorageType(
            GetArrayType(linearType));
        var elementStorage = new StorageType(
            GetArrayElemType(linearType));
        return (
            NdimArrayAbi.IsNdimArray(type)
                ? EmitNdimGetBacking(
                    value, linearType)
                : value,
            arrayStorage, elementStorage);
    }

    internal CLeaf EmitNdimClone(
        CLeaf bundle,
        IArrayTypeSymbol ndimType)
    {
        var rank = ndimType.Rank;
        var backingType =
            NdimArrayAbi.BackingType(_compilation, ndimType);
        var backingStorage = new StorageType(
            GetArrayType(backingType));
        var elementStorage = new StorageType(
            GetArrayElemType(backingType));
        var source = EmitNdimGetBacking(
            bundle, backingType);
        var length = ExternCall(
            UdonAbi.SystemArrayLength,
            new List<CLeaf> { source },
            StorageTypes.Int32);
        var destination = ExternCall(
            UdonAbi.ArrayConstructor(backingStorage.Name),
            new List<CLeaf> { length }, backingStorage);
        var index = _builder.AllocScratch(
            StorageTypes.Int32);
        _builder.EmitFor(
            _ => EmitAssign(
                index, Const(0, StorageTypes.Int32)),
            () => ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf>
                {
                    SlotRef(index), length
                },
                StorageTypes.Boolean),
            _ => EmitAssign(
                index,
                ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf>
                    {
                        SlotRef(index),
                        Const(1, StorageTypes.Int32)
                    },
                    StorageTypes.Int32)),
            _ =>
            {
                CLeaf value = ExternCall(
                    UdonAbi.ArrayGet(
                        backingStorage.Name,
                        elementStorage.Name),
                    new List<CLeaf>
                    {
                        source, SlotRef(index)
                    },
                    elementStorage);
                if (ndimType.ElementType
                    is INamedTypeSymbol aggregate
                    && _lowering.IsAggregateValue(aggregate))
                    value = AggregateAbi.DeepClone(
                        _builder, value, aggregate,
                        _state.Aggregates.GetLayout);
                EmitExternVoid(
                    UdonAbi.ArraySet(
                        backingStorage.Name,
                        elementStorage.Name),
                    new List<CLeaf>
                    {
                        destination, SlotRef(index), value
                    });
            });

        var dimSlots = new int[rank];
        for (var dimension = 0;
             dimension < rank;
             dimension++)
        {
            dimSlots[dimension] = _builder.AllocScratch(
                StorageTypes.Int32);
            EmitAssign(
                dimSlots[dimension],
                NdimArrayAbi.ReadDimLength(
                    _builder, bundle,
                    Const(
                        NdimArrayAbi.DimSlotIndex(dimension),
                        StorageTypes.Int32)));
        }
        var backingSlot = _builder.AllocScratch(
            backingStorage);
        EmitAssign(backingSlot, destination);
        var cloneSlot = _builder.AllocScratch(
            StorageTypes.ObjectArray);
        NdimArrayAbi.MintBundleToSlot(
            _builder, cloneSlot, backingSlot, dimSlots,
            BundleAbi.RuntimeTypeId(ndimType));
        return SlotRef(cloneSlot);
    }

    /// <summary>N-R4: every OTHER Array instance member (Clone/CopyTo/SetValue/GetValue/Equals/…) is a
    /// loud reject on a Rank>1 receiver. Not "the existing unsupported-extern path" (audit-refuted —
    /// SystemObjectArray.__Clone__SystemObject IS a real, valid extern; letting it through would
    /// silently shallow-copy the 3-element bundle WRAPPER, aliasing the same flat backing between the
    /// "clone" and the original instead of copying elements).</summary>
}
