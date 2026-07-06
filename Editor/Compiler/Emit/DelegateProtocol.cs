using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// First-class delegate ABI. A delegate VALUE is a single reference to a runtime object[5] bundle;
/// assignment is reference copy; dispatch reads the bundle elements. The ABI reserves slot 0 for an
/// explicit provenance tag so SystemObjectArray can be distinguished from class/aggregate/env bundles
/// by convention instead of only by the static type at the producer.
/// </summary>
public static class DelegateAbi
{
    /// <summary>bundle[0]: SystemString ABI provenance tag.</summary>
    public const int Kind = 0;
    /// <summary>bundle[1]: IUdonEventReceiver target. Delegate-null is the BUNDLE reference being null — not [Target].</summary>
    public const int Target = 1;
    /// <summary>bundle[2]: SystemString — the receiving program's bridge EXPORT name (__dlg_{ExportName} / __dlg_{lambdaPrefix}).</summary>
    public const int Method = 2;
    /// <summary>bundle[3]: boxed System.UInt32 — funcaddr of the bridge's {name}__body label. 0u for third-party method groups.
    /// Only ever sourced from a funcaddr const (back-patched) or Const(0u) — never an Int32 intermediate (§1.3).</summary>
    public const int Addr = 3;
    /// <summary>bundle[4]: closure env record, variance wrapper payload, or multicast invocation list.</summary>
    public const int Env = 4;

    public const int BundleSize = 5;
    public const string KindTag = "__usugar_delegate";

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

    /// <summary>The ONE name source for the signature-keyed `__dlgc_{sig}__*` convention globals
    /// (§3.2) — argument slot i, the return slot, and the env slot every dispatch site stages.</summary>
    public static string ConvArgName(string sigPart, int i) => $"__dlgc_{sigPart}__a{i}";
    public static string ConvRetName(string sigPart) => $"__dlgc_{sigPart}__ret";
    public static string ConvEnvName(string sigPart) => $"__dlgc_{sigPart}__env";

