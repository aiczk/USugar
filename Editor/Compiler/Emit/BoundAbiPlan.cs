using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Immutable ABI authority published as part of <see cref="BoundProgram"/> before body emission.
/// Lowerers can require one semantic ABI decision, but cannot access the catalog binder, enumerate
/// candidates, or mutate a cache. Candidate ordering remains private to the binding layer.
/// </summary>
internal sealed class BoundAbiPlan
{
    readonly UdonAbiBinder _binder;
    readonly UdonAbiCatalog _catalog;

    internal BoundAbiPlan(UdonAbiCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _binder = new UdonAbiBinder(_catalog);
    }

    internal bool ContainsExact(UdonAbiKey key) => _catalog.Contains(key);

    internal BoundExtern RequireExact(UdonAbiKey key)
        => _binder.BindExact(key);

    internal BoundExtern RequireConversion(
        IMethodSymbol method,
        ITypeSymbol expressionSource,
        ITypeSymbol expressionDestination,
        Func<ITypeSymbol, string> getUdonType)
        => _binder.BindConversion(
            method, expressionSource, expressionDestination, getUdonType);

    internal BoundExtern RequireOperator(
        IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => _binder.BindOperator(method, getUdonType);

    internal BoundExtern RequireMethod(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride = null)
        => _binder.BindMethod(method, owner, getUdonType, parameterOverride);

    /// <summary>
    /// Explicit feature query used only for Roslyn-expanded params calls: an expanded overload and
    /// the array overload are two different SDK features, not a recovery fallback.
    /// </summary>
    internal bool TryGetMethod(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride,
        out BoundExtern bound)
        => _binder.TryBindMethod(
            method, owner, getUdonType, parameterOverride, out bound);

    internal BoundExtern RequireFieldSetter(
        string owner,
        string fieldName,
        string valueType,
        bool isValueType = true,
        bool hasReceiver = true)
        => _binder.BindFieldSetter(
            owner, fieldName, valueType, isValueType, hasReceiver);

    internal BoundExtern RequirePropertySetter(
        string owner,
        string propertyName,
        string valueType,
        bool hasReceiver = true)
        => _binder.BindPropertySetter(
            owner, propertyName, valueType, hasReceiver);

    internal BoundExtern RequirePropertyGetter(
        string owner,
        string propertyName,
        string returnType,
        bool hasReceiver = true)
        => _binder.BindPropertyGetter(
            owner, propertyName, returnType, hasReceiver);

    internal BoundExtern RequireIndexerGetter(
        string owner,
        string propertyName,
        IReadOnlyList<string> indexTypes,
        string returnType,
        bool hasReceiver = true)
        => _binder.BindIndexerGetter(
            owner, propertyName, indexTypes, returnType, hasReceiver);

    internal BoundExtern RequireIndexerSetter(
        string owner,
        string propertyName,
        IReadOnlyList<string> indexTypes,
        string valueType,
        bool hasReceiver = true)
        => _binder.BindIndexerSetter(
            owner, propertyName, indexTypes, valueType, hasReceiver);
}
