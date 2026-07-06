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