    /// <summary>
    /// Generation-time compile errors (§3.4): ref/out delegate params (no copy-in/write-back protocol —
    /// today a silent miscompile, made loud). Tuple-return delegates are SUPPORTED (Stage 1.75 design
    /// 2026-07-04 §1): a tuple return is already a single SystemObjectArray aggregate slot (same
    /// representation as a user-struct return), so the delegate conv-ret and the target method's own
    /// return slot agree with zero adapter code. Variant method-group conversions are SUPPORTED too
    /// (Stage 1.75 §2): the caller (<see cref="HandlerBase.ResolveDelegateBridge"/>) mints a sig adapter
    /// (same-program target, §2.2) or a wrapper (third-party target, §2.2's hinge) BEFORE this runs, so
    /// <paramref name="varianceResolved"/> tells this call the mismatch it's about to see was already
    /// handled — the throw below is armor for a mismatch reaching here UNRESOLVED (should be
    /// unreachable: C# only permits reference-conversion variance in a delegate binding, which the
    /// caller's resolution always handles).
    /// <paramref name="targetMethod"/> is the bound method for method-group bindings, null for lambdas
    /// (a lambda's signature is inferred from the delegate type, so it can never be variant).
    /// </summary>
    public static void ValidateDelegateBinding(INamedTypeSymbol delegateType, IMethodSymbol targetMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null, bool varianceResolved = false)
    {
        var invoke = delegateType?.DelegateInvokeMethod;
        if (invoke == null) return;

        ValidateNoRefOutParams(invoke);
        ValidateNoUserClassSignature(invoke);

        if (!varianceResolved && targetMethod != null
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

    /// <summary>CA-M1 §2-1: the __dlgc_ conv vars are an explicit cross-program byte contract, and a v1
    /// class parameter/return is a program-local object[] bundle that cannot ride it (a foreign delegate
    /// binding is aligned and poison-proof — the only defence is a reject). Shared by the creation-site
    /// binding and the dispatch-site conv-var declaration (a delegate VALUE received from elsewhere never
    /// went through the creation-site check).</summary>
    public static void ValidateNoUserClassSignature(IMethodSymbol invoke)
    {
        foreach (var p in invoke.Parameters)
            if (EmitPolicy.ContainsUserClassType(p.Type))
                throw new System.NotSupportedException(
                    $"A delegate carrying a v1 user class parameter '{p.Name}' is not supported: a class "
                    + "value is a program-local object[] bundle and cannot cross the delegate convention "
                    + "channel. Pass plain data through the delegate instead.");
        if (EmitPolicy.ContainsUserClassType(invoke.ReturnType))
            throw new System.NotSupportedException(
                "A delegate returning a v1 user class is not supported: a class value is a program-local "
                + "object[] bundle and cannot cross the delegate convention channel.");
    }

    /// <summary>The ONLY name source for a plain per-method delegate bridge (a same-program target's
    /// own exact-sig entry point — every other bridge flavor below is keyed by signature instead).</summary>
    public static string BridgeName(string key) => $"__dlg_{key}";

    /// <summary>
    /// Multicast design (2026-07-03 §1.1): the ONLY name source for the per-sig synthetic fan-out
    /// bridge / combine / remove helpers. A multicast bundle routes DelegateAbi.Method/Addr to this bridge;
    /// the combine/remove helpers detect a multicast OPERAND via a compile-time constant string
    /// compare against MulticastFanoutName (§1.4) — never re-derive these strings at another site.
    /// </summary>
    public static string MulticastFanoutName(string sigPart) => $"__dlg_fanout_{sigPart}";
    public static string MulticastCombineName(string sigPart) => $"__dlg_combine_{sigPart}";
    public static string MulticastRemoveName(string sigPart) => $"__dlg_remove_{sigPart}";

    /// <summary>
    /// Variance design (2026-07-04 §2.2, B-1): the ONLY name source for a per-(target, sig-S) sig
    /// adapter bridge — mints under the DELEGATE's declared signature (sig-S) so Method/Addr point at
    /// a sig-S-protocol entry point (Stage-2 §5.4 sig-filter invariant preserved by construction).
    /// <paramref name="targetKey"/> disambiguates two different targets adapting to the same sig-S
    /// (the target's own plain bridge/export name, unique per NameAllocator).
    /// </summary>
    public static string SigAdapterName(string targetKey, string sigPart) => $"__dlg_adapt_{targetKey}_{sigPart}";

    /// <summary>
    /// Variance design (2026-07-04 §2.3, B-2): the ONLY name source for a wrapper-with-payload bridge —
    /// OUTER protocol is sig-S (what callers holding the declared delegate type use), receives an INNER
    /// bundle via slot[3] (bridge-private payload, same principle as a capturing bridge's env record or
    /// a multicast fan-out's invocation list) and fires it through the existing unified dispatch (the
    /// fan-out's one-element form) using the INNER bundle's OWN native protocol (sig-T — the wrapped
    /// value's actual declared type, or a third-party target's own method signature). Keyed by BOTH:
    /// unlike the fan-out (every invocation-list element is ALREADY sig-S-compliant by construction), a
    /// wrapper's inner bundle speaks a DIFFERENT protocol than the outer one, so two different sig-T's
    /// wrapped to the same sig-S need two distinct wrapper bodies (the inner dispatch's conv-var names
    /// are sig-T's, and staging them under the wrong sig would silently drop values across the dispatch
    /// — the exact hazard variance rejection used to prevent). Used both for a third-party variant
    /// method-group target (§2.2's hinge — sig-T is the target method's own signature) and a delegate-
    /// VALUE variant conversion (sig-T is the source delegate type's own Invoke signature).
    /// </summary>
    public static string WrapperName(string outerSigPart, string innerSigPart) => $"__dlg_wrap_{outerSigPart}_{innerSigPart}";

    /// <summary>
    /// The ONE bundle-mint sequence (§1.1): a fresh object[BundleSize] with [Kind]/[Target]/[Method]/
    /// [Addr]/[Env] set in that order — shared by every mint site (delegate creation, the sig-adapter's inner
    /// third-party bundle, the variance wrapper bundle, the multicast fan-out bundle) instead of each
    /// hand-rolling the same four <c>__Set</c> calls. <paramref name="targetFn"/> is a thunk, not a
    /// plain value: every existing call site emits the ctor FIRST and only THEN evaluates its target
    /// operand (which sometimes needs a fresh field load) — passing target eagerly would let C#'s
    /// left-to-right argument evaluation run that load before this method's ctor call, reordering the
    /// emitted instructions (and their scratch-slot numbers) relative to every site's current output.
    /// method/addr/env are cheap pure leaves (Const/FuncRef/already-materialized values) at every
    /// existing call site, so evaluating them eagerly as ordinary arguments never reorders anything.
    /// </summary>
    public static CLeaf EmitBundleMint(CoreBuilder builder, Func<CLeaf> targetFn,
        CLeaf methodNameLeaf, CLeaf addrLeaf, CLeaf envLeaf)
    {
        var setSig = ExternResolver.BuildArraySetSignature("SystemObjectArray", "SystemObject");
        var bundle = builder.ExternCall(ExternResolver.BuildArrayCtorSignature("SystemObjectArray"),
            new List<CLeaf> { builder.Const(BundleSize, "SystemInt32") }, "SystemObjectArray");
        var target = targetFn();
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Kind, "SystemInt32"), builder.Const(KindTag, "SystemString") });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Target, "SystemInt32"), target });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Method, "SystemInt32"), methodNameLeaf });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Addr, "SystemInt32"), addrLeaf });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Env, "SystemInt32"), envLeaf });
        return bundle;
    }

    /// <summary>Read a typed slot from a delegate ABI bundle. All delegate slot reads should route here
    /// so slot numbers stay ABI-owned rather than being re-open-coded at dispatch/equality sites.</summary>
    public static CLeaf ReadSlot(CoreBuilder builder, CLeaf bundle, int slot, string udonType)
        => builder.ExternCall(
            ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
            new List<CLeaf> { bundle, builder.Const(slot, "SystemInt32") },
            udonType);
}

/// <summary>
/// Calling convention for delegate parameters: which UASM fields hold arguments and return value.
/// </summary>
public struct DelegateConvention
{
    public string[] ArgVarIds;
    public string RetVarId;
}
