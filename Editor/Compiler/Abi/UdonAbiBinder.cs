using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Maps closed Roslyn member semantics onto the exact ABI surface exposed by
/// the installed Udon SDK. Candidate construction and registry selection live
/// here; emit handlers never probe or repair signature strings themselves.
/// </summary>
public sealed class UdonAbiBinder
{
    static readonly string[] UnityInstanceOwnerFallbacks =
    {
        "UnityEngineComponent",
        "UnityEngineBehaviour",
        "UnityEngineMonoBehaviour",
        "UnityEngineObject",
    };

    readonly UdonAbiCatalog _catalog;

    public UdonAbiBinder(UdonAbiCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public UdonAbiCatalog Catalog => _catalog;

    public BoundExtern BindExact(UdonAbiKey key) => _catalog.Require(key);

    public BoundExtern BindFirst(string operation, IEnumerable<UdonAbiKey> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        var attempted = new List<string>();
        var seen = new HashSet<UdonAbiKey>();
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate)) continue;
            attempted.Add(candidate.ToString());
            if (_catalog.Contains(candidate))
                return _catalog.Require(candidate);
        }
        throw new NotSupportedException(
            $"No registered Udon extern implements {operation}. Tried: {string.Join(", ", attempted)}");
    }

    /// <summary>
    /// Bind a C# conversion operator. Its ABI parameter and return types come
    /// from the operator declaration, never from the expression's storage type.
    /// The latter may have been erased or remapped by generic specialization.
    /// </summary>
    public BoundExtern BindConversion(IMethodSymbol method,
        ITypeSymbol expressionSource, ITypeSymbol expressionDestination,
        Func<ITypeSymbol, string> getUdonType)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (getUdonType == null) throw new ArgumentNullException(nameof(getUdonType));
        var parameterTypes = DeclaredParameterTypes(method, getUdonType);
        var returnType = getUdonType(method.ReturnType);
        var methodName = method.Name;
        var owners = new[]
        {
            getUdonType(method.ContainingType),
            expressionSource == null ? null : getUdonType(expressionSource),
            expressionDestination == null ? null : getUdonType(expressionDestination),
        };
        return BindFirst(
            $"conversion operator '{method.ToDisplayString()}'",
            owners.Where(owner => !string.IsNullOrEmpty(owner))
                .Select(owner => UdonAbiKey.Method(
                    owner, methodName, parameterTypes, returnType)));
    }

    /// <summary>
    /// Bind a user-declared operator using its declared ref modes. The SDK uses
    /// a Ref suffix for ref/out/in/read-only-ref parameters.
    /// </summary>
    public BoundExtern BindOperator(IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (getUdonType == null) throw new ArgumentNullException(nameof(getUdonType));
        var signature = UdonAbiKey.Method(
            getUdonType(method.ContainingType),
            method.Name,
            DeclaredParameterTypes(method, getUdonType),
            getUdonType(method.ReturnType));
        return BindFirst(
            $"operator '{method.ToDisplayString()}'",
            new[] { signature });
    }

    public BoundExtern BindMethod(IMethodSymbol method, string owner,
        Func<ITypeSymbol, string> getUdonType, string[] parameterOverride = null)
    {
        if (TryBindMethod(method, owner, getUdonType, parameterOverride, out var bound))
            return bound;
        throw new NotSupportedException(
            $"No registered Udon extern implements method '{method.ToDisplayString()}' "
            + $"for ABI owner '{owner}'.");
    }

    public bool TryBindMethod(IMethodSymbol method, string owner,
        Func<ITypeSymbol, string> getUdonType, string[] parameterOverride,
        out BoundExtern bound)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (string.IsNullOrEmpty(owner)) throw new ArgumentException("An ABI owner is required.", nameof(owner));
        if (getUdonType == null) throw new ArgumentNullException(nameof(getUdonType));

        var candidates = new List<UdonAbiKey>();
        var methodName = method.Name;
        var returnType = getUdonType(method.ReturnType);

        void AddShape(IMethodSymbol shape, string[] overrideTypes = null)
        {
            var parameterTypes = overrideTypes ?? DeclaredParameterTypes(shape, getUdonType);
            var primary = UdonAbiKey.Method(
                owner, methodName, parameterTypes, returnType);
            candidates.Add(primary);

            if (method.IsStatic) return;
            var mappedOwner = ExternResolver.RemapExternOwnerType(
                ExternResolver.SanitizeTypeName(owner));
            if (!mappedOwner.StartsWith("UnityEngine", StringComparison.Ordinal)
                && mappedOwner != "VRCUdonCommonInterfacesIUdonEventReceiver")
                return;
            foreach (var fallbackOwner in UnityInstanceOwnerFallbacks)
                if (fallbackOwner != primary.Owner)
                    candidates.Add(primary.WithOwner(fallbackOwner));
        }

        AddShape(method, parameterOverride);

        if (parameterOverride == null && method.IsGenericMethod)
            AddShape(method.OriginalDefinition);
        else if (parameterOverride == null
                 && method.Parameters.Any(parameter =>
                     parameter.Type.IsReferenceType && parameter.Type.TypeKind != TypeKind.Array))
        {
            var coerced = method.Parameters.Select(parameter =>
            {
                var type = parameter.Type.IsReferenceType && parameter.Type.TypeKind != TypeKind.Array
                    ? "SystemObject"
                    : getUdonType(parameter.Type);
                return parameter.RefKind == RefKind.None ? type : type + "Ref";
            }).ToArray();
            AddShape(method, coerced);
        }

        var seen = new HashSet<UdonAbiKey>();
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate) || !_catalog.Contains(candidate)) continue;
            bound = _catalog.Require(candidate);
            return true;
        }
        bound = null;
        return false;
    }

    public BoundExtern BindFieldSetter(string owner, string fieldName,
        string valueType, bool isValueType = true, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var plain = UdonAbiKey.OmittedResult(mappedOwner, "set_" + fieldName, valueType);
        var withVoid = UdonAbiKey.VoidMethod(mappedOwner, "set_" + fieldName, valueType);
        var candidates = new List<UdonAbiKey>(isValueType
            ? new[] { plain, withVoid }
            : new[] { withVoid, plain });
        AddUnityOwnerFallbacks(candidates, mappedOwner, hasReceiver);
        return BindFirst(
            $"field setter '{owner}.{fieldName}'", candidates);
    }

    public BoundExtern BindPropertySetter(string owner, string propertyName,
        string valueType, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var candidates = new List<UdonAbiKey>
        {
            UdonAbiKey.VoidMethod(mappedOwner, "set_" + propertyName, valueType),
            UdonAbiKey.OmittedResult(mappedOwner, "set_" + propertyName, valueType),
        };
        AddUnityOwnerFallbacks(candidates, mappedOwner, hasReceiver);
        return BindFirst(
            $"property setter '{owner}.{propertyName}'", candidates);
    }

    public BoundExtern BindPropertyGetter(string owner, string propertyName,
        string returnType, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var candidates = new List<UdonAbiKey>
        {
            UdonAbiKey.Method(mappedOwner, "get_" + propertyName, returnType),
        };
        AddUnityOwnerFallbacks(candidates, mappedOwner, hasReceiver);
        return BindFirst(
            $"property getter '{owner}.{propertyName}'", candidates);
    }

    public BoundExtern BindIndexerGetter(string owner, string propertyName,
        IReadOnlyList<string> indexTypes, string returnType, bool hasReceiver = true)
    {
        if (indexTypes == null) throw new ArgumentNullException(nameof(indexTypes));
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var candidates = new List<UdonAbiKey>
        {
            UdonAbiKey.Method(mappedOwner, "get_" + propertyName, indexTypes, returnType),
        };
        AddUnityOwnerFallbacks(candidates, mappedOwner, hasReceiver);
        return BindFirst(
            $"indexer getter '{owner}.{propertyName}'", candidates);
    }

    public BoundExtern BindIndexerSetter(string owner, string propertyName,
        IReadOnlyList<string> indexTypes, string valueType, bool hasReceiver = true)
    {
        if (indexTypes == null) throw new ArgumentNullException(nameof(indexTypes));
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var parameters = indexTypes.Concat(new[] { valueType }).ToArray();
        var candidates = new List<UdonAbiKey>
        {
            UdonAbiKey.Method(mappedOwner, "set_" + propertyName,
                parameters, "SystemVoid"),
            UdonAbiKey.OmittedResult(mappedOwner, "set_" + propertyName,
                parameters),
        };
        AddUnityOwnerFallbacks(candidates, mappedOwner, hasReceiver);
        return BindFirst(
            $"indexer setter '{owner}.{propertyName}'", candidates);
    }

    static void AddUnityOwnerFallbacks(List<UdonAbiKey> candidates,
        string mappedOwner, bool hasReceiver)
    {
        if (!hasReceiver
            || (!mappedOwner.StartsWith("UnityEngine", StringComparison.Ordinal)
                && mappedOwner != "VRCUdonCommonInterfacesIUdonEventReceiver"))
            return;
        var primaryShapes = candidates.ToArray();
        foreach (var fallbackOwner in UnityInstanceOwnerFallbacks)
        {
            if (fallbackOwner == mappedOwner) continue;
            foreach (var shape in primaryShapes)
                candidates.Add(shape.WithOwner(fallbackOwner));
        }
    }

    static string[] DeclaredParameterTypes(IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => method.Parameters.Select(parameter =>
        {
            var type = getUdonType(parameter.Type);
            return parameter.RefKind == RefKind.None ? type : type + "Ref";
        }).ToArray();
}
