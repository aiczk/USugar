using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Stable semantic lookup key for an invocation intrinsic. Matching uses
/// Roslyn symbols only; emitted extern names are not part of the key. The
/// parameter constraint applies only when its ordinal is present, so one key
/// describes an overload family with optional SDK parameters such as
/// includeInactive.
/// </summary>
internal sealed class IntrinsicKey
{
    readonly HashSet<string> _containingTypes;
    readonly HashSet<string> _methods;
    readonly int _minimumParameters;
    readonly int _maximumParameters;
    readonly int _constrainedOrdinal;
    readonly string _constrainedTypeName;

    /// <summary>-1 means any generic arity.</summary>
    public int GenericArity { get; }

    public IntrinsicKey(IEnumerable<string> containingTypes,
        IEnumerable<string> methods, int genericArity,
        int minimumParameters, int maximumParameters,
        int constrainedOrdinal = -1, string constrainedTypeName = null)
    {
        _containingTypes = new HashSet<string>(
            containingTypes ?? throw new ArgumentNullException(nameof(containingTypes)),
            StringComparer.Ordinal);
        _methods = new HashSet<string>(
            methods ?? throw new ArgumentNullException(nameof(methods)),
            StringComparer.Ordinal);
        if (_containingTypes.Count == 0 || _methods.Count == 0)
            throw new ArgumentException(
                "An intrinsic key requires at least one containing type and method.");
        if (genericArity < -1) throw new ArgumentOutOfRangeException(nameof(genericArity));
        if (minimumParameters < 0 || maximumParameters < minimumParameters)
            throw new ArgumentOutOfRangeException(nameof(minimumParameters));
        if (constrainedOrdinal < -1)
            throw new ArgumentOutOfRangeException(nameof(constrainedOrdinal));
        if (constrainedOrdinal >= 0 == string.IsNullOrEmpty(constrainedTypeName))
            throw new ArgumentException(
                "A parameter constraint requires both an ordinal and a type name.");
        GenericArity = genericArity;
        _minimumParameters = minimumParameters;
        _maximumParameters = maximumParameters;
        _constrainedOrdinal = constrainedOrdinal;
        _constrainedTypeName = constrainedTypeName;
    }

    public bool Matches(IMethodSymbol method)
    {
        if (method == null) return false;
        var containingType = method.ContainingType?.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat);
        return containingType != null
               && _containingTypes.Contains(containingType)
               && _methods.Contains(method.Name)
               && (GenericArity < 0 || method.Arity == GenericArity)
               && MatchesParameters(method);
    }

    bool MatchesParameters(IMethodSymbol method)
    {
        var parameters = method.OriginalDefinition.Parameters;
        if (parameters.Length < _minimumParameters
            || parameters.Length > _maximumParameters)
            return false;
        if (_constrainedOrdinal < 0 || _constrainedOrdinal >= parameters.Length)
            return true;
        var parameterType = parameters[_constrainedOrdinal].Type;
        var actual = parameterType.SpecialType == SpecialType.None
            ? parameterType.ToDisplayString(
                SymbolDisplayFormat.CSharpErrorMessageFormat)
            : ExternResolver.GetSpecialTypeName(parameterType.SpecialType);
        return string.Equals(actual, _constrainedTypeName, StringComparison.Ordinal);
    }
}

internal delegate bool InvocationIntrinsicPredicate(
    InvocationHandler handler, IInvocationOperation operation, IMethodSymbol target);

internal delegate CLeaf InvocationIntrinsicLowerer(
    InvocationHandler handler, IInvocationOperation operation, IMethodSymbol target);

/// <summary>One declarative match key plus semantic applicability and lowering.</summary>
internal sealed class InvocationIntrinsicRule
{
    public string Name { get; }
    public IntrinsicKey Key { get; }
    readonly InvocationIntrinsicPredicate _predicate;
    readonly InvocationIntrinsicLowerer _lower;

    public InvocationIntrinsicRule(string name, IntrinsicKey key,
        InvocationIntrinsicLowerer lower,
        InvocationIntrinsicPredicate predicate = null)
    {
        Name = !string.IsNullOrEmpty(name)
            ? name
            : throw new ArgumentException("An intrinsic rule name is required.", nameof(name));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        _lower = lower ?? throw new ArgumentNullException(nameof(lower));
        _predicate = predicate;
    }

    public bool TryLower(InvocationHandler handler, IInvocationOperation operation,
        IMethodSymbol target, out CLeaf result)
    {
        result = null;
        if (!Key.Matches(target)
            || _predicate != null && !_predicate(handler, operation, target))
            return false;
        result = _lower(handler, operation, target);
        return true;
    }
}

/// <summary>Ordered, immutable invocation-intrinsic dispatch table.</summary>
internal sealed class InvocationIntrinsicRegistry
{
    readonly IReadOnlyList<InvocationIntrinsicRule> _rules;

    public InvocationIntrinsicRegistry(IEnumerable<InvocationIntrinsicRule> rules)
    {
        _rules = (rules ?? throw new ArgumentNullException(nameof(rules))).ToArray();
        var duplicate = _rules.GroupBy(rule => rule.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException(
                $"Duplicate invocation intrinsic rule '{duplicate.Key}'.");
    }

    public bool TryLower(InvocationHandler handler, IInvocationOperation operation,
        IMethodSymbol target, out CLeaf result)
    {
        foreach (var rule in _rules)
            if (rule.TryLower(handler, operation, target, out result))
                return true;
        result = null;
        return false;
    }

    public IReadOnlyList<InvocationIntrinsicRule> Rules => _rules;
}
