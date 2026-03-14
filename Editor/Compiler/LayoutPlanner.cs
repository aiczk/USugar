using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Immutable layout for a single method's UASM naming.</summary>
public class MethodLayout
{
    public string ExportName { get; }
    public string BodyLabel { get; }
    public IReadOnlyList<string> ParamIds { get; }
    public string ReturnId { get; }

    public MethodLayout(string exportName, string bodyLabel, IReadOnlyList<string> paramIds, string returnId)
    {
        ExportName = exportName;
        BodyLabel = bodyLabel;
        ParamIds = paramIds;
        ReturnId = returnId;
    }
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

/// <summary>Immutable layout for a type's complete UASM variable naming.</summary>
public class TypeLayout
{
    public IReadOnlyDictionary<IMethodSymbol, MethodLayout> Methods { get; }
    public IReadOnlyDictionary<IFieldSymbol, FieldLayout> Fields { get; }
    public IReadOnlyDictionary<string, int> SymbolCounters { get; }

    public TypeLayout(
        Dictionary<IMethodSymbol, MethodLayout> methods,
        Dictionary<IFieldSymbol, FieldLayout> fields,
        IReadOnlyDictionary<string, int> symbolCounters = null)
    {
        Methods = methods;
        Fields = fields;
        SymbolCounters = symbolCounters ?? new Dictionary<string, int>();
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
                     && !m.IsImplicitlyDeclared)
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
                // Mangle if: has parameters, OR name collides with a Udon event export name
                exportName = (method.Parameters.Length > 0 || UdonEventExportNames.Contains(safeName))
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

            // Compute return ID: always for non-void methods (matches pure compiler)
            string returnId = null;
            if (!method.ReturnsVoid)
            {
                var retKey = exportName + "__ret";
                returnId = NameAllocator.FormatId(retKey, alloc.Allocate(retKey));
            }

            var bodyLabel = exportName + "__body";
            methods[method] = new MethodLayout(exportName, bodyLabel, paramIds, returnId);
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
                             && !m.IsImplicitlyDeclared && !m.IsGenericMethod && !m.IsAbstract))
                {
                    if (!overriddenMethods.Contains(bm) && baseLayout.Methods.TryGetValue(bm, out var baseMl))
                        methods.TryAdd(bm, baseMl);
                }
            }
            inheritBase = inheritBase.BaseType;
        }

        // Compute field layouts
        foreach (var member in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            var udonType = ExternResolver.GetUdonTypeName(member.Type);
            var flags = FieldFlags.None;
            if (member.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute")) flags |= FieldFlags.Sync;
            fields[member] = new FieldLayout(member.Name, udonType, flags);
        }

        return new TypeLayout(methods, fields, alloc.GetCounters());
    }

    TypeLayout PlanInterface(INamedTypeSymbol interfaceType)
    {
        var methods = new Dictionary<IMethodSymbol, MethodLayout>(SymbolEqualityComparer.Default);
        var alloc = new NameAllocator();

        foreach (var method in interfaceType.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared))
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

            string returnId = null;
            if (!method.ReturnsVoid)
            {
                var retKey = exportName + "__ret";
                returnId = NameAllocator.FormatId(retKey, alloc.Allocate(retKey));
            }

            methods[method] = new MethodLayout(exportName, exportName + "__body", paramIds, returnId);
        }

        return new TypeLayout(methods, new Dictionary<IFieldSymbol, FieldLayout>(SymbolEqualityComparer.Default));
    }

    /// <summary>
    /// Compute bridge exports needed when a class implements an interface
    /// whose method layout doesn't match the class's own layout.
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
                if (impl == null) continue;
                if (!classLayout.Methods.TryGetValue(impl, out var classMl)) continue;

                // Bridge needed when export name, param IDs, or return ID differ
                bool needsBridge = ifaceMl.ExportName != classMl.ExportName;
                if (!needsBridge)
                {
                    for (int i = 0; i < ifaceMl.ParamIds.Count && i < classMl.ParamIds.Count; i++)
                    {
                        if (ifaceMl.ParamIds[i] != classMl.ParamIds[i]) { needsBridge = true; break; }
                    }
                }
                if (!needsBridge && ifaceMl.ReturnId != classMl.ReturnId)
                    needsBridge = true;
                if (needsBridge)
                    bridges.Add((ifaceMethod, ifaceMl, classMl));
            }
        }

        return bridges;
    }

    /// <summary>
    /// Get layout for a method on a foreign UdonBehaviour. Checks the target's
    /// containing type, then walks up the base type hierarchy.
    /// </summary>
    public MethodLayout GetCalleeLayout(IMethodSymbol target)
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
        if (layout.Methods.TryGetValue(method, out var ml))
            return ml;

        throw new System.InvalidOperationException(
            $"Method {target.Name} not found in layout for {target.ContainingType.Name}");
    }
}
