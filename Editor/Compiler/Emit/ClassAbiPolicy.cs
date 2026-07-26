using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Class ABI invariants and runtime-type-test policy, independent of IR emission.</summary>
internal static class ClassAbiPolicy
{
    public static void AssertClosed(ITypeSymbol type, string site)
    {
        if (!ClassTypeObjectContext.ContainsTypeParameter(type)) return;
        throw new InvalidOperationException(
            $"Open type '{type.ToDisplayString()}' reached {site} after the closed-specialization census.");
    }

    public static void ValidateRuntimeTypeTest(ITypeSymbol resolvedTarget,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        IUdonTypeSystem types)
    {
        if (types == null) throw new ArgumentNullException(nameof(types));
        if (types.IsRuntimeDistinguishable(resolvedTarget, map)) return;
        ClassAbi.RejectRuntimeTypeTest(resolvedTarget);
        var display = resolvedTarget.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var hint = resolvedTarget is INamedTypeSymbol named && named.DelegateInvokeMethod != null
            ? " (Udon represents every delegate as one runtime type, so it cannot tell delegate signatures "
              + "apart and would match any delegate, then read the wrong argument/return channel)"
            : "";
        throw new NotSupportedException(
            $"Runtime type test against '{display}' is not supported: Udon collapses it and several distinct "
            + "types onto one runtime type tag, so an 'is'/'switch'/'as' test cannot tell them apart and "
            + "would match the wrong value" + hint + ". Keep the value typed as its static type instead of "
            + "recovering it with a runtime type test.");
    }
}
