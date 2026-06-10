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
    /// today a silent miscompile, made loud), tuple-return delegates (multi-ReturnSlot bridges are
    /// unplanned — Stage 1 scope-out), and variant method-group conversions (the caller derives the
    /// __dlgc_ name from the delegate type while the bridge derives it from the target method, so a
    /// co/contravariant binding diverges the names — a silent miscompile, made loud).
    /// <paramref name="targetMethod"/> is the bound method for method-group bindings, null for lambdas
    /// (a lambda's signature is inferred from the delegate type, so it can never be variant).
    /// </summary>
    public static void ValidateDelegateBinding(INamedTypeSymbol delegateType, IMethodSymbol targetMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var invoke = delegateType?.DelegateInvokeMethod;
        if (invoke == null) return;

        ValidateNoRefOutParams(invoke);

        if (!invoke.ReturnsVoid && invoke.ReturnType.IsTupleType)
            throw new System.NotSupportedException(
                $"Tuple-return delegate '{delegateType.Name}' is not supported.");

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
}

/// <summary>
/// Represents the three synthetic UASM fields that back a single delegate variable.
/// Constructed from a base field name; provides the resolved field IDs.
/// LEGACY (pre-bundle ABI): remaining consumers (InvocationHandler / NullableHandler / OperatorHandler)
/// are replaced by EmitDelegateDispatch in M2 — deleted then (design §5.1 #1).
/// </summary>
public readonly struct DelegateBundle
{
    public readonly string Target;
    public readonly string Method;
    public readonly string Addr;

    public const string TargetSuffix = "__target";
    public const string MethodSuffix = "__method";
    public const string AddrSuffix   = "__addr";

    public DelegateBundle(string fieldName)
    {
        Target = fieldName + TargetSuffix;
        Method = fieldName + MethodSuffix;
        Addr   = fieldName + AddrSuffix;
    }
}

/// <summary>
/// Calling convention for delegate parameters: which UASM fields hold arguments and return value.
/// </summary>
public struct DelegateConvention
{
    public string[] ArgVarIds;
    public string RetVarId;
}
