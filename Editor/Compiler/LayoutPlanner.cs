using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>A single return value slot: UASM variable ID + Udon type name.</summary>
public sealed class ReturnSlot
{
    public readonly string Id;
    public readonly string UdonType;
    public ReturnSlot(string id, string udonType) { Id = id; UdonType = udonType; }
}

/// <summary>Immutable layout for a single method's UASM naming.</summary>
public class MethodLayout
{
    public string ExportName { get; }
    public string BodyLabel { get; }
    public IReadOnlyList<string> ParamIds { get; }
    /// <summary>Return slots: empty for void, 1 element for scalar, N for tuple.</summary>
    public IReadOnlyList<ReturnSlot> Returns { get; }

    public MethodLayout(string exportName, string bodyLabel, IReadOnlyList<string> paramIds,
        IReadOnlyList<ReturnSlot> returns)
    {
        ExportName = exportName;
        BodyLabel = bodyLabel;
        ParamIds = paramIds;
        Returns = returns ?? Array.Empty<ReturnSlot>();
    }

    // ── Convenience accessors (eases migration of downstream consumers) ──

    /// <summary>Single return ID (N=1 only). Null for void or tuple.</summary>
    public string ReturnId => Returns.Count == 1 ? Returns[0].Id : null;
}
 
/// <summary>Immutable layout for a single field's UASM naming.</summary>
public class FieldLayout
{
    public string VarId { get; }
    public string UdonType { get; }
    public FieldFlags Flags { get; }

    public FieldLayout(string varId, string udonType, FieldFlags flags)
    {
        VarId = varId;
        UdonType = udonType;
        Flags = flags;
    }
}

/// <summary>Bridge export layout for a method used as a delegate target.</summary>
public class DelegateBridgeLayout
{
    public string BridgeExportName { get; }
    public MethodLayout RealMethodLayout { get; }

    public DelegateBridgeLayout(string bridgeExportName, MethodLayout realMethodLayout)
    {
        BridgeExportName = bridgeExportName;
        RealMethodLayout = realMethodLayout;
    }
}

/// <summary>Immutable layout for a type's complete UASM variable naming.</summary>
public class TypeLayout
{
    public IReadOnlyDictionary<IMethodSymbol, MethodLayout> Methods { get; }
    public IReadOnlyDictionary<IFieldSymbol, FieldLayout> Fields { get; }
    public IReadOnlyDictionary<string, int> SymbolCounters { get; }
    public IReadOnlyDictionary<IMethodSymbol, DelegateBridgeLayout> DelegateBridges { get; }

    public TypeLayout(
        Dictionary<IMethodSymbol, MethodLayout> methods,
        Dictionary<IFieldSymbol, FieldLayout> fields,
        IReadOnlyDictionary<string, int> symbolCounters = null,
        Dictionary<IMethodSymbol, DelegateBridgeLayout> delegateBridges = null)
    {
        Methods = methods;
        Fields = fields;
        SymbolCounters = symbolCounters ?? new Dictionary<string, int>();
        DelegateBridges = delegateBridges
            ?? new Dictionary<IMethodSymbol, DelegateBridgeLayout>(SymbolEqualityComparer.Default);
    }
}

/// <summary>
/// Single source of truth for all UASM variable naming.
/// Computes TypeLayout once per type (cached). All consumers
/// (Emitter, cross-class calls) get consistent names.
/// </summary>
public class LayoutPlanner
{
    readonly Compilation _compilation;
    readonly Dictionary<INamedTypeSymbol, TypeLayout> _cache = new(SymbolEqualityComparer.Default);
    bool _frozen;

