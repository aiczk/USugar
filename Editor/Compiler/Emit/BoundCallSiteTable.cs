using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;

internal readonly struct CallSiteBindingScope : IEquatable<CallSiteBindingScope>
{
    readonly SpecializationKey? _method;
    readonly INamedTypeSymbol _type;

    CallSiteBindingScope(SpecializationKey method)
    {
        _method = method;
        _type = null;
    }

    CallSiteBindingScope(INamedTypeSymbol type)
    {
        _method = null;
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public static CallSiteBindingScope ForMethod(SpecializationKey method)
        => new CallSiteBindingScope(method);

    public static CallSiteBindingScope ForType(INamedTypeSymbol type)
        => new CallSiteBindingScope(type);

    public bool Equals(CallSiteBindingScope other)
        => Nullable.Equals(_method, other._method)
           && SymbolEqualityComparer.Default.Equals(_type, other._type);

    public override bool Equals(object obj)
        => obj is CallSiteBindingScope other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (_method?.GetHashCode() ?? 0) * 31
                   + (_type == null
                       ? 0
                       : SymbolEqualityComparer.Default.GetHashCode(_type));
        }
    }
}

internal readonly struct BoundCallSiteKey : IEquatable<BoundCallSiteKey>
{
    public readonly SyntaxNode Syntax;
    public readonly CallableSiteKind Kind;
    public readonly IMethodSymbol TargetDefinition;
    public readonly CallSiteBindingScope? Scope;

    public BoundCallSiteKey(
        SyntaxNode syntax,
        CallableSiteKind kind,
        IMethodSymbol target,
        CallSiteBindingScope? scope)
    {
        Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
        Kind = kind;
        TargetDefinition = target?.OriginalDefinition
            ?? throw new ArgumentNullException(nameof(target));
        Scope = scope;
    }

    public bool Equals(BoundCallSiteKey other)
        => ReferenceEquals(Syntax, other.Syntax)
           && Kind == other.Kind
           && SymbolEqualityComparer.Default.Equals(
               TargetDefinition, other.TargetDefinition)
           && Nullable.Equals(Scope, other.Scope);

    public override bool Equals(object obj)
        => obj is BoundCallSiteKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Syntax.GetHashCode();
            hash = hash * 31 + (int)Kind;
            hash = hash * 31
                + SymbolEqualityComparer.Default.GetHashCode(TargetDefinition);
            hash = hash * 31 + (Scope?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

internal sealed class BoundCallSite
{
    public readonly ResolvedCallableSite Callable;
    public readonly DispatchPlan? Dispatch;

    public BoundCallSite(ResolvedCallableSite callable, DispatchPlan? dispatch)
    {
        Callable = callable ?? throw new ArgumentNullException(nameof(callable));
        Dispatch = dispatch;
    }

    public DispatchPlan RequireDispatch()
        => Dispatch ?? throw new InvalidOperationException(
            $"Instance callable site '{Callable.Site.Target}' has no bound dispatch plan.");
}

/// <summary>
/// Exact specialization-keyed callable and dispatch decisions for every source call site.
/// </summary>
internal sealed class BoundCallSiteTable
{
    readonly IReadOnlyDictionary<BoundCallSiteKey, BoundCallSite> _sites;
    public int Count => _sites.Count;

    public BoundCallSiteTable(IDictionary<BoundCallSiteKey, BoundCallSite> sites)
        => _sites = new ReadOnlyDictionary<BoundCallSiteKey, BoundCallSite>(
            new Dictionary<BoundCallSiteKey, BoundCallSite>(
                sites ?? throw new ArgumentNullException(nameof(sites))));

    public BoundCallSite Require(
        SyntaxNode syntax,
        CallableSiteKind kind,
        IMethodSymbol target,
        CallSiteBindingScope? scope)
    {
        var key = new BoundCallSiteKey(syntax, kind, target, scope);
        if (_sites.TryGetValue(key, out var site)) return site;
        throw new InvalidOperationException(
            $"Callable site '{syntax}' ({kind}, {target?.ToDisplayString()}) "
            + "was absent from the bound program.");
    }
}
