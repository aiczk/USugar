using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.CodeAnalysis;

internal enum GenericComponentQueryDisposition
{
    BehaviourShim,
    TypedGenericExtern,
    ErasedTypeQuery,
}

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
    public readonly CallSiteBindingScope? Scope;

    public BoundCallSiteKey(
        SyntaxNode syntax,
        CallableSiteKind kind,
        CallSiteBindingScope? scope)
    {
        Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
        Kind = kind;
        Scope = scope;
    }

    public bool Equals(BoundCallSiteKey other)
        => ReferenceEquals(Syntax, other.Syntax)
           && Kind == other.Kind
           && Nullable.Equals(Scope, other.Scope);

    public override bool Equals(object obj)
        => obj is BoundCallSiteKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Syntax.GetHashCode();
            hash = hash * 31 + (int)Kind;
            hash = hash * 31 + (Scope?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

internal sealed class BoundCallSite
{
    public readonly ResolvedCallableSite Callable;
    public readonly DispatchPlan? Dispatch;
    public readonly INamedTypeSymbol ReceiverType;
    public readonly bool UsesRuntimeDispatch;
    public readonly GenericComponentQueryDisposition?
        ComponentQueryDisposition;
    public IMethodSymbol Target
        => Dispatch?.BoundTarget ?? Callable.Site.Target;

    public BoundCallSite(
        ResolvedCallableSite callable,
        DispatchPlan? dispatch,
        INamedTypeSymbol receiverType,
        bool usesRuntimeDispatch,
        GenericComponentQueryDisposition?
            componentQueryDisposition)
    {
        Callable = callable ?? throw new ArgumentNullException(nameof(callable));
        Dispatch = dispatch;
        ReceiverType = receiverType;
        UsesRuntimeDispatch = usesRuntimeDispatch;
        ComponentQueryDisposition = componentQueryDisposition;
    }

    public DispatchPlan RequireDispatch()
        => Dispatch ?? throw new InvalidOperationException(
            $"Instance callable site '{Callable.Site.Target}' has no bound dispatch plan.");

    public GenericComponentQueryDisposition
        RequireComponentQueryDisposition()
        => ComponentQueryDisposition
           ?? throw new InvalidOperationException(
               $"Generic component query '{Callable.Site.Target}' has no bound lowering disposition.");
}
