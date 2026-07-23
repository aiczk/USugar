using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Discovers the legacy UdonSharp idiom that treats an object[] as a nominal
/// UdonSharpBehaviour-derived marker type via <c>(T)(object)array</c>.
///
/// These types are not scene components.  Their nominal type exists only to provide compile-time
/// extension-method grouping; their runtime ABI is SystemObjectArray at every storage boundary.
/// Discovery is compilation-wide so fields, generic specializations, parameters and returns cannot
/// independently choose conflicting representations.
/// </summary>
internal sealed class ObjectArrayBehaviourAliasCensus
{
    static readonly ConditionalWeakTable<Compilation, ObjectArrayBehaviourAliasCensus> Cache = new();

    readonly HashSet<INamedTypeSymbol> _aliases =
        new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    public static ObjectArrayBehaviourAliasCensus For(Compilation compilation)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        return Cache.GetValue(compilation, Build);
    }

    ObjectArrayBehaviourAliasCensus()
    {
    }

    public bool IsAlias(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        type = Resolve(type, typeParameterMap);
        return type is INamedTypeSymbol named && _aliases.Contains(named.OriginalDefinition);
    }

    public bool UsesObjectArrayStorage(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        type = Resolve(type, typeParameterMap);
        if (IsAlias(type, typeParameterMap))
            return true;
        return type is IArrayTypeSymbol { Rank: 1 } array
               && IsAlias(array.ElementType, typeParameterMap);
    }

    static ObjectArrayBehaviourAliasCensus Build(Compilation compilation)
    {
        var result = new ObjectArrayBehaviourAliasCensus();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var syntax in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(syntax) is not IInvocationOperation invocation
                    || invocation.Type is not INamedTypeSymbol concrete
                    || !ExternResolver.IsUdonSharpBehaviour(concrete))
                    continue;

                var target = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                var definition = target.OriginalDefinition;
                if (!IsObjectArrayAliasCast(definition, compilation))
                    continue;

                for (var current = concrete;
                     current != null
                     && current.Name != "UdonSharpBehaviour"
                     && current.DeclaringSyntaxReferences.Length > 0;
                     current = current.BaseType)
                    result._aliases.Add(current.OriginalDefinition);
            }
        }
        return result;
    }

    static bool IsObjectArrayAliasCast(IMethodSymbol method, Compilation compilation)
    {
        if (!method.IsGenericMethod
            || method.ReturnType is not ITypeParameterSymbol returnParameter
            || method.Parameters.Length != 1
            || !IsObjectArray(method.Parameters[0].Type)
            || method.DeclaringSyntaxReferences.Length == 0)
            return false;

        var carrier = method.Parameters[0];
        foreach (var syntaxReference in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxReference.GetSyntax();
            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            var root = model.GetOperation(syntax);
            if (root == null) continue;
            foreach (var returned in root.DescendantsAndSelf().OfType<IReturnOperation>())
                if (IsAliasCast(returned.ReturnedValue, carrier, returnParameter))
                    return true;
        }
        return false;
    }

    static bool IsAliasCast(
        IOperation value,
        IParameterSymbol carrier,
        ITypeParameterSymbol returnParameter)
    {
        bool crossedObject = false;
        bool crossedReturnType = false;
        while (value is IConversionOperation conversion)
        {
            crossedObject |= conversion.Type?.SpecialType == SpecialType.System_Object;
            crossedReturnType |= SymbolEqualityComparer.Default.Equals(
                conversion.Type, returnParameter);
            value = conversion.Operand;
        }
        return crossedObject
               && crossedReturnType
               && value is IParameterReferenceOperation parameterReference
               && SymbolEqualityComparer.Default.Equals(parameterReference.Parameter, carrier);
    }

    static bool IsObjectArray(ITypeSymbol type)
        => type is IArrayTypeSymbol array
           && array.Rank == 1
           && array.ElementType.SpecialType == SpecialType.System_Object;

    static ITypeSymbol Resolve(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        var seen = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        while (type is ITypeParameterSymbol parameter
               && typeParameterMap != null
               && typeParameterMap.TryGetValue(parameter, out var resolved))
        {
            if (!seen.Add(parameter)
                || SymbolEqualityComparer.Default.Equals(type, resolved))
                break;
            type = resolved;
        }
        return type;
    }
}
