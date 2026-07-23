using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>One exact parameter constraint within an intrinsic overload family.</summary>
public readonly struct IntrinsicParameterConstraint
{
    public int Ordinal { get; }
    public string TypeName { get; }

    public IntrinsicParameterConstraint(int ordinal, string typeName)
    {
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        Ordinal = ordinal;
        TypeName = !string.IsNullOrEmpty(typeName)
            ? typeName
            : throw new ArgumentException("A parameter type name is required.", nameof(typeName));
    }
}

/// <summary>
/// Declarative shape of an intrinsic overload family. Constraints apply when
/// their ordinal is present, allowing one key to describe optional SDK
/// parameters such as includeInactive.
/// </summary>
public sealed class IntrinsicParameterShape
{
    readonly IReadOnlyList<IntrinsicParameterConstraint> _constraints;

    public int MinimumCount { get; }
    public int MaximumCount { get; }

    public IntrinsicParameterShape(int minimumCount, int maximumCount,
        params IntrinsicParameterConstraint[] constraints)
    {
        if (minimumCount < 0 || maximumCount < minimumCount)
            throw new ArgumentOutOfRangeException(nameof(minimumCount));
        MinimumCount = minimumCount;
        MaximumCount = maximumCount;
        _constraints = constraints ?? Array.Empty<IntrinsicParameterConstraint>();
    }

    public bool Matches(IMethodSymbol method)
    {
        var parameters = method.OriginalDefinition.Parameters;
        if (parameters.Length < MinimumCount || parameters.Length > MaximumCount)
            return false;
        foreach (var constraint in _constraints)
        {
            if (constraint.Ordinal >= parameters.Length) continue;
            var parameterType = parameters[constraint.Ordinal].Type;
            var actual = parameterType.SpecialType == SpecialType.None
                ? parameterType.ToDisplayString(
                    SymbolDisplayFormat.CSharpErrorMessageFormat)
                : ExternResolver.GetSpecialTypeName(parameterType.SpecialType);
            if (!string.Equals(actual, constraint.TypeName, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Stable semantic lookup key for an invocation intrinsic. Matching uses
/// Roslyn symbols only; emitted extern names are not part of the key.
/// </summary>
public sealed class IntrinsicKey
{
    readonly HashSet<string> _containingTypes;
    readonly HashSet<string> _methods;
    readonly IReadOnlyList<IntrinsicParameterShape> _parameterShapes;

    /// <summary>-1 means any generic arity.</summary>
    public int GenericArity { get; }

    public IntrinsicKey(IEnumerable<string> containingTypes,
        IEnumerable<string> methods, int genericArity,
        params IntrinsicParameterShape[] parameterShapes)
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
        GenericArity = genericArity;
        _parameterShapes = parameterShapes == null || parameterShapes.Length == 0
            ? new[] { new IntrinsicParameterShape(0, int.MaxValue) }
            : parameterShapes;
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
               && _parameterShapes.Any(shape => shape.Matches(method));
    }
}

public delegate bool InvocationIntrinsicPredicate(
    InvocationHandler handler, IInvocationOperation operation, IMethodSymbol target);

public delegate CLeaf InvocationIntrinsicLowerer(
    InvocationHandler handler, IInvocationOperation operation, IMethodSymbol target);

/// <summary>One declarative match key plus semantic applicability and lowering.</summary>
public sealed class InvocationIntrinsicRule
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
public sealed class InvocationIntrinsicRegistry
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
