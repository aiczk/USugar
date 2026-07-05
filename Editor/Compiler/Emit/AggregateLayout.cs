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