    public static readonly Dictionary<string, string> UdonEventNames = new()
    {
        // Lifecycle
        ["Start"] = "_start", ["Update"] = "_update", ["LateUpdate"] = "_lateUpdate",
        ["PostLateUpdate"] = "_postLateUpdate",
        ["FixedUpdate"] = "_fixedUpdate", ["OnEnable"] = "_onEnable", ["OnDisable"] = "_onDisable",
        ["OnDestroy"] = "_onDestroy",
        // Player
        ["OnPlayerJoined"] = "_onPlayerJoined", ["OnPlayerLeft"] = "_onPlayerLeft",
        ["OnPlayerRespawn"] = "_onPlayerRespawn", ["OnPlayerRestored"] = "_onPlayerRestored",
        ["OnPlayerSuspendChanged"] = "_onPlayerSuspendChanged",
        // Player trigger
        ["OnPlayerTriggerEnter"] = "_onPlayerTriggerEnter",
        ["OnPlayerTriggerExit"] = "_onPlayerTriggerExit",
        ["OnPlayerTriggerStay"] = "_onPlayerTriggerStay",
        // Player collision
        ["OnPlayerCollisionEnter"] = "_onPlayerCollisionEnter",
        ["OnPlayerCollisionExit"] = "_onPlayerCollisionExit",
        ["OnPlayerCollisionStay"] = "_onPlayerCollisionStay",
        // Player particle
        ["OnPlayerParticleCollision"] = "_onPlayerParticleCollision",
        ["OnControllerColliderHitPlayer"] = "_onControllerColliderHitPlayer",
        // Avatar
        ["OnAvatarChanged"] = "_onAvatarChanged",
        ["OnAvatarEyeHeightChanged"] = "_onAvatarEyeHeightChanged",
        ["OnMasterTransferred"] = "_onMasterTransferred",
        // Serialization
        ["OnDeserialization"] = "_onDeserialization",
        ["OnPreSerialization"] = "_onPreSerialization",
        ["OnPostSerialization"] = "_onPostSerialization",
        // Interaction / Pickup
        ["Interact"] = "_interact", ["OnPickup"] = "_onPickup", ["OnDrop"] = "_onDrop",
        ["OnPickupUseDown"] = "_onPickupUseDown", ["OnPickupUseUp"] = "_onPickupUseUp",
        ["OnOwnershipTransferred"] = "_onOwnershipTransferred",
        ["OnOwnershipRequest"] = "_onOwnershipRequest",
        // Station
        ["OnStationEntered"] = "_onStationEntered", ["OnStationExited"] = "_onStationExited",
        // Video
        ["OnVideoError"] = "_onVideoError", ["OnVideoReady"] = "_onVideoReady",
        ["OnVideoStart"] = "_onVideoStart", ["OnVideoPlay"] = "_onVideoPlay",
        ["OnVideoPause"] = "_onVideoPause", ["OnVideoEnd"] = "_onVideoEnd",
        ["OnVideoLoop"] = "_onVideoLoop",
        // Network download
        ["OnStringLoadSuccess"] = "_onStringLoadSuccess", ["OnStringLoadError"] = "_onStringLoadError",
        ["OnImageLoadSuccess"] = "_onImageLoadSuccess", ["OnImageLoadError"] = "_onImageLoadError",
        // Input
        ["InputJump"] = "_inputJump", ["InputUse"] = "_inputUse",
        ["InputGrab"] = "_inputGrab", ["InputDrop"] = "_inputDrop",
        ["InputMoveHorizontal"] = "_inputMoveHorizontal", ["InputMoveVertical"] = "_inputMoveVertical",
        ["InputLookHorizontal"] = "_inputLookHorizontal", ["InputLookVertical"] = "_inputLookVertical",
        ["OnInputMethodChanged"] = "_onInputMethodChanged",
        ["OnLanguageChanged"] = "_onLanguageChanged",
        // Collision / Trigger (non-player)
        ["OnTriggerEnter"] = "_onTriggerEnter", ["OnTriggerExit"] = "_onTriggerExit",
        ["OnTriggerStay"] = "_onTriggerStay",
        ["OnCollisionEnter"] = "_onCollisionEnter", ["OnCollisionExit"] = "_onCollisionExit",
        ["OnCollisionStay"] = "_onCollisionStay",
        // Drone
        ["OnDroneTriggerEnter"] = "_onDroneTriggerEnter",
        ["OnDroneTriggerExit"] = "_onDroneTriggerExit",
        ["OnDroneTriggerStay"] = "_onDroneTriggerStay",
        // Rendering
        ["OnPostRender"] = "_onPostRender", ["OnPreRender"] = "_onPreRender",
        ["OnWillRenderObject"] = "_onWillRenderObject",
        ["OnBecameVisible"] = "_onBecameVisible", ["OnBecameInvisible"] = "_onBecameInvisible",
        ["OnVRCCameraSettingsChanged"] = "_onVRCCameraSettingsChanged",
        ["OnVRCQualitySettingsChanged"] = "_onVRCQualitySettingsChanged",
        ["OnScreenUpdate"] = "_onScreenUpdate",
        // Animation
        ["OnAnimatorIK"] = "_onAnimatorIK", ["OnAnimatorMove"] = "_onAnimatorMove",
        // Particle
        ["OnParticleCollision"] = "_onParticleCollision",
        // GPU readback
        ["OnAsyncGpuReadbackComplete"] = "_onAsyncGpuReadbackComplete",
        // MIDI
        ["MidiNoteOn"] = "_midiNoteOn", ["MidiNoteOff"] = "_midiNoteOff",
        ["MidiControlChange"] = "_midiControlChange",
        // PhysBone / Contact
        ["OnPhysBoneGrabbed"] = "_onPhysBoneGrabbed",
        ["OnPhysBoneReleased"] = "_onPhysBoneReleased",
        ["OnPhysBonePosed"] = "_onPhysBonePosed",
        ["OnPhysBoneUnPosed"] = "_onPhysBoneUnPosed",
        ["OnContactEnter"] = "_onContactEnter",
        ["OnContactExit"] = "_onContactExit",
        // Spawn
        ["OnSpawn"] = "_onSpawn",
        // VRC Plus
        ["OnVRCPlusMassGift"] = "_onVRCPlusMassGift",
        // Persistence
        ["OnPersistenceUsageUpdated"] = "_onPersistenceUsageUpdated",
        ["OnPlayerDataUpdated"] = "_onPlayerDataUpdated",
        ["OnPlayerDataStorageExceeded"] = "_onPlayerDataStorageExceeded",
        ["OnPlayerDataStorageWarning"] = "_onPlayerDataStorageWarning",
        ["OnPlayerObjectStorageExceeded"] = "_onPlayerObjectStorageExceeded",
        ["OnPlayerObjectStorageWarning"] = "_onPlayerObjectStorageWarning",
        // Creator Economy
        ["OnPurchaseConfirmed"] = "_onPurchaseConfirmed",
        ["OnPurchaseConfirmedMultiple"] = "_onPurchaseConfirmedMultiple",
        ["OnPurchaseExpired"] = "_onPurchaseExpired",
        ["OnPurchasesLoaded"] = "_onPurchasesLoaded",
        ["OnProductEvent"] = "_onProductEvent",
        ["OnListPurchases"] = "_onListPurchases",
        ["OnListAvailableProducts"] = "_onListAvailableProducts",
        ["OnListProductOwners"] = "_onListProductOwners",
    };

