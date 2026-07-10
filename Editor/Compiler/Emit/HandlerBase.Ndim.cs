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
public abstract partial class HandlerBase
{
    /// <summary>Fetch bundle[0] (the flat backing) as a value of its real rank-1 array type. The
    /// bundle stores it boxed as SystemObject; Udon unboxes on the typed COPY into a backing-typed
    /// scratch slot (same mechanism as the recursion-stack reload, CoreFlatOptimizer.ReloadValue).
    /// <paramref name="bundleVal"/> must already be a stable (single-assignment or slot-ref) leaf.</summary>
    protected CLeaf EmitNdimGetBacking(CLeaf bundleVal, IArrayTypeSymbol backingType)
    {
        var backingUdonType = GetArrayType(backingType);
        return NdimArrayAbi.ReadBacking(_builder, bundleVal, backingUdonType);
    }

    /// <summary>A fully-prepared N-dim element access: every index expression evaluated EXACTLY ONCE
    /// (B38 — a side-effecting index must not re-run for bounds-check vs. Horner-flatten vs.
    /// message), every dimension length fetched once, the AND-ed per-dimension in-bounds flag, and
    /// the Horner-flattened index (computed unconditionally — cheap arithmetic, only USED when
    /// in-bounds). Shared verbatim by read, write, and the ref/out prepare leg so a violation is
    /// detected identically regardless of access shape.</summary>
    protected NdimArrayAbi.AccessPlan PrepareNdimAccess(IOperation arrayRefOp, IReadOnlyList<IOperation> indexOps, IArrayTypeSymbol ndimType)
    {
        int rank = ndimType.Rank;
        var backingType = NdimArrayAbi.BackingType(_compilation, ndimType);

        var bundleSlot = _ctx.Builder.AllocScratch(NdimArrayAbi.BundleUdonType);
        EmitAssign(bundleSlot, VisitExpression(arrayRefOp));
        var bundleVal = SlotRef(bundleSlot);

        var idxSlots = new int[rank];
        for (int d = 0; d < rank; d++)
        {
            idxSlots[d] = _ctx.Builder.AllocScratch("SystemInt32");
            EmitAssign(idxSlots[d], VisitExpression(indexOps[d]));
        }

        var dimSlots = new int[rank];
        for (int d = 0; d < rank; d++)
        {
            dimSlots[d] = _ctx.Builder.AllocScratch("SystemInt32");
            EmitAssign(dimSlots[d], NdimArrayAbi.ReadDimLength(_builder, bundleVal,
                Const(NdimArrayAbi.DimSlotIndex(d), "SystemInt32")));
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
    protected CLeaf EmitNdimReadFromPlan(IArrayElementReferenceOperation ae, NdimArrayAbi.AccessPlan plan, string elemUdonType)
    {
        var backingUdonType = GetArrayType(plan.BackingType);
        var backingElemUdonType = GetArrayElemType(plan.BackingType);
        return NdimArrayAbi.ReadFromPlan(_builder, plan, elemUdonType, backingUdonType, backingElemUdonType,
            ae.Syntax.ToString(), type => InvocationHandler.DefaultConst(_builder, type));
    }

    /// <summary>Shared in-bounds Set from an already-prepared plan: in-bounds branch Sets on the flat
    /// backing (the EXISTING 1-D Set choke point); else branch LogErrors and SKIPS the write (D-N1 —
    /// no partial state, the write either fully happens or not at all).</summary>
    protected void EmitNdimWriteFromPlan(IArrayElementReferenceOperation ae, NdimArrayAbi.AccessPlan plan, CLeaf value)
    {
        var backingUdonType = GetArrayType(plan.BackingType);
        var backingElemUdonType = GetArrayElemType(plan.BackingType);
        NdimArrayAbi.WriteFromPlan(_builder, plan, value, backingUdonType, backingElemUdonType,
            ae.Syntax.ToString());
    }

    /// <summary>N-dim element READ (§1/§2). Struct/tuple elements are deep-cloned on the way out,
    /// mirroring ArrayHandler.VisitArrayElementReference's rank-1 value-copy semantics.</summary>
    protected CLeaf EmitNdimElementRead(IArrayElementReferenceOperation ae)
    {
        var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
        var elemUdonType = GetUdonType(ndimType.ElementType);
        var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
        var resultLeaf = EmitNdimReadFromPlan(ae, plan, elemUdonType);
        return ndimType.ElementType is INamedTypeSymbol elemAggT && EmitPolicy.IsAggregateType(elemAggT)
            ? AggregateAbi.DeepClone(_builder, resultLeaf, elemAggT, _ctx.Aggregates.GetLayout) : resultLeaf;
    }

    /// <summary>N-dim element WRITE prepare (mirrors HandlerBase.PrepareArrayElementSet's rank-1
    /// legs-now/value-later split): the array reference and every index are evaluated NOW (C#
    /// evaluates an lvalue's component expressions before the RHS), the store itself deferred to the
    /// returned closure so callers control exactly when the value is materialized relative to other
    /// evaluation (matches the existing array-element write-site ordering).</summary>
    protected System.Action<CLeaf> PrepareNdimElementSet(IArrayElementReferenceOperation ae)
    {
        var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
        var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
        return value => EmitNdimWriteFromPlan(ae, plan, value);
    }

    /// <summary>N-dim ref/out prepare leg (lifts TryPrepareRefOutArg's multi-index exclusion): the
    /// read/store closures share ONE NdimAccessPlan (indices/dims/bounds/backing evaluated once),
    /// mirroring the rank-1 array-element arm's (arrayVal, indexVal) caching.</summary>
    protected (System.Func<CLeaf> read, System.Action<CLeaf> store) PrepareNdimRefOutArg(IArrayElementReferenceOperation ae)
    {
        var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
        var elemUdonType = GetUdonType(ndimType.ElementType);
        var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
        return (() => EmitNdimReadFromPlan(ae, plan, elemUdonType), value => EmitNdimWriteFromPlan(ae, plan, value));
    }

    // ── Creation + initializer (§2) ──

    /// <summary>`new T[a,b,…]` (+ optional `{{1,2},{3,4}}` initializer): mint the object[1+r] bundle
    /// — flat backing allocated at total size (Πdᵢ), boxed dimension lengths at [1..r]. A nested
    /// initializer is recursively flattened to one row-major leaf list and written sequentially onto
    /// the flat backing (Horner order falls out of Roslyn's own nesting order — no flattening math
    /// needed here, unlike element access).</summary>
    protected CLeaf EmitNdimArrayCreation(IArrayCreationOperation op)
    {
        var ndimType = (IArrayTypeSymbol)op.Type;
        int rank = ndimType.Rank;
        var backingType = NdimArrayAbi.BackingType(_compilation, ndimType);
        var backingUdonType = GetArrayType(backingType);
        var elemUdonType = GetArrayElemType(backingType);
        var elemSym = ndimType.ElementType;
        bool aggElem = elemSym is INamedTypeSymbol && EmitPolicy.IsAggregateType(elemSym);

        var dimSlots = new int[rank];
        for (int d = 0; d < rank; d++)
        {
            dimSlots[d] = _ctx.Builder.AllocScratch("SystemInt32");
            EmitAssign(dimSlots[d], VisitExpression(op.DimensionSizes[d]));
        }

        var totalSize = NdimArrayAbi.BuildTotalElementCount(_builder, dimSlots);
        var totalSlot = _ctx.Builder.AllocScratch("SystemInt32");
        EmitAssign(totalSlot, totalSize);

        var backingSlot = _ctx.Builder.AllocScratch(backingUdonType);
        EmitAssign(backingSlot, ExternCall(ExternResolver.BuildArrayCtorSignature(backingUdonType),
            new List<CLeaf> { SlotRef(totalSlot) }, backingUdonType));

        var bundleSlot = _ctx.Builder.AllocScratch(NdimArrayAbi.BundleUdonType);
        NdimArrayAbi.MintBundleToSlot(_builder, bundleSlot, backingSlot, dimSlots);

        if (op.Initializer != null)
        {
            var leaves = new List<IOperation>();
            NdimArrayAbi.FlattenInitializer(op.Initializer, leaves);
            for (int i = 0; i < leaves.Count; i++)
                EmitExternVoid(ExternResolver.BuildArraySetSignature(backingUdonType, elemUdonType),
                    new List<CLeaf> { SlotRef(backingSlot), Const(i, "SystemInt32"), VisitExpression(leaves[i]) });
        }
        else if (aggElem)
        {
            // struct[]/tuple[] zero-init: each flat slot gets a fresh default struct (mirrors the rank-1 path
            // in ArrayHandler.VisitArrayCreation — `arr[i,j].field = x` must work on a freshly allocated array).
            var iSlot = _ctx.Builder.AllocScratch("SystemInt32");
            EmitAssign(iSlot, Const(0, "SystemInt32"));
            _builder.EmitWhile(
                () => ExternCall("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                    new List<CLeaf> { SlotRef(iSlot), SlotRef(totalSlot) }, "SystemBoolean"),
                _ =>
                {
                    EmitExternVoid(ExternResolver.BuildArraySetSignature(backingUdonType, elemUdonType),
                        new List<CLeaf> { SlotRef(backingSlot), SlotRef(iSlot),
                            AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout((INamedTypeSymbol)elemSym),
                                _ctx.Aggregates.GetLayout, GetUdonType) });
                    EmitAssign(iSlot, ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                        new List<CLeaf> { SlotRef(iSlot), Const(1, "SystemInt32") }, "SystemInt32"));
                });
        }

        return SlotRef(bundleSlot);
    }

    // ── Length / GetLength / Rank / GetUpperBound (§2) ──

    /// <summary>`ndimArr.Length` — the FLAT BACKING's length (total element count), not the bundle
    /// wrapper's own length (1+r). Must be intercepted before the generic property-getter extern
    /// path: SystemObjectArray.__get_Length__SystemInt32 is a REAL, valid extern (the bundle IS a
    /// real object[]), so without this it would silently return 1+r instead of Πdᵢ.</summary>
    protected CLeaf EmitNdimLength(CLeaf bundleVal, IArrayTypeSymbol ndimType)
    {
        var backing = EmitNdimGetBacking(bundleVal, NdimArrayAbi.BackingType(_compilation, ndimType));
        return ExternCall("SystemArray.__get_Length__SystemInt32", new List<CLeaf> { backing }, "SystemInt32");
    }

    /// <summary>`ndimArr.GetLength(d)` — bundle[1+d] unboxed. <paramref name="dimArg"/> need not be a
    /// compile-time constant (design §2 allows an expression); when it is a Roslyn constant we still
    /// go through the same runtime bundle-index math (no special-cased fast path — a compile-time
    /// bounds proof / constant-fold optimization is explicitly out of scope, §1).</summary>
    protected CLeaf EmitNdimGetLength(CLeaf bundleVal, CLeaf dimArg)
    {
        var plusOne = NdimArrayAbi.BuildRuntimeDimSlotIndex(_builder, dimArg);
        return NdimArrayAbi.ReadDimLength(_builder, bundleVal, plusOne);
    }

    /// <summary>`ndimArr.Rank` — the static rank is known at compile time from the declared type; no
    /// runtime code at all.</summary>
    protected CLeaf EmitNdimRank(IArrayTypeSymbol ndimType) => Const(ndimType.Rank, "SystemInt32");

    /// <summary>`ndimArr.GetUpperBound(d)` = `GetLength(d) - 1`.</summary>
    protected CLeaf EmitNdimGetUpperBound(CLeaf bundleVal, CLeaf dimArg)
    {
        var len = EmitNdimGetLength(bundleVal, dimArg);
        return NdimArrayAbi.BuildUpperBound(_builder, len);
    }

    /// <summary>N-R4: every OTHER Array instance member (Clone/CopyTo/SetValue/GetValue/Equals/…) is a
    /// loud reject on a Rank>1 receiver. Not "the existing unsupported-extern path" (audit-refuted —
    /// SystemObjectArray.__Clone__SystemObject IS a real, valid extern; letting it through would
    /// silently shallow-copy the 3-element bundle WRAPPER, aliasing the same flat backing between the
    /// "clone" and the original instead of copying elements).</summary>
}
