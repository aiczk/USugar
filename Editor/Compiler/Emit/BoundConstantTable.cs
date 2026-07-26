using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
}

/// <summary>
/// Roslyn-facing constant evaluator confined to bound-program construction.
/// </summary>
internal sealed class BoundConstantTableBuilder
{
    readonly Compilation _compilation;
    readonly Dictionary<IFieldSymbol, BoundConstantValue> _fields =
        new(SymbolEqualityComparer.Default);
    bool _published;

    public BoundConstantTableBuilder(Compilation compilation)
        => _compilation = compilation
            ?? throw new ArgumentNullException(nameof(compilation));

    public void Record(IFieldSymbol field)
    {
        RequireMutable();
        if (field == null || !field.IsStatic || !field.IsReadOnly)
            return;
        if (_fields.ContainsKey(field)) return;
        var hasValue = EmitPolicy.TryGetConstFieldInitializer(
            _compilation, field, out var value);
        _fields.Add(
            field, new BoundConstantValue(hasValue, value));
    }

    public BoundConstantTable Publish()
    {
        RequireMutable();
        _published = true;
        return new BoundConstantTable(_fields);
    }

    void RequireMutable()
    {
        if (_published)
            throw new InvalidOperationException(
                "The constant table was already published.");
    }
}
