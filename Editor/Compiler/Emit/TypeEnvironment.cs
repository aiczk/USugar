using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Single authority for closing Roslyn types and methods under a monomorphization environment.</summary>
public static class TypeEnvironment
{
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
        var bindings = new List<(IReadOnlyList<ITypeParameterSymbol>, IReadOnlyList<ITypeSymbol>)>(2);
        if (method.IsGenericMethod)
            bindings.Add((method.OriginalDefinition.TypeParameters, method.TypeArguments));
        if (method.ContainingType.IsGenericType)
            bindings.Add((method.ContainingType.OriginalDefinition.TypeParameters, method.ContainingType.TypeArguments));
        return TypeParamScope.Compose(parent, true, bindings);
    }

    public static IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ForContainingType(
        INamedTypeSymbol type, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> parent = null)
        => !type.IsGenericType ? parent : TypeParamScope.Compose(parent, true, new[]
        {
            ((IReadOnlyList<ITypeParameterSymbol>)type.OriginalDefinition.TypeParameters,
             (IReadOnlyList<ITypeSymbol>)type.TypeArguments)
        });
}
