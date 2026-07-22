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
        if (type is INamedTypeSymbol named && named.IsGenericType
            && named.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter))
            return named.OriginalDefinition.Construct(
                named.TypeArguments.Select(t => CloseType(compilation, t, map)).ToArray());
        return type;
    }

    public static IMethodSymbol CloseMethod(Compilation compilation, IMethodSymbol method,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (method == null || map == null) return method;
        var definition = method.OriginalDefinition;
        bool closeOwner = method.MethodKind is not (MethodKind.LocalFunction or MethodKind.LambdaMethod)
            && method.ContainingType.IsGenericType
            && method.ContainingType.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter);
        bool closeMethod = method.IsGenericMethod
            && method.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter);
        if (!closeOwner && !closeMethod) return method;
        IMethodSymbol relocated = definition;
        if (closeOwner)
        {
            var owner = method.ContainingType.OriginalDefinition.Construct(method.ContainingType.TypeArguments
                .Select(t => CloseType(compilation, t, map)).ToArray());
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
