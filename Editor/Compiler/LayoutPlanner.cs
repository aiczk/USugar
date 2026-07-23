using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>A single return value slot: UASM variable ID + Udon type name.</summary>
public sealed class ReturnSlot
{
    public readonly string Id;
    public readonly StorageType StorageType;
    public ReturnSlot(string id, StorageType storageType) { Id = id; StorageType = storageType; }
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
    public CompilationTypeCensus Census { get; }
    readonly Dictionary<INamedTypeSymbol, TypeLayout> _cache = new(SymbolEqualityComparer.Default);
    bool _frozen;

    // Wave-14 r3: interfaces implemented by at least one user STRUCT, populated (serially, Phase 1) by
    // every caller that walks StructDeclarationSyntax alongside the existing class-interface walk — see
    // USugarCompilationOrchestrator and UasmEmitter.EnsurePlannerReady. Struct methods never get an
    // interface bridge (ComputeBridges only bridges the CLASS's own interface implementations), so an
    // interface-typed receiver that actually holds struct data dispatches SendCustomEvent to a bridge name
    // that is exported by NO program — VM-proven infinite self re-entry / stack overflow
    // (GenBoxInterfaceScorable, wave-14 r3), not merely a wrong value. An interface with NO struct
    // implementor (the common case — implemented only by UdonSharpBehaviour classes, possibly ones not
    // present in a narrow test compile) is unaffected: that is the pre-existing, working cross-behaviour
    // dispatch feature, and must NOT be rejected just because no CLASS implementor happens to be visible.
    readonly HashSet<INamedTypeSymbol> _interfacesWithStructImplementor = new(SymbolEqualityComparer.Default);
    readonly HashSet<INamedTypeSymbol> _interfacesWithUserClassImplementor = new(SymbolEqualityComparer.Default);
    readonly HashSet<INamedTypeSymbol> _interfacesWithBehaviourImplementor = new(SymbolEqualityComparer.Default);

    public void RegisterStructImplementedInterface(INamedTypeSymbol iface)
        => _interfacesWithStructImplementor.Add(iface);

    /// <summary>True if some user struct in this compilation implements `iface`. Dispatching a call/
    /// accessor through an `iface`-typed receiver can then never soundly resolve (see field comment
    /// above) and must be rejected loudly rather than emitted.</summary>
    public bool InterfaceHasStructImplementor(INamedTypeSymbol iface)
        => _interfacesWithStructImplementor.Contains(iface);

    public void RegisterClassImplementedInterface(INamedTypeSymbol iface, bool isBehaviour)
    {
        if (isBehaviour) _interfacesWithBehaviourImplementor.Add(iface);
        else _interfacesWithUserClassImplementor.Add(iface);
    }

    public bool InterfaceIsLocalUserClassOnly(INamedTypeSymbol iface)
        => iface != null
           && _interfacesWithUserClassImplementor.Contains(iface)
           && !_interfacesWithBehaviourImplementor.Contains(iface)
           && !_interfacesWithStructImplementor.Contains(iface);

    public bool InterfaceHasMixedRuntimeRepresentations(INamedTypeSymbol iface)
        => iface != null && (_interfacesWithStructImplementor.Contains(iface)
            || _interfacesWithUserClassImplementor.Contains(iface)
               && _interfacesWithBehaviourImplementor.Contains(iface));