    // Cache of Udon event export names for O(1) collision checks
    public static readonly HashSet<string> UdonEventExportNames = new(UdonEventNames.Values);

    // Fixed parameter names for Udon events (from Udon node definitions).
    // Format: {lowerCasedEventName}{UpperCasedParamName}
    // These do NOT go through NameAllocator — they are hardcoded by the Udon runtime.
    public static readonly Dictionary<string, string[]> UdonEventParamNames = new()
    {
        // Player
        ["OnPlayerJoined"] = new[] { "onPlayerJoinedPlayer" },
        ["OnPlayerLeft"] = new[] { "onPlayerLeftPlayer" },
        ["OnPlayerRespawn"] = new[] { "onPlayerRespawnPlayer" },
        ["OnPlayerRestored"] = new[] { "onPlayerRestoredPlayer" },
        ["OnPlayerSuspendChanged"] = new[] { "onPlayerSuspendChangedPlayer" },
        // Player trigger
        ["OnPlayerTriggerEnter"] = new[] { "onPlayerTriggerEnterPlayer" },
        ["OnPlayerTriggerExit"] = new[] { "onPlayerTriggerExitPlayer" },
        ["OnPlayerTriggerStay"] = new[] { "onPlayerTriggerStayPlayer" },
        // Player collision
        ["OnPlayerCollisionEnter"] = new[] { "onPlayerCollisionEnterPlayer" },
        ["OnPlayerCollisionExit"] = new[] { "onPlayerCollisionExitPlayer" },
        ["OnPlayerCollisionStay"] = new[] { "onPlayerCollisionStayPlayer" },
        // Player particle
        ["OnPlayerParticleCollision"] = new[] { "onPlayerParticleCollisionPlayer" },
        ["OnControllerColliderHitPlayer"] = new[] { "onControllerColliderHitPlayerHit" },
        // Avatar
        ["OnAvatarChanged"] = new[] { "onAvatarChangedPlayer" },
        ["OnAvatarEyeHeightChanged"] = new[] { "onAvatarEyeHeightChangedPlayer", "onAvatarEyeHeightChangedPrevEyeHeightAsMeters" },
        ["OnMasterTransferred"] = new[] { "onMasterTransferredNewMaster" },
        // Ownership
        ["OnOwnershipTransferred"] = new[] { "onOwnershipTransferredPlayer" },
        ["OnOwnershipRequest"] = new[] { "onOwnershipRequestRequestingPlayer", "onOwnershipRequestRequestedOwner" },
        // Station
        ["OnStationEntered"] = new[] { "onStationEnteredPlayer" },
        ["OnStationExited"] = new[] { "onStationExitedPlayer" },
        // Serialization
        ["OnDeserialization"] = new[] { "onDeserializationResult" },
        ["OnPostSerialization"] = new[] { "onPostSerializationResult" },
        // Video
        ["OnVideoError"] = new[] { "onVideoErrorVideoError" },
        // Network download
        ["OnStringLoadSuccess"] = new[] { "onStringLoadSuccessResult" },
        ["OnStringLoadError"] = new[] { "onStringLoadErrorResult" },
        ["OnImageLoadSuccess"] = new[] { "onImageLoadSuccessResult" },
        ["OnImageLoadError"] = new[] { "onImageLoadErrorResult" },
        // Collision / Trigger (non-player)
        ["OnTriggerEnter"] = new[] { "onTriggerEnterOther" },
        ["OnTriggerExit"] = new[] { "onTriggerExitOther" },
        ["OnTriggerStay"] = new[] { "onTriggerStayOther" },
        ["OnCollisionEnter"] = new[] { "onCollisionEnterOther" },
        ["OnCollisionExit"] = new[] { "onCollisionExitOther" },
        ["OnCollisionStay"] = new[] { "onCollisionStayOther" },
        // Drone
        ["OnDroneTriggerEnter"] = new[] { "onDroneTriggerEnterDrone" },
        ["OnDroneTriggerExit"] = new[] { "onDroneTriggerExitDrone" },
        ["OnDroneTriggerStay"] = new[] { "onDroneTriggerStayDrone" },
        // Animation
        ["OnAnimatorIK"] = new[] { "onAnimatorIKLayerIndex" },
        // Particle
        ["OnParticleCollision"] = new[] { "onParticleCollisionOther" },
        // GPU readback
        ["OnAsyncGpuReadbackComplete"] = new[] { "onAsyncGpuReadbackCompleteRequest" },
        // Rendering
        ["OnVRCCameraSettingsChanged"] = new[] { "onVRCCameraSettingsChangedCameraSettings" },
        ["OnScreenUpdate"] = new[] { "onScreenUpdateData" },
        // MIDI
        ["MidiNoteOn"] = new[] { "midiNoteOnChannel", "midiNoteOnNumber", "midiNoteOnVelocity" },
        ["MidiNoteOff"] = new[] { "midiNoteOffChannel", "midiNoteOffNumber", "midiNoteOffVelocity" },
        ["MidiControlChange"] = new[] { "midiControlChangeChannel", "midiControlChangeNumber", "midiControlChangeValue" },
        // Input
        ["InputJump"] = new[] { "inputJumpValue", "inputJumpArgs" },
        ["InputUse"] = new[] { "inputUseValue", "inputUseArgs" },
        ["InputGrab"] = new[] { "inputGrabValue", "inputGrabArgs" },
        ["InputDrop"] = new[] { "inputDropValue", "inputDropArgs" },
        ["InputMoveHorizontal"] = new[] { "inputMoveHorizontalValue", "inputMoveHorizontalArgs" },
        ["InputMoveVertical"] = new[] { "inputMoveVerticalValue", "inputMoveVerticalArgs" },
        ["InputLookHorizontal"] = new[] { "inputLookHorizontalValue", "inputLookHorizontalArgs" },
        ["InputLookVertical"] = new[] { "inputLookVerticalValue", "inputLookVerticalArgs" },
        ["OnInputMethodChanged"] = new[] { "onInputMethodChangedInputMethod" },
        ["OnLanguageChanged"] = new[] { "onLanguageChangedLanguage" },
        // PhysBone / Contact
        ["OnPhysBoneGrabbed"] = new[] { "onPhysBoneGrabbedPhysBoneInfo" },
        ["OnPhysBoneReleased"] = new[] { "onPhysBoneReleasedPhysBoneInfo" },
        ["OnPhysBonePosed"] = new[] { "onPhysBonePosedPhysBoneInfo" },
        ["OnPhysBoneUnPosed"] = new[] { "onPhysBoneUnPosedPhysBoneInfo" },
        ["OnContactEnter"] = new[] { "onContactEnterContactInfo" },
        ["OnContactExit"] = new[] { "onContactExitContactInfo" },
        // VRC Plus
        ["OnVRCPlusMassGift"] = new[] { "onVRCPlusMassGiftGifter", "onVRCPlusMassGiftNumGifts" },
        // Persistence
        ["OnPlayerDataUpdated"] = new[] { "onPlayerDataUpdatedPlayer", "onPlayerDataUpdatedInfos" },
        ["OnPlayerDataStorageExceeded"] = new[] { "onPlayerDataStorageExceededPlayer" },
        ["OnPlayerDataStorageWarning"] = new[] { "onPlayerDataStorageWarningPlayer" },
        ["OnPlayerObjectStorageExceeded"] = new[] { "onPlayerObjectStorageExceededPlayer" },
        ["OnPlayerObjectStorageWarning"] = new[] { "onPlayerObjectStorageWarningPlayer" },
        // Creator Economy
        ["OnPurchaseConfirmed"] = new[] { "onPurchaseConfirmedProduct", "onPurchaseConfirmedPlayer", "onPurchaseConfirmedPurchasedNow" },
        ["OnPurchaseConfirmedMultiple"] = new[] { "onPurchaseConfirmedMultipleProduct", "onPurchaseConfirmedMultiplePlayer", "onPurchaseConfirmedMultiplePurchasedNow", "onPurchaseConfirmedMultipleQuantity" },
        ["OnPurchaseExpired"] = new[] { "onPurchaseExpiredProduct", "onPurchaseExpiredPlayer" },
        ["OnPurchasesLoaded"] = new[] { "onPurchasesLoadedProducts", "onPurchasesLoadedPlayer" },
        ["OnProductEvent"] = new[] { "onProductEventProduct", "onProductEventPlayer" },
        ["OnListPurchases"] = new[] { "onListPurchasesProducts", "onListPurchasesPlayer" },
        ["OnListAvailableProducts"] = new[] { "onListAvailableProductsProducts" },
        ["OnListProductOwners"] = new[] { "onListProductOwnersProduct", "onListProductOwnersOwners" },
    };

