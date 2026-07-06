using Microsoft.CodeAnalysis;

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
