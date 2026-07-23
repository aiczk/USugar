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

    public BoundExtern BindExact(ExternSignature signature) => _catalog.Require(signature);
    public BoundExtern BindExact(string signature) => _catalog.Require(signature);

    public BoundExtern BindFirst(string operation, IEnumerable<ExternSignature> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        var attempted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Text)) continue;
            attempted.Add(candidate.Text);
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
        var methodName = ExternResolver.GetOperatorExternName(method.Name);
        var owners = new[]
        {
            getUdonType(method.ContainingType),
            expressionSource == null ? null : getUdonType(expressionSource),
            expressionDestination == null ? null : getUdonType(expressionDestination),
        };
        return BindFirst(
            $"conversion operator '{method.ToDisplayString()}'",
            owners.Where(owner => !string.IsNullOrEmpty(owner))
                .Select(owner => (ExternSignature)ExternResolver.BuildMethodSignature(
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
        var signature = ExternResolver.BuildMethodSignature(
            getUdonType(method.ContainingType),
            ExternResolver.GetOperatorExternName(method.Name),
            DeclaredParameterTypes(method, getUdonType),
            getUdonType(method.ReturnType));
        return BindFirst(
            $"operator '{method.ToDisplayString()}'",
            new[] { (ExternSignature)signature });
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

        var candidates = new List<ExternSignature>();
        var methodName = $"__{method.Name}";
        var returnType = getUdonType(method.ReturnType);

        void AddShape(IMethodSymbol shape, string[] overrideTypes = null)
        {
            var parameterTypes = overrideTypes ?? DeclaredParameterTypes(shape, getUdonType);
            var primary = ExternResolver.BuildMethodSignature(
                owner, methodName, parameterTypes, returnType);
            candidates.Add(primary);

            if (method.IsStatic) return;
            var mappedOwner = ExternResolver.RemapExternOwnerType(
                ExternResolver.SanitizeTypeName(owner));
            if (!mappedOwner.StartsWith("UnityEngine", StringComparison.Ordinal)
                && mappedOwner != "VRCUdonCommonInterfacesIUdonEventReceiver")
                return;
            var rest = primary.Substring(primary.IndexOf(".__", StringComparison.Ordinal));
            foreach (var fallbackOwner in UnityInstanceOwnerFallbacks)
                if (fallbackOwner != mappedOwner)
                    candidates.Add(fallbackOwner + rest);
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Text) || !_catalog.Contains(candidate)) continue;
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
        var suffix = $".__set_{fieldName}__{ExternResolver.SanitizeTypeName(valueType)}";
        var prefix = mappedOwner + suffix;
        var withVoid = prefix + "__SystemVoid";
        var candidates = new List<string>(isValueType
            ? new[] { prefix, withVoid }
            : new[] { withVoid, prefix });
        AddUnityOwnerFallbacks(candidates, mappedOwner, suffix, hasReceiver, isValueType);
        return BindFirst(
            $"field setter '{owner}.{fieldName}'",
            candidates.Select(candidate => (ExternSignature)candidate));
    }

    public BoundExtern BindPropertySetter(string owner, string propertyName,
        string valueType, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var suffix = $".__set_{propertyName}__{ExternResolver.SanitizeTypeName(valueType)}";
        var prefix = mappedOwner + suffix;
        var candidates = new List<string> { prefix + "__SystemVoid", prefix };
        AddUnityOwnerFallbacks(candidates, mappedOwner, suffix, hasReceiver, preferPlain: false);
        return BindFirst(
            $"property setter '{owner}.{propertyName}'",
            candidates.Select(candidate => (ExternSignature)candidate));
    }

    public BoundExtern BindPropertyGetter(string owner, string propertyName,
        string returnType, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var suffix = $".__get_{propertyName}__{ExternResolver.SanitizeTypeName(returnType)}";
        var candidates = new List<ExternSignature> { mappedOwner + suffix };
        if (hasReceiver
            && (mappedOwner.StartsWith("UnityEngine", StringComparison.Ordinal)
                || mappedOwner == "VRCUdonCommonInterfacesIUdonEventReceiver"))
        {
            foreach (var fallbackOwner in UnityInstanceOwnerFallbacks)
                if (fallbackOwner != mappedOwner)
                    candidates.Add(fallbackOwner + suffix);
        }
        return BindFirst(
            $"property getter '{owner}.{propertyName}'", candidates);
    }

    public BoundExtern BindIndexerGetter(string owner, string propertyName,
        IReadOnlyList<string> indexTypes, string returnType, bool hasReceiver = true)
    {
        if (indexTypes == null) throw new ArgumentNullException(nameof(indexTypes));
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var parameters = string.Join("_", indexTypes.Select(ExternResolver.SanitizeTypeName));
        var suffix = $".__get_{propertyName}__{parameters}__"
                     + ExternResolver.SanitizeTypeName(returnType);
        var candidates = new List<string> { mappedOwner + suffix };
        AddUnityOwnerFallbacks(candidates, mappedOwner, suffix, hasReceiver, preferPlain: true);
        return BindFirst(
            $"indexer getter '{owner}.{propertyName}'",
            candidates.Select(candidate => (ExternSignature)candidate));
    }

    public BoundExtern BindIndexerSetter(string owner, string propertyName,
        IReadOnlyList<string> indexTypes, string valueType, bool hasReceiver = true)
    {
        if (indexTypes == null) throw new ArgumentNullException(nameof(indexTypes));
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var parameters = indexTypes
            .Select(ExternResolver.SanitizeTypeName)
            .Concat(new[] { ExternResolver.SanitizeTypeName(valueType) });
        var suffix = $".__set_{propertyName}__{string.Join("_", parameters)}";
        var prefix = mappedOwner + suffix;
        var candidates = new List<string> { prefix + "__SystemVoid", prefix };
        AddUnityOwnerFallbacks(candidates, mappedOwner, suffix, hasReceiver, preferPlain: false);
        return BindFirst(
            $"indexer setter '{owner}.{propertyName}'",
            candidates.Select(candidate => (ExternSignature)candidate));
    }

    static void AddUnityOwnerFallbacks(List<string> candidates, string mappedOwner,
        string suffix, bool hasReceiver, bool preferPlain)
    {
        if (!hasReceiver
            || (!mappedOwner.StartsWith("UnityEngine", StringComparison.Ordinal)
                && mappedOwner != "VRCUdonCommonInterfacesIUdonEventReceiver"))
            return;
        foreach (var fallbackOwner in UnityInstanceOwnerFallbacks)
        {
            if (fallbackOwner == mappedOwner) continue;
            var prefix = fallbackOwner + suffix;
            if (preferPlain)
            {
                candidates.Add(prefix);
                candidates.Add(prefix + "__SystemVoid");
            }
            else
            {
                candidates.Add(prefix + "__SystemVoid");
                candidates.Add(prefix);
            }
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
