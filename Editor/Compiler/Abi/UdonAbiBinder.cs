using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Maps closed Roslyn member semantics onto the exact ABI surface exposed by
/// the installed Udon SDK. Candidate construction and registry selection live
/// here; emit handlers never probe or repair signature strings themselves.
/// </summary>
internal sealed class UdonAbiBinder
{
    readonly UdonAbiCatalog _catalog;

    internal UdonAbiBinder(UdonAbiCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    internal BoundExtern BindExact(UdonAbiKey key) => _catalog.Require(key);

    BoundExtern BindFirst(string operation, IEnumerable<UdonAbiKey> candidates)
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

    BoundExtern BindMostSpecificOwner(
        string operation,
        string actualOwner,
        bool hasReceiver,
        Func<string, IEnumerable<IEnumerable<UdonAbiKey>>>
            shapeTiers)
    {
        if (TryBindMostSpecificOwner(
                operation, actualOwner, hasReceiver, shapeTiers,
                out var bound, out var attempted))
            return bound;
        throw new NotSupportedException(
            $"No registered Udon extern implements {operation}. "
            + $"Tried: {string.Join(", ", attempted)}");
    }

    bool TryBindMostSpecificOwner(
        string operation,
        string actualOwner,
        bool hasReceiver,
        Func<string, IEnumerable<IEnumerable<UdonAbiKey>>>
            shapeTiers,
        out BoundExtern bound,
        out IReadOnlyList<string> attempted)
    {
        if (string.IsNullOrWhiteSpace(actualOwner))
            throw new ArgumentException(
                "An ABI owner is required.", nameof(actualOwner));
        if (shapeTiers == null)
            throw new ArgumentNullException(nameof(shapeTiers));

        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(actualOwner));
        var owners = hasReceiver
            ? _catalog.GetAssignableOwners(mappedOwner)
            : new[] { mappedOwner };
        var attempts = new List<string>();
        var matches = new Dictionary<string, BoundExtern>(
            StringComparer.Ordinal);

        foreach (var owner in owners)
        {
            BoundExtern ownerMatch = null;
            foreach (var tier in shapeTiers(owner))
            {
                var tierMatches = tier
                    .Distinct()
                    .Select(candidate =>
                    {
                        attempts.Add(candidate.ToString());
                        return _catalog.Contains(candidate)
                            ? _catalog.Require(candidate)
                            : null;
                    })
                    .Where(candidate => candidate != null)
                    .ToArray();
                if (tierMatches.Length > 1)
                    throw new InvalidOperationException(
                        $"Installed Udon ABI is ambiguous for "
                        + $"{operation} at owner '{owner}': "
                        + string.Join(", ",
                            tierMatches.Select(match => match.Text)));
                if (tierMatches.Length == 0) continue;
                ownerMatch = tierMatches[0];
                break;
            }
            if (ownerMatch != null)
                matches.Add(owner, ownerMatch);
        }

        var mostSpecific = matches
            .Where(candidate => !matches.Keys.Any(other =>
                other != candidate.Key
                && _catalog.IsAssignableOwner(
                    other, candidate.Key)))
            .ToArray();
        if (mostSpecific.Length > 1)
            throw new InvalidOperationException(
                $"Installed Udon ABI has no unique most-specific owner "
                + $"for {operation} on '{mappedOwner}': "
                + string.Join(", ",
                    mostSpecific.Select(candidate =>
                        candidate.Value.Text)));

        attempted = attempts;
        if (mostSpecific.Length == 1)
        {
            bound = mostSpecific[0].Value;
            return true;
        }
        bound = null;
        return false;
    }