    // C# event method name → Udon export name ("_" + lowerFirst). Regenerated from the SDK's Event_*
    // node definitions — the same source stock UdonSharp derives from (CompilerUdonInterface.CacheInit).
    // Pinned bidirectionally against Editor~/Tests/Fixtures/udon_event_registry.txt by
    // EventRegistryCensusTests (regenerate the fixture via USugarCompiler.DumpEventRegistry after an
    // SDK update). A miss here silently compiles a new SDK event as an inert ordinary method.
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
        // Video — OnVideoPlay/OnVideoPause have NO Event_* node in the SDK registry (client fires them
        // as _onVideoPlay/_onVideoPause; graph programs subscribe via a literal custom event). They are
        // the census exempt-list (EventRegistryCensusTests.RegistryLessEvents); keep in sync.
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
        ["OnTriggerEnter2D"] = "_onTriggerEnter2D", ["OnTriggerExit2D"] = "_onTriggerExit2D",
        ["OnTriggerStay2D"] = "_onTriggerStay2D",
        ["OnCollisionEnter"] = "_onCollisionEnter", ["OnCollisionExit"] = "_onCollisionExit",
        ["OnCollisionStay"] = "_onCollisionStay",
        ["OnCollisionEnter2D"] = "_onCollisionEnter2D", ["OnCollisionExit2D"] = "_onCollisionExit2D",
        ["OnCollisionStay2D"] = "_onCollisionStay2D",
        ["OnControllerColliderHit"] = "_onControllerColliderHit",
        ["OnJointBreak"] = "_onJointBreak", ["OnJointBreak2D"] = "_onJointBreak2D",
        // Mouse
        ["OnMouseDown"] = "_onMouseDown", ["OnMouseDrag"] = "_onMouseDrag",
        ["OnMouseEnter"] = "_onMouseEnter", ["OnMouseExit"] = "_onMouseExit",
        ["OnMouseOver"] = "_onMouseOver", ["OnMouseUp"] = "_onMouseUp",
        ["OnMouseUpAsButton"] = "_onMouseUpAsButton",
        // Transform
        ["OnTransformChildrenChanged"] = "_onTransformChildrenChanged",
        ["OnTransformParentChanged"] = "_onTransformParentChanged",
        // Drone
        ["OnDroneTriggerEnter"] = "_onDroneTriggerEnter",
        ["OnDroneTriggerExit"] = "_onDroneTriggerExit",
        ["OnDroneTriggerStay"] = "_onDroneTriggerStay",
        // Rendering
        ["OnPostRender"] = "_onPostRender", ["OnPreRender"] = "_onPreRender",
        ["OnPreCull"] = "_onPreCull", ["OnRenderImage"] = "_onRenderImage",
        ["OnRenderObject"] = "_onRenderObject",
        ["OnWillRenderObject"] = "_onWillRenderObject",
        ["OnBecameVisible"] = "_onBecameVisible", ["OnBecameInvisible"] = "_onBecameInvisible",
        ["OnVRCCameraSettingsChanged"] = "_onVRCCameraSettingsChanged",
        ["OnVRCQualitySettingsChanged"] = "_onVRCQualitySettingsChanged",
        ["OnScreenUpdate"] = "_onScreenUpdate",
        // Animation
        ["OnAnimatorIK"] = "_onAnimatorIK", ["OnAnimatorMove"] = "_onAnimatorMove",
        // Particle
        ["OnParticleCollision"] = "_onParticleCollision",
        ["OnParticleTrigger"] = "_onParticleTrigger",
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

