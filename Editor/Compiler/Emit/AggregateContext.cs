using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns aggregate layout caches and aggregate-field default initialization registrations.
/// </summary>
public sealed class AggregateContext
{
    readonly Dictionary<ITypeSymbol, AggregateLayout> _layoutCache = new(SymbolEqualityComparer.Default);

    public readonly List<(string FieldName, INamedTypeSymbol AggregateType)> FieldDefaults = new();

    public AggregateLayout GetLayout(INamedTypeSymbol type)
    {
        if (_layoutCache.TryGetValue(type, out var cached)) return cached;
        var layout = AggregateLayout.Build(type);
        _layoutCache[type] = layout;
        return layout;
    }
}
