using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;

internal readonly struct BoundConstantValue
{
    public readonly bool HasValue;
    public readonly object Value;

    public BoundConstantValue(bool hasValue, object value)
    {
        HasValue = hasValue;
        Value = value;
    }
}

/// <summary>
/// Compile-time values for every static-readonly field reference reachable
/// from a bound source tree. A negative result is stored explicitly.
/// </summary>
internal sealed class BoundConstantTable
{
    readonly IReadOnlyDictionary<IFieldSymbol, BoundConstantValue> _fields;

    public BoundConstantTable(
        IDictionary<IFieldSymbol, BoundConstantValue> fields)
        => _fields = new ReadOnlyDictionary<IFieldSymbol, BoundConstantValue>(
            new Dictionary<IFieldSymbol, BoundConstantValue>(
                fields ?? throw new ArgumentNullException(nameof(fields)),
                SymbolEqualityComparer.Default));

    public bool TryGet(IFieldSymbol field, out object value)
    {
        if (field == null) throw new ArgumentNullException(nameof(field));
        if (!_fields.TryGetValue(field, out var constant))
            throw new InvalidOperationException(
                $"Constant fact for field '{field}' was absent "
                + "from the bound program.");
        value = constant.Value;
        return constant.HasValue;
    }

    public static BoundConstantTable Materialize(
        Compilation compilation,
        IEnumerable<IFieldSymbol> fields)
    {
        if (compilation == null)
            throw new ArgumentNullException(nameof(compilation));
        if (fields == null)
            throw new ArgumentNullException(nameof(fields));
        var values = new Dictionary<
            IFieldSymbol, BoundConstantValue>(
            SymbolEqualityComparer.Default);
        foreach (var field in fields
                     .Where(field => field is
                         { IsStatic: true, IsReadOnly: true })
                     .Distinct<IFieldSymbol>(
                         SymbolEqualityComparer.Default))
        {
            var hasValue = EmitPolicy.TryGetConstFieldInitializer(
                compilation, field, out var value);
            values.Add(
                field,
                new BoundConstantValue(hasValue, value));
        }
        return new BoundConstantTable(values);
    }
}
