using System;
using System.Collections.Generic;

/// <summary>
/// Total runtime discriminator for compiler-owned object[] bundles. Header reads stay dominated by
/// the carrier, length, and header-type guards so arbitrary object values and malformed arrays
/// produce false instead of a VM fault.
/// </summary>
internal static class BundleProbe
{
    public static CLeaf IsTagged(
        CoreBuilder builder,
        CLeaf value,
        string tagPrefix,
        int minimumLength = BundleAbi.HeaderSize)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        if (tagPrefix == null)
            throw new ArgumentNullException(nameof(tagPrefix));
        if (minimumLength < BundleAbi.HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(minimumLength));

        var result = builder.AllocScratch(StorageTypes.Boolean);
        builder.EmitAssign(
            result,
            builder.Const(false, StorageTypes.Boolean));
        var isArray = builder.ExternCall(
            UdonAbiKey.Method(
                "SystemType", "IsInstanceOfType",
                new[] { "SystemObject" },
                "SystemBoolean"),
            new List<CLeaf>
            {
                builder.Const(
                    AggregateAbi.ArrayType,
                    StorageTypes.Type),
                value,
            },
            StorageTypes.Boolean);
        builder.EmitIf(isArray, _ =>
        {
            var length = builder.ExternCall(
                UdonAbi.ArrayLength(AggregateAbi.ArrayType),
                new List<CLeaf> { value },
                StorageTypes.Int32);
            var hasRequiredSlots = builder.ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf>
                {
                    builder.Const(
                        minimumLength - 1,
                        StorageTypes.Int32),
                    length,
                },
                StorageTypes.Boolean);
            builder.EmitIf(hasRequiredSlots, __ =>
            {
                var header = AggregateAbi.ReadSlot(
                    builder,
                    value,
                    BundleAbi.Type,
                    StorageTypes.Object);
                var isString = builder.ExternCall(
                    UdonAbiKey.Method(
                        "SystemType", "IsInstanceOfType",
                        new[] { "SystemObject" },
                        "SystemBoolean"),
                    new List<CLeaf>
                    {
                        builder.Const(
                            StorageTypes.String.Name,
                            StorageTypes.Type),
                        header,
                    },
                    StorageTypes.Boolean);
                builder.EmitIf(isString, ___ =>
                {
                    var typeId = AggregateAbi.ReadSlot(
                        builder,
                        value,
                        BundleAbi.Type,
                        StorageTypes.String);
                    builder.EmitAssign(
                        result,
                        builder.ExternCall(
                            UdonAbiKey.Method(
                                "SystemString",
                                "StartsWith",
                                new[] { "SystemString" },
                                "SystemBoolean"),
                            new List<CLeaf>
                            {
                                typeId,
                                builder.Const(
                                    tagPrefix,
                                    StorageTypes.String),
                            },
                            StorageTypes.Boolean));
                }, null);
            }, null);
        }, null);
        return builder.SlotRef(result);
    }
}
