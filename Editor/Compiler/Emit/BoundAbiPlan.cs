using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

internal enum BoundAbiRole
{
    Invocation,
    ParamsInvocation,
    Conversion,
    Operator,
    RemainderDivision,
    RemainderMultiplication,
    RemainderSubtraction,
    FieldGet,
    FieldSetValue,
    FieldSetReference,
    PropertyGet,
    PropertySet,
    IndexerGet,
    IndexerSet,
}

internal readonly struct BoundAbiOperationKey
    : IEquatable<BoundAbiOperationKey>
{
    readonly IOperation _operation;
    readonly CallSiteBindingScope _scope;
    readonly BoundAbiRole _role;

    public BoundAbiOperationKey(
        IOperation operation,
        CallSiteBindingScope scope,
        BoundAbiRole role)
    {
        _operation = operation
            ?? throw new ArgumentNullException(nameof(operation));
        _scope = scope;
        _role = role;
    }

    public bool Equals(BoundAbiOperationKey other)
        => ReferenceEquals(_operation, other._operation)
           && _scope.Equals(other._scope)
           && _role == other._role;

    public override bool Equals(object obj)
        => obj is BoundAbiOperationKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = _operation.GetHashCode();
            hash = hash * 31 + _scope.GetHashCode();
            return hash * 31 + (int)_role;
        }
    }
}

/// <summary>
/// Frozen ABI input to body lowering. The immutable installed-SDK schema is
/// shared, while every program-specific semantic query result is copied before
/// <see cref="BoundProgram"/> is published; no catalog or binder survives this
/// boundary.
/// </summary>
internal sealed class BoundAbiPlan
{
    readonly IReadOnlyDictionary<string, UdonExternPrototype> _exact;
    readonly IReadOnlyDictionary<BoundAbiOperationKey, BoundExtern>
        _operations;
    readonly IReadOnlyDictionary<BoundAbiOperationKey, string>
        _missingOperations;

    internal BoundAbiPlan(
        IReadOnlyDictionary<string, UdonExternPrototype> exact,
        IDictionary<BoundAbiOperationKey, BoundExtern> operations,
        IDictionary<BoundAbiOperationKey, string> missingOperations)
    {
        _exact = exact ?? throw new ArgumentNullException(nameof(exact));
        _operations = new ReadOnlyDictionary<
            BoundAbiOperationKey, BoundExtern>(
            new Dictionary<BoundAbiOperationKey, BoundExtern>(
                operations
                ?? throw new ArgumentNullException(nameof(operations))));
        _missingOperations = new ReadOnlyDictionary<
            BoundAbiOperationKey, string>(
            new Dictionary<BoundAbiOperationKey, string>(
                missingOperations
                ?? throw new ArgumentNullException(
                    nameof(missingOperations))));
    }

    internal static BoundAbiPlan ExactCatalog(UdonAbiCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        return new BoundAbiPlan(
            catalog.ExactPrototypes,
            new Dictionary<BoundAbiOperationKey, BoundExtern>(),
            new Dictionary<BoundAbiOperationKey, string>());
    }

    internal BoundExtern RequireExact(UdonAbiKey key)
    {
        var name = key.ToRegistryName();
        if (_exact.TryGetValue(name, out var prototype))
            return new BoundExtern(key, prototype);
        throw new NotSupportedException(
            $"Udon extern '{name}' is not registered by the installed SDK.");
    }

    internal BoundExtern RequireOperation(
        IOperation operation,
        CallSiteBindingScope scope,
        BoundAbiRole role)
    {
        var key = new BoundAbiOperationKey(operation, scope, role);
        if (_operations.TryGetValue(key, out var bound))
            return bound;
        if (_missingOperations.TryGetValue(key, out var message))
            throw new NotSupportedException(message);
        throw new InvalidOperationException(
            $"ABI role '{role}' for '{operation?.Syntax}' "
            + "was absent from the bound program.");
    }

    internal BoundExtern RequireParamsInvocation(
        IOperation operation,
        CallSiteBindingScope scope,
        out bool expand)
    {
        var selected = RequireOperation(
            operation, scope, BoundAbiRole.ParamsInvocation);
        var standardKey = new BoundAbiOperationKey(
            operation, scope, BoundAbiRole.Invocation);
        if (_operations.TryGetValue(standardKey, out var standard))
        {
            expand = !string.Equals(
                selected.Text, standard.Text,
                StringComparison.Ordinal);
            return selected;
        }
        if (_missingOperations.ContainsKey(standardKey))
        {
            expand = true;
            return selected;
        }
        throw new InvalidOperationException(
            $"ABI role '{BoundAbiRole.Invocation}' for '{operation?.Syntax}' "
            + "was absent from the bound program.");
    }

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
    readonly Dictionary<BoundAbiOperationKey, BoundExtern> _operations =
        new();
    readonly Dictionary<BoundAbiOperationKey, string> _missingOperations =
        new();
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

    public BoundExtern BindExact(UdonAbiKey key)
    {
        RequireMutable();
        return _catalog.Require(key);
    }

    public bool ContainsExact(UdonAbiKey key)
    {
        RequireMutable();
        return _catalog.ExactPrototypes.ContainsKey(
            key.ToRegistryName());
    }

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

    public void RecordOperation(
        IOperation operation,
        CallSiteBindingScope scope,
        BoundAbiRole role,
        BoundExtern bound)
    {
        RequireMutable();
        var key = new BoundAbiOperationKey(operation, scope, role);
        if (_operations.TryGetValue(key, out var existing))
        {
            if (existing.Text == bound.Text) return;
            throw new InvalidOperationException(
                $"ABI role '{role}' for '{operation.Syntax}' resolved to "
                + $"both '{existing.Text}' and '{bound.Text}'.");
        }
        _operations.Add(
            key, bound ?? throw new ArgumentNullException(nameof(bound)));
        _missingOperations.Remove(key);
    }

    public void RecordOperationFailure(
        IOperation operation,
        CallSiteBindingScope scope,
        BoundAbiRole role,
        string message)
    {
        RequireMutable();
        var key = new BoundAbiOperationKey(operation, scope, role);
        if (_operations.ContainsKey(key)) return;
        _missingOperations[key] =
            string.IsNullOrWhiteSpace(message)
                ? $"No installed Udon extern implements ABI role '{role}' "
                  + $"for '{operation.Syntax}'."
                : message;
    }

    public BoundAbiPlan Publish()
    {
        RequireMutable();
        _published = true;
        return new BoundAbiPlan(
            _catalog.ExactPrototypes,
            _operations,
            _missingOperations);
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
