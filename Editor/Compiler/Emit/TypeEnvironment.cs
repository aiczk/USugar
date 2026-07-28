using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Single authority for closing Roslyn types and methods under a monomorphization environment.</summary>
public static class TypeEnvironment
{
    /// <summary>
    /// Canonical specialization vector for a method: every containing-type argument from the
    /// outermost owner to the innermost owner, followed by the method's own arguments.
    /// Roslyn exposes a nested type's own arguments separately from its containing type, so reading
    /// only <c>method.ContainingType.TypeArguments</c> aliases distinct outer-owner specializations.
    /// </summary>
    internal static ImmutableArray<ITypeSymbol> SpecializationArguments(IMethodSymbol method)
    {
        if (method == null)
            throw new System.ArgumentNullException(nameof(method));
        var result = ImmutableArray.CreateBuilder<ITypeSymbol>();
        foreach (var owner in ContainingTypes(method.ContainingType))
            result.AddRange(owner.TypeArguments);
        if (method.IsGenericMethod)
            result.AddRange(method.TypeArguments);
        return result.ToImmutable();
    }

    public static ITypeSymbol CloseType(Compilation compilation, ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (type == null || map == null) return type;
        if (type is ITypeParameterSymbol tp && map.TryGetValue(tp, out var resolved))
        {
            if (TypeParamIdComparer.Instance.Equals(tp, resolved as ITypeParameterSymbol)) return type;
            return CloseType(compilation, resolved, map);
        }
        if (type is IArrayTypeSymbol array)
            return compilation.CreateArrayTypeSymbol(CloseType(compilation, array.ElementType, map), array.Rank);
        if (type is INamedTypeSymbol named && ClassTypeObjectContext.ContainsTypeParameter(named))
            return CloseNamedType(compilation, named, map);
        return type;
    }

    static INamedTypeSymbol CloseNamedType(Compilation compilation, INamedTypeSymbol named,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        var definition = named.OriginalDefinition;
        INamedTypeSymbol relocated = definition;
        if (named.ContainingType != null)
        {
            var closedOwner = (INamedTypeSymbol)CloseType(compilation, named.ContainingType, map);
            relocated = closedOwner.GetTypeMembers(definition.Name, definition.Arity)
                .First(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, definition));
        }

        if (definition.Arity == 0) return relocated;
        return relocated.Construct(named.TypeArguments
            .Select(t => CloseType(compilation, t, map)).ToArray());
    }

    public static IMethodSymbol CloseMethod(Compilation compilation, IMethodSymbol method,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (method == null || map == null) return method;
        var definition = method.OriginalDefinition;
        bool closeOwner = method.MethodKind is not (MethodKind.LocalFunction or MethodKind.LambdaMethod)
            && ClassTypeObjectContext.ContainsTypeParameter(method.ContainingType);
        bool closeMethod = method.IsGenericMethod
            && method.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter);
        if (!closeOwner && !closeMethod) return method;
        IMethodSymbol relocated = definition;
        if (closeOwner)
        {
            var owner = (INamedTypeSymbol)CloseType(compilation, method.ContainingType, map);
            relocated = owner.GetMembers(definition.Name).OfType<IMethodSymbol>()
                .First(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, definition));
        }
        if (closeMethod)
            relocated = relocated.Construct(method.TypeArguments
                .Select(t => CloseType(compilation, t, map)).ToArray());
        return relocated;
    }

    public static IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ForMethod(
        IMethodSymbol method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> parent = null)
    {
        if (method == null)
            throw new System.ArgumentNullException(nameof(method));
        var bindings = ContainingTypeBindings(method.ContainingType).ToList();
        if (method.IsGenericMethod)
            bindings.Add((method.OriginalDefinition.TypeParameters, method.TypeArguments));
        return TypeParamScope.Compose(parent, true, bindings);
    }

    public static IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ForContainingType(
        INamedTypeSymbol type, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> parent = null)
    {
        if (type == null) return parent;
        var bindings = ContainingTypeBindings(type).ToList();
        return bindings.Count == 0
            ? parent
            : TypeParamScope.Compose(parent, true, bindings);
    }

    static IEnumerable<INamedTypeSymbol> ContainingTypes(INamedTypeSymbol type)
    {
        var owners = new Stack<INamedTypeSymbol>();
        for (var current = type; current != null; current = current.ContainingType)
            owners.Push(current);
        while (owners.Count > 0)
            yield return owners.Pop();
    }

    static IEnumerable<(
        IReadOnlyList<ITypeParameterSymbol> Parameters,
        IReadOnlyList<ITypeSymbol> Arguments)> ContainingTypeBindings(INamedTypeSymbol type)
    {
        foreach (var owner in ContainingTypes(type))
        {
            var parameters = owner.OriginalDefinition.TypeParameters;
            if (parameters.Length == 0) continue;
            yield return (parameters, owner.TypeArguments);
        }
    }
}
