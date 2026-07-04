using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// First-class delegate ABI (Stage 1, design 2026-06-10 §1). A delegate VALUE is a single reference to a
/// runtime object[4] bundle; assignment is reference copy; dispatch reads the bundle elements. The bundle
/// layout is FROZEN — Stage 2 only starts writing/reading Env, never re-shapes the bundle.
/// </summary>
public static class DelegateAbi
{
    /// <summary>bundle[0]: IUdonEventReceiver target. Delegate-null is the BUNDLE reference being null — not [0].</summary>
    public const int Target = 0;
    /// <summary>bundle[1]: SystemString — the receiving program's bridge EXPORT name (__dlg_{ExportName} / __dlg_{lambdaPrefix}).</summary>
    public const int Method = 1;
    /// <summary>bundle[2]: boxed System.UInt32 — funcaddr of the bridge's {name}__body label. 0u for third-party method groups.
    /// Only ever sourced from a funcaddr const (back-patched) or Const(0u) — never an Int32 intermediate (§1.3).</summary>
    public const int Addr = 2;
    /// <summary>bundle[3]: reserved env slot. Stage 1 writes null at every creation site and never reads it (§1.4).</summary>
    public const int Env = 3;

    public const int BundleSize = 4;

    /// <summary>
    /// Canonical signature key for the global __dlgc_{sig}__a{i} / __dlgc_{sig}__ret convention vars — a
    /// cross-program byte contract (§3.2). Single source of truth (replaces the former
    /// HandlerBase.BuildConventionSigPart / UasmEmitter.BuildBridgeSigPart duplicates). Delegate-typed
    /// params map to SystemObjectArray (bundle references) via the ExternResolver delegate arm.
    /// </summary>
    public static string BuildSigPart(IMethodSymbol invokeOrTarget,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var paramParts = invokeOrTarget.Parameters
            .Select(p => ExternResolver.GetUdonTypeName(p.Type, typeParamMap));
        var retPart = invokeOrTarget.ReturnsVoid
            ? "Void"
            : ExternResolver.GetUdonTypeName(invokeOrTarget.ReturnType, typeParamMap);
        var paramStr = string.Join("_", paramParts);
        if (paramStr == "") paramStr = "Void";
        return $"{paramStr}__{retPart}";
    }

    /// <summary>
    /// Generation-time compile errors (§3.4): ref/out delegate params (no copy-in/write-back protocol —
    /// today a silent miscompile, made loud) and variant method-group conversions (the caller derives the
    /// __dlgc_ name from the delegate type while the bridge derives it from the target method, so a
    /// co/contravariant binding diverges the names — a silent miscompile, made loud). Tuple-return
    /// delegates are SUPPORTED (Stage 1.75 design 2026-07-04 §1): a tuple return is already a single
    /// SystemObjectArray aggregate slot (same representation as a user-struct return), so the delegate
    /// conv-ret and the target method's own return slot agree with zero adapter code.
    /// <paramref name="targetMethod"/> is the bound method for method-group bindings, null for lambdas
    /// (a lambda's signature is inferred from the delegate type, so it can never be variant).
    /// </summary>
    public static void ValidateDelegateBinding(INamedTypeSymbol delegateType, IMethodSymbol targetMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var invoke = delegateType?.DelegateInvokeMethod;
        if (invoke == null) return;

        ValidateNoRefOutParams(invoke);

        if (targetMethod != null
            && BuildSigPart(invoke, typeParamMap) != BuildSigPart(targetMethod, typeParamMap))
            throw new System.NotSupportedException(
                "Variant method-group conversion to a delegate is not supported "
              + "(parameter/return types must match the delegate signature exactly under Udon type mapping).");
    }

    /// <summary>ref/out reject (§3.4-1) — shared by the creation path and the convention-var declaration path.</summary>
    public static void ValidateNoRefOutParams(IMethodSymbol invoke)
    {
        foreach (var p in invoke.Parameters)
            if (p.RefKind != RefKind.None)
                throw new System.NotSupportedException(
                    "Delegate types with ref/out parameters are not supported.");
    }

    /// <summary>
    /// Multicast design (2026-07-03 §1.1): the ONLY name source for the per-sig synthetic fan-out
    /// bridge / combine / remove helpers. A multicast bundle routes bundle[1]/[2] to this bridge;
    /// the combine/remove helpers detect a multicast OPERAND via a compile-time constant string
    /// compare against MulticastFanoutName (§1.4) — never re-derive these strings at another site.
    /// </summary>
    public static string MulticastFanoutName(string sigPart) => $"__dlg_fanout_{sigPart}";
    public static string MulticastCombineName(string sigPart) => $"__dlg_combine_{sigPart}";
    public static string MulticastRemoveName(string sigPart) => $"__dlg_remove_{sigPart}";
}

/// <summary>
/// Calling convention for delegate parameters: which UASM fields hold arguments and return value.
/// </summary>
public struct DelegateConvention
{
    public string[] ArgVarIds;
    public string RetVarId;
}
