using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

public class AggregateLayout
{
    public readonly struct FieldInfo
    {
        public readonly string Name;
        public readonly int Index;
        public readonly ITypeSymbol Type;
        public FieldInfo(string name, int index, ITypeSymbol type)
        { Name = name; Index = index; Type = type; }
    }

    public readonly IReadOnlyList<FieldInfo> Fields;
    readonly Dictionary<string, int> _nameToIndex;

    // Class ABI v1: a user class reserves object[] slot 0 (null placeholder for the future type-object
    // reference); its fields live at 1..F. A struct/tuple reserves nothing (fields at 0..F-1). SlotCount is
    // the backing array size; Count stays the field count. Each FieldInfo.Index already carries the reserved
    // offset, so field-slot resolution (TryGetIndex) is correct without a per-site +1.
    readonly int _reservedLeadingSlots;

    public int Count => Fields.Count;
    public int SlotCount => _reservedLeadingSlots + Fields.Count;

    public bool TryGetIndex(string fieldName, out int index)
        => _nameToIndex.TryGetValue(fieldName, out index);

    public bool TryGetIndex(IFieldSymbol field, out int index)
    {
        if (_nameToIndex.TryGetValue(field.Name, out index)) return true;
        if (field.CorrespondingTupleField != null
            && _nameToIndex.TryGetValue(field.CorrespondingTupleField.Name, out index)) return true;
        // Reverse: check if any layout field's CorrespondingTupleField matches
        return false;
    }

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex, int reservedLeadingSlots)
    { Fields = fields; _nameToIndex = nameToIndex; _reservedLeadingSlots = reservedLeadingSlots; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();
        // Class ABI v1: reserve slot 0, fields start at index 1. Struct/tuple: no reservation.
        int reserved = EmitPolicy.IsUserClassType(type) ? 1 : 0;

        if (type.IsTupleType)
        {
            var elements = type.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                var name = elements[i].Name;
                fields.Add(new FieldInfo(name, i, elements[i].Type));
                nameToIndex[name] = i;
                var itemName = $"Item{i + 1}";
                if (name != itemName) nameToIndex[itemName] = i;
                if (elements[i].CorrespondingTupleField != null)
                {
                    var corrName = elements[i].CorrespondingTupleField.Name;
                    if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
                }
            }
        }
        else if (type.TypeKind == TypeKind.Struct || reserved > 0)
        {
            // User struct / v1 user class → instance fields mapped to indices in declaration order (a class
            // starts at `reserved`=1, slot 0 held for the future type-object reference). Auto-property backing
            // fields are implicitly declared but carry the property as AssociatedSymbol; map them by the
            // property name so `get`/`set`/`init` resolve to the same object[] element.
            int i = reserved;
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
                if (!f.IsImplicitlyDeclared)
                {
                    fields.Add(new FieldInfo(f.Name, i, f.Type));
                    nameToIndex[f.Name] = i++;
                }
                else if (f.AssociatedSymbol is IPropertySymbol prop)
                {
                    fields.Add(new FieldInfo(prop.Name, i, f.Type));
                    nameToIndex[prop.Name] = i++;
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-aggregate type '{type.Name}'");
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex, reserved);
    }
}

/// <summary>
/// Object[] ABI for class, struct, and tuple values. Layout decides which slot a logical member owns;
/// this type owns the backing array representation and element access protocol.
/// </summary>
public static class AggregateAbi
{
    public const string ArrayType = "SystemObjectArray";
    public const string ElementType = "SystemObject";

    public static CLeaf Allocate(CoreBuilder builder, int slotCount)
        => builder.ExternCall(
            ExternResolver.BuildArrayCtorSignature(ArrayType),
            new List<CLeaf> { builder.Const(slotCount, "SystemInt32") },
            ArrayType);

