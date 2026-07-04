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

    public int Count => Fields.Count;

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

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex)
    { Fields = fields; _nameToIndex = nameToIndex; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();

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
        else if (type.TypeKind == TypeKind.Struct)
        {
            // User struct → instance fields mapped to indices in declaration order. Auto-property backing
            // fields are implicitly declared but carry the property as AssociatedSymbol; map them by the
            // property name so `get`/`set`/`init` resolve to the same object[] element.
            int i = 0;
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

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex);
    }
}
