using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// ABI for rank-2+ CLR arrays. A logical T[d0,...,dn] is represented as a SystemObjectArray bundle:
/// slot 0 is the rank-1 typed backing array, slots 1..rank are boxed dimension lengths.
/// </summary>
public static class NdimArrayAbi
{
    public enum PropertyKind
    {
        Length,
        Rank
    }

    public enum MethodKind
    {
        GetLength,
        GetUpperBound
    }

    public readonly struct AccessPlan
    {
        public readonly CLeaf BundleVal;
        public readonly CLeaf InBounds;
        public readonly CLeaf FlatIndex;
        public readonly IArrayTypeSymbol BackingType;
        public readonly int[] IdxSlots;
        public readonly int[] DimSlots;

        public AccessPlan(CLeaf bundleVal, CLeaf inBounds, CLeaf flatIndex, IArrayTypeSymbol backingType, int[] idxSlots, int[] dimSlots)
        {
            BundleVal = bundleVal;
            InBounds = inBounds;
            FlatIndex = flatIndex;
            BackingType = backingType;
            IdxSlots = idxSlots;
            DimSlots = dimSlots;
        }
    }

    public const string BundleUdonType = "SystemObjectArray";
    public const string BoxedElementUdonType = "SystemObject";
    public const int BackingSlotIndex = 0;

    public static bool IsNdimArray(ITypeSymbol type) => type is IArrayTypeSymbol { Rank: > 1 };

    public static IArrayTypeSymbol BackingType(Compilation compilation, IArrayTypeSymbol ndimType)
        => (IArrayTypeSymbol)compilation.CreateArrayTypeSymbol(ndimType.ElementType, 1);

    public static int BundleLength(int rank) => 1 + rank;

    public static int DimSlotIndex(int dim) => 1 + dim;

    public static UdonAbiKey BundleConstructorKey() => UdonAbi.ArrayConstructor(BundleUdonType);

    public static UdonAbiKey BundleGetKey()
        => UdonAbi.ArrayGet(BundleUdonType, BoxedElementUdonType);

    public static UdonAbiKey BundleSetKey()
        => UdonAbi.ArraySet(BundleUdonType, BoxedElementUdonType);

    public static CLeaf ReadBacking(CoreBuilder builder, CLeaf bundleVal, StorageType backingUdonType)
        => ReadBoxedSlot(builder, bundleVal, builder.Const(BackingSlotIndex, StorageTypes.Int32), backingUdonType);

    public static CLeaf ReadDimLength(CoreBuilder builder, CLeaf bundleVal, CLeaf dimIndexPlusOne)
        => ReadBoxedSlot(builder, bundleVal, dimIndexPlusOne, StorageTypes.Int32);

    public static CLeaf ReadFromPlan(CoreBuilder builder, AccessPlan plan, StorageType elemUdonType,
        StorageType backingUdonType, StorageType backingElemUdonType, string arrayExprSyntax,
        Func<string, CLeaf> defaultConst)
    {
        var resultSlot = builder.AllocScratch(elemUdonType);
        builder.EmitAssign(resultSlot, defaultConst(elemUdonType.Name));
        builder.EmitIf(plan.InBounds,
            _ =>
            {
                var backing = ReadBacking(builder, plan.BundleVal, backingUdonType);
                var elemVal = builder.ExternCall(
                    UdonAbi.ArrayGet(backingUdonType.Name, backingElemUdonType.Name),
                    new List<CLeaf> { backing, plan.FlatIndex }, elemUdonType);
                builder.EmitAssign(resultSlot, elemVal);
            },
            _ => EmitBoundsLogError(builder, arrayExprSyntax, "read", plan));
        return builder.SlotRef(resultSlot);
    }