    // Fixed parameter names for Udon events: {lowerCasedEventName}{UpperCasedParamName}, where the
    // param name is the Event_* node definition's OUT-param name (NOT the C# parameter name on
    // UdonSharpBehaviour — e.g. OnVRCCameraSettingsChanged's node param is "camera" while the C#
    // signature says "cameraSettings"). The runtime writes exactly these heap vars
    // (UdonBehaviour.GetEventParameterName), so a wrong row silently unbinds the parameter.
    // Regenerated from the registry; pinned against Editor~/Tests/Fixtures/udon_event_registry.txt
    // by EventRegistryCensusTests. These do NOT go through NameAllocator.
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
        ["OnOwnershipRequest"] = new[] { "onOwnershipRequestRequester", "onOwnershipRequestNewOwner" },
        // Station
        ["OnStationEntered"] = new[] { "onStationEnteredPlayer" },
        ["OnStationExited"] = new[] { "onStationExitedPlayer" },
        // Serialization
        ["OnDeserialization"] = new[] { "onDeserializationResult" },
        ["OnPostSerialization"] = new[] { "onPostSerializationResult" },
        // Video
        ["OnVideoError"] = new[] { "onVideoErrorVideoError" },
        // Network download — the node param name is literally the interface name
        ["OnStringLoadSuccess"] = new[] { "onStringLoadSuccessIVRCStringDownload" },
        ["OnStringLoadError"] = new[] { "onStringLoadErrorIVRCStringDownload" },
        ["OnImageLoadSuccess"] = new[] { "onImageLoadSuccessIVRCImageDownload" },
        ["OnImageLoadError"] = new[] { "onImageLoadErrorIVRCImageDownload" },
        // Collision / Trigger (non-player)
        ["OnTriggerEnter"] = new[] { "onTriggerEnterOther" },
        ["OnTriggerExit"] = new[] { "onTriggerExitOther" },
        ["OnTriggerStay"] = new[] { "onTriggerStayOther" },
        ["OnTriggerEnter2D"] = new[] { "onTriggerEnter2DOther" },
        ["OnTriggerExit2D"] = new[] { "onTriggerExit2DOther" },
        ["OnTriggerStay2D"] = new[] { "onTriggerStay2DOther" },
        ["OnCollisionEnter"] = new[] { "onCollisionEnterOther" },
        ["OnCollisionExit"] = new[] { "onCollisionExitOther" },
        ["OnCollisionStay"] = new[] { "onCollisionStayOther" },
        ["OnCollisionEnter2D"] = new[] { "onCollisionEnter2DOther" },
        ["OnCollisionExit2D"] = new[] { "onCollisionExit2DOther" },
        ["OnCollisionStay2D"] = new[] { "onCollisionStay2DOther" },
        ["OnControllerColliderHit"] = new[] { "onControllerColliderHitHit" },
        ["OnJointBreak"] = new[] { "onJointBreakBreakForce" },
        ["OnJointBreak2D"] = new[] { "onJointBreak2DBrokenJoint" },
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
        ["OnRenderImage"] = new[] { "onRenderImageSrc", "onRenderImageDest" },
        ["OnVRCCameraSettingsChanged"] = new[] { "onVRCCameraSettingsChangedCamera" },
        ["OnScreenUpdate"] = new[] { "onScreenUpdateData" },
        // MIDI
        ["MidiNoteOn"] = new[] { "midiNoteOnChannel", "midiNoteOnNumber", "midiNoteOnVelocity" },
        ["MidiNoteOff"] = new[] { "midiNoteOffChannel", "midiNoteOffNumber", "midiNoteOffVelocity" },
        ["MidiControlChange"] = new[] { "midiControlChangeChannel", "midiControlChangeNumber", "midiControlChangeValue" },
        // Input — button events carry "boolValue", axis events "floatValue" (UdonBehaviour.RunInputEvent)
        ["InputJump"] = new[] { "inputJumpBoolValue", "inputJumpArgs" },
        ["InputUse"] = new[] { "inputUseBoolValue", "inputUseArgs" },
        ["InputGrab"] = new[] { "inputGrabBoolValue", "inputGrabArgs" },
        ["InputDrop"] = new[] { "inputDropBoolValue", "inputDropArgs" },
        ["InputMoveHorizontal"] = new[] { "inputMoveHorizontalFloatValue", "inputMoveHorizontalArgs" },
        ["InputMoveVertical"] = new[] { "inputMoveVerticalFloatValue", "inputMoveVerticalArgs" },
        ["InputLookHorizontal"] = new[] { "inputLookHorizontalFloatValue", "inputLookHorizontalArgs" },
        ["InputLookVertical"] = new[] { "inputLookVerticalFloatValue", "inputLookVerticalArgs" },
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
        // Creator Economy — the node's product param is named "result" (ClientSimStoreManager dispatch)
        ["OnPurchaseConfirmed"] = new[] { "onPurchaseConfirmedResult", "onPurchaseConfirmedPlayer", "onPurchaseConfirmedPurchasedNow" },
        ["OnPurchaseConfirmedMultiple"] = new[] { "onPurchaseConfirmedMultipleResult", "onPurchaseConfirmedMultiplePlayer", "onPurchaseConfirmedMultiplePurchasedNow", "onPurchaseConfirmedMultipleQuantity" },
        ["OnPurchaseExpired"] = new[] { "onPurchaseExpiredResult", "onPurchaseExpiredPlayer" },
        ["OnPurchasesLoaded"] = new[] { "onPurchasesLoadedResult", "onPurchasesLoadedPlayer" },
        ["OnProductEvent"] = new[] { "onProductEventResult", "onProductEventPlayer" },
        ["OnListPurchases"] = new[] { "onListPurchasesResult", "onListPurchasesPlayer" },
        ["OnListAvailableProducts"] = new[] { "onListAvailableProductsResult" },
        ["OnListProductOwners"] = new[] { "onListProductOwnersResult", "onListProductOwnersOwners" },
    };

    public CompilationSession Session { get; }

    public LayoutPlanner(Compilation compilation)
        : this(new CompilationSession(compilation, UdonAbiCatalog.Empty))
    {
    }

    public LayoutPlanner(CompilationSession session)
    {
        Session = session ?? throw new System.ArgumentNullException(nameof(session));
        _compilation = session.Compilation;
        Census = CompilationTypeCensus.For(_compilation);
    }

    public UdonTypeFactRegistry TypeFacts => Session.TypeFacts;

    /// <summary>
    /// Compute or retrieve cached TypeLayout for the given type.
    /// This is the ONLY place naming decisions are made.
    /// </summary>
    public bool IsFrozen => _frozen;
    public IReadOnlyDictionary<INamedTypeSymbol, TypeLayout> AllLayouts => _cache;
    public void Freeze() => _frozen = true;

    public void PrepareCompilation()
    {
        if (_frozen) return;
        foreach (var type in Census.Classes)
        {
            var isBehaviour = ExternResolver.IsUdonSharpBehaviour(type);
            foreach (var iface in type.AllInterfaces)
                RegisterClassImplementedInterface(iface, isBehaviour);
            if (!isBehaviour) continue;
            Plan(type);
            foreach (var iface in type.AllInterfaces) Plan(iface);
        }
        foreach (var iface in Census.Interfaces) Plan(iface);
        foreach (var type in Census.Structs)
            foreach (var iface in type.AllInterfaces)
                RegisterStructImplementedInterface(iface);
        Freeze();
    }

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
    static string SanitizeId(string name) => NameAllocator.Sanitize(name);

    /// <summary>R-M2 (design §2): a method excluded from SPECULATIVE delegate-bridge planning because no
    /// third-party program can bind it (C# accessibility) — private and private protected
    /// (<see cref="Accessibility.ProtectedAndInternal"/>). protected is NOT excluded (a derived class in
    /// another program can bind it). The single predicate shared by the planner's bridge loop and
    /// HandlerBase.ResolveDelegateBridge's on-demand private arm so the two can never drift.</summary>
    public static bool IsExcludedFromSpeculativeBridge(IMethodSymbol method)
        => method.DeclaredAccessibility is Accessibility.Private or Accessibility.ProtectedAndInternal;

    /// <summary>True if the method carries [VRC.SDK3.UdonNetworkCalling.NetworkCallable], which makes it a
    /// remotely-invokable entry point (kept unmangled, with network-calling metadata emitted for it).</summary>
    public static bool IsNetworkCallable(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
            if (attr.AttributeClass?.Name == "NetworkCallableAttribute")
                return true;
        return false;
    }

    /// <summary>Wave-12 r5 [W3]: true when a parameterless non-event member method would take the plain
    /// export name that an inherited (non-overridden) member method already owns. The closest user base's
    /// layout folds the whole ancestor chain (its own inheritance walk inherited everything above it), so
    /// one lookup covers all bases. An override chain legitimately shares its slot's export and is excluded
    /// (user-base overrides never reach the caller anyway — they reuse the base layout and continue).</summary>
    bool ShadowsInheritedPlainExport(INamedTypeSymbol type, IMethodSymbol method, string plainName)
    {
        var baseType = type.BaseType;
        if (baseType == null || baseType.Name == "UdonSharpBehaviour" || baseType.DeclaringSyntaxReferences.IsEmpty
            || USugarCompilerHelper.IsFrameworkNamespace(baseType.ContainingNamespace))
            return false;
        var baseLayout = Plan(baseType);
        foreach (var (bm, bml) in baseLayout.Methods)
        {
            if (bml.ExportName != plainName) continue;
            bool overridden = false;
            for (var cur = method.OverriddenMethod; cur != null; cur = cur.OverriddenMethod)
                if (SymbolEqualityComparer.Default.Equals(cur, bm)) { overridden = true; break; }
            if (!overridden) return true;
        }
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
                      || m.MethodKind == MethodKind.PropertySet
                      || m.MethodKind == MethodKind.EventAdd
                      || m.MethodKind == MethodKind.EventRemove)
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
                // Otherwise mangle if: has parameters, OR name collides with a Udon event export name,
                // OR the method `new`-hides an inherited member that owns the plain export (wave-12 r5 [W3]:
                // cross-program dispatch is name-keyed, so the statically-bound BASE method must keep its
                // chain-wide export name in every descendant program — pre-fix the INHERITED member was the
                // one collision-renamed below, and a base-typed receiver's method group / call resolved via
                // Plan(Base) to the plain name, which in the derived program was the `new` method's export
                // (VM-proven 162 where C# statically binds the base and gives 2). Parameterized methods are
                // already consistent through counter inheritance; events and [NetworkCallable] keep the raw
                // name channel bound to the most-derived declaration, matching Unity/network name reflection.
                exportName = (!IsNetworkCallable(method)
                              && (method.Parameters.Length > 0 || UdonEventExportNames.Contains(safeName)
                                  || ShadowsInheritedPlainExport(type, method, safeName)))
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
                        var key = NameAllocator.ParamKey(method.Parameters[i].Name);
                        paramIds[i] = NameAllocator.FormatId(key, alloc.Allocate(key));
                    }
                }
            }
            else
            {
                // Regular parameters: go through NameAllocator
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    var key = NameAllocator.ParamKey(method.Parameters[i].Name);
                    paramIds[i] = NameAllocator.FormatId(key, alloc.Allocate(key));
                }
            }

            var returns = BuildReturnSlots(method, exportName, alloc);

            var bodyLabel = NameAllocator.BodyLabel(exportName);
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
                // Round-8 [R3]: ExplicitInterfaceImplementation is inherited too — without it a
                // derived class never lays out a base class's `int IFoo.F()` bridge target, so
                // ComputeBridges silently skipped the bridge while the call site dispatched the
                // canonical __iface_* name: silent no-op + stale return on device (unbounded
                // SendCustomEvent self-reentry in the harness).
                foreach (var bm in inheritBase.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => (m.MethodKind == MethodKind.Ordinary
                              || m.MethodKind == MethodKind.ExplicitInterfaceImplementation
                              || m.MethodKind == MethodKind.PropertyGet
                              || m.MethodKind == MethodKind.PropertySet
                              || m.MethodKind == MethodKind.EventAdd
                              || m.MethodKind == MethodKind.EventRemove)
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
                            var rk = NameAllocator.RetKey(ue);
                            newReturns.Add(new ReturnSlot(NameAllocator.FormatId(rk, alloc.Allocate(rk)), rs.StorageType));
                        }
                        ml = new MethodLayout(ue, NameAllocator.BodyLabel(ue), baseMl.ParamIds, newReturns);
                    }
                    if (methods.TryAdd(bm, ml))
                    {
                        usedExports.Add(ml.ExportName);
                        // Wave-9 [W4]: an inherited OVERRIDE owns its whole chain's virtual slot in this
                        // program — mark its overridden ancestors so a chain-ROOT declaration visible from
                        // a HIGHER base is not laid out as a second, collision-renamed function (the
                        // pre-fix dead `__N_get_P`/`__N_M` over stale storage that a root-typed receiver
                        // dispatch then bound: silent stale read / lost write, VM-proven 0 vs 7).
                        // The walk is closest-base-first, so the override is always seen before its root.
                        for (var cur = bm.OverriddenMethod; cur != null; cur = cur.OverriddenMethod)
                            overriddenMethods.Add(cur);
                    }
                }
            }
            inheritBase = inheritBase.BaseType;
        }

        // Compute field layouts
        foreach (var member in type.GetMembers().OfType<IFieldSymbol>()
            .Where(f => SymbolEqualityComparer.Default.Equals(f.ContainingType, type)))
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            var udonType = Session.Types.GetUdonTypeName(member.Type);
            var flags = FieldFlags.None;
            if (member.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute")) flags |= FieldFlags.Sync;
            fields[member] = new FieldLayout(member.Name, udonType, flags);
        }

        // Generate SPECULATIVE delegate bridge layouts for non-generic, non-event user methods. The
        // speculation exists for cross-program visibility (another program may bind a method of THIS class
        // as a delegate). R-M2 (design §2): a private / private-protected method cannot be bound by a
        // third party (C# accessibility), so its speculative bridge has no reason to exist — exclude it.
        // A SAME-program private method-group binding registers its bridge on demand instead
        // (HandlerBase.ResolveDelegateBridge's private arm). protected is kept (derived-class edges).
        var delegateBridges = new Dictionary<IMethodSymbol, DelegateBridgeLayout>(SymbolEqualityComparer.Default);
        foreach (var (method, ml) in methods)
        {
            if (method.IsGenericMethod) continue;
            if (UdonEventNames.ContainsKey(method.Name)) continue;
            if (ml.Returns.Count > 1) continue;
            if (IsExcludedFromSpeculativeBridge(method)) continue;
            delegateBridges[method] = new DelegateBridgeLayout(DelegateAbi.BridgeName(ml.ExportName), ml);
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
                      || m.MethodKind == MethodKind.PropertySet
                      || m.MethodKind == MethodKind.EventAdd
                      || m.MethodKind == MethodKind.EventRemove)
                     && m.DeclaringSyntaxReferences.Length > 0))
        {
            var safeName = SanitizeId(method.Name);
            var exportName = method.Parameters.Length > 0
                ? NameAllocator.FormatId(safeName, alloc.Allocate(safeName))
                : safeName;

            var paramIds = new string[method.Parameters.Length];
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var key = NameAllocator.ParamKey(method.Parameters[i].Name);
                paramIds[i] = NameAllocator.FormatId(key, alloc.Allocate(key));
            }

            var returns = BuildReturnSlots(method, exportName, alloc);

            methods[method] = new MethodLayout(exportName, NameAllocator.BodyLabel(exportName), paramIds, returns);
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
    public List<(IMethodSymbol method, MethodLayout interfaceLayout, IMethodSymbol implMethod, MethodLayout classLayout)>
        ComputeBridges(INamedTypeSymbol classType)
    {
        var bridges = new List<(IMethodSymbol, MethodLayout, IMethodSymbol, MethodLayout)>();
        var classLayout = Plan(classType);

        foreach (var iface in classType.AllInterfaces)
        {
            var ifaceLayout = Plan(iface);
            foreach (var (ifaceMethod, ifaceMl) in ifaceLayout.Methods)
            {
                var impl = classType.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
                // A default interface member is emitted as an internal function in each implementing
                // program; the canonical interface bridge targets that function like a class method.
                if (impl != null && impl.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    if (ifaceMl.Returns.Count <= 1)
                        bridges.Add((ifaceMethod, ifaceMl, impl, ifaceMl));
                    continue;
                }
                if (impl == null) continue;
                // Wave-9 round-2 [W5]: when the implicit implementation is a base-class VIRTUAL with an
                // override anywhere in the chain, FindImplementationForInterfaceMember returns the chain
                // ROOT — which the [W4] one-function-per-virtual-slot rule folds OUT of the layout, so the
                // direct lookup misses and the bridge was silently skipped: the call site SendCustomEvents
                // the canonical __iface_* name with no export anywhere (silent no-op + stale 0 on a real
                // client; unbounded self-reentry in the harness). Resolve to the chain member that OWNS
                // impl's virtual slot (unique by the [W4] invariant: one laid-out function per chain).
                var implOwner = impl;
                if (!classLayout.Methods.TryGetValue(implOwner, out var classMl))
                {
                    implOwner = null;
                    foreach (var (m, ml) in classLayout.Methods)
                    {
                        for (var cur = m.OverriddenMethod; cur != null; cur = cur.OverriddenMethod)
                            if (SymbolEqualityComparer.Default.Equals(cur, impl)) { implOwner = m; classMl = ml; break; }
                        if (implOwner != null) break;
                    }
                    if (implOwner == null) continue; // pre-existing skip: impl genuinely absent from the layout
                }
                // Tuple returns (N>1, ReturnId null) go through CrossCall directly, not a bridge.
                if (ifaceMl.Returns.Count > 1) continue;
                bridges.Add((ifaceMethod, ifaceMl, implOwner, classMl));
            }
        }

        return bridges;
    }

    List<ReturnSlot> BuildReturnSlots(IMethodSymbol method, string exportName, NameAllocator alloc)
    {
        var returns = new List<ReturnSlot>();
        if (method.ReturnsVoid) return returns;

        var retKey = NameAllocator.RetKey(exportName);
        var id = NameAllocator.FormatId(retKey, alloc.Allocate(retKey));

        if (TypeClassifier.IsAggregateValue(method.ReturnType))
            returns.Add(new ReturnSlot(id, StorageTypes.ObjectArray));
        else
            returns.Add(new ReturnSlot(id, Session.Types.GetStorageType(method.ReturnType)));

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