    /// <summary>
    /// Bind a C# conversion operator. Its ABI parameter and return types come
    /// from the operator declaration, never from the expression's storage type.
    /// The latter may have been erased or remapped by generic specialization.
    /// </summary>
    internal BoundExtern BindConversion(IMethodSymbol method,
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
    internal BoundExtern BindOperator(IMethodSymbol method,
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

    internal BoundExtern BindMethod(IMethodSymbol method, string owner,
        Func<ITypeSymbol, string> getUdonType, string[] parameterOverride = null)
    {
        if (TryBindMethod(
                method, owner, getUdonType, parameterOverride,
                out var bound))
            return bound;
        throw new NotSupportedException(
            $"No registered Udon extern implements method '{method.ToDisplayString()}' "
            + $"for ABI owner '{owner}'.");
    }

    internal bool TryBindMethod(IMethodSymbol method, string owner,
        Func<ITypeSymbol, string> getUdonType, string[] parameterOverride,
        out BoundExtern bound)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (string.IsNullOrEmpty(owner)) throw new ArgumentException("An ABI owner is required.", nameof(owner));
        if (getUdonType == null) throw new ArgumentNullException(nameof(getUdonType));

        var methodName = method.Name;
        var returnType = getUdonType(method.ReturnType);

        IEnumerable<IEnumerable<UdonAbiKey>> ShapeTiers(
            string candidateOwner)
        {
            yield return new[]
            {
                UdonAbiKey.Method(
                    candidateOwner,
                    methodName,
                    parameterOverride
                    ?? DeclaredParameterTypes(method, getUdonType),
                    returnType),
            };

            if (parameterOverride == null && method.IsGenericMethod)
                yield return new[]
                {
                    UdonAbiKey.Method(
                        candidateOwner,
                        methodName,
                        DeclaredParameterTypes(
                            method.OriginalDefinition, getUdonType),
                        returnType),
                };
            else if (parameterOverride == null
                     && method.Parameters.Any(parameter =>
                         parameter.Type.IsReferenceType
                         && parameter.Type.TypeKind
                         != TypeKind.Array))
            {
                var coerced = method.Parameters.Select(parameter =>
                {
                    var type = parameter.Type.IsReferenceType
                               && parameter.Type.TypeKind
                               != TypeKind.Array
                        ? "SystemObject"
                        : getUdonType(parameter.Type);
                    return parameter.RefKind == RefKind.None
                        ? type
                        : type + "Ref";
                }).ToArray();
                yield return new[]
                {
                    UdonAbiKey.Method(
                        candidateOwner,
                        methodName,
                        coerced,
                        returnType),
                };
            }
        }

        return TryBindMostSpecificOwner(
            $"method '{method.ToDisplayString()}'",
            owner,
            hasReceiver: !method.IsStatic,
            ShapeTiers,
            out bound,
            out _);
    }

    internal BoundExtern BindFieldSetter(string owner, string fieldName,
        string valueType, bool isValueType = true, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var plain = UdonAbiKey.OmittedResult(mappedOwner, "set_" + fieldName, valueType);
        var withVoid = UdonAbiKey.VoidMethod(mappedOwner, "set_" + fieldName, valueType);
        return BindMostSpecificOwner(
            $"field setter '{owner}.{fieldName}'",
            mappedOwner,
            hasReceiver,
            candidateOwner => new[]
            {
                isValueType
                    ? new[]
                    {
                        plain.WithOwner(candidateOwner),
                        withVoid.WithOwner(candidateOwner),
                    }
                    : new[]
                    {
                        withVoid.WithOwner(candidateOwner),
                        plain.WithOwner(candidateOwner),
                    },
            });
    }

    internal BoundExtern BindPropertySetter(string owner, string propertyName,
        string valueType, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        return BindMostSpecificOwner(
            $"property setter '{owner}.{propertyName}'",
            mappedOwner,
            hasReceiver,
            candidateOwner => new[]
            {
                new[]
                {
                    UdonAbiKey.VoidMethod(
                        candidateOwner,
                        "set_" + propertyName,
                        valueType),
                    UdonAbiKey.OmittedResult(
                        candidateOwner,
                        "set_" + propertyName,
                        valueType),
                },
            });
    }

    internal BoundExtern BindPropertyGetter(string owner, string propertyName,
        string returnType, bool hasReceiver = true)
    {
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        return BindMostSpecificOwner(
            $"property getter '{owner}.{propertyName}'",
            mappedOwner,
            hasReceiver,
            candidateOwner => new[]
            {
                new[]
                {
                    UdonAbiKey.Method(
                        candidateOwner,
                        "get_" + propertyName,
                        returnType),
                },
            });
    }

    internal BoundExtern BindIndexerGetter(string owner, string propertyName,
        IReadOnlyList<string> indexTypes, string returnType, bool hasReceiver = true)
    {
        if (indexTypes == null) throw new ArgumentNullException(nameof(indexTypes));
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        return BindMostSpecificOwner(
            $"indexer getter '{owner}.{propertyName}'",
            mappedOwner,
            hasReceiver,
            candidateOwner => new[]
            {
                new[]
                {
                    UdonAbiKey.Method(
                        candidateOwner,
                        "get_" + propertyName,
                        indexTypes,
                        returnType),
                },
            });
    }

    internal BoundExtern BindIndexerSetter(string owner, string propertyName,
        IReadOnlyList<string> indexTypes, string valueType, bool hasReceiver = true)
    {
        if (indexTypes == null) throw new ArgumentNullException(nameof(indexTypes));
        var mappedOwner = ExternResolver.RemapExternOwnerType(
            ExternResolver.SanitizeTypeName(owner));
        var parameters = indexTypes.Concat(new[] { valueType }).ToArray();
        return BindMostSpecificOwner(
            $"indexer setter '{owner}.{propertyName}'",
            mappedOwner,
            hasReceiver,
            candidateOwner => new[]
            {
                new[]
                {
                    UdonAbiKey.Method(
                        candidateOwner,
                        "set_" + propertyName,
                        parameters,
                        "SystemVoid"),
                    UdonAbiKey.OmittedResult(
                        candidateOwner,
                        "set_" + propertyName,
                        parameters),
                },
            });
    }

    static string[] DeclaredParameterTypes(IMethodSymbol method,
        Func<ITypeSymbol, string> getUdonType)
        => method.Parameters.Select(parameter =>
        {
            var type = getUdonType(parameter.Type);
            return parameter.RefKind == RefKind.None ? type : type + "Ref";
        }).ToArray();
}
