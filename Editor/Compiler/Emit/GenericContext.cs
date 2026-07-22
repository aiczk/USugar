using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns the active generic type-parameter substitution map for a single emitter. The current model is
/// deliberately depth-1: EmitMethod is drained serially, and nested closure methods get their own later
/// EmitMethod entry with a freshly composed map.
/// </summary>
public sealed class GenericContext
{
    public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap { get; private set; }

    // SS2B (M2b): a pending GENERIC LOCAL FUNCTION spec rides with its per-spec closure record (one
    // constructed symbol can pend once per enclosing spec); named specs carry null.
    public readonly List<(IMethodSymbol Method, MethodContext.ClosureSpec Spec)> PendingSpecs = new();

    // First registered specialization per generic DEFINITION (first-wins record; historical [X6]
    // origin). Since the per-spec closure separation, closures are NOT shared across specs and no
    // second-instantiation reject exists — the sole remaining read is ComposeClosureKeyArgs' owner
    // fallback for owners outside the current chain. LOOKUP-ONLY.
    public readonly Dictionary<IMethodSymbol, IMethodSymbol> FirstSpecByDefinition
        = new(SymbolEqualityComparer.Default);

    /// <summary>First-wins seed: record the FIRST specialization seen per generic definition. Single-sourced
    /// so every registration path (HandlerBase.RegisterFirstGenericSpec + the emitter's foreign-static /
    /// struct-member / base-copy registration loops, which cannot call the protected HandlerBase method)
    /// seeds identically — a past open-coded loop forgot to seed and shipped a bogus TArray (B70).</summary>
    public void SeedFirstSpec(IMethodSymbol constructed)
    {
        var genericDef = constructed.OriginalDefinition;
        if (!FirstSpecByDefinition.ContainsKey(genericDef))
            FirstSpecByDefinition[genericDef] = constructed;
    }

    public IDisposable EnterScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map, IMethodSymbol currentMethod)
    {
        if (TypeParamMap != null)
            throw new InvalidOperationException(
                $"EnterTypeParamScope: a type-param map is already active on entry to "
                + $"'{currentMethod?.ToDisplayString() ?? "(none)"}' — a prior scope was not disposed.");
        TypeParamMap = map;
        return new TypeParamScopeToken(this, null);
    }

    public IDisposable EnterOverlayScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        var previous = TypeParamMap;
        TypeParamMap = map;
        return new TypeParamScopeToken(this, previous);
    }

    sealed class TypeParamScopeToken : IDisposable
    {
        readonly GenericContext _ctx;
        readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _previous;
        bool _disposed;

        public TypeParamScopeToken(GenericContext ctx,
            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> previous)
        {
            _ctx = ctx;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                throw new InvalidOperationException("TypeParamScopeToken disposed twice.");
            _disposed = true;
            _ctx.TypeParamMap = _previous;
        }
    }
}
