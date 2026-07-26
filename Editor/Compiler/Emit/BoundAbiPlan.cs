using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Frozen ABI input to body lowering. The immutable installed-SDK schema is
/// shared, while every program-specific semantic query result is copied before
/// <see cref="BoundProgram"/> is published; no catalog or binder survives this
/// boundary.
/// </summary>
internal sealed class BoundAbiPlan
{
    readonly IReadOnlyDictionary<string, UdonExternPrototype> _exact;
    readonly IReadOnlyDictionary<string, BoundExtern> _decisions;
    readonly IReadOnlyDictionary<string, string> _missingFeatures;

    internal BoundAbiPlan(
        IReadOnlyDictionary<string, UdonExternPrototype> exact,
        IDictionary<string, BoundExtern> decisions,
        IDictionary<string, string> missingFeatures)
    {
        _exact = exact ?? throw new ArgumentNullException(nameof(exact));
        _decisions = new ReadOnlyDictionary<string, BoundExtern>(
            new Dictionary<string, BoundExtern>(
                decisions ?? throw new ArgumentNullException(nameof(decisions)),
                StringComparer.Ordinal));
        _missingFeatures = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                missingFeatures
                ?? throw new ArgumentNullException(nameof(missingFeatures)),
                StringComparer.Ordinal));
    }

    internal static BoundAbiPlan ExactCatalog(UdonAbiCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        return new BoundAbiPlan(
            catalog.ExactPrototypes,
            new Dictionary<string, BoundExtern>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal bool ContainsExact(UdonAbiKey key)
        => _exact.ContainsKey(key.ToRegistryName());

    internal BoundExtern RequireExact(UdonAbiKey key)
    {
        var name = key.ToRegistryName();
        if (_exact.TryGetValue(name, out var prototype))
            return new BoundExtern(key, prototype);
        throw new NotSupportedException(
            $"Udon extern '{name}' is not registered by the installed SDK.");
    }

    internal BoundExtern RequireConversion(
        IMethodSymbol method,
        ITypeSymbol expressionSource,
        ITypeSymbol expressionDestination,
        Func<ITypeSymbol, string> getUdonType)
        => RequireDecision(AbiDecisionKey.Conversion(
            method, expressionSource, expressionDestination, getUdonType));

    internal BoundExtern RequireOperator(
        IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => RequireExact(AbiDecisionKey.Operator(method, getUdonType));

    internal BoundExtern RequireMethod(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride = null)
        => RequireDecision(AbiDecisionKey.Method(
            method, owner, getUdonType, parameterOverride));

    internal bool TryGetMethod(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride,
        out BoundExtern bound)
    {
        var key = AbiDecisionKey.Method(
            method, owner, getUdonType, parameterOverride);
        if (_decisions.TryGetValue(key, out bound)) return true;
        if (_missingFeatures.ContainsKey(key))
        {
            bound = null;
            return false;
        }
        throw MissingDecision(key);
    }

    internal BoundExtern RequireFieldSetter(
        string owner,
        string fieldName,
        string valueType,
        bool isValueType = true,
        bool hasReceiver = true)
        => RequireDecision(AbiDecisionKey.FieldSetter(
            owner, fieldName, valueType, isValueType, hasReceiver));

    internal BoundExtern RequirePropertySetter(
        string owner,
        string propertyName,
        string valueType,
        bool hasReceiver = true)
        => RequireDecision(AbiDecisionKey.PropertySetter(
            owner, propertyName, valueType, hasReceiver));

    internal BoundExtern RequirePropertyGetter(
        string owner,
        string propertyName,
        string returnType,
        bool hasReceiver = true)
        => RequireDecision(AbiDecisionKey.PropertyGetter(
            owner, propertyName, returnType, hasReceiver));

    internal BoundExtern RequireIndexerGetter(
        string owner,
        string propertyName,
        IReadOnlyList<string> indexTypes,
        string returnType,
        bool hasReceiver = true)
        => RequireDecision(AbiDecisionKey.IndexerGetter(
            owner, propertyName, indexTypes, returnType, hasReceiver));

    internal BoundExtern RequireIndexerSetter(
        string owner,
        string propertyName,
        IReadOnlyList<string> indexTypes,
        string valueType,
        bool hasReceiver = true)
        => RequireDecision(AbiDecisionKey.IndexerSetter(
            owner, propertyName, indexTypes, valueType, hasReceiver));

    BoundExtern RequireDecision(string key)
    {
        if (_decisions.TryGetValue(key, out var bound)) return bound;
        if (_missingFeatures.TryGetValue(key, out var message))
            throw new NotSupportedException(message);
        throw MissingDecision(key);
    }

    static InvalidOperationException MissingDecision(string key)
        => new InvalidOperationException(
            $"ABI decision '{key}' was absent from the bound program.");
}

/// <summary>
/// Mutable ABI authority confined to semantic materialization. Publish returns
/// a <see cref="BoundAbiPlan"/> with no back-reference to this builder.
/// </summary>
internal sealed class BoundAbiPlanBuilder
{
    readonly UdonAbiCatalog _catalog;
    readonly UdonAbiBinder _binder;
    readonly Dictionary<string, BoundExtern> _decisions =
        new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _missingFeatures =
        new(StringComparer.Ordinal);
    bool _published;

    public BoundAbiPlanBuilder(UdonAbiCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _binder = new UdonAbiBinder(catalog);
    }

    public BoundExtern BindConversion(
        IMethodSymbol method,
        ITypeSymbol expressionSource,
        ITypeSymbol expressionDestination,
        Func<ITypeSymbol, string> getUdonType)
        => Record(
            AbiDecisionKey.Conversion(
                method, expressionSource, expressionDestination, getUdonType),
            _binder.BindConversion(
                method, expressionSource, expressionDestination, getUdonType));

    public BoundExtern BindMethod(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride = null)
        => Record(
            AbiDecisionKey.Method(
                method, owner, getUdonType, parameterOverride),
            _binder.BindMethod(
                method, owner, getUdonType, parameterOverride));

    public bool TryBindMethod(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride,
        out BoundExtern bound)
    {
        RequireMutable();
        var key = AbiDecisionKey.Method(
            method, owner, getUdonType, parameterOverride);
        if (_binder.TryBindMethod(
                method, owner, getUdonType, parameterOverride, out bound))
        {
            Record(key, bound);
            return true;
        }
        _missingFeatures[key] =
            $"No registered Udon extern implements method "
            + $"'{method.ToDisplayString()}' for ABI owner '{owner}'.";
        return false;
    }

    public BoundExtern BindFieldSetter(
        string owner,
        string fieldName,
        string valueType,
        bool isValueType = true,
        bool hasReceiver = true)
        => Record(
            AbiDecisionKey.FieldSetter(
                owner, fieldName, valueType, isValueType, hasReceiver),
            _binder.BindFieldSetter(
                owner, fieldName, valueType, isValueType, hasReceiver));

    public BoundExtern BindPropertySetter(
        string owner,
        string propertyName,
        string valueType,
        bool hasReceiver = true)
        => Record(
            AbiDecisionKey.PropertySetter(
                owner, propertyName, valueType, hasReceiver),
            _binder.BindPropertySetter(
                owner, propertyName, valueType, hasReceiver));

    public BoundExtern BindPropertyGetter(
        string owner,
        string propertyName,
        string returnType,
        bool hasReceiver = true)
        => Record(
            AbiDecisionKey.PropertyGetter(
                owner, propertyName, returnType, hasReceiver),
            _binder.BindPropertyGetter(
                owner, propertyName, returnType, hasReceiver));

    public BoundExtern BindIndexerGetter(
        string owner,
        string propertyName,
        IReadOnlyList<string> indexTypes,
        string returnType,
        bool hasReceiver = true)
        => Record(
            AbiDecisionKey.IndexerGetter(
                owner, propertyName, indexTypes, returnType, hasReceiver),
            _binder.BindIndexerGetter(
                owner, propertyName, indexTypes, returnType, hasReceiver));

    public BoundExtern BindIndexerSetter(
        string owner,
        string propertyName,
        IReadOnlyList<string> indexTypes,
        string valueType,
        bool hasReceiver = true)
        => Record(
            AbiDecisionKey.IndexerSetter(
                owner, propertyName, indexTypes, valueType, hasReceiver),
            _binder.BindIndexerSetter(
                owner, propertyName, indexTypes, valueType, hasReceiver));

    public void TryBind(Action bind)
    {
        RequireMutable();
        try
        {
            bind();
        }
        catch (NotSupportedException)
        {
            // A conservative census can ask about an accessor that is not used.
            // A real use still fails loudly because no decision is published.
        }
    }

    public BoundAbiPlan Publish()
    {
        RequireMutable();
        _published = true;
        return new BoundAbiPlan(
            _catalog.ExactPrototypes,
            _decisions,
            _missingFeatures);
    }

    BoundExtern Record(string key, BoundExtern bound)
    {
        RequireMutable();
        if (_decisions.TryGetValue(key, out var existing))
        {
            if (existing.Text == bound.Text) return existing;
            throw new InvalidOperationException(
                $"ABI decision '{key}' resolved to both "
                + $"'{existing.Text}' and '{bound.Text}'.");
        }
        _decisions.Add(key, bound);
        _missingFeatures.Remove(key);
        return bound;
    }

    void RequireMutable()
    {
        if (_published)
            throw new InvalidOperationException(
                "The ABI plan was already published.");
    }
}

internal static class AbiDecisionKey
{
    public static string Conversion(
        IMethodSymbol method,
        ITypeSymbol source,
        ITypeSymbol destination,
        Func<ITypeSymbol, string> getUdonType)
        => Join(
            "conversion",
            Method(method),
            source == null ? null : getUdonType(source),
            destination == null ? null : getUdonType(destination),
            DeclaredParameters(method, getUdonType),
            getUdonType(method.ReturnType));

    public static UdonAbiKey Operator(
        IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => UdonAbiKey.Method(
            getUdonType(method.ContainingType),
            method.Name,
            DeclaredParameterTypes(method, getUdonType),
            getUdonType(method.ReturnType));

    public static string Method(
        IMethodSymbol method,
        string owner,
        Func<ITypeSymbol, string> getUdonType,
        string[] parameterOverride)
        => Join(
            "method",
            Method(method),
            owner,
            parameterOverride == null
                ? null
                : string.Join(",", parameterOverride),
            DeclaredParameters(method, getUdonType),
            method.IsGenericMethod
                ? DeclaredParameters(
                    method.OriginalDefinition, getUdonType)
                : null,
            getUdonType(method.ReturnType),
            method.IsStatic ? "static" : "instance");

    public static string FieldSetter(
        string owner,
        string name,
        string valueType,
        bool isValueType,
        bool hasReceiver)
        => Join("field-set", owner, name, valueType,
            isValueType ? "value" : "reference",
            hasReceiver ? "instance" : "static");

    public static string PropertySetter(
        string owner,
        string name,
        string valueType,
        bool hasReceiver)
        => Join("property-set", owner, name, valueType,
            hasReceiver ? "instance" : "static");

    public static string PropertyGetter(
        string owner,
        string name,
        string returnType,
        bool hasReceiver)
        => Join("property-get", owner, name, returnType,
            hasReceiver ? "instance" : "static");

    public static string IndexerGetter(
        string owner,
        string name,
        IReadOnlyList<string> indexes,
        string returnType,
        bool hasReceiver)
        => Join("indexer-get", owner, name,
            string.Join(",", indexes), returnType,
            hasReceiver ? "instance" : "static");

    public static string IndexerSetter(
        string owner,
        string name,
        IReadOnlyList<string> indexes,
        string valueType,
        bool hasReceiver)
        => Join("indexer-set", owner, name,
            string.Join(",", indexes), valueType,
            hasReceiver ? "instance" : "static");

    static string Method(IMethodSymbol method)
        => method?.ToDisplayString(
               SymbolDisplayFormat.CSharpErrorMessageFormat)
           ?? throw new ArgumentNullException(nameof(method));

    static string DeclaredParameters(
        IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => string.Join(",", DeclaredParameterTypes(method, getUdonType));

    static string[] DeclaredParameterTypes(
        IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => method.Parameters.Select(parameter =>
        {
            var type = getUdonType(parameter.Type);
            return parameter.RefKind == RefKind.None
                ? type
                : type + "Ref";
        }).ToArray();

    static string Join(params string[] parts)
        => string.Concat(parts.Select(part =>
            part == null ? "-1:" : part.Length + ":" + part));
}
