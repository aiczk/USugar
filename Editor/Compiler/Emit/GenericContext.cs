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

    // Wave-9 round-5 [X6]: first registered specialization per generic DEFINITION. Closures hoisted
    // from a generic body are keyed by IMethodSymbol and therefore SHARED across that body's
    // specializations (last-spec-wins seeding; VM-proven r1=8 vs 3) - a second DISTINCT instantiation
    // of a definition whose body contains a capturing closure is loud. LOOKUP-ONLY.
    public readonly Dictionary<IMethodSymbol, IMethodSymbol> FirstSpecByDefinition
        = new(SymbolEqualityComparer.Default);

    public IDisposable EnterScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map, IMethodSymbol currentMethod)
    {
        if (TypeParamMap != null)
            throw new InvalidOperationException(
                $"EnterTypeParamScope: a type-param map is already active on entry to "
                + $"'{currentMethod?.ToDisplayString() ?? "(none)"}' — a prior scope was not disposed.");
        TypeParamMap = map;
        return new TypeParamScopeToken(this);
    }

    sealed class TypeParamScopeToken : IDisposable
    {
        readonly GenericContext _ctx;
        bool _disposed;

        public TypeParamScopeToken(GenericContext ctx) => _ctx = ctx;

        public void Dispose()
        {
            if (_disposed)
                throw new InvalidOperationException("TypeParamScopeToken disposed twice.");
            _disposed = true;
            _ctx.TypeParamMap = null;
        }
    }
}
