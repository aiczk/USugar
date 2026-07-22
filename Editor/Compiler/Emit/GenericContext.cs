using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Owns the active generic substitution map and specialization plan for one emitter.</summary>
public sealed class GenericContext
{
    public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap { get; private set; }

    readonly HashSet<IMethodSymbol> _plannedSpecializations =
        new(SymbolEqualityComparer.Default);
    public bool BodyEmissionStarted { get; private set; }

    public void SetPlannedSpecializations(IEnumerable<IMethodSymbol> methods)
    {
        if (BodyEmissionStarted)
            throw new InvalidOperationException("Specialization plan cannot change after body emission starts.");
        _plannedSpecializations.Clear();
        foreach (var method in methods) _plannedSpecializations.Add(method);
    }

    public bool IsPlannedSpecialization(IMethodSymbol method)
        => _plannedSpecializations.Contains(method);

    public void BeginBodyEmission() => BodyEmissionStarted = true;

    public IDisposable EnterScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        IMethodSymbol currentMethod)
    {
        if (TypeParamMap != null)
            throw new InvalidOperationException(
                $"EnterTypeParamScope: a type-param map is already active on entry to "
                + $"'{currentMethod?.ToDisplayString() ?? "(none)"}'; a prior scope was not disposed.");
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