    public static void WriteFromPlan(CoreBuilder builder, AccessPlan plan, CLeaf value,
        StorageType backingUdonType, StorageType backingElemUdonType, string arrayExprSyntax)
    {
        builder.EmitIf(plan.InBounds,
            _ =>
            {
                var backing = ReadBacking(builder, plan.BundleVal, backingUdonType);
                builder.EmitExternVoid(
                    UdonAbi.ArraySet(backingUdonType.Name, backingElemUdonType.Name),
                    new List<CLeaf> { backing, plan.FlatIndex, value });
            },
            _ => EmitBoundsLogError(builder, arrayExprSyntax, "write", plan));
    }

    public static void MintBundleToSlot(CoreBuilder builder, int bundleSlot, int backingSlot, int[] dimSlots)
    {
        builder.EmitAssign(bundleSlot, builder.ExternCall(BundleConstructorKey(),
            new List<CLeaf> { builder.Const(BundleLength(dimSlots.Length), StorageTypes.Int32) }, new StorageType(BundleUdonType)));
        var bundle = builder.SlotRef(bundleSlot);
        builder.EmitExternVoid(BundleSetKey(),
            new List<CLeaf> { bundle, builder.Const(BackingSlotIndex, StorageTypes.Int32), builder.SlotRef(backingSlot) });
        for (int d = 0; d < dimSlots.Length; d++)
            builder.EmitExternVoid(BundleSetKey(),
                new List<CLeaf> { bundle, builder.Const(DimSlotIndex(d), StorageTypes.Int32), builder.SlotRef(dimSlots[d]) });
    }

    static CLeaf ReadBoxedSlot(CoreBuilder builder, CLeaf bundleVal, CLeaf bundleIndex, StorageType targetUdonType)
    {
        var boxed = builder.ExternCall(BundleGetKey(),
            new List<CLeaf> { bundleVal, bundleIndex }, new StorageType(BoxedElementUdonType));
        var slot = builder.AllocScratch(targetUdonType);
        builder.EmitAssign(slot, boxed);
        return builder.SlotRef(slot);
    }

    public static CLeaf BuildInBounds(CoreBuilder builder, int[] idxSlots, int[] dimSlots)
    {
        CLeaf inBounds = null;
        for (int d = 0; d < idxSlots.Length; d++)
        {
            var geZero = builder.ExternCall(UdonAbiKey.Method("SystemInt32", "op_GreaterThanOrEqual", new[] { "SystemInt32", "SystemInt32" }, "SystemBoolean"),
                new List<CLeaf> { builder.SlotRef(idxSlots[d]), builder.Const(0, StorageTypes.Int32) }, StorageTypes.Boolean);
            var ltDim = builder.ExternCall(UdonAbi.Int32LessThan,
                new List<CLeaf> { builder.SlotRef(idxSlots[d]), builder.SlotRef(dimSlots[d]) }, StorageTypes.Boolean);
            var dimOk = builder.ExternCall(UdonAbi.BooleanConditionalAnd,
                new List<CLeaf> { geZero, ltDim }, StorageTypes.Boolean);
            inBounds = inBounds == null ? dimOk
                : builder.ExternCall(UdonAbi.BooleanConditionalAnd,
                    new List<CLeaf> { inBounds, dimOk }, StorageTypes.Boolean);
        }
        return inBounds;
    }

    public static CLeaf BuildFlatIndex(CoreBuilder builder, int[] idxSlots, int[] dimSlots)
    {
        CLeaf flatIndex = builder.SlotRef(idxSlots[0]);
        for (int d = 1; d < idxSlots.Length; d++)
        {
            var mul = builder.ExternCall(UdonAbi.Int32Multiply,
                new List<CLeaf> { flatIndex, builder.SlotRef(dimSlots[d]) }, StorageTypes.Int32);
            flatIndex = builder.ExternCall(UdonAbi.Int32Add,
                new List<CLeaf> { mul, builder.SlotRef(idxSlots[d]) }, StorageTypes.Int32);
        }
        return flatIndex;
    }

