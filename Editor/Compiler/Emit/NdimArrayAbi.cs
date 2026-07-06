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

    public static string BundleCtorSignature() => ExternResolver.BuildArrayCtorSignature(BundleUdonType);

    public static string BundleGetSignature()
        => ExternResolver.BuildArrayGetSignature(BundleUdonType, BoxedElementUdonType);

    public static string BundleSetSignature()
        => ExternResolver.BuildArraySetSignature(BundleUdonType, BoxedElementUdonType);

    public static CLeaf BuildInBounds(CoreBuilder builder, int[] idxSlots, int[] dimSlots)
    {
        CLeaf inBounds = null;
        for (int d = 0; d < idxSlots.Length; d++)
        {
            var geZero = builder.ExternCall("SystemInt32.__op_GreaterThanOrEqual__SystemInt32_SystemInt32__SystemBoolean",
                new List<CLeaf> { builder.SlotRef(idxSlots[d]), builder.Const(0, "SystemInt32") }, "SystemBoolean");
            var ltDim = builder.ExternCall("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<CLeaf> { builder.SlotRef(idxSlots[d]), builder.SlotRef(dimSlots[d]) }, "SystemBoolean");
            var dimOk = builder.ExternCall("SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                new List<CLeaf> { geZero, ltDim }, "SystemBoolean");
            inBounds = inBounds == null ? dimOk
                : builder.ExternCall("SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                    new List<CLeaf> { inBounds, dimOk }, "SystemBoolean");
        }
        return inBounds;
    }

    public static CLeaf BuildFlatIndex(CoreBuilder builder, int[] idxSlots, int[] dimSlots)
    {
        CLeaf flatIndex = builder.SlotRef(idxSlots[0]);
        for (int d = 1; d < idxSlots.Length; d++)
        {
            var mul = builder.ExternCall("SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32",
                new List<CLeaf> { flatIndex, builder.SlotRef(dimSlots[d]) }, "SystemInt32");
            flatIndex = builder.ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new List<CLeaf> { mul, builder.SlotRef(idxSlots[d]) }, "SystemInt32");
        }
        return flatIndex;
    }

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
