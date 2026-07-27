using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Per-program aggregate layout authority. Layouts may be discovered only
/// during semantic materialization; body emission is lookup-only.
/// </summary>
internal sealed class AggregateLayoutTable
{
    readonly Dictionary<ITypeSymbol, AggregateLayout> _layoutCache = new(SymbolEqualityComparer.Default);
    bool _published;

    public IReadOnlyList<KeyValuePair<ITypeSymbol, AggregateLayout>>
        Layouts
        => _layoutCache
            .OrderBy(
                pair => ClassTypeObjectContext.SpecKey(pair.Key),
                StringComparer.Ordinal)
            .ToArray();

    public AggregateLayout GetLayout(INamedTypeSymbol type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (_layoutCache.TryGetValue(type, out var cached)) return cached;
        if (_published)
            throw new InvalidOperationException(
                $"Aggregate layout '{type.ToDisplayString()}' was absent "
                + "from the bound program.");
        var layout = AggregateLayout.Build(type);
        _layoutCache[type] = layout;
        return layout;
    }

    public AggregateLayoutTable Publish()
    {
        if (_published)
            throw new InvalidOperationException(
                "The aggregate layout table was published twice.");
        _published = true;
        return this;
    }
}