    public LayoutPlanner(Compilation compilation)
    {
        _compilation = compilation;
    }

    /// <summary>
    /// Compute or retrieve cached TypeLayout for the given type.
    /// This is the ONLY place naming decisions are made.
    /// </summary>
    public bool IsFrozen => _frozen;
    public IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> AllLayouts => _cache;
    public void Freeze() => _frozen = true;

    public TypeLayout Plan(INamedTypeSymbol type)
    {
        if (_cache.TryGetValue(type, out var cached))
            return cached;
        if (_frozen)
            throw new System.InvalidOperationException(
                $"LayoutPlanner is frozen but type '{type.Name}' was not pre-planned");

        TypeLayout layout;
        if (type.TypeKind == TypeKind.Interface)
            layout = PlanInterface(type);
        else
            layout = PlanClass(type);

        _cache[type] = layout;
        return layout;
    }

    /// <summary>
    /// Retrieve a pre-planned TypeLayout. Only valid after Freeze().
    /// Throws if the planner is not frozen or the type was not pre-planned.
    /// </summary>
    public TypeLayout GetLayout(INamedTypeSymbol type)
    {
        if (!_frozen)
            throw new System.InvalidOperationException(
                "LayoutPlanner must be frozen before accessing layouts via GetLayout().");
        if (!_cache.TryGetValue(type, out var layout))
            throw new System.InvalidOperationException(
                $"Type '{type.Name}' was not pre-planned.");
        return layout;
    }