    public static CLeaf ReadSlot(CoreBuilder builder, CLeaf instance, int index, string udonType)
        => builder.ExternCall(
            ExternResolver.BuildArrayGetSignature(ArrayType, ElementType),
            new List<CLeaf> { instance, builder.Const(index, "SystemInt32") },
            udonType);

    public static void WriteSlot(CoreBuilder builder, CLeaf instance, int index, CLeaf value)
        => builder.EmitExternVoid(
            ExternResolver.BuildArraySetSignature(ArrayType, ElementType),
            new List<CLeaf> { instance, builder.Const(index, "SystemInt32"), value });

    /// <summary>Default-initialize an allocated aggregate bundle. Nested aggregate fields are allocated
    /// recursively; class-typed fields stay null by default.</summary>
    public static void DefaultInitialize(CoreBuilder builder, CValue arrayVal, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout, Func<ITypeSymbol, string> getUdonType)
    {
        var slot = builder.AllocScratch(ArrayType);
        builder.EmitAssign(slot, arrayVal);
        foreach (var fi in layout.Fields)
        {
            int i = fi.Index;
            var fieldType = fi.Type;
            if (fieldType is INamedTypeSymbol nested && EmitPolicy.IsAggregateType(nested))
            {
                var nestedLayout = getLayout(nested);
                var subSlot = builder.AllocScratch(ArrayType);
                builder.EmitAssign(subSlot, Allocate(builder, nestedLayout.SlotCount));
                WriteSlot(builder, builder.SlotRef(slot), i, builder.SlotRef(subSlot));
                DefaultInitialize(builder, builder.SlotRef(subSlot), nestedLayout, getLayout, getUdonType);
                continue;
            }

            var defVal = DefaultScalarValue(fieldType);
            if (defVal != null)
                WriteSlot(builder, builder.SlotRef(slot), i, builder.Const(defVal, getUdonType(fieldType)));
        }
    }

    /// <summary>Deep value-copy of an object[]-backed struct/tuple aggregate. Nested aggregate elements
    /// are recursively cloned; scalar boxed elements are copied by reference.</summary>
    public static CLeaf DeepClone(CoreBuilder builder, CLeaf source, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout)
    {
        var dstSlot = builder.AllocScratch(ArrayType);
        builder.EmitAssign(dstSlot, Allocate(builder, layout.Count));
        for (int i = 0; i < layout.Count; i++)
        {
            var elem = ReadSlot(builder, source, i, ElementType);
            CLeaf copy = layout.Fields[i].Type is INamedTypeSymbol nested && EmitPolicy.IsAggregateType(nested)
                ? DeepClone(builder, elem, getLayout(nested), getLayout)
                : elem;
            WriteSlot(builder, builder.SlotRef(dstSlot), i, copy);
        }
        return builder.SlotRef(dstSlot);
    }

    /// <summary>Allocate and default-initialize a fresh aggregate bundle.</summary>
    public static CLeaf MintDefault(CoreBuilder builder, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout, Func<ITypeSymbol, string> getUdonType)
    {
        var slot = builder.AllocScratch(ArrayType);
        builder.EmitAssign(slot, Allocate(builder, layout.SlotCount));
        DefaultInitialize(builder, builder.SlotRef(slot), layout, getLayout, getUdonType);
        return builder.SlotRef(slot);
    }

    static object DefaultScalarValue(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return false;
            case SpecialType.System_Int32: return 0;
            case SpecialType.System_Single: return 0f;
            case SpecialType.System_Double: return 0d;
            case SpecialType.System_Int64: return 0L;
            case SpecialType.System_Byte: return (byte)0;
            case SpecialType.System_UInt32: return 0u;
            case SpecialType.System_UInt64: return 0UL;
            case SpecialType.System_Int16: return (short)0;
            case SpecialType.System_UInt16: return (ushort)0;
            case SpecialType.System_Char: return '\0';
            case SpecialType.System_SByte: return (sbyte)0;
            default: return null;
        }
    }
}
