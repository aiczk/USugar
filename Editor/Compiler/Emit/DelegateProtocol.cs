using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// First-class delegate ABI. A delegate VALUE is a single reference to a runtime object[5] bundle;
/// assignment is reference copy; dispatch reads the bundle elements. Slot zero follows BundleAbi
/// and carries the exact declared delegate identity.
/// </summary>
internal static class DelegateAbi
{
    /// <summary>bundle[0]: SystemString exact runtime type identity.</summary>
    public const int Type = BundleAbi.Type;
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
    public static readonly string KindTag =
        BundleAbi.KindTag(RuntimeBundleKind.Delegate);

    /// <summary>A delegate value's VM representation: the bundle object[] and its boxed slot element type.</summary>
    public const string BundleType = "SystemObjectArray";
    public const string SlotType = "SystemObject";

    /// <summary>
    /// Canonical signature key for the global __dlgc_{sig}__a{i} / __dlgc_{sig}__ret convention vars — a
    /// cross-program byte contract (§3.2). Single source of truth (replaces the former
    /// LoweringServices.BuildConventionSigPart / UasmEmitter.BuildBridgeSigPart duplicates). Delegate-typed
    /// params map to SystemObjectArray (bundle references) via the ExternResolver delegate arm.
    /// </summary>
    public static string BuildSigPart(IMethodSymbol invokeOrTarget,
        IUdonTypeSystem types,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        if (invokeOrTarget == null)
            throw new ArgumentNullException(nameof(invokeOrTarget));
        if (types == null) throw new ArgumentNullException(nameof(types));
        var paramParts = invokeOrTarget.Parameters
            .Select(p =>
            {
                var type = types.GetUdonTypeName(p.Type, typeParamMap);
                return p.RefKind switch
                {
                    RefKind.Ref => "Ref" + type,
                    RefKind.Out => "Out" + type,
                    RefKind.In => "In" + type,
                    _ => type,
                };
            });
        var retPart = invokeOrTarget.ReturnsVoid
            ? "Void"
            : types.GetUdonTypeName(
                invokeOrTarget.ReturnType, typeParamMap);
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
    /// (Stage 1.75 §2): the caller (<see cref="LoweringServices.ResolveDelegateBridge"/>) mints a sig adapter
    /// (same-program target, §2.2) or a wrapper (third-party target, §2.2's hinge) BEFORE this runs, so
    /// the finalized <see cref="DelegateBindingPlan"/> tells this call whether the mismatch was already
    /// handled — the throw below is armor for a mismatch reaching here UNRESOLVED (should be
    /// unreachable: C# only permits reference-conversion variance in a delegate binding, which the
    /// caller's resolution always handles).
    /// The plan's target is the bound method; a lambda's inferred signature
    /// therefore remains an exact, non-adapter binding.
    /// </summary>
    public static void ValidateDelegateBinding(INamedTypeSymbol delegateType,
        DelegateBindingPlan binding, IUdonTypeSystem types,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        if (binding == null)
            throw new ArgumentNullException(nameof(binding));
        if (types == null) throw new ArgumentNullException(nameof(types));
        var invoke = delegateType?.DelegateInvokeMethod;
        if (invoke == null) return;

        var signaturesDiffer =
            BuildSigPart(invoke, types, typeParamMap)
            != BuildSigPart(
                binding.TargetMethod, types, typeParamMap);
        var planAdaptsSignature =
            binding.Kind is DelegateBindingKind.SignatureAdapter
                or DelegateBindingKind.Wrapper;
        if (signaturesDiffer && !planAdaptsSignature)
            throw new System.NotSupportedException(
                "Variant method-group conversion to a delegate is not supported "
              + "(parameter/return types must match the delegate signature exactly under Udon type mapping).");
        if (!signaturesDiffer && planAdaptsSignature)
            throw new InvalidOperationException(
                $"Delegate binding '{binding.BridgeName}' is classified "
                + $"as {binding.Kind}, but its source and target signatures "
                + "are already identical.");
    }

    /// <summary>True when the convention carries a program-local user-class bundle. Such a signature
    /// may use the convention globals inside one program, but must never take the SPV/SCE cross-program
    /// dispatch arm.</summary>
    public static bool IsProgramLocalSignature(
        IMethodSymbol invoke,
        IUdonTypeSystem types,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        if (types == null) throw new ArgumentNullException(nameof(types));
        return invoke.Parameters.Any(p =>
                   types.SourceShape(p.Type, typeParameterMap)
                       .ContainsUserClassPayload)
               || types.SourceShape(
                       invoke.ReturnType, typeParameterMap)
                   .ContainsUserClassPayload;
    }

    /// <summary>The bridge-name prefix owned by <see cref="BridgeName"/>/<see cref="BridgeTargetKey"/> —
    /// never re-spell it at a use site (census-pinned by NamingContractCensusTests).</summary>
    public const string BridgePrefix = "__dlg_";

    /// <summary>The ONLY name source for a plain per-method delegate bridge (a same-program target's
    /// own exact-sig entry point — every other bridge flavor below is keyed by signature instead).</summary>
    public static string BridgeName(string key) => BridgePrefix + key;

    /// <summary>Inverse of <see cref="BridgeName"/>: strip the bridge prefix back to the target key,
    /// passing a plain export name (a named method's own export) through unchanged.</summary>
    public static string BridgeTargetKey(string bridgeOrExportName)
        => bridgeOrExportName.StartsWith(BridgePrefix)
            ? bridgeOrExportName.Substring(BridgePrefix.Length)
            : bridgeOrExportName;

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
    public static CLeaf EmitBundleMint(
        CoreBuilder builder, CLeaf runtimeTypeId,
        Func<CLeaf> targetFn,
        CLeaf methodNameLeaf, CLeaf addrLeaf, CLeaf envLeaf)
    {
        var bundle = builder.ExternCall(UdonAbi.ArrayConstructor(BundleType),
            new List<CLeaf> { builder.Const(BundleSize, StorageTypes.Int32) }, new StorageType(BundleType));
        EmitBundleSlotWrites(
            builder, bundle, runtimeTypeId, targetFn,
            methodNameLeaf, addrLeaf, envLeaf);
        return bundle;
    }

    /// <summary>Mint a delegate bundle into a caller-owned slot. Use this when the surrounding ABI helper
    /// already exposes a stable temporary slot and changing that materialization would perturb generated
    /// bytecode layout.</summary>
    public static CLeaf EmitBundleMintToSlot(
        CoreBuilder builder, int bundleSlot, CLeaf runtimeTypeId,
        Func<CLeaf> targetFn,
        CLeaf methodNameLeaf, CLeaf addrLeaf, CLeaf envLeaf)
    {
        builder.EmitAssign(bundleSlot, builder.ExternCall(
            UdonAbi.ArrayConstructor(BundleType),
            new List<CLeaf> { builder.Const(BundleSize, StorageTypes.Int32) },
            new StorageType(BundleType)));
        var bundle = builder.SlotRef(bundleSlot);
        EmitBundleSlotWrites(
            builder, bundle, runtimeTypeId, targetFn,
            methodNameLeaf, addrLeaf, envLeaf);
        return bundle;
    }

    static void EmitBundleSlotWrites(
        CoreBuilder builder, CLeaf bundle, CLeaf runtimeTypeId,
        Func<CLeaf> targetFn,
        CLeaf methodNameLeaf, CLeaf addrLeaf, CLeaf envLeaf)
    {
        if (runtimeTypeId == null)
            throw new ArgumentNullException(nameof(runtimeTypeId));
        var setSig = UdonAbi.ArraySet(BundleType, SlotType);
        var target = targetFn();
        builder.EmitExternVoid(setSig, new List<CLeaf>
        {
            bundle,
            builder.Const(Type, StorageTypes.Int32),
            runtimeTypeId
        });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Target, StorageTypes.Int32), target });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Method, StorageTypes.Int32), methodNameLeaf });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Addr, StorageTypes.Int32), addrLeaf });
        builder.EmitExternVoid(setSig, new List<CLeaf> { bundle, builder.Const(Env, StorageTypes.Int32), envLeaf });
    }

    /// <summary>Read a typed slot from a delegate ABI bundle. All delegate slot reads should route here
    /// so slot numbers stay ABI-owned rather than being re-open-coded at dispatch/equality sites.</summary>
    public static CLeaf ReadSlot(CoreBuilder builder, CLeaf bundle, int slot, string udonType)
        => builder.ExternCall(
            UdonAbi.ArrayGet(BundleType, SlotType),
            new List<CLeaf> { bundle, builder.Const(slot, StorageTypes.Int32) },
            new StorageType(udonType));

    public static bool IsDelegateType(ITypeSymbol type)
        => type is INamedTypeSymbol named && named.DelegateInvokeMethod != null;

    public static CLeaf CompareToNull(CoreBuilder builder, CLeaf bundle, bool isNotEquals)
    {
        var signature = isNotEquals
            ? UdonAbi.ObjectInequality
            : UdonAbi.ObjectEquality;
        return builder.ExternCall(signature,
            new List<CLeaf> { bundle, builder.Const(null, StorageTypes.Object) }, StorageTypes.Boolean);
    }

    public static CLeaf IsTaggedBundle(CoreBuilder builder, CLeaf bundle)
        => BundleProbe.IsTagged(
            builder, bundle, KindTag, BundleSize);

    public static CLeaf HasTarget(CoreBuilder builder, CLeaf target)
        => builder.ExternCall(UdonAbiKey.Method("UnityEngineObject", "op_Inequality", new[] { "UnityEngineObject", "UnityEngineObject" }, "SystemBoolean"),
            new List<CLeaf> { target, builder.Const(null, StorageTypes.Object) }, StorageTypes.Boolean);

    public static CLeaf HasMethod(CoreBuilder builder, CLeaf methodName)
        => builder.ExternCall(UdonAbi.ObjectInequality,
            new List<CLeaf> { methodName, builder.Const(null, StorageTypes.Object) }, StorageTypes.Boolean);

    public static CLeaf HasAddress(CoreBuilder builder, CLeaf address)
        => builder.ExternCall(UdonAbiKey.Method("SystemUInt32", "op_Inequality", new[] { "SystemUInt32", "SystemUInt32" }, "SystemBoolean"),
            new List<CLeaf> { address, builder.Const(0u, StorageTypes.UInt32) }, StorageTypes.Boolean);

    public static void EmitInvalidBundleLog(CoreBuilder builder, string className, string receiverDescription)
        => builder.EmitExternVoid(UdonAbi.DebugLogError,
            new List<CLeaf>
            {
                builder.Const(
                    $"USugar: invalid delegate bundle — value is not a USugar delegate ({className}.{receiverDescription})",
                    StorageTypes.String)
            });

    public static CLeaf CompareDelegates(CoreBuilder builder, CLeaf left, CLeaf right, bool isNotEquals)
    {
        var nullValue = builder.Const(null, StorageTypes.Object);
        var leftNull = builder.ExternCall(
            UdonAbi.ObjectEquality,
            new List<CLeaf> { left, nullValue }, StorageTypes.Boolean);
        var rightNull = builder.ExternCall(
            UdonAbi.ObjectEquality,
            new List<CLeaf> { right, nullValue }, StorageTypes.Boolean);
        var anyNull = builder.ExternCall(
            UdonAbi.BooleanLogicalOr,
            new List<CLeaf> { leftNull, rightNull }, StorageTypes.Boolean);

        var resultSlot = builder.AllocScratch(StorageTypes.Boolean);
        builder.EmitIf(anyNull,
            _ => builder.EmitAssign(resultSlot, builder.ExternCall(
                UdonAbiKey.Method("SystemBoolean", "op_Equality", new[] { "SystemBoolean", "SystemBoolean" }, "SystemBoolean"),
                new List<CLeaf> { leftNull, rightNull }, StorageTypes.Boolean)),
            _ =>
            {
                // CW10 (2026-07-15): dispatch reads bundle slots only inside IsTaggedBundle (hand-rolled
                // object[] cast back to a delegate, §2.6; security-filter interference) — this twin read
                // slots 1/2/4 bare, so `d == handler` faulted (OOB __Get / string-equality cast) where
                // invoking the same value LogErrors and continues. Either operand untagged → plain
                // reference equality of the two bundle refs (C#-consistent for identical laundered refs).
                var leftTagged = IsTaggedBundle(builder, left);
                var rightTagged = IsTaggedBundle(builder, right);
                var bothTagged = builder.ExternCall(
                    UdonAbi.BooleanLogicalAnd,
                    new List<CLeaf> { leftTagged, rightTagged }, StorageTypes.Boolean);
                builder.EmitIf(bothTagged,
                    _ =>
                    {
                        var leftTarget = ReadSlot(builder, left, Target, "SystemObject");
                        var rightTarget = ReadSlot(builder, right, Target, "SystemObject");
                        var targetEq = builder.ExternCall(
                            UdonAbi.ObjectEquality,
                            new List<CLeaf> { leftTarget, rightTarget }, StorageTypes.Boolean);

                        var leftMethod = ReadSlot(builder, left, Method, "SystemString");
                        var rightMethod = ReadSlot(builder, right, Method, "SystemString");
                        var methodEq = builder.ExternCall(
                            UdonAbi.StringEquality,
                            new List<CLeaf> { leftMethod, rightMethod }, StorageTypes.Boolean);

                        var leftEnv = ReadSlot(builder, left, Env, "SystemObject");
                        var rightEnv = ReadSlot(builder, right, Env, "SystemObject");
                        var envEq = builder.ExternCall(
                            UdonAbi.ObjectEquality,
                            new List<CLeaf> { leftEnv, rightEnv }, StorageTypes.Boolean);

                        var targetMethodEq = builder.ExternCall(
                            UdonAbi.BooleanLogicalAnd,
                            new List<CLeaf> { targetEq, methodEq }, StorageTypes.Boolean);
                        builder.EmitAssign(resultSlot, builder.ExternCall(
                            UdonAbi.BooleanLogicalAnd,
                            new List<CLeaf> { targetMethodEq, envEq }, StorageTypes.Boolean));
                    },
                    _ => builder.EmitAssign(resultSlot, builder.ExternCall(
                        UdonAbi.ObjectEquality,
                        new List<CLeaf> { left, right }, StorageTypes.Boolean)));
            });

        CLeaf result = builder.SlotRef(resultSlot);
        if (isNotEquals)
            result = builder.ExternCall(
                UdonAbi.BooleanNot,
                new List<CLeaf> { result }, StorageTypes.Boolean);
        return result;
    }
}