    // Explicit interface implementations produce names with dots — invalid in UASM.
    static string SanitizeId(string name) => name.Replace('.', '_');

    /// <summary>True if the method carries [VRC.SDK3.UdonNetworkCalling.NetworkCallable], which makes it a
    /// remotely-invokable entry point (kept unmangled, with network-calling metadata emitted for it).</summary>
    public static bool IsNetworkCallable(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
            if (attr.AttributeClass?.Name == "NetworkCallableAttribute")
                return true;
        return false;
    }

    TypeLayout PlanClass(INamedTypeSymbol type)
    {
        var methods = new Dictionary<IMethodSymbol, MethodLayout>(SymbolEqualityComparer.Default);
        var fields = new Dictionary<IFieldSymbol, FieldLayout>(SymbolEqualityComparer.Default);

        // --- Counter inheritance: walk ancestor chain up to UdonSharpBehaviour, plan parents first ---
        var ancestors = new List<INamedTypeSymbol>();
        var walk = type;
        while (walk != null && walk.Name != "UdonSharpBehaviour")
        {
            ancestors.Add(walk);
            walk = walk.BaseType;
        }
        ancestors.Reverse(); // UdonSharpBehaviour child first → type last

        // Plan all ancestors so their SymbolCounters are cached
        IReadOnlyDictionary<string, int> parentCounters = null;
        for (int i = 0; i < ancestors.Count - 1; i++)
        {
            var parentLayout = Plan(ancestors[i]);
            parentCounters = parentLayout.SymbolCounters;
        }

        var alloc = parentCounters != null
            ? new NameAllocator(parentCounters)
            : new NameAllocator();

        // --- Main loop ---
        var memberMethods = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => (m.MethodKind == MethodKind.Ordinary
                      || m.MethodKind == MethodKind.ExplicitInterfaceImplementation
                      || m.MethodKind == MethodKind.PropertyGet
                      || m.MethodKind == MethodKind.PropertySet)
                     && m.DeclaringSyntaxReferences.Length > 0)
            .ToArray();