    public static CLeaf BuildTotalElementCount(CoreBuilder builder, int[] dimSlots)
    {
        CLeaf totalSize = builder.SlotRef(dimSlots[0]);
        for (int d = 1; d < dimSlots.Length; d++)
            totalSize = builder.ExternCall(UdonAbi.Int32Multiply,
                new List<CLeaf> { totalSize, builder.SlotRef(dimSlots[d]) }, StorageTypes.Int32);
        return totalSize;
    }

    public static CLeaf BuildRuntimeDimSlotIndex(CoreBuilder builder, CLeaf dimArg)
        => builder.ExternCall(UdonAbi.Int32Add,
            new List<CLeaf> { dimArg, builder.Const(DimSlotIndex(0), StorageTypes.Int32) }, StorageTypes.Int32);

    public static CLeaf BuildUpperBound(CoreBuilder builder, CLeaf length)
        => builder.ExternCall(UdonAbi.Int32Subtract,
            new List<CLeaf> { length, builder.Const(1, StorageTypes.Int32) }, StorageTypes.Int32);

    public static void EmitBoundsLogError(CoreBuilder builder, string arrayExprSyntax, string verb, AccessPlan plan)
    {
        CLeaf msg = builder.Const($"USugar: index out of range on '{arrayExprSyntax}' ({verb}, rank {plan.IdxSlots.Length}):", StorageTypes.String);
        for (int d = 0; d < plan.IdxSlots.Length; d++)
        {
            msg = ConcatString(builder, msg, builder.Const($" dim{d}[", StorageTypes.String));
            msg = ConcatString(builder, msg, IntToString(builder, builder.SlotRef(plan.IdxSlots[d])));
            msg = ConcatString(builder, msg, builder.Const("/", StorageTypes.String));
            msg = ConcatString(builder, msg, IntToString(builder, builder.SlotRef(plan.DimSlots[d])));
            msg = ConcatString(builder, msg, builder.Const("]", StorageTypes.String));
        }
        builder.EmitExternVoid(UdonAbi.DebugLogError, new List<CLeaf> { msg });
    }

    static CLeaf IntToString(CoreBuilder builder, CLeaf intVal)
        => builder.ExternCall(UdonAbiKey.Method("SystemInt32", "ToString", "SystemString"), new List<CLeaf> { intVal }, StorageTypes.String);

    static CLeaf ConcatString(CoreBuilder builder, CLeaf a, CLeaf b)
        => builder.ExternCall(UdonAbiKey.Method("SystemString", "op_Addition", new[] { "SystemString", "SystemString" }, "SystemString"),
            new List<CLeaf> { a, b }, StorageTypes.String);

    public static void FlattenInitializer(IArrayInitializerOperation init, List<IOperation> outLeaves)
    {
        foreach (var elem in init.ElementValues)
        {
            if (elem is IArrayInitializerOperation nested) FlattenInitializer(nested, outLeaves);
            else outLeaves.Add(elem);
        }
    }

    public static bool TryGetProperty(string memberName, out PropertyKind kind)
    {
        switch (memberName)
        {
            case "Length":
                kind = PropertyKind.Length;
                return true;
            case "Rank":
                kind = PropertyKind.Rank;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static bool TryGetMethod(string memberName, out MethodKind kind)
    {
        switch (memberName)
        {
            case "GetLength":
                kind = MethodKind.GetLength;
                return true;
            case "GetUpperBound":
                kind = MethodKind.GetUpperBound;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static void RejectMember(string memberName)
        => throw new System.NotSupportedException(
            $"'{memberName}' is not supported on a multi-dimensional array (T[,], ...): its runtime value "
            + "is an object[] bundle (flat backing + dimension lengths), so a generic Array member would "
            + "operate on the bundle wrapper instead of the logical array (e.g. Clone would alias the "
            + "backing rather than copy it). Only Length, GetLength, Rank, and GetUpperBound are supported.");
}