        foreach (var method in memberMethods)
        {
            if (method.IsGenericMethod) continue;

            // Override skip: match pure compiler's condition.
            // Skip only if overriding a user-defined USB subclass (not UdonSharpBehaviour, not extern).
            if (method.IsOverride && method.OverriddenMethod != null)
            {
                var ct = method.OverriddenMethod.ContainingType;
                bool isUdonSharpBehaviour = ct.Name == "UdonSharpBehaviour";
                bool isExtern = ct.DeclaringSyntaxReferences.IsEmpty;

                if (!isUdonSharpBehaviour && !isExtern)
                {
                    // User-defined base class override → reuse base layout, don't consume counters
                    var baseLayout = Plan(ct);
                    if (baseLayout.Methods.TryGetValue(method.OverriddenMethod, out var baseMl))
                    {
                        methods[method] = baseMl;
                        continue;
                    }
                }
                // UdonSharpBehaviour or extern override → fall through, build layout normally
            }

            // Compute export name
            string exportName;
            bool isUdonEvent = UdonEventNames.TryGetValue(method.Name, out var udonEventName);
            if (isUdonEvent)
            {
                exportName = udonEventName;
            }
            else
            {
                var safeName = SanitizeId(method.Name);
                // [NetworkCallable] methods must keep their unmangled name — other clients invoke them by name
                // through SendCustomNetworkEvent, and the runtime's TryGetEntrypointHashFromName looks them up
                // by the original method name. (Their parameters are still mangled below, like stock UdonSharp.)
                // Otherwise mangle if: has parameters, OR name collides with a Udon event export name.
                exportName = (!IsNetworkCallable(method)
                              && (method.Parameters.Length > 0 || UdonEventExportNames.Contains(safeName)))
                    ? NameAllocator.FormatId(safeName, alloc.Allocate(safeName))
                    : safeName;
            }

            // Compute param IDs
            var paramIds = new string[method.Parameters.Length];
            if (isUdonEvent && UdonEventParamNames.TryGetValue(method.Name, out var fixedNames))
            {
                // Event parameters: use fixed names, don't consume NameAllocator counter
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    if (i < fixedNames.Length)
                        paramIds[i] = fixedNames[i];
                    else
                    {
                        // Fallback for parameters beyond fixedNames (SDK mismatch)
                        var key = method.Parameters[i].Name + "__param";
                        paramIds[i] = NameAllocator.FormatId(key, alloc.Allocate(key));
                    }
                }
            }
            else
            {
                // Regular parameters: go through NameAllocator
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    var key = method.Parameters[i].Name + "__param";
                    paramIds[i] = NameAllocator.FormatId(key, alloc.Allocate(key));
                }
            }

            var returns = BuildReturnSlots(method, exportName, alloc);

            var bodyLabel = exportName + "__body";
            methods[method] = new MethodLayout(exportName, bodyLabel, paramIds, returns);
        }

        // Inherit non-overridden methods from user-defined base classes.
        // In Udon VM each class compiles to a standalone program, so inherited
        // methods must be present in the derived class's layout.
        var overriddenMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var m in memberMethods)
        {
            var cur = m.OverriddenMethod;
            while (cur != null)
            {
                overriddenMethods.Add(cur);
                cur = cur.OverriddenMethod;
            }
        }
        // Export names already taken by the derived class's own methods — an inherited member that is
        // `new`-shadowed by a derived member shares its simple name, so its base layout's export/body/ret
        // names would collide (two distinct functions, one .export + one entry label = silent overwrite →
        // stack-balance corruption). Re-allocate the inherited names on collision.
        var usedExports = new HashSet<string>(methods.Values.Select(m => m.ExportName));
        var inheritBase = type.BaseType;
        while (inheritBase != null && inheritBase.Name != "UdonSharpBehaviour")
        {
            if (!inheritBase.DeclaringSyntaxReferences.IsEmpty)
            {
                var baseLayout = Plan(inheritBase);
                foreach (var bm in inheritBase.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => (m.MethodKind == MethodKind.Ordinary
                              || m.MethodKind == MethodKind.PropertyGet
                              || m.MethodKind == MethodKind.PropertySet)
                             && m.DeclaringSyntaxReferences.Length > 0 && !m.IsGenericMethod && !m.IsAbstract))
                {
                    if (overriddenMethods.Contains(bm) || !baseLayout.Methods.TryGetValue(bm, out var baseMl))
                        continue;
                    var ml = baseMl;
                    if (usedExports.Contains(baseMl.ExportName))
                    {
                        var ue = NameAllocator.FormatId(baseMl.ExportName, alloc.Allocate(baseMl.ExportName));
                        var newReturns = new List<ReturnSlot>();
                        foreach (var rs in baseMl.Returns)
                        {
                            var rk = ue + "__ret";
                            newReturns.Add(new ReturnSlot(NameAllocator.FormatId(rk, alloc.Allocate(rk)), rs.UdonType));
                        }
                        ml = new MethodLayout(ue, ue + "__body", baseMl.ParamIds, newReturns);
                    }
                    if (methods.TryAdd(bm, ml)) usedExports.Add(ml.ExportName);
                }
            }
            inheritBase = inheritBase.BaseType;
        }

        // Compute field layouts
        foreach (var member in type.GetMembers().OfType<IFieldSymbol>()
            .Where(f => SymbolEqualityComparer.Default.Equals(f.ContainingType, type)))
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            var udonType = ExternResolver.GetUdonTypeName(member.Type);
            var flags = FieldFlags.None;
            if (member.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute")) flags |= FieldFlags.Sync;
            fields[member] = new FieldLayout(member.Name, udonType, flags);
        }

        // Generate delegate bridge layouts for all non-generic, non-event user methods
        var delegateBridges = new Dictionary<IMethodSymbol, DelegateBridgeLayout>(SymbolEqualityComparer.Default);
        foreach (var (method, ml) in methods)
        {
            if (method.IsGenericMethod) continue;
            if (UdonEventNames.ContainsKey(method.Name)) continue;
            if (ml.Returns.Count > 1) continue;
            delegateBridges[method] = new DelegateBridgeLayout($"__dlg_{ml.ExportName}", ml);
        }

        return new TypeLayout(methods, fields, alloc.GetCounters(), delegateBridges);
    }

    TypeLayout PlanInterface(INamedTypeSymbol interfaceType)
    {
        var methods = new Dictionary<IMethodSymbol, MethodLayout>(SymbolEqualityComparer.Default);
        var alloc = new NameAllocator();

        foreach (var method in interfaceType.GetMembers().OfType<IMethodSymbol>()
            .Where(m => (m.MethodKind == MethodKind.Ordinary
                      || m.MethodKind == MethodKind.PropertyGet
                      || m.MethodKind == MethodKind.PropertySet)
                     && m.DeclaringSyntaxReferences.Length > 0))
        {
            var safeName = SanitizeId(method.Name);
            var exportName = method.Parameters.Length > 0
                ? NameAllocator.FormatId(safeName, alloc.Allocate(safeName))
                : safeName;

            var paramIds = new string[method.Parameters.Length];
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var key = method.Parameters[i].Name + "__param";
                paramIds[i] = NameAllocator.FormatId(key, alloc.Allocate(key));
            }

            var returns = BuildReturnSlots(method, exportName, alloc);

            methods[method] = new MethodLayout(exportName, exportName + "__body", paramIds, returns);
        }

        return new TypeLayout(methods, new Dictionary<IFieldSymbol, FieldLayout>(SymbolEqualityComparer.Default));
    }

    /// <summary>Canonical, collision-free dispatch/export name for an interface method's bridge, qualified by
    /// the interface's full name so it (a) never collides with a class-method export (__N_X), (b) keeps two
    /// interfaces with the same method name distinct (explicit impls), and (c) lets caller and callee — even
    /// in separate programs — derive the SAME dispatch string from the interface type alone.</summary>
    public static string InterfaceDispatchName(IMethodSymbol ifaceMethod, MethodLayout ifaceMl)
        => $"__iface_{SanitizeId(ifaceMethod.ContainingType.ToDisplayString())}_{ifaceMl.ExportName}";

    /// <summary>
    /// Bridge exports for every interface method a class implements (except tuple-returning ones, which are
    /// dispatched directly via CrossCall). A bridge re-maps the canonical interface param/return names to the
    /// class method's and JUMPs to it; it is always emitted so the canonical name is the single, unique,
    /// cross-program-stable dispatch entry — avoiding the export-name collisions that arose when the bridge
    /// reused the interface's bare export name (which could equal a sibling class method or another bridge).
    /// </summary>
    public List<(IMethodSymbol method, MethodLayout interfaceLayout, MethodLayout classLayout)>
        ComputeBridges(INamedTypeSymbol classType)
    {
        var bridges = new List<(IMethodSymbol, MethodLayout, MethodLayout)>();
        var classLayout = Plan(classType);

        foreach (var iface in classType.AllInterfaces)
        {
            var ifaceLayout = Plan(iface);
            foreach (var (ifaceMethod, ifaceMl) in ifaceLayout.Methods)
            {
                var impl = classType.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
                // Round-7 follow-up [Q1]: a DEFAULT interface member with no class-level implementation
                // resolves to the interface member ITSELF here. Skipping it silently (the pre-fix
                // behavior) ships a call site that SendCustomEvents the canonical dispatch name with no
                // entry point in any implementer's program — a silent no-op + stale 0/null return on a
                // real client (unbounded self-reentry in the local harness). Neither LOUD nor CORRECT
                // (design §8-3): reject at the implementing class.
                if (impl != null && impl.ContainingType?.TypeKind == TypeKind.Interface)
                    throw new System.NotSupportedException(
                        $"Interface member '{ifaceMethod.ContainingType.Name}.{ifaceMethod.Name}' has a "
                        + $"default implementation and no implementation in '{classType.Name}'. A default "
                        + "interface body has no dispatch entry point on the implementing program "
                        + "(SendCustomEvent would silently no-op) — implement the member in the class.");
                if (impl == null) continue;
                if (!classLayout.Methods.TryGetValue(impl, out var classMl)) continue;
                // Tuple returns (N>1, ReturnId null) go through CrossCall directly, not a bridge.
                if (ifaceMl.Returns.Count > 1) continue;
                bridges.Add((ifaceMethod, ifaceMl, classMl));
            }
        }

        return bridges;
    }

    static List<ReturnSlot> BuildReturnSlots(IMethodSymbol method, string exportName, NameAllocator alloc)
    {
        var returns = new List<ReturnSlot>();
        if (method.ReturnsVoid) return returns;

        var retKey = exportName + "__ret";
        var id = NameAllocator.FormatId(retKey, alloc.Allocate(retKey));

        if (EmitContext.IsAggregateType(method.ReturnType))
            returns.Add(new ReturnSlot(id, "SystemObjectArray"));
        else
            returns.Add(new ReturnSlot(id, ExternResolver.GetUdonTypeName(method.ReturnType)));

        return returns;
    }

    /// <summary>Get bridge layout for any method, including on foreign types.</summary>
    public DelegateBridgeLayout GetDelegateBridgeLayout(IMethodSymbol method)
    {
        var m = method;
        while (m.IsOverride && m.OverriddenMethod != null)
        {
            var ct = m.OverriddenMethod.ContainingType;
            if (ct.Name == "UdonSharpBehaviour" || ct.DeclaringSyntaxReferences.IsEmpty) break;
            m = m.OverriddenMethod;
        }
        var layout = Plan(m.ContainingType);
        if (layout.DelegateBridges.TryGetValue(m, out var bridge)) return bridge;
        throw new System.InvalidOperationException($"No delegate bridge for '{method.Name}' on '{method.ContainingType.Name}'");
    }

    /// <summary>
    /// Get layout for a method on a foreign UdonBehaviour. Checks the target's
    /// containing type, then walks up the base type hierarchy.
    /// </summary>
    public MethodLayout GetCalleeLayout(IMethodSymbol target)
    {
        var ml = TryGetCalleeLayout(target);
        if (ml != null) return ml;
        throw new System.InvalidOperationException(
            $"Method {target.Name} not found in layout for {target.ContainingType.Name}");
    }

    /// <summary>Non-throwing twin of <see cref="GetCalleeLayout(IMethodSymbol)"/>: null when the
    /// (override-chain-normalized) method has no planned layout — e.g. a local function or a
    /// monomorphized generic specialization, which exist only in per-emitter registration.</summary>
    public MethodLayout TryGetCalleeLayout(IMethodSymbol target)
    {
        // Normalize override chain: walk to the defining base type,
        // matching the pure compiler's GetUsbMethodLayout logic.
        var method = target;
        while (method.IsOverride && method.OverriddenMethod != null)
        {
            var ct = method.OverriddenMethod.ContainingType;
            if (ct.Name == "UdonSharpBehaviour" || ct.DeclaringSyntaxReferences.IsEmpty)
                break;
            method = method.OverriddenMethod;
        }

        var layout = Plan(method.ContainingType);
        return layout.Methods.TryGetValue(method, out var ml) ? ml : null;
    }
}
