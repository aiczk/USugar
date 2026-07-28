using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

public sealed class UasmEmitter
{
    readonly CompilationSession _session;
    readonly MaterializingUdonTypeSystem _materializingTypes;
    readonly LoweringState _state;
    readonly OperationLowerer _operations;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _delegateConvention;
    readonly LoweringServices _lowering;
    FieldDiscoveryPlanBuilder _fieldDiscovery;
    RecursionAnalysisPass _recursionAnalysis;
    VirtualDispatch _virtualDispatch;


    // Phase-local projections; immutable services remain on the session.
    Compilation _compilation => _state.Compilation;
    INamedTypeSymbol _classSymbol => _state.ClassSymbol;
    FlatModule _module => _state.Module;
    CoreBuilder _builder => _state.Builder;
    FrozenLayoutPlan _planner => _state.Planner;
    IReadOnlyDictionary<IMethodSymbol, MethodSlot> _methodSlots => _state.Methods.Slots;
    IMethodSymbol _currentMethod { get => _state.Methods.CurrentMethod; set => _state.Methods.CurrentMethod = value; }
    IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap
        => _state.TypeParamMap;
    HashSet<IMethodSymbol> _inheritedMethods = new(SymbolEqualityComparer.Default);
    HashSet<IMethodSymbol> _userClassDefaultMethods = new(SymbolEqualityComparer.Default);
    List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _state.FieldInitOps;
    Dictionary<string, string> _fieldChangeCallbacks => _state.FieldChangeCallbacks;
    List<EmitDiagnostic> _diagnostics => _state.Diagnostics;

    CodeGenResult _codeGenResult;
    VerifiedFlatModule _flatModule;

    public IReadOnlyList<EmitDiagnostic> Diagnostics => _diagnostics;
    public CodeGenResult CodeGenResult => _codeGenResult;
    internal BoundProgram Program
        => _state.Program
           ?? throw new InvalidOperationException(
               "The bound program is unavailable before emission completes.");
    internal CompilationSession Session => _session;
    internal IUdonTypeSystem Types => _state.Types;
    internal IEnumerable<OperationKind> HandledOperationKinds
        => _operations.HandledOperationKinds;

    internal RuntimeShape SourceShape(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => _state.Types.SourceShape(type, typeParameterMap);

    internal bool IsUserClass(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => SourceShape(type, typeParameterMap).Bundle
           == RuntimeBundleKind.Class;

    internal bool IsAggregateValue(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => SourceShape(type, typeParameterMap).Bundle
           == RuntimeBundleKind.Aggregate;

    internal bool IsObjectArrayEmulated(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        var bundle = SourceShape(type, typeParameterMap).Bundle;
        return bundle is RuntimeBundleKind.Class
            or RuntimeBundleKind.Aggregate;
    }

    static Dictionary<string, string> UdonEventNames => LayoutPlanBuilder.UdonEventNames;

    public UasmEmitter(Compilation compilation, INamedTypeSymbol classSymbol,
        FrozenLayoutPlan planner = null,
        UdonAbiCatalog externRegistry = null)
        : this(CreateSession(compilation, externRegistry), classSymbol, planner)
    {
    }

    public UasmEmitter(CompilationSession session, INamedTypeSymbol classSymbol,
        FrozenLayoutPlan planner = null)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        _session = session;
        var layouts =
            planner ?? new LayoutPlanBuilder(session).Build();
        _materializingTypes =
            new MaterializingUdonTypeSystem(
                session.Types, layouts, session.Compilation);
        _state = new LoweringState(
            session.Compilation,
            classSymbol,
            layouts,
            _materializingTypes);
        _operations = new OperationLowerer(_state);
        _lowering = _operations.Services;
        _bridge = new SyntheticBridgeBuilder(_state.Builder);
        _delegateConvention = new DelegateConventionStorage(_state);
    }

    static CompilationSession CreateSession(
        Compilation compilation, UdonAbiCatalog externRegistry)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (externRegistry == null)
            throw new ArgumentNullException(nameof(externRegistry),
                "USugar compilation requires the installed SDK's Udon ABI catalog.");
        return new CompilationSession(compilation, externRegistry);
    }

    string SourceStorageName(ISymbol member)
    {
        if (member is IPropertySymbol explicitProperty
            && explicitProperty.ExplicitInterfaceImplementations.Length > 0)
            return "__ifaceprop_"
                   + NameAllocator.Sanitize(
                       ClassTypeObjectContext.SpecKey(
                           explicitProperty.ContainingType))
                   + "_"
                   + NameAllocator.Sanitize(
                       explicitProperty.MetadataName);
        if (member == null
            || member.ContainingType == null
            || SymbolEqualityComparer.Default.Equals(
                member.ContainingType, _classSymbol))
            return member?.Name;
        for (var type = _classSymbol;
             type != null
             && !SymbolEqualityComparer.Default.Equals(
                 type, member.ContainingType);
             type = type.BaseType)
            if (type.GetMembers(member.Name).Any(candidate =>
                    candidate is IFieldSymbol or IPropertySymbol
                    && !candidate.IsStatic))
                return member is IPropertySymbol
                    ? "__baseprop_"
                      + NameAllocator.Sanitize(
                          ClassTypeObjectContext.SpecKey(
                              member.ContainingType))
                      + "_"
                      + NameAllocator.Sanitize(member.MetadataName)
                    : "__basefield_"
                      + NameAllocator.Sanitize(
                          ClassTypeObjectContext.SpecKey(
                              member.ContainingType))
                      + "_"
                      + NameAllocator.Sanitize(member.MetadataName);
        return member.Name;
    }

    // Type name resolution helper
    StorageType GetStorageType(ITypeSymbol type)
        => _state.ResolveStorageType(type);
    ITypeSymbol ResolveTypeForStorage(ITypeSymbol type)
        => TypeEnvironment.CloseType(_compilation, type, _typeParamMap);
    string GetStorageTypeName(ITypeSymbol type) => GetStorageType(type).Name;
    string GetArrayType(IArrayTypeSymbol arrType) => GetStorageTypeName(arrType);
    string GetArrayElemType(IArrayTypeSymbol arrType)
    {
        var t = GetArrayType(arrType);
        return t.Substring(0, t.Length - "Array".Length);
    }

    // ── CoreBuilder bridge helpers (old IrBuilder API → CoreBuilder) ──

    // ── Emit ──

    /// <summary>Compiler-internal CFG inspection surface.</summary>
    internal FlatModule Module => _module;
    /// <summary>Compiler-internal verified CFG inspection surface.</summary>
    internal VerifiedFlatModule FlatModule => _flatModule;

    internal CaptureScopeAnalysis CaptureScope => _state.Captures;
    public Compilation Compilation => _state.Compilation;
    public INamedTypeSymbol ClassSymbol => _state.ClassSymbol;

    // C4 (M5d): the one per-class ResolvedEdgeResolver instance — the relocated classifier core plus
    // the reach cores; stateless beyond this emitter, so every consumer (reach worklist, recursion
    // walk, tail matchers, legacy oracle, tests) shares it.
    ResolvedEdgeResolver _edgeResolver;
    internal ResolvedEdgeResolver EdgeResolver => _edgeResolver ??= new ResolvedEdgeResolver(this);
    RecursionAnalysisPass RecursionAnalysis => _recursionAnalysis ??=
        new RecursionAnalysisPass(
            _materializingTypes,
            _state,
            _planner,
            EdgeResolver);
    internal ResolvedEdgeResolver DebugBuildResolver() => EdgeResolver; // test entry (post-Emit state)

    // C4: the seeded-context reads the relocated CallEdge classifier consumes (null/empty before Emit
    // seeds them at the compile-plan build — the resolver fails loud on a pre-seed CallEdge call).
    internal VirtualDispatch VirtualDispatchInstance
        => _virtualDispatch
           ?? throw new InvalidOperationException(
               "Virtual dispatch was queried before the class type census was frozen.");
    internal ClassTypeObjectContext ClassTypes => _state.ClassTypes;
    internal FrozenLayoutPlan Planner => _planner;

    // CA call-graph rewrite (M5b prerequisite): test-only accessor exposing the populated RecursionInfo
    // (all six facets: RecursionGraphNodes, per-node RecursiveCallees/CycleCallees edge sets,
    // ThisFieldTouches, ReentrantDispatchSites, TailSparedDirectCallSites) post-Emit, so
    // RecursionFacetEquivalenceTests can census the legacy BuildRecursionInfo product and diff it
    // against the worklist-produced facets before the M5b swap. Unused by production emission.
    internal RecursionInfo DebugRecursionInfo => _state.Recursion;
    public string Emit()
    {
        var fields = DiscoverFields();
        var fieldInitializers =
            fields.InitializerOperations.ToArray();
        var sourceBodies =
            new BoundMethodBodyTable.Materializer(_compilation);
        _state.Methods.ConfigureBodyAuthority(sourceBodies);
        sourceBodies.RegisterNestedCallables(fieldInitializers);
        var (callables, reach) =
            DiscoverCallablesAndReach(
                fieldInitializers, sourceBodies);
        // Stage 2: closure-scope analysis feeding real codegen — EnvEmit's alloc/read/write and every
        // IsCapturingClosure call site (LoweringServices, InvocationHandler.Extern, this file) key off it.
        // Its roots are the reach definition projection (ComputeCaptureRoots); root bodies come from the
        // shared pre-emission callable graph — no re-fetch or second body walk (F1).
        // C1 fix: roots = the FULL reach artifact (all provenances); field inits = the emitter's own
        // fieldInitializers (own + base + auto-property + static, already discovered and ordered),
        // NOT CaptureScopeAnalysis's own own-class-instance-only re-collection which missed base field and
        // auto-property initializers.
        // CA rewrite (M4): seed the typeobj registry in stable-key order (not mint-walk discovery order),
        // so typeobj alloc / is-chain / virtual-dispatch-chain byte order is traversal-independent.
        _state.ClassTypes.Seed(reach.RuntimeClassTypes
            .OrderBy(StableOrdinalKey, StringComparer.Ordinal)
            .ThenBy(ClassTypeObjectContext.SpecKey, StringComparer.Ordinal));
        _virtualDispatch = new VirtualDispatch(_state.ClassTypes);
        var initializerRoots = fieldInitializers
            .Concat(CollectConstructedClassInitializerRoots(reach))
            .ToArray();
        sourceBodies.RegisterNestedCallables(initializerRoots);
        var bodyGraph = new RecursionNodeWalk(
            EdgeResolver, reach, initializerRoots,
            callables.Definitions, sourceBodies).Run();
        var closureIdentities = ClosureIdentityPlan.Build(bodyGraph.AllNodes);
        var captureRoots = ComputeCaptureRoots(
            reach, sourceBodies);
        var captures = CaptureScopeAnalysis.Build(
            _compilation, _classSymbol, captureRoots,
            bodyGraph.Bodies, initializerRoots);
        _state.SetClosurePlans(closureIdentities, captures);
        BindAndEmitMethods(
            callables,
            reach,
            fields,
            fieldInitializers,
            bodyGraph,
            closureIdentities,
            captures,
            sourceBodies);
        _builder.Complete();
        FlatVerify.Verify(_module);
        CoreFlatOptimizer.CoalesceSlots(_module);
        CoreFlatOptimizer.InsertRecursionSpills(_module);
        _flatModule =
            VerifiedFlatModule.VerifyAndFreeze(_module);
        _codeGenResult = CoreToUasm.Generate(_flatModule);
        return _codeGenResult.Uasm;
    }

    public uint GetHeapSize() => _codeGenResult.HeapSize;

    (CallableDefinitionPlan Callables, ReachabilityPlan Reach)
        DiscoverCallablesAndReach(
            IReadOnlyList<IOperation> fieldInitializers,
            BoundMethodBodyTable.Materializer sourceBodies)
    {
        // Build the single ReachableBodies fixpoint once after field discovery, but before any field
        // declaration reaches Structured IR. Initializers are semantic roots for the plan.
        // BuildRecursionInfo roots, and CaptureScope roots (all in EmitMethods / injected below).
        var methods = ComputeMethods();
        var reachable = BuildReachableBodiesViaResolver(
            methods, fieldInitializers, sourceBodies);
        var methodSet = new HashSet<IMethodSymbol>(
            methods, SymbolEqualityComparer.Default);
        var programMethods = ExecutableProjection(
            methods, sourceBodies, rejectUnsupported: true);
        var baseInstanceMethods = ExecutableProjection(
            reachable.BaseCopies
                .Where(method => !methodSet.Contains(method)),
            sourceBodies,
            rejectUnsupported: true);
        // Local functions register at their declaration/forward-reference
        // site. Eagerly projecting them as foreign statics creates a dead
        // duplicate FlatFunction.
        var foreignStatics = ExecutableProjection(
            reachable.ForeignStatics
                .Where(method =>
                    method.MethodKind
                        != MethodKind.LocalFunction),
            sourceBodies,
            rejectUnsupported: true);
        var structMethods = ExecutableProjection(
            reachable.StructMembers,
            sourceBodies,
            rejectUnsupported: true);
        var additionalDefinitions =
            ExecutableProjection(
                EnumerateAdditionalCallableDefinitions(),
                sourceBodies,
                rejectUnsupported: false);
        var definitions = programMethods
            .Concat(foreignStatics)
            .Concat(structMethods)
            .Concat(baseInstanceMethods)
            .Concat(reachable.BodyByDef.Keys)
            .Concat(reachable.GenericForeignStaticBodies.Keys)
            .Concat(reachable.StructMemberDefs)
            .Select(method =>
                method.OriginalDefinition)
            .Distinct<IMethodSymbol>(
                SymbolEqualityComparer.Default);
        definitions = ExecutableProjection(
                definitions,
                sourceBodies,
                rejectUnsupported: true)
            .Concat(additionalDefinitions)
            .Select(method => method.OriginalDefinition)
            .Distinct<IMethodSymbol>(
                SymbolEqualityComparer.Default)
            .ToArray();
        var census = new GenericTypeSpecCensus(
            _compilation,
            sourceBodies.GetOperation,
            EnumerateClassFieldInitOps,
            _classSymbol).Build(
                programMethods
                    .Concat(foreignStatics)
                    .Concat(structMethods)
                    .Concat(baseInstanceMethods),
                fieldInitializers);
        var definitionSet = new HashSet<IMethodSymbol>(
            definitions, SymbolEqualityComparer.Default);
        var eagerlyRegistered = new HashSet<IMethodSymbol>(
            programMethods
                .Where(method => !method.IsGenericMethod)
                .Concat(foreignStatics)
                .Concat(structMethods)
                .Concat(baseInstanceMethods),
            SymbolEqualityComparer.Default);
        var specializations = census.MethodSpecializations
            .Where(method =>
                definitionSet.Contains(method.OriginalDefinition)
                && !eagerlyRegistered.Contains(method))
            .ToArray();
        var callables = new CallableDefinitionPlan(
            programMethods,
            foreignStatics,
            structMethods,
            baseInstanceMethods,
            definitions,
            specializations,
            census.ClosureSpecializations);
        var reach = reachable.Freeze(census.ConstructedClasses);
        return (callables, reach);
    }

    static IMethodSymbol[] ExecutableProjection(
        IEnumerable<IMethodSymbol> methods,
        BoundMethodBodyTable.Materializer sourceBodies,
        bool rejectUnsupported)
    {
        var result = new List<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(
            SymbolEqualityComparer.Default);
        foreach (var method in methods
                     ?? Enumerable.Empty<IMethodSymbol>())
        {
            if (method == null || !seen.Add(method))
                continue;
            var body = sourceBodies.Get(method);
            if (body.Disposition
                == CallableBodyDisposition.Unsupported)
            {
                if (rejectUnsupported)
                    throw body.UnsupportedCallableException(
                        "Callable planning");
                continue;
            }
            if (body.HasExecutableCallableBody)
                result.Add(method);
        }
        return result.ToArray();
    }

    static IReadOnlyList<IMethodSymbol> ComputeCaptureRoots(
        ReachabilityPlan reach,
        BoundMethodBodyTable.Materializer sourceBodies)
    {
        var roots = reach.BodyByDef.Keys
            .Where(method =>
                sourceBodies.Get(method)
                    .HasExecutableCallableBody)
            .ToList();
        roots.AddRange(
            reach.GenericForeignStaticBodies.Keys
                .Where(method =>
                    sourceBodies.Get(method)
                        .HasExecutableCallableBody
                    && !reach.BodyByDef.ContainsKey(method)));
        return Array.AsReadOnly(roots.ToArray());
    }

    IEnumerable<IMethodSymbol> EnumerateAdditionalCallableDefinitions()
        => _planner.Census.Classes
            .Where(type => IsUserClass(type))
            .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>());

    // CA call-graph rewrite (M5a cutover): the reach fixpoint now runs through the unified resolver-driven
    // worklist instead of the legacy 5-collector BuildReachableBodies. Byte-neutral — M4's stable ordinal
    // decouples emit order from the worklist's (different) discovery order, and the worklist reproduces every
    // ReachableBodies facet (proven by golden + DiffFuzz). The open-base-generic defs ride the reach result
    // (ReachableBodies.OpenGenericBaseRoots) and reach the recursion graph through BodyByDef, so the former
    // legacy _openGenericBaseDefs side-effect field is gone — the recursion node source is the reach result.
    ReachableBodies BuildReachableBodiesViaResolver(
        IMethodSymbol[] methods,
        IReadOnlyList<IOperation> fieldInitializers,
        BoundMethodBodyTable.Materializer sourceBodies)
        => new ResolverDrivenReach(EdgeResolver, sourceBodies.Get,
            () => fieldInitializers, IsCollectibleStructMember, StableOrdinalKey).Build(methods);

    void SetReflectionValues()
    {
        var typeName = UdonBehaviourTypeMetadata.TypeName(_classSymbol);
        long typeId = UdonBehaviourTypeMetadata.TypeId(typeName);
        _state.Storage.DeclareGeneratedField(RuntimeReflectionFields.TypeId, StorageTypes.Int64, typeId);
        _state.Storage.DeclareGeneratedField(RuntimeReflectionFields.TypeName, StorageTypes.String, typeName);

        var ancestorIds = UdonBehaviourTypeMetadata.AssignableTypeIds(_classSymbol);
        if (UdonBehaviourTypeMetadata.ProgramRequiresAssignableIds(_classSymbol, _planner.Census))
            _state.Storage.DeclareField(
                RuntimeReflectionFields.AssignableTypeIds,
                StorageTypes.Int64Array,
                defaultValue: ancestorIds);
    }

    internal static long ComputeTypeId(string typeName)
        => UdonBehaviourTypeMetadata.TypeId(typeName);

    // ── Field discovery ──

    FieldDiscoveryPlan DiscoverFields()
    {
        if (_fieldDiscovery != null)
            throw new InvalidOperationException("Field discovery is already active.");
        _fieldDiscovery = new FieldDiscoveryPlanBuilder();
        foreach (var member in _classSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsImplicitlyDeclared) continue;
            if (member.IsStatic)
            {
                RequireStorageFreeStaticField(member);
                continue;
            }

            // First-class delegate field (design §2.1): ONE SystemObjectArray heap var holding the bundle
            // reference, null-initialized in UASM data. Private fields are bundled too; assign/invoke
            // lowering is type-directed rather than accessibility-directed. Intercepted BEFORE the generic
            // sync/flags block (M4 [T2]) so a [UdonSynced] delegate field hits the delegate-specific
            // reject in DeclareDelegateField — the single choke point shared with the base-class path.
            // Flags/syncMode were never used for delegate fields, so this reorder changes no output.
            if (member.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod != null)
            {
                DeclareDelegateField(member, delegateType);
                RecordSourceField(member, StorageTypes.ObjectArray, null);
                continue; // Skip normal field declaration
            }

            // Class bundles have a stable cross-program ABI, but Udon network sync still cannot serialize
            // the object[] representation.
            if (SourceShape(member.Type).ContainsUserClassPayload)
            {
                bool synced = member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute");
                if (synced)
                    throw new NotSupportedException(
                        $"Field '{member.Name}' carries a user class and is [UdonSynced]: the class object[] "
                        + "ABI can cross between programs on one client, but cannot be network-serialized. "
                        + "Sync plain data and reconstruct the class locally.");
            }

            var udonType = GetStorageTypeName(member.Type);
            var flags = FieldFlags.None;
            if (member.DeclaredAccessibility == Accessibility.Public
                || member.GetAttributes().Any(a => a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute"))
                flags |= FieldFlags.Export;
            string syncMode = ReadFieldSyncMode(member, udonType, ref flags);

            // Try to resolve constant field initializers as CLR objects
            object constValue = null;
            var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef?.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } declarator)
            {
                var model = _compilation.GetSemanticModel(declarator.SyntaxTree);
                var initOp = model.GetOperation(declarator.Initializer.Value);
                if (initOp != null)
                {
                    var constVal = initOp.ConstantValue;
                    if (constVal.HasValue && constVal.Value != null)
                    {
                        // Store CLR object directly; CodeGen + ApplyConstantValues handles application
                        constValue = constVal.Value;
                    }
                    if (constValue == null)
                    {
                        constValue = TryEvaluateFieldInitForHeap(initOp, member.Type);
                        if (constValue == null)
                            _fieldDiscovery.InstanceInitializers.Add(
                                new FieldInitializerPlan(member.Name, initOp, member.Type));
                    }
                }
            }
            _fieldDiscovery.DeclareField(
                member.Name, new StorageType(udonType), flags, constValue, syncMode);
            RecordSourceField(
                member, new StorageType(udonType), syncMode);

            // Aggregate (struct/tuple) field with NO explicit initializer → C# default-initializes it to a
            // zeroed struct. In the object[] emulation that requires a fresh default array; without it the heap
            // var stays null and `f.x = …` faults (NRE on __Set__). Reference-type/array fields stay null (correct).
            if (syntaxRef?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: not null }
                && member.Type is INamedTypeSymbol aggFieldType
                && IsAggregateValue(aggFieldType))
            {
                _fieldDiscovery.AggregateDefaults.Add((member.Name, aggFieldType));
            }

            // Detect [FieldChangeCallback("PropertyName")]
            var fcbAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
            if (fcbAttr != null && fcbAttr.ConstructorArguments.Length > 0
                && fcbAttr.ConstructorArguments[0].Value is string propName)
            {
                _fieldDiscovery.FieldChangeCallbacks[member.Name] = propName;
                _fieldDiscovery.DeclareGeneratedField(
                    $"__old_{member.Name}", new StorageType(udonType));
            }
        }

        // Field-like events (design §2.1, A-M2): materialize each as a private multicast delegate field
        // via the SAME DeclareDelegateField choke point plain delegate fields use (heap var = event
        // name, __dlgc_{sig} conv globals, sync/NetworkCallable/tuple-
        // return/ref-out reject all inherited for free). The compiler-synthesized backing IFieldSymbol
        // stays IsImplicitlyDeclared and is skipped by the field loop above — materialize here instead,
        // so it never double-declares.
        foreach (var evt in _classSymbol.GetMembers().OfType<IEventSymbol>())
            DeclareEvent(evt);

        // Properties → declare as heap variables
        foreach (var prop in _classSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsImplicitlyDeclared) continue;
            if (prop.IsStatic)
            {
                if (!IsComputedProperty(prop))
                    throw ClassAbiPolicy.UnsupportedStaticStorage(prop);
                continue;
            }
            // Auto-property iff it has a compiler-generated backing field (its accessors have empty bodies).
            // The old DeclaringSyntaxReferences.IsEmpty check was always false for source `{ get; set; }`
            // accessors, so a PRIVATE auto-property was never detected and its backing field went undeclared.
            var isAuto = prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
                .Any(f => f.IsImplicitlyDeclared && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));
            if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.Type is INamedTypeSymbol propertyDelegate
                && propertyDelegate.DelegateInvokeMethod != null)
                _state.Boundary.RequireCanDeclareDelegateSurface(prop, propertyDelegate);
            var udonType = GetStorageTypeName(prop.Type);
            var flags = FieldFlags.None;
            if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            var storageName = SourceStorageName(prop);
            _fieldDiscovery.DeclareField(storageName, new StorageType(udonType), flags,
                isAuto ? ResolveAutoPropInitializer(storageName, prop) : null);
            RecordSourceProperty(prop, new StorageType(udonType));
        }

        // Record count of derived-class field init ops; base class init ops added below
        // must be reordered to come first (C# spec: base → derived initializer order).
        int derivedFieldInitCount = _fieldDiscovery.InstanceInitializers.Count;
        var baseClassInitBoundaries = new List<int>(); // track boundaries per base class

        // Collect declared member SYMBOLS (name → derived-most declaration). A base member whose
        // name matches is either (a) part of one override chain — legal, one virtual slot — or
        // (b) `new`-style shadowing, where C# has TWO storages but this emitter's name-keyed heap
        // model would collapse both symbols onto ONE heap var (VM-verified silent state corruption:
        // SetBase/GetBase through the base symbol read the derived symbol's writes, and a
        // type-conflicting shadow halts the VM with HeapTypeMismatchException at runtime).
        // Storage collision is never acceptable → loud reject per design §8-3 (predates fcd-stage1).
        // Field-like events materialize storage under their bare name too (DeclareEvent/DeclareDelegateField)
        // — tracked here so a base field/prop/event of the same name collides loudly (design §8 item 6).
        var declaredMemberSyms = new Dictionary<string, ISymbol>();
        foreach (var m in _classSymbol.GetMembers())
            if (m is IFieldSymbol or IPropertySymbol or IEventSymbol && !m.IsImplicitlyDeclared
                && !m.IsStatic
                && !declaredMemberSyms.ContainsKey(m.Name))
                declaredMemberSyms[m.Name] = m;

        // Inherited fields and properties from user-defined base classes
        var baseType = _classSymbol.BaseType;
        while (baseType != null)
        {
            if (USugarCompilerHelper.IsFrameworkNamespace(baseType.ContainingNamespace) || baseType.Name == "UdonSharpBehaviour") break;
            baseClassInitBoundaries.Add(_fieldDiscovery.InstanceInitializers.Count);
            foreach (var member in baseType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsImplicitlyDeclared) continue;
                // A FIELD can never be overridden, so any name match with a nearer declaration is
                // `new`-style shadowing — two distinct symbols, one heap var. Loud. (Materialized static
                // readonly fields are name-keyed heap vars too, so this applies to them identically;
                // static MUTABLE fields carry no storage and were never tracked into declaredMemberSyms.)
                if (declaredMemberSyms.TryGetValue(member.Name, out var fieldShadower))
                {
                    var externallyVisible = member.DeclaredAccessibility == Accessibility.Public
                        || member.GetAttributes().Any(a => a.AttributeClass?.Name is
                            "SerializeField" or "SerializeFieldAttribute");
                    if (externallyVisible)
                        throw new NotSupportedException(ShadowedStorageError(member, fieldShadower));
                }
                if (member.IsStatic)
                {
                    RequireStorageFreeStaticField(member);
                    continue;
                }

                // Delegate field from a base class → same single-SystemObjectArray declaration as the derived
                // path (private bundled too). Must intercept BEFORE the generic initializer scan below, which
                // would otherwise also enqueue the init op (the helper routes it via _fieldInitOps itself) —
                // this fixes the old base-path store into a never-declared variable.
                if (member.Type is INamedTypeSymbol baseDelegateType && baseDelegateType.DelegateInvokeMethod != null)
                {
                    DeclareDelegateField(member, baseDelegateType);
                    RecordSourceField(member, StorageTypes.ObjectArray, null);
                    declaredMemberSyms[member.Name] = member;
                    continue;
                }

                var udonType = GetStorageTypeName(member.Type);
                object constValue = null;
                var syntaxRef2 = member.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxRef2?.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } decl)
                {
                    var model = _compilation.GetSemanticModel(decl.SyntaxTree);
                    var initOp = model.GetOperation(decl.Initializer.Value);
                    if (initOp != null)
                    {
                        var constVal = initOp.ConstantValue;
                        if (constVal.HasValue && constVal.Value != null)
                            constValue = constVal.Value;
                        if (constValue == null)
                        {
                            constValue = TryEvaluateFieldInitForHeap(initOp, member.Type);
                            if (constValue == null)
                                _fieldDiscovery.InstanceInitializers.Add(new FieldInitializerPlan(
                                    SourceStorageName(member), initOp, member.Type));
                        }
                    }
                }
                declaredMemberSyms[member.Name] = member;
                var baseFlags = FieldFlags.None;
                if (member.DeclaredAccessibility == Accessibility.Public
                    || member.GetAttributes().Any(a => a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute"))
                    baseFlags |= FieldFlags.Export;
                // B37: a [UdonSynced] field declared on a user base class must keep its .sync
                // directive in the DERIVED program — pre-fix this walk read no sync attributes, so
                // the field compiled clean but shipped unsynced (networking silently dead on device).
                var baseSyncMode = ReadFieldSyncMode(member, udonType, ref baseFlags);

                var memberStorageName = SourceStorageName(member);
                _fieldDiscovery.DeclareField(
                    memberStorageName,
                    new StorageType(udonType),
                    baseFlags,
                    constValue,
                    baseSyncMode);
                RecordSourceField(
                    member, new StorageType(udonType), baseSyncMode);

                var baseFcbAttr = member.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
                if (baseFcbAttr != null && baseFcbAttr.ConstructorArguments.Length > 0
                    && baseFcbAttr.ConstructorArguments[0].Value is string basePropName)
                {
                    _fieldDiscovery.FieldChangeCallbacks[memberStorageName] = basePropName;
                    _fieldDiscovery.DeclareGeneratedField(
                        $"__old_{memberStorageName}", new StorageType(udonType));
                }
            }
            // Field-like events inherited from a user base class (design §2.1, A-M2) — same
            // shadow-collision guard as base fields/props (design §8 item 6).
            foreach (var evt in baseType.GetMembers().OfType<IEventSymbol>())
            {
                if (declaredMemberSyms.TryGetValue(evt.Name, out var evtShadower))
                    throw new NotSupportedException(ShadowedStorageError(evt, evtShadower));
                DeclareEvent(evt);
                declaredMemberSyms[evt.Name] = evt;
            }
            foreach (var prop in baseType.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsImplicitlyDeclared) continue;
                if (prop.IsStatic)
                {
                    if (!IsComputedProperty(prop))
                        throw ClassAbiPolicy.UnsupportedStaticStorage(
                            prop);
                    continue;
                }
                // Auto-property iff it has a compiler-generated backing field (its accessors have empty bodies).
            // The old DeclaringSyntaxReferences.IsEmpty check was always false for source `{ get; set; }`
            // accessors, so a PRIVATE auto-property was never detected and its backing field went undeclared.
            var isAuto = prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
                .Any(f => f.IsImplicitlyDeclared && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));
                if (declaredMemberSyms.TryGetValue(prop.Name, out var propShadower))
                {
                    // Override chain (one virtual slot) → the leaf declaration owns the bare name.
                    if (propShadower is IPropertySymbol leafProp && IsOverrideChainAncestor(leafProp, prop))
                    {
                        // C# gives an override auto-property its OWN backing field: base.P (the only
                        // non-virtual property access) statically binds this BASE declaration's
                        // storage while every receiver-based read binds the chain leaf. The round-5
                        // [N1] name-unification overshot exactly here (`base.P=5; P=7;` → VM 77 vs
                        // CLR 57): declare a per-declaration backing var for the overridden AUTO
                        // declaration, used only by its base-instance copy accessors (base.P calls).
                        // Round-8 [R2]: C# runs the BASE declaration's initializer into THIS backing
                        // (the leaf's stays default — DiffFuzz: base.P*10+P ref=50).
                        if (isAuto)
                            _fieldDiscovery.DeclareGeneratedField(
                                BaseAutoPropBackingName(prop), GetStorageType(prop.Type),
                                ResolveAutoPropInitializer(BaseAutoPropBackingName(prop), prop));
                        continue;
                    }
                    // A storage-BEARING base member (auto-prop) hidden without an override relation
                    // is the heap-var collision — loud. A MANUAL base property has no storage (its
                    // accessors are real functions; the planner already disambiguates their export
                    // names on collision), so `new`-shadowing it stays legal (wave-7 pinned).
                    if (isAuto)
                        _fieldDiscovery.DeclareGeneratedField(
                            SourceStorageName(prop), GetStorageType(prop.Type),
                            ResolveAutoPropInitializer(SourceStorageName(prop), prop));
                    continue;
                }
                if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
                var udonType = GetStorageTypeName(prop.Type);
                var flags = FieldFlags.None;
                if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
                declaredMemberSyms[prop.Name] = prop;
                var storageName = SourceStorageName(prop);
                _fieldDiscovery.DeclareField(storageName, new StorageType(udonType), flags,
                    isAuto ? ResolveAutoPropInitializer(storageName, prop) : null);
                RecordSourceProperty(prop, new StorageType(udonType));
            }
            baseType = baseType.BaseType;
        }

        // Reorder field init ops: base class initializers must run before derived (C# spec).
        // Base classes were walked nearest-parent-first, so reverse class-level order
        // while preserving field order within each class.
        ReorderBaseFirst(
            _fieldDiscovery.InstanceInitializers, baseClassInitBoundaries, derivedFieldInitCount);

        var result = _fieldDiscovery.Build();
        _fieldDiscovery = null;
        return result;
    }

    void RecordSourceField(
        IFieldSymbol field,
        StorageType storageType,
        string syncMode)
        => _fieldDiscovery.RecordSourceField(
            field.Name,
            field,
            field.Type,
            storageType,
            IsSerializedField(field),
            syncMode);

    void RecordSourceProperty(
        IPropertySymbol property,
        StorageType storageType)
        => _fieldDiscovery.RecordSourceField(
            property.Name,
            property,
            property.Type,
            storageType,
            property.DeclaredAccessibility == Accessibility.Public);

    static bool IsSerializedField(IFieldSymbol field)
    {
        if (field.IsConst || field.IsStatic || field.IsReadOnly)
            return false;
        var attributes = field.GetAttributes();
        if (attributes.Any(attribute =>
                attribute.AttributeClass?.Name == "OdinSerializeAttribute"))
            return true;
        if (attributes.Any(attribute =>
                attribute.AttributeClass?.Name == "NonSerializedAttribute"))
            return false;
        return field.DeclaredAccessibility == Accessibility.Public
               || attributes.Any(attribute =>
                   attribute.AttributeClass?.Name is
                       "SerializeField"
                       or "SerializeFieldAttribute"
                       or "SerializeReference"
                       or "SerializeReferenceAttribute");
    }

    /// <summary>Reorders inherited initializer groups to C# base-first order.</summary>
    static void ReorderBaseFirst<T>(List<T> ops, List<int> boundaries, int derivedCount)
    {
        if (ops.Count <= derivedCount) return;
        boundaries.Add(ops.Count); // sentinel
        var reordered = new List<T>();
        for (int i = boundaries.Count - 2; i >= 0; i--)
        {
            int start = boundaries[i];
            int end = boundaries[i + 1];
            for (int j = start; j < end; j++)
                reordered.Add(ops[j]);
        }
        for (int j = 0; j < derivedCount; j++)
            reordered.Add(ops[j]);
        ops.Clear();
        ops.AddRange(reordered);
    }

    /// <summary>
    /// Reads a field's [UdonSynced] attribute — the ONE knowledge source for the Sync flag, the
    /// sync-mode string, and the syncable-type validation, shared by the own-class and base-class
    /// field declaration paths (B37: the base walk read no sync attributes, so a [UdonSynced] field
    /// declared on a user base class shipped in the derived program WITHOUT its .sync directive).
    /// Sets FieldFlags.Sync and returns the UASM sync mode ("none"/"linear"/"smooth"); returns null
    /// (flags untouched) for unsynced fields. Throws on a type Udon sync cannot carry.
    /// </summary>
    string ReadFieldSyncMode(IFieldSymbol member, string udonType, ref FieldFlags flags)
    {
        var syncAttr = member.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "UdonSyncedAttribute");
        if (syncAttr == null) return null;

        flags |= FieldFlags.Sync;
        string syncMode;
        if (syncAttr.ConstructorArguments.Length > 0 && syncAttr.ConstructorArguments[0].Value is int modeVal)
            syncMode = modeVal switch { 2 => "linear", 3 => "smooth", _ => "none" };
        else
            syncMode = "none";

        var syncCheckType = (member.Type is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Enum)
            ? GetStorageTypeName(nt.EnumUnderlyingType)
            : udonType;
        if (!ExternResolver.IsSyncableType(syncCheckType))
            throw new NotSupportedException(
                $"Cannot sync field '{member.Name}': type '{member.Type}' is not supported by "
                + "Udon sync. Udon can sync only bool, char, byte, sbyte, short, ushort, int, "
                + "uint, long, ulong, float, double, string, VRCUrl, Vector2/3/4, Quaternion, "
                + "Color, Color32, and arrays of these.");

        var behaviourSyncMode =
            EmitPolicy.GetBehaviourSyncModeName(_classSymbol);
        if (behaviourSyncMode is "None" or "NoVariableSync")
            throw new NotSupportedException(
                $"Field '{member.Name}' cannot be synced on an UdonBehaviour "
                + "with sync mode None.");
        if (behaviourSyncMode == "Continuous"
            && member.Type is IArrayTypeSymbol)
            throw new NotSupportedException(
                $"Field '{member.Name}' is an array, which is not supported "
                + "by Continuous sync. Use Manual sync for array fields.");
        if (behaviourSyncMode == "Manual"
            && syncMode is "linear" or "smooth")
            throw new NotSupportedException(
                $"Field '{member.Name}' uses {syncMode} interpolation, which "
                + "cannot be used with Manual sync.");
        if (syncMode == "linear"
            && !ExternResolver.IsLinearSyncableType(syncCheckType))
            throw new NotSupportedException(
                $"Field '{member.Name}' type '{member.Type}' is not supported "
                + "for linear sync.");
        if (syncMode == "smooth"
            && !ExternResolver.IsSmoothSyncableType(syncCheckType))
            throw new NotSupportedException(
                $"Field '{member.Name}' type '{member.Type}' is not supported "
                + "for smooth sync.");
        return syncMode;
    }

    void RequireStorageFreeStaticField(IFieldSymbol member)
    {
        if (member.HasConstantValue)
            return;
        if (member.IsReadOnly
            && EmitPolicy.TryGetConstFieldInitializer(
                _compilation, member, out _))
            return;
        throw ClassAbiPolicy.UnsupportedStaticStorage(member);
    }

    /// <summary>
    /// First-class delegate field (design §2.1/§1.6): ONE SystemObjectArray heap var holding the bundle
    /// reference, null-initialized in UASM data. Never exported (exported/synced vars must not be retyped;
    /// SetProgramVariable needs no export) — [UdonSynced] is rejected HERE (M4 [T2], single choke point):
    /// pre-fix the own-class path threw the generic IsSyncableType message while the base-class path
    /// (which reads no sync attributes at all) compiled the synced delegate field CLEAN with no .sync
    /// directive. An initializer (e.g. `public Action cb = M;`) always becomes runtime bundle construction
    /// at _start via _fieldInitOps, which also fixes the old silent drop of derived-class initializers.
    /// Shared by the derived-class and base-class field paths — AND (design §2.1, A-M2) by field-like
    /// event materialization: an IEventSymbol exposes the same ISymbol surface this method actually
    /// uses (GetAttributes/Name/DeclaringSyntaxReferences — the delegate TYPE is passed separately by
    /// every caller), so widening from IFieldSymbol to ISymbol is behavior-neutral for existing callers.
    /// </summary>
    void DeclareDelegateField(ISymbol member, INamedTypeSymbol delegateType, string storageName = null)
    {
        storageName ??= member.Name;
        // M4 [T2]: delegate bundles are outside the supported Udon sync-type surface, so reject them
        // consistently on both declaration paths.
        if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute"))
            throw new NotSupportedException(
                $"[UdonSynced] delegate field '{member.Name}' cannot be synced: a delegate value is a "
                + "runtime object[] bundle (kind/target/method/addr), which Udon sync cannot carry. Sync "
                + "plain data instead and re-create the delegate locally.");
        // Design §2.4 (A-M2): [NetworkCallable] marks a METHOD as a remotely-invokable entry point —
        // it has no meaning on a delegate value (a bundle is program-local and cannot cross the
        // network), so reject it the same way RejectNetworkCallableDelegates rejects a delegate-typed
        // method param/return. Applies uniformly to plain delegate fields and event backing storage.
        if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "NetworkCallableAttribute"))
            throw new NotSupportedException(
                $"[NetworkCallable] delegate '{member.Name}' is not supported: NetworkCallable marks a "
                + "method as a remotely-invokable entry point, which does not apply to a delegate value.");
        // §3.4-1: ref/out delegate signatures are rejected at the convention-var declaration side too.
        _state.Boundary.RequireCanDeclareDelegateSurface(member, delegateType);

        _fieldDiscovery.DeclareField(storageName, StorageTypes.ObjectArray, FieldFlags.None);

        // Declare the signature-keyed __dlgc_ convention vars for this delegate signature (§3.2).
        var invoke = delegateType.DelegateInvokeMethod;
        // A delegate field alone does not expose a callable bridge, so its declaration only needs
        // argument/return convention storage. DelegateConventionStorage declares the complete surface,
        // including env, when an actual bridge is emitted.
        var (convArgs, convRet, _) = LoweringServices.GetConventionFieldNames(
            delegateType, _materializingTypes);
        for (int ci = 0; ci < convArgs.Length; ci++)
            _fieldDiscovery.TryDeclareVar(convArgs[ci],
                _materializingTypes.GetStorageType(
                    invoke.Parameters[ci].Type, _typeParamMap));
        if (convRet != null)
            _fieldDiscovery.TryDeclareVar(convRet,
                _materializingTypes.GetStorageType(
                    invoke.ReturnType, _typeParamMap));

        if (member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is VariableDeclaratorSyntax { Initializer: not null } dlgDeclarator)
        {
            var model = _compilation.GetSemanticModel(dlgDeclarator.SyntaxTree);
            // Binding the initializer VALUE syntax returns the conversion-STRIPPED inner operation
            // (IAnonymousFunctionOperation / IMethodReferenceOperation) which no expression handler
            // accepts — the initializer would be silently dropped to default(T). Bind the
            // EqualsValueClause itself and take its Value: the IDelegateCreationOperation (possibly
            // under a conversion) that VisitDelegateCreation lowers to the runtime bundle.
            var initOp = (model.GetOperation(dlgDeclarator.Initializer) as ISymbolInitializerOperation)?.Value
                         ?? model.GetOperation(dlgDeclarator.Initializer.Value);
            if (initOp != null)
                _fieldDiscovery.InstanceInitializers.Add(
                    new FieldInitializerPlan(storageName, initOp, delegateType));
        }
    }

    /// <summary>
    /// Field-like event materialization (design §2.1, A-M2). Custom-accessor events
    /// (`event E { add{...} remove{...} }`) are R1 — field-like only, since a custom add/remove has no
    /// well-defined backing storage this model can materialize. Static events have no shared-memory
    /// story on Udon (no design provision at all — same rationale as static mutable fields, R8) and are
    /// rejected rather than silently materialized as per-instance storage, which would diverge from C#'s
    /// single shared static event. Shared by the derived-class and base-class event paths.
    /// </summary>
    void DeclareEvent(IEventSymbol evt)
    {
        if (evt.IsStatic)
            throw ClassAbiPolicy.UnsupportedStaticStorage(evt);
        // Field-like events get compiler-synthesized (IsImplicitlyDeclared) add/remove accessors; a
        // custom accessor body means the user wrote add{...}/remove{...} explicitly.
        if (evt.AddMethod == null || !evt.AddMethod.IsImplicitlyDeclared
            || evt.RemoveMethod == null || !evt.RemoveMethod.IsImplicitlyDeclared)
            return;
        if (evt.Type is not INamedTypeSymbol delegateType || delegateType.DelegateInvokeMethod == null)
            throw new NotSupportedException($"Event '{evt.Name}' has a non-delegate type.");
        DeclareDelegateField(evt, delegateType, evt.Name);
        _fieldDiscovery.RecordSourceField(
            evt.Name,
            evt,
            evt.Type,
            StorageTypes.ObjectArray,
            isSerialized: false);
    }

    /// <summary>True when <paramref name="ancestor"/> is reachable from <paramref name="leaf"/> via
    /// the OverriddenProperty chain — i.e. both declarations share ONE virtual slot.</summary>
    static bool IsOverrideChainAncestor(IPropertySymbol leaf, IPropertySymbol ancestor)
    {
        for (var p = leaf; p != null; p = p.OverriddenProperty)
            if (SymbolEqualityComparer.Default.Equals(p, ancestor)) return true;
        return false;
    }

    /// <summary>Storage var for an overridden base auto-property DECLARATION (per-declaration
    /// backing, mirroring C#'s one-backing-field-per-auto-prop-declaration semantics).</summary>
    static string BaseAutoPropBackingName(IPropertySymbol prop)
        => $"__basebk_{SanitizeId(prop.ContainingType.ToDisplayString())}_{prop.Name}";

    /// <summary>Backing heap var an auto-property ACCESSOR body reads/writes. The chain-LEAF
    /// declaration visible from the compiled class owns the bare property name (exported storage,
    /// all virtual dispatch); an overridden base declaration uses its per-declaration var, reached
    /// only through its base-instance copy accessors (`base.P`). ABSTRACT declarations have no
    /// backing field at all — their registered copies are dead stubs (base.P on an abstract member
    /// is illegal C#; receiver-based access dispatches the exported leaf accessor), so they read
    /// the leaf var to stay validator-clean.</summary>
    string AutoPropBackingVar(IPropertySymbol autoProp)
    {
        if (autoProp.IsAbstract) return autoProp.Name;
        for (var t = _classSymbol; t != null
            && !SymbolEqualityComparer.Default.Equals(t, autoProp.ContainingType); t = t.BaseType)
            foreach (var p in t.GetMembers(autoProp.Name).OfType<IPropertySymbol>())
                if (IsOverrideChainAncestor(p, autoProp))
                    return BaseAutoPropBackingName(autoProp);
        return SourceStorageName(autoProp);
    }

    /// <summary>Round-8 [R2]: auto-property initializers (IPropertyInitializerOperation) were
    /// handled nowhere — `public int P{get;set;}=5;` compiled clean and read 0 (DiffFuzz native-CLR
    /// oracle ref=5; overridden-base flavor base.P*10+P ref=50, the base declaration's initializer
    /// runs into its __basebk storage). Resolve the declaration's initializer for the given backing
    /// var: constants (and heap-evaluable values) become heap defaults exactly like field
    /// initializers; anything else is enqueued for runtime initialization at _start (user-Start and
    /// _start-synthesized paths both drain _fieldInitOps), behind the same base-first reordering as
    /// fields. Binds the EqualsValueClause itself (delegate-typed initializers need the
    /// conversion-carrying ISymbolInitializerOperation.Value, mirroring DeclareDelegateField).</summary>
    object ResolveAutoPropInitializer(string backingVar, IPropertySymbol prop)
    {
        if (prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is not PropertyDeclarationSyntax { Initializer: not null } decl)
            return null;
        var model = _compilation.GetSemanticModel(decl.SyntaxTree);
        var initOp = (model.GetOperation(decl.Initializer) as ISymbolInitializerOperation)?.Value
                     ?? model.GetOperation(decl.Initializer.Value);
        if (initOp == null) return null;
        var constVal = initOp.ConstantValue;
        if (constVal.HasValue && constVal.Value != null) return constVal.Value;
        var heapVal = TryEvaluateFieldInitForHeap(initOp, prop.Type);
        if (heapVal != null) return heapVal;
        _fieldDiscovery.InstanceInitializers.Add(
            new FieldInitializerPlan(backingVar, initOp, prop.Type));
        return null;
    }

    static string ShadowedStorageError(ISymbol baseMember, ISymbol shadower)
        => $"Member '{baseMember.ContainingType.Name}.{baseMember.Name}' is hidden by "
         + $"'{shadower.ContainingType.Name}.{shadower.Name}' without an override relation "
         + "('new'-style shadowing). C# gives the two members separate storages, but the compiled "
         + "program keys heap variables by member NAME, so both symbols would silently collapse "
         + "onto one heap var (wrong values, or a runtime heap-type mismatch for type-conflicting "
         + "shadows). Shadowing an inherited field/property is not supported by the current storage ABI — rename the "
         + "member, or use virtual/override.";

    // ── EmitMethods ──

    /// <summary>The class's own + inherited method universe — the ReachableBodies SEED and the first-pass
    /// registration set. F3: single-sourced from the planner's FROZEN Phase-1 result
    /// (<see cref="TypeLayout.Methods"/>) — the ONE place the method family is derived (own non-generic +
    /// inherited-non-overridden, with the override-reuse / [W4] chain-slot / collision-rename rules) — so
    /// this method no longer re-derives the same inherit walk (deleting the loop whose own comment admitted
    /// it "mirrors the planner's inherit loop").
    ///
    /// One EXPLICIT emitter-only projection is layered on: own GENERIC ordinary/accessor methods. The
    /// planner deliberately excludes them (they are monomorphized per call-site and have no per-spec
    /// layout), but the reach fixpoint must walk their bodies (a generic method may reach struct/foreign/
    /// base members) and BuildRecursionInfo needs their DEFINITIONS as recursion-graph roots (a recursive
    /// generic method must spill). Empirically (rm3 method-universe probe) this is the ONLY membership
    /// difference between the two derivations across the census corpus + deep-inheritance / new-shadow /
    /// explicit-interface / generic-mix stress shapes.
    ///
    /// Sets <see cref="_inheritedMethods"/> (the inherited subset = planned methods NOT declared on this
    /// class). The pipeline accepts only a frozen layout plan, so this class is already planned.</summary>
    IMethodSymbol[] ComputeMethods()
    {
        var planned = _planner.GetLayout(_classSymbol).Methods.Keys.ToArray();
        _inheritedMethods = new HashSet<IMethodSymbol>(
            planned.Where(m => !SymbolEqualityComparer.Default.Equals(m.ContainingType, _classSymbol)),
            SymbolEqualityComparer.Default);

        // Emitter-only projection: own generic user-method DEFINITIONS (see summary).
        var ownGenerics = _classSymbol.GetMembers().OfType<IMethodSymbol>().Where(IsOwnGenericSeed);

        var defaultInterfaceMethods = _classSymbol.AllInterfaces
            .Where(iface =>
                _planner.AllLayouts.ContainsKey(iface))
            .SelectMany(iface => _planner.GetLayout(iface).Methods.Keys)
            .Where(method => !method.IsAbstract
                && SymbolEqualityComparer.Default.Equals(
                    _classSymbol.FindImplementationForInterfaceMember(method), method));

        _userClassDefaultMethods = new HashSet<IMethodSymbol>(
            _planner.Census.Classes
                .Where(type => IsUserClass(type))
                .SelectMany(type => type.AllInterfaces
                    .Where(iface =>
                        _planner.AllLayouts.ContainsKey(iface))
                    .SelectMany(iface => _planner.GetLayout(iface).Methods.Keys)
                    .Where(method => !method.IsAbstract
                        && SymbolEqualityComparer.Default.Equals(
                            type.FindImplementationForInterfaceMember(method), method))),
            SymbolEqualityComparer.Default);

        return planned.Concat(ownGenerics).Concat(defaultInterfaceMethods).Concat(_userClassDefaultMethods)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
    }

    /// <summary>C3: the own-generic method-DEFINITION projection, single-sourced so the ComputeMethods reach
    /// seed and the BuildRecursionInfo recursion-root arm can never drift on which MethodKinds they cover.
    /// A generic method with no per-spec planner layout (monomorphized at call sites) still needs (a) its
    /// body walked as a reach seed and (b) a recursion-graph node so a recursive spec's frame spills. The
    /// user-method kinds are Ordinary / ExplicitInterfaceImplementation / PropertyGet / PropertySet — the
    /// last two are dead for generics (accessors are never generic), included only to make the two projections
    /// textually identical. Explicit-interface generic methods are currently loud-rejected at their (interface-
    /// dispatch-only) call site, so covering them here is defensive: if that reject is ever lifted, the
    /// recursion root is already present rather than silently missing.</summary>
    static bool IsOwnGenericSeed(IMethodSymbol m)
        => m.IsGenericMethod && !m.IsImplicitlyDeclared
           && m.MethodKind is MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation
              or MethodKind.PropertyGet or MethodKind.PropertySet;

    void BindAndEmitMethods(
        CallableDefinitionPlan callables,
        ReachabilityPlan reach,
        FieldDiscoveryPlan fields,
        IReadOnlyList<IOperation> fieldInitializers,
        CallableBodyGraph bodyGraph,
        ClosureIdentityPlan closureIdentities,
        CaptureScopeAnalysis captures,
        BoundMethodBodyTable.Materializer sourceBodies)
    {
        RegisterProgram(
            callables, reach, fieldInitializers);
        var specializationRegistrar =
            new SpecializationRegistrar(_lowering);
        foreach (var specialization
                 in callables.SpecializationsByKey.Values)
            specializationRegistrar.Register(specialization);
        foreach (var closure in callables.ClosureSpecializations)
        {
            var definition = _state.ClosureIdentities.CanonicalDefinition(closure.Method);
            var method = closure.Method.IsGenericMethod
                && !closure.Method.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter)
                ? definition.Construct(closure.Method.TypeArguments.ToArray())
                : definition;
            specializationRegistrar.Register(
                new ClosureSpecializationCandidate(
                    method,
                    closure.OwnerSpecs,
                    closure.ContainingTypeSpec));
        }
        _state.Methods.FreezeCallableRegistry();
        var callableBodies =
            _state.Methods.RegisteredBodies.ToArray();
        var methodBodies = sourceBodies.Freeze(
            callableBodies.Select(
                body => body.Method.OriginalDefinition));
        var abiBuilder = new BoundAbiPlanBuilder(
            _session.AbiCatalog);
        var boundSource = BindSourceSemantics(
            fields,
            reach,
            fieldInitializers,
            methodBodies,
            abiBuilder);
        _state.SyntheticDemandPlanner
            .ExpandDynamicBundleStringDemands(
                _materializingTypes
                    .SnapshotKnownBundleTypes(),
                _lowering);
        var syntheticDemands = _state.PublishSyntheticDemands();
        var syntheticDispatch = BindSyntheticDispatch(syntheticDemands);
        var recursion = RecursionAnalysis.Analyze(bodyGraph);
        _state.SetRecursionPlan(recursion);
        var abi = abiBuilder.Publish();
        var types = _materializingTypes.Publish();
        var typeFacts = _session.TypeFacts.FreezeCopy();
        var aggregates = _state.Aggregates.Publish();
        var classTypes = _state.ClassTypes.Publish();
        var program = new BoundProgram(
            callables,
            callableBodies,
            fields,
            closureIdentities,
            captures,
            recursion,
            syntheticDemands,
            boundSource.CallSites,
            boundSource.Initializers,
            boundSource.ClassInitializers,
            boundSource.Deconstructions,
            boundSource.Conversions,
            boundSource.Constants,
            methodBodies,
            boundSource.Values,
            boundSource.SourceStorageNames,
            syntheticDispatch.ObjectToStringSlot,
            syntheticDispatch.Sites,
            abi,
            types,
            typeFacts,
            _planner,
            aggregates,
            classTypes);
        _state.PublishBoundProgram(program);
        FieldPlanEmitter.Emit(program.Fields, _state);
        SetReflectionValues();
        DeclareTypeObjectConstants();
        new CallableRegistrar(_state).Materialize(program);
        EmitRegisteredBodies(program);
        RecursionAnalysis.VerifyRegisteredCallablesAreNodes(bodyGraph);
    }

    (IMethodSymbol ObjectToStringSlot,
        IDictionary<BoundSyntheticDispatchKey, DispatchPlan> Sites)
        BindSyntheticDispatch(
        SyntheticDemandPlan demands)
    {
        var objectToString = _compilation
            .GetSpecialType(SpecialType.System_Object)
            .GetMembers("ToString")
            .OfType<IMethodSymbol>()
            .First(method =>
                !method.IsStatic && method.Parameters.Length == 0);
        var sites = new Dictionary<
            BoundSyntheticDispatchKey, DispatchPlan>();

        void Bind(INamedTypeSymbol receiver, IMethodSymbol target)
        {
            var key = new BoundSyntheticDispatchKey(receiver, target);
            if (sites.ContainsKey(key)) return;
            sites.Add(
                key,
                _virtualDispatch.Resolve(
                    CallableSites.Synthetic(
                        CallableSiteKind.Method, target),
                    receiver));
        }

        foreach (var receiver in demands.ClassToStringTypes)
            Bind(receiver, objectToString);
        foreach (var demand in demands.ReceiverBridges)
        {
            var member = demand.Binding.TargetMethod;
            if (member.ContainingType is INamedTypeSymbol receiver)
                Bind(receiver, member);
        }

        return (objectToString, sites);
    }

    (IDictionary<BoundCallSiteKey, BoundCallSite> CallSites,
        IDictionary<BoundInitializerKey, BoundInitializer> Initializers,
        IDictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>>
            ClassInitializers,
        IDictionary<
            BoundDeconstructionKey, IMethodSymbol> Deconstructions,
        IDictionary<
            BoundConversionKey, ClosedConversionPlan> Conversions,
        BoundConstantTable Constants,
        BoundValueTable Values,
        IReadOnlyDictionary<IFieldSymbol, string> SourceStorageNames)
        BindSourceSemantics(
            FieldDiscoveryPlan fields,
            ReachabilityPlan reach,
            IReadOnlyList<IOperation> fieldInitializers,
            BoundMethodBodyTable methodBodies,
            BoundAbiPlanBuilder abiBuilder)
    {
        var sites = new Dictionary<BoundCallSiteKey, BoundCallSite>();
        var initializers =
            new Dictionary<BoundInitializerKey, BoundInitializer>();
        var classInitializers = new Dictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>>(
            SymbolEqualityComparer.Default);
        var deconstructions =
            new Dictionary<BoundDeconstructionKey, IMethodSymbol>();
        var conversions =
            new Dictionary<BoundConversionKey, ClosedConversionPlan>();
        var values = new List<(
            IOperation Operation,
            CallSiteBindingScope Scope,
            ValueInfo Value)>();
        var methodPayloads =
            new Dictionary<CallSiteBindingScope, bool>();
        var abiPlanner = new AbiDemandPlanner(
            _lowering, abiBuilder);
        var conversionPlanner =
            new ConversionSemanticPlanner(_lowering);
        var constantFields =
            new HashSet<IFieldSymbol>(
                SymbolEqualityComparer.Default);
        var sourceStorageNames =
            new Dictionary<IFieldSymbol, string>(
                SymbolEqualityComparer.Default);
        var typePlanner =
            new TypeDemandPlanner(
                _materializingTypes,
                _compilation,
                _state.Aggregates);

        foreach (var aggregateDefault in fields.AggregateDefaults)
            typePlanner.Plan(aggregateDefault.AggregateType, null);

        void BindTree(
            IOperation operation,
            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
            CallSiteBindingScope scope,
            BoundMethodBody body,
            bool root)
        {
            if (operation == null) return;
            if (!root && operation is ILocalFunctionOperation or IAnonymousFunctionOperation)
                return;
            typePlanner.Plan(operation, typeMap);
            SyntheticDemandPlanner.PlanOperation(
                operation, _lowering, scope);
            values.Add((
                operation,
                scope,
                ValueClassifier.ClassifyStable(
                    operation,
                    new TypeClassifierContext(typeMap),
                    _state.Captures,
                    body?.StableLocalInitializers)));
            ClosedConversionPlan? closedConversion = null;
            if (operation is IFieldReferenceOperation fieldReference)
            {
                constantFields.Add(fieldReference.Field);
                var storageName = SourceStorageName(
                    fieldReference.Field);
                if (sourceStorageNames.TryGetValue(
                        fieldReference.Field, out var existingName))
                {
                    if (!string.Equals(
                            storageName, existingName,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Field '{fieldReference.Field}' was bound "
                            + "to two source storage names.");
                }
                else
                {
                    sourceStorageNames.Add(
                        fieldReference.Field, storageName);
                }
            }
            if (operation is IConversionOperation conversion)
            {
                var conversionKey = new BoundConversionKey(
                    conversion, scope);
                closedConversion =
                    conversionPlanner.Plan(conversion);
                if (!conversions.TryAdd(
                        conversionKey,
                        closedConversion.Value))
                    throw new InvalidOperationException(
                        $"Conversion '{operation.Syntax}' was bound twice.");
            }
            abiPlanner.Plan(
                operation, scope, closedConversion);
            if (operation is IDeconstructionAssignmentOperation
                && operation.Syntax is AssignmentExpressionSyntax assignment)
            {
                var method = _compilation.GetSemanticModel(assignment.SyntaxTree)
                    .GetDeconstructionInfo(assignment).Method;
                if (method != null)
                    method = TypeEnvironment.CloseMethod(
                        _compilation, method, typeMap);
                var deconstructionKey = new BoundDeconstructionKey(
                    operation.Syntax, scope);
                if (!deconstructions.TryAdd(
                        deconstructionKey, method))
                    throw new InvalidOperationException(
                        $"Deconstruction '{operation.Syntax}' was bound twice.");
            }
            foreach (var rawSite in CallableSites.FromOperation(operation))
            {
                var target = TypeEnvironment.CloseMethod(
                    _compilation, rawSite.Target, typeMap);
                var site = new CallableSite(
                    rawSite.Kind, target, rawSite.Operation, rawSite.Receiver);
                var resolved = EdgeResolver.ResolveCallableSite(site);
                DispatchPlan? dispatch = null;
                INamedTypeSymbol receiver = null;
                var usesRuntimeDispatch = false;
                if (!target.IsStatic)
                {
                    receiver = TypeEnvironment.CloseType(
                            _compilation, rawSite.Receiver?.Type, typeMap)
                        as INamedTypeSymbol ?? target.ContainingType;
                    dispatch = _virtualDispatch.Resolve(
                        site, receiver, _classSymbol);
                    usesRuntimeDispatch =
                        VirtualDispatch.IsDispatchSite(
                            target, rawSite.Receiver, receiver)
                        || rawSite.Kind is
                            CallableSiteKind.PropertyGet
                            or CallableSiteKind.PropertySet
                           && receiver.TypeKind == TypeKind.Interface
                           && _planner.InterfaceIsLocalUserClassOnly(
                               receiver);
                }
                var key = new BoundCallSiteKey(
                    operation.Syntax, rawSite.Kind, scope);
                var componentQueryDisposition =
                    rawSite.Kind == CallableSiteKind.Method
                    && InvocationIntrinsicEmitter
                        .GenericComponentQueryKey
                        .Matches(target)
                        ? BindGenericComponentQueryDisposition(
                            target, typeMap, abiBuilder)
                        : (GenericComponentQueryDisposition?)null;
                if (!sites.TryAdd(
                        key,
                        new BoundCallSite(
                            resolved,
                            dispatch,
                            receiver,
                            usesRuntimeDispatch,
                            componentQueryDisposition)))
                    throw new InvalidOperationException(
                        $"Callable site '{operation.Syntax}' was bound twice in one specialization.");
            }
            foreach (var child in operation.ChildOps())
                BindTree(child, typeMap, scope, body, false);
        }

        GenericComponentQueryDisposition
            BindGenericComponentQueryDisposition(
                IMethodSymbol target,
                IReadOnlyDictionary<
                    ITypeParameterSymbol, ITypeSymbol>
                    typeMap,
                BoundAbiPlanBuilder abi)
        {
            var typeArg = target.TypeArguments[0];
            var resolvedTypeArg = _state.Types.Resolve(
                typeArg, typeMap);
            if (_state.Types.Describe(
                    typeArg,
                    typeMap).Representation
                == UdonRepresentationKind
                    .ObjectArrayBehaviourAlias)
                throw new NotSupportedException(
                    $"GetComponent<{resolvedTypeArg.ToDisplayString()}> is invalid: this type is used "
                    + "as a legacy object[] nominal alias in the same compilation and therefore has "
                    + "SystemObjectArray storage, not a scene-component representation.");

            if (ExternResolver.IsUdonSharpBehaviour(
                    resolvedTypeArg))
                return GenericComponentQueryDisposition
                    .BehaviourShim;

            var tokenUdonType =
                _lowering.TypeTokenName(
                    typeArg, typeMap);
            var parameterTypes = target.OriginalDefinition
                .Parameters
                .Select(parameter =>
                    _state.Types.GetStorageType(
                        parameter.Type, typeMap).Name)
                .ToArray();
            var key = UdonAbiKey.Method(
                tokenUdonType,
                target.Name,
                parameterTypes,
                target.Name.StartsWith("GetComponents")
                    ? "TArray"
                    : "T");

            // UdonAbiKey normalizes extern ownership. A token is a legal
            // generic dispatch key only when that remap preserves its
            // runtime identity and the same frozen catalog contains the
            // token-owned getter module.
            return key.Owner == tokenUdonType
                   && abi.ContainsExact(key)
                ? GenericComponentQueryDisposition
                    .TypedGenericExtern
                : GenericComponentQueryDisposition
                    .ErasedTypeQuery;
        }

        void BindRoot(
            IOperation operation,
            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
            CallSiteBindingScope scope,
            BoundMethodBody body,
            MethodContext.RegisteredCallableBody callableBody = null)
        {
            var receiverId = callableBody?.Callable.Receiver
                             == MethodContext.ReceiverAbi.ObjectArray
                ? callableBody.Callable.ReceiverFieldId
                : null;
            using var callableScope =
                _state.Methods.EnterCallableScope(
                    callableBody?.Method,
                    callableBody?.Closure,
                    receiverId,
                    callableBody?.OwnerSpecs
                    ?? System.Collections.Immutable
                        .ImmutableArray<IMethodSymbol>.Empty);
            using var genericScope =
                _state.EnterTypeParamOverlay(typeMap);
            BindTree(operation, typeMap, scope, body, true);
        }

        foreach (var body in _state.Methods.RegisteredBodies)
        {
            var scope = body.BindingScope;
            typePlanner.Plan(body.Method, body.TypeParameterMap);
            var boundBody = body.BoundBody;
            var mentionsPayload =
                LambdaCaptureAnalyzer.ReceiverCaptureKey(body.Method) != null
                || boundBody.ReferencedTypes.Any(type =>
                    type != null
                    && SourceShape(type, body.TypeParameterMap)
                        .ContainsUserClassPayload);
            if (!methodPayloads.TryAdd(scope, mentionsPayload))
                throw new InvalidOperationException(
                    $"Method payload semantics '{body.Method}' "
                    + "were materialized twice.");
            BindRoot(
                boundBody.AnalysisRoot,
                body.TypeParameterMap,
                scope,
                boundBody,
                body);
        }

        (CallSiteBindingScope Scope,
            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeMap)
            InitializerEnvironment(
                IOperation initializer,
                INamedTypeSymbol executionType)
        {
            var lexical = _compilation.GetSemanticModel(
                    initializer.Syntax.SyntaxTree)
                .GetEnclosingSymbol(initializer.Syntax.SpanStart);
            var lexicalType = lexical as INamedTypeSymbol
                              ?? lexical?.ContainingType
                              ?? throw new InvalidOperationException(
                                  $"Initializer '{initializer.Syntax}' "
                                  + "has no lexical containing type.");
            var closedOwner = lexicalType;
            for (var current = executionType;
                 current != null;
                 current = current.BaseType)
                if (SymbolEqualityComparer.Default.Equals(
                        current.OriginalDefinition,
                        lexicalType.OriginalDefinition))
                {
                    closedOwner = current;
                    break;
                }
            var typeMap = TypeEnvironment.ForContainingType(
                closedOwner, null);
            return (
                CallSiteBindingScope.ForType(closedOwner),
                typeMap);
        }

        BoundInitializer BindInitializer(
            IOperation initializer,
            INamedTypeSymbol executionType,
            INamedTypeSymbol mintedKey)
        {
            var environment = InitializerEnvironment(
                initializer, executionType);
            var key = new BoundInitializerKey(
                initializer.Syntax, mintedKey);
            var binding = new BoundInitializer(
                environment.Scope,
                environment.TypeMap);
            if (!initializers.TryAdd(key, binding))
                throw new InvalidOperationException(
                    $"Initializer '{initializer.Syntax}' was bound twice "
                    + $"for '{mintedKey?.ToDisplayString() ?? "program fields"}'.");
            methodPayloads.TryAdd(environment.Scope, false);
            BindRoot(
                initializer,
                environment.TypeMap,
                environment.Scope,
                null);
            return binding;
        }

        foreach (var initializer in fieldInitializers)
            BindInitializer(
                initializer, _classSymbol, null);

        // User-class instance initializers execute when a class bundle is constructed locally, even though they
        // are not fields of the compiled behaviour. They are therefore body-emission inputs and
        // must be bound under the constructed class's closed containing-type map as well.
        foreach (var constructedClass in reach.ConstructedClasses)
        {
            for (var owner = constructedClass;
                 owner != null && IsUserClass(owner);
                 owner = owner.BaseType)
            {
                if (classInitializers.ContainsKey(owner))
                    continue;
                var layout = _state.Aggregates.GetLayout(owner);
                var planned = new List<BoundClassFieldInitializer>();
                foreach (var pair in EnumerateClassFieldInitializers(owner))
                {
                    if (!layout.TryGetIndex(pair.Field, out var slot))
                        throw new InvalidOperationException(
                            $"Initializer field '{pair.Field}' has no "
                            + $"aggregate slot in '{owner}'.");
                    var binding = BindInitializer(
                        pair.Operation, owner, owner);
                    planned.Add(new BoundClassFieldInitializer(
                        pair.Operation, slot, binding));
                }
                classInitializers.Add(
                    owner, Array.AsReadOnly(planned.ToArray()));
            }
        }

        return (
            sites,
            initializers,
            classInitializers,
            deconstructions,
            conversions,
            BoundConstantTable.Materialize(
                _compilation, constantFields),
            new BoundValueTable(values, methodPayloads),
            sourceStorageNames);
    }

    void RegisterProgram(
        CallableDefinitionPlan callables,
        ReachabilityPlan reach,
        IReadOnlyList<IOperation> fieldInitializers)
    {
        var methods = callables.ProgramMethods;
        var typeLayout = _planner.GetLayout(_classSymbol);
        var crossDispatchExports = CollectCrossDispatchExports(
            reach, fieldInitializers);

        // First pass: create IrFunctions, assign params, return vars (skip generic definitions)
        _state.Methods.NextMethodIndex = 0;
        foreach (var method in methods)
        {
            EmitPolicy.RejectNetworkCallableDelegates(
                method, _state.Types); // M4 [T1], declaration-side
            EmitPolicy.RejectPublicProgramLocalDelegateSignature(
                method, _state.Types);
            if (method.IsGenericMethod) continue;

            var methodLayout = method.ContainingType.TypeKind == TypeKind.Interface
                ? _planner.GetLayout(method.ContainingType)
                : typeLayout;
            var ml = methodLayout.Methods[method];
            var exportName = ml.ExportName;

            // Determine if this method should be exported
            bool isOwnOrInherited = SymbolEqualityComparer.Default.Equals(method.ContainingType, _classSymbol)
                || _inheritedMethods.Contains(method);

            bool shouldExport = !method.IsGenericMethod
                && isOwnOrInherited
                && (method.MethodKind == MethodKind.Ordinary
                    || method.MethodKind == MethodKind.PropertyGet
                    || method.MethodKind == MethodKind.PropertySet
                    || method.MethodKind == MethodKind.EventAdd
                    || method.MethodKind == MethodKind.EventRemove)
                && (method.DeclaredAccessibility == Accessibility.Public
                    || UdonEventNames.ContainsKey(method.Name)
                    || crossDispatchExports.Any(target => SameVirtualSlot(method, target)));

            var isDefaultMethod = _userClassDefaultMethods.Contains(method);
            var parameters = method.Parameters.Select((parameter, index) =>
                new CallableParameterPlan(_ => ml.ParamIds[index], GetStorageType(parameter.Type))).ToArray();
            var returns = ml.Returns.Select(result =>
                new CallableReturnPlan(_ => result.Id, result.StorageType)).ToArray();
            new CallableRegistrar(_state).Register(new CallableLayoutPlan(
                method, _ => exportName,
                exportName: shouldExport ? exportName : null,
                slotPrefix: _ => exportName,
                receiver: isDefaultMethod
                    ? MethodContext.ReceiverAbi.ObjectArray : MethodContext.ReceiverAbi.None,
                receiverId: isDefaultMethod
                    ? _ => "__dimrcv_" + SanitizeId(ClassTypeObjectContext.SpecKey(method.ContainingType))
                        + "_" + SanitizeId(method.MetadataName)
                    : null,
                parameters: parameters,
                returns: returns,
                layout: ml));
        }

        // ReachableBodies (design §1): ONE reach fixpoint replaces the three separate Phase-1 collector
        // fixpoints + their duplicated body fetches. The registration regimes below are projections of it:
        // foreign statics / collectible struct members / base-instance copies. Registration ORDER
        // (foreign → struct → base) is unchanged; the ungated struct-member DEFINITION projection
        // (reach.StructMemberDefs) feeds BuildRecursionInfo. The former [X5] base-first / [Y6]
        // open-generic-base seeding is subsumed: the single fixpoint walks every reachable body (own,
        // base, struct, foreign, field-init) once and applies all three per-operation rules to each, so a
        // struct/foreign/using call inside any reached body is seen. Gates (IsCollectibleStructMember /
        // IsClosedForeignStaticTarget / methodSet exclusion) stay on the projection side — meaning preserved.
        var baseInstanceMethods = callables.BaseInstanceMethods;
        var structMethods = callables.StructMethods;
        // C4 retirement (the C2-incidental duplicate): a static LOCAL FUNCTION declared inside a foreign
        // static classifies as a foreign static itself (IsForeignStatic has no MethodKind filter — its
        // reach leg seeding BodyByDef is the C2-proven recursion-node arm and stays), but local functions
        // register on demand at their declaration statement (or the [Y9] forward-reference arm), which
        // overwrote this eager Phase-1 callable and left it emitted-but-unreachable (a
        // dead __N_ duplicate body + heap vars, probe-proven __2_Twice/__3_Twice). Gate the REGISTRATION
        // projection only.
        var foreignStatics = callables.ForeignStatics;
        foreach (var fm in foreignStatics)
        {

            // B70 root 1 (A14/A15): a static method on a CLOSED generic struct (GS14<bool>.Run) is registered
            // here, but this loop — unlike the struct-instance and base-instance loops — never seeded
            // FirstGenericSpec. A nested LF then could not reach the enclosing struct's closed T (the
            // closureBindings walk at EmitMethod misses the owner), so `new T[]` emitted a bogus TArray. Seed
            // it the same way the struct-methods loop does (including the two-instantiation aliasing guard,
            // which GS15<int>/GS15<string> exercises).
            RegisterInternalCallable(fm, idx => NameAllocator.FormatId(SanitizeId(fm.Name), idx));
        }

        // Register user-struct constructors + instance methods (object[]-emulated; synthetic receiver = param0).
        // structMethods was collected above (before the foreign-static scan, which it also seeds).
        foreach (var sm in structMethods)
        {

            // Feature G: a member of a CONSTRUCTED generic struct (Box<int>.Get(), Box<int>(x), a
            // generic struct's operator, etc.) gets its own per-spec body — the containing-type
            // dimension's version of RegisterGenericSpecialization's discipline (constructed key,
            // First-wins spec seed (ComposeClosureKeyArgs owner fallback; pin gates retired), type-arg-suffixed
            // name: containing type's args, then the method's own if it is ALSO generic). A
            // non-generic-struct member (sm.ContainingType.IsGenericType false, so sm ==
            // sm.OriginalDefinition trivially) takes the unchanged path below byte-identically.
            string typeArgSuffix = "";
            if (sm.ContainingType.IsGenericType)
            {
                var containingArgPart = string.Join("_",
                    sm.ContainingType.TypeArguments.Select(
                        type => _materializingTypes.GetUdonTypeName(type)));
                var methodArgPart = sm.IsGenericMethod
                    ? "_" + string.Join("_", sm.TypeArguments.Select(
                        type => _materializingTypes.GetUdonTypeName(type)))
                    : "";
                typeArgSuffix = $"_{containingArgPart}{methodArgPart}";
            }

            var isCtor = sm.MethodKind == MethodKind.Constructor;
            RegisterInternalCallable(sm, idx => NameAllocator.FormatId(isCtor
                    ? SanitizeId(sm.ContainingType.Name) + "__ctor" + typeArgSuffix
                    : SanitizeId(sm.Name) + typeArgSuffix, idx),
                sm.IsStatic ? MethodContext.ReceiverAbi.None : MethodContext.ReceiverAbi.ObjectArray);
        }

        // Register base class instance copies (collected above, before the [X5] collector seeds).
        foreach (var bm in baseInstanceMethods)
        {
            // Wave-9 round-8 [Y10]: an INHERITED generic method's call-site-constructed copy is the
            // de-facto specialization this path emits (EmitMethod sets the type-param map from it),
            // but it bypassed RegisterGenericSpecialization — so FirstGenericSpec never learned it
            // and a hoisted closure inside the base generic body could not resolve the enclosing
            // method's params (loud "Cannot resolve parameter") or its type-param map. Seed it here,
            // with the same second-distinct-instantiation guard ([X6] r5, widened in round 8).
            RegisterInternalCallable(bm, idx => NameAllocator.FormatId(SanitizeId(bm.Name), idx));
        }

    }

    MethodContext.RegisteredCallable RegisterInternalCallable(IMethodSymbol method,
        Func<int, string> functionName, MethodContext.ReceiverAbi receiver = MethodContext.ReceiverAbi.None)
    {
        var parameters = method.Parameters.Select(parameter => new CallableParameterPlan(
            index => NameAllocator.ParamId(parameter.Name, index), GetStorageType(parameter.Type))).ToArray();
        var returns = method.ReturnsVoid ? Array.Empty<CallableReturnPlan>() : new[]
        {
            new CallableReturnPlan(index => NameAllocator.RetId(SanitizeId(method.Name), index),
                new StorageType(GetStorageTypeName(method.ReturnType)))
        };
        return new CallableRegistrar(_state).Register(new CallableLayoutPlan(
            method, functionName,
            receiver: receiver,
            receiverId: receiver == MethodContext.ReceiverAbi.ObjectArray
                ? index => NameAllocator.ParamId("this", index) : null,
            parameters: parameters,
            returns: returns));
    }

    static HashSet<IMethodSymbol> CollectCrossDispatchExports(
        ReachabilityPlan reach,
        IReadOnlyList<IOperation> fieldInitializers)
    {
        var exports = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var root in reach.BodyByDef.Values
                     .Select(body => body?.AnalysisRoot)
                     .Where(body => body != null)
                     .Concat(fieldInitializers))
            foreach (var operation in root.DescendantsAndSelf())
            {
                foreach (var site in CallableSites.FromOperation(operation))
                    if (site.Receiver != null && site.Receiver is not IInstanceReferenceOperation
                        && (site.Kind is not CallableSiteKind.EventAdd and not CallableSiteKind.EventRemove
                            || !site.Target.IsImplicitlyDeclared))
                        exports.Add(site.Target.OriginalDefinition);
            }
        return exports;
    }

    static bool SameVirtualSlot(IMethodSymbol left, IMethodSymbol right)
    {
        for (var method = left; method != null; method = method.OverriddenMethod)
            if (SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, right.OriginalDefinition))
                return true;
        for (var method = right; method != null; method = method.OverriddenMethod)
            if (SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, left.OriginalDefinition))
                return true;
        return false;
    }

    void EmitRegisteredBodies(BoundProgram plan)
    {
        var methods = plan.Callables.ProgramMethods;
        foreach (var body in plan.CallableBodies)
            if (!body.Callable.IsDeferredBody)
                EmitMethod(body);

        // A behaviour can receive an exported event before its Start event.  Build one construction
        // barrier and later prepend it to every export; tying these operations to _start leaves a
        // receiver observably half-constructed during cross-behaviour startup calls.
        bool hasProgramInitializers = _fieldInitOps.Count > 0 || _fieldChangeCallbacks.Count > 0
            || _state.Program.Fields.AggregateDefaults.Count > 0;
        if (hasProgramInitializers)
        {
            var initialization = new ProgramInitializationEmitter(_state);
            initialization.Emit(EmitFieldInitializers);

            // Keep the ordinary Udon lifecycle hook so construction still happens eagerly in the
            // common case.  Its body is empty because the export guard performs the actual work.
            if (!methods.Any(m => UdonEventNames.TryGetValue(m.Name, out var en) && en == "_start"))
            {
                var startFunc = _module.AddFunction("_start", "_start");
                _builder.SetFunction(startFunc);
                _builder.EmitReturn();
            }
        }

        // Emit interface bridge exports
        new InterfaceBridgeEmitter(_state, _bridge).Emit();

        // Emit delegate bridge exports
        new DelegateBridgeEmitter(
            _state, _bridge, _delegateConvention, plan.SyntheticDemands).EmitLayoutBridges();

        foreach (var body in plan.CallableBodies)
            if (body.Callable.IsDeferredBody)
                EmitMethod(body);

        _state.VerifySyntheticEmissionComplete();

        // Emit pending delegate bridges for hoisted lambdas/local functions
        new DelegateBridgeEmitter(
            _state, _bridge, _delegateConvention, plan.SyntheticDemands).EmitPending();
        new ReceiverBridgeEmitter(
            _state, _bridge, _delegateConvention, plan.SyntheticDemands).EmitPending();

        // Variance design (2026-07-04 §2.2/§2.3) T-M2: sig adapters (B-1) + wrapper-with-payload
        // bridges (B-2), for every variant method-group binding / third-party-variant hinge / variant
        // delegate-value conversion registered in this class. A class with no variance emits neither —
        // single-cast golden untouched (§5 gate).
        new WrapperBridgeEmitter(
            _state, _bridge, _delegateConvention, plan.SyntheticDemands).EmitPending();

        // Multicast design (2026-07-03 §1) A-M1: per-sig synthetic combine/remove helpers + fan-out
        // bridge, for every sig a `+=`/`-=` site registered in this class (RegisterMulticastSig). A
        // class with no delegate compound assignment emits none of this — single-cast golden is
        // untouched (§6 gate). Reentrancy graph-node registration for the fan-out is A-M3 scope (§1.6),
        // deliberately not wired here.
        new MulticastDelegateEmitter(
            _state, _bridge, _delegateConvention, plan.SyntheticDemands).EmitPending();
        new EnumToStringSyntheticEmitter(
            _state, _bridge, plan.SyntheticDemands).Emit();

        if (hasProgramInitializers)
            new ProgramInitializationEmitter(_state).GuardEveryExport();

        // §5.5 (graft #2): now that every capturing bridge is registered, assert each has a graph node.
        RecursionAnalysis.VerifyBridgeTargetsAreNodes();
    }

    static string SanitizeId(string name) => NameAllocator.Sanitize(name);

    static bool IsHoistedClosureMethod(IMethodSymbol method)
        => method.MethodKind is MethodKind.LocalFunction
            or MethodKind.LambdaMethod or MethodKind.AnonymousFunction;

    // ── EmitMethod ──

    void EmitMethod(MethodContext.RegisteredCallableBody body)
    {
        var callable = body.Callable;
        var method = body.Method;
        var closureSpec = body.Closure;
        var func = _state.Methods.RequireFunction(callable);

        // Receiver ABI, owner specialization and binding identity were frozen with the callable.
        var receiverParamId = callable.ReceiverFieldId;

        using var _methodScope = _state.Methods.EnterCallableScope(
            method, closureSpec, receiverParamId, body.OwnerSpecs);
        using var _bindingScope = _state.EnterBindingScope(body.BindingScope);

        // FieldChangeCallback: check if this setter has an associated callback field
        string fcbFieldName = null;
        string fcbFieldType = null;
        if (method.MethodKind == MethodKind.PropertySet
            && method.AssociatedSymbol is IPropertySymbol setterProp)
        {
            foreach (var kvp in _fieldChangeCallbacks)
                if (kvp.Value == setterProp.Name)
                {
                    fcbFieldName = kvp.Key;
                    fcbFieldType = GetStorageTypeName(setterProp.Type);
                    break;
                }
        }

        // FCB: Create separate _onVarChange_ function
        if (fcbFieldName != null)
        {
            var varChangeName = $"_onVarChange_{fcbFieldName}";
            var varChangeFunc = _module.AddFunction(varChangeName, varChangeName);
            _builder.SetFunction(varChangeFunc);

            // Preamble: read new value from field, restore old value to field
            var newVal = _bridge.Load(fcbFieldName, new StorageType(fcbFieldType));
            var oldVal = _bridge.Load($"__old_{fcbFieldName}", new StorageType(fcbFieldType));
            _bridge.Store(fcbFieldName, oldVal);

            // Call setter with new value
            _bridge.CallInternal(func, new CLeaf[] { newVal });
            _builder.EmitReturn();
        }

        // Switch to the method's function for body emission
        _builder.SetFunction(func);

        // Registration owns the complete specialization environment. Emission only installs it.
        var typeMap = body.TypeParameterMap;

        // Get method body IOperation
        var boundBody = body.BoundBody;
        {
            var syntax = boundBody.Declaration;
            var bodyOp = boundBody.Root;

            // The former [Y8]/B51/B70 rekey block (re-binding the map under each walk's fresh
            // type-parameter symbols) is retired: TypeParamScope composes its maps with
            // TypeParamIdComparer, so per-walk twins of one declared parameter hit one key directly
            // (design 2026-07-10 symbol-intern v2, T1 — red-proofed: disabling the comparer-era
            // compensation reproduced the 15 rekey-class failures before this landed).

            // Open the depth-1 scope now that the map is fully composed; Dispose (at block end) is the
            // sole clear, running even if body emission throws. Non-generic methods carry a null map
            // and open no scope. A closure/spec emitted later recomposes its own map at its own entry.
            using var _typeScope = typeMap != null ? _state.EnterTypeParamScope(typeMap) : null;

            // Emit tail-call optimization label at function entry (jump target for TCO goto)
            _builder.EmitLabel($"__tco_{func.Name}");

            // Stage 2 M2 (design §3.0 INV-2): the MethodEntry-scope EnvAlloc lowers AFTER the __tco_
            // label so a self-tail loopback re-runs it every logical activation (per-activation env
            // freshness). A closure reaches its own body scope via ClosureScopes (its MethodEntry
            // Node is the lambda/LF body, not this bodyOp); a root method via ScopeFor(bodyOp).
            // No-ops on a null / non-capture-bearing scope, so the call is unconditional.
            CaptureScope entryScope = null;
            if (_state.Captures != null)
            {
                if (IsHoistedClosureMethod(method))
                    _state.Captures.ClosureScopes.TryGetValue(method.OriginalDefinition, out entryScope);
                else
                    entryScope = _state.Captures.ScopeFor(bodyOp, CaptureScopeKind.MethodEntry);
            }
            EnvEmit.Alloc(_builder, _state, entryScope);

            // Consume every captured PARAMETER of this method out of its flat param field into its env
            // cell (the arg arrived positionally in the flat field; all body reads route through env).
            var entryParamIds = callable.ParamVarIds;
            if (_state.Captures != null)
                foreach (var p in method.Parameters)
                    if (p.Ordinal < entryParamIds.Length && _state.TryGetEnvBinding(p, out _))
                        EnvEmit.Write(_builder, _state, p,
                            _bridge.Load(entryParamIds[p.Ordinal], GetStorageType(p.Type)));

            // Class receiver capture (design 2026-07-10 v2 §1.3): consume the receiver param0 into its
            // env cell exactly like a captured parameter — after __tco_ + EnvAlloc, so a self-tail
            // loopback re-seeds each logical activation's fresh env. Null CurrentStructReceiverParamId
            // (behaviour methods, hoisted closures) and an uncaptured receiver both skip.
            if (_state.Captures != null
                && _state.Methods.CurrentStructReceiverParamId is { } rcvParamId
                && LambdaCaptureAnalyzer.ReceiverCaptureKey(method) is { } rcvKey
                && _state.TryGetEnvBinding(rcvKey, out _))
                EnvEmit.Write(_builder, _state, rcvKey, _bridge.Load(rcvParamId, new StorageType(AggregateAbi.ArrayType)));

            if (boundBody.Disposition
                == CallableBodyDisposition
                    .SynthesizedAutoAccessor)
            {
                if (syntax
                        is not AccessorDeclarationSyntax
                        || method.AssociatedSymbol
                            is not IPropertySymbol autoProp)
                    throw new InvalidOperationException(
                        $"Synthesized accessor "
                        + $"'{method.ToDisplayString()}' has no "
                        + "auto-property declaration.");

                // Per-declaration backing: the chain leaf owns the bare
                // name; an overridden base declaration owns its own slot.
                var backingVar =
                    AutoPropBackingVar(autoProp);
                var propType =
                    GetStorageTypeName(autoProp.Type);
                if (method.MethodKind
                        == MethodKind.PropertyGet
                    && callable.ReturnSlots.Length == 1)
                    _bridge.Store(
                        callable.ReturnSlots[0].Id,
                        _bridge.Load(
                            backingVar,
                            new StorageType(propType)));
                else if (method.MethodKind
                             == MethodKind.PropertySet
                         && callable.ParamVarIds.Length > 0)
                    _bridge.Store(
                        backingVar,
                        _bridge.Load(
                            callable.ParamVarIds[0],
                            new StorageType(propType)));
                else
                    throw new InvalidOperationException(
                        $"Synthesized accessor "
                        + $"'{method.ToDisplayString()}' has an "
                        + "invalid callable ABI.");
            }
            else if (boundBody.Disposition
                     != CallableBodyDisposition.SourceBody)
                throw boundBody.UnsupportedCallableException(
                    "Body emission");
            else if (bodyOp is IMethodBodyOperation methodBody)
            {
                if (methodBody.BlockBody != null)
                    _operations.VisitOperation(methodBody.BlockBody);
                else if (methodBody.ExpressionBody != null)
                    _operations.VisitOperation(methodBody.ExpressionBody);
                else
                    throw new InvalidOperationException(
                        $"Source method "
                        + $"'{method.ToDisplayString()}' "
                        + "materialized without a body.");
            }
            else if (bodyOp is ILocalFunctionOperation localFuncOp)
            {
                if (localFuncOp.Body != null)
                    _operations.VisitOperation(localFuncOp.Body);
                else
                    throw new InvalidOperationException(
                        $"Local function "
                        + $"'{method.ToDisplayString()}' "
                        + "materialized without a body.");
            }
            else if (bodyOp is IConstructorBodyOperation ctorBodyOp)
            {
                // CA-v2 M1: a v1 CLASS ctor orchestrates its own chain (charter #6, field inits + base
                // call, in InvocationHandler which owns EmitCallToMethod/ResolveStructMember). A STRUCT
                // ctor has no base — just its body.
                if (method.ContainingType is INamedTypeSymbol cctClass
                    && IsUserClass(cctClass)
                    && _state.Methods.CurrentStructReceiverParamId != null)
                    new InvocationHandler(_lowering).EmitClassCtorPrologue(method, ctorBodyOp,
                        _state.Methods.CurrentStructReceiverParamId);
                if (ctorBodyOp.BlockBody != null)
                    _operations.VisitOperation(ctorBodyOp.BlockBody);
                else if (ctorBodyOp.ExpressionBody != null)
                    _operations.VisitOperation(
                        ctorBodyOp.ExpressionBody);
                else
                    throw new InvalidOperationException(
                        $"Constructor "
                        + $"'{method.ToDisplayString()}' "
                        + "materialized without a body.");
            }
            else if (bodyOp is IAnonymousFunctionOperation anonFunc)
            {
                if (anonFunc.Body is IBlockOperation anonBlock)
                    _operations.VisitOperation(anonBlock);
                else if (anonFunc.Body != null)
                {
                    var lambdaRets = callable.ReturnSlots;
                    if (lambdaRets.Length == 1)
                    {
                        var resultVal = _operations.VisitExpression(anonFunc.Body);
                        _bridge.Store(lambdaRets[0].Id, resultVal);
                    }
                    else
                        _operations.VisitOperation(
                            anonFunc.Body);
                }
                else
                    throw new InvalidOperationException(
                        "Anonymous function materialized "
                        + "without a body.");
            }
            else if (bodyOp is IBlockOperation block)
                _operations.VisitOperation(block);
            // Expression-bodied property: int X => expr;
            else if (syntax is PropertyDeclarationSyntax propDecl
                     && propDecl.ExpressionBody != null)
            {
                var exprOp = boundBody.ExpressionBody;
                if (exprOp != null && callable.ReturnSlots.Length == 1)
                {
                    var resultVal = _operations.VisitExpression(exprOp);
                    _bridge.Store(callable.ReturnSlots[0].Id, resultVal);
                }
                else
                    throw new InvalidOperationException(
                        $"Expression-bodied property "
                        + $"'{method.ToDisplayString()}' has an "
                        + "invalid body or callable ABI.");
            }
            // A source accessor normally materializes as
            // IMethodBodyOperation. Keep the direct-root form explicit,
            // but never accept an unknown operation shape as an empty
            // callable.
            else if (syntax
                     is AccessorDeclarationSyntax)
            {
                var accessorOp = boundBody.Root;
                if (accessorOp
                    is IMethodBodyOperation accessorBody)
                {
                    if (accessorBody.BlockBody != null)
                        _operations.VisitOperation(
                            accessorBody.BlockBody);
                    else if (accessorBody.ExpressionBody
                             != null)
                        _operations.VisitOperation(
                            accessorBody.ExpressionBody);
                    else
                        throw new InvalidOperationException(
                            $"Source accessor "
                            + $"'{method.ToDisplayString()}' "
                            + "materialized without a body.");
                }
                else if (accessorOp
                         is IBlockOperation accessorBlock)
                    _operations.VisitOperation(
                        accessorBlock);
                else
                    throw new InvalidOperationException(
                        $"Source accessor "
                        + $"'{method.ToDisplayString()}' has no "
                        + "lowerable operation body.");
            }
            else
                throw new InvalidOperationException(
                    $"Source callable "
                    + $"'{method.ToDisplayString()}' has unsupported "
                    + $"body root '{bodyOp?.Kind.ToString() ?? "null"}'.");
        }

        // FieldChangeCallback epilogue: update _old_ to current value
        if (fcbFieldName != null)
        {
            var curVal = _bridge.Load(fcbFieldName, new StorageType(fcbFieldType));
            _bridge.Store($"__old_{fcbFieldName}", curVal);
        }

        // Method epilogue: return
        _builder.EmitReturn();
    }

    // ── Field Initializers ──

    void DeclareTypeObjectConstants()
    {
        // Type identities are immutable compile-time strings, not construction work. Giving their
        // heap variables defaults makes class minting safe before Start without forcing every program
        // that merely uses a user class through the runtime initialization barrier.
        foreach (var type in _state.ClassTypes.RuntimeClasses)
            _state.Storage.DeclareGeneratedField(
                _state.ClassTypes.TryGetTypeObjVar(type),
                StorageTypes.String,
                ClassTypeObjectContext.RuntimeTypeId(type));
    }

    void EmitFieldInitializers()
    {
        // 2026-07-11 audit: field-initializer expressions belong to the CLASS context — never to
        // whatever spec/closure happened to emit last (the synthesized-_start path runs outside any
        // EmitMethod, so the ambient would otherwise be stale). A delegate-field initializer lambda
        // registers against this clean ambient.
        _state.Methods.CurrentClosureSpec = null;
        _state.Methods.CurrentOwnerSpecs = System.Collections.Immutable.ImmutableArray<IMethodSymbol>.Empty;
        // Default-init aggregate (struct/tuple) fields with no explicit initializer FIRST, so any explicit
        // initializer that references one sees a non-null backing array (C# default-then-initializer order).
        foreach (var (fieldId, aggType) in _state.Program.Fields.AggregateDefaults)
            _bridge.Store(fieldId, AggregateAbi.MintDefault(_builder, _state.Aggregates.GetLayout(aggType),
                _state.Aggregates.GetLayout, GetStorageTypeName));

        foreach (var (fieldId, initOp, fieldType) in _fieldInitOps)
        {
            try
            {
                var initializer =
                    _state.Program.RequireInitializer(initOp);
                using var bindingScope = _state.EnterBindingScope(
                    initializer.Scope);
                using var genericScope = initializer.TypeParameterMap != null
                    ? _state.EnterTypeParamOverlay(
                        initializer.TypeParameterMap)
                    : null;
                // Bare array initializer { 1, 2, 3 } → synthesize array creation + element Set
                if (initOp is IArrayInitializerOperation arrayInit)
                {
                    var arrTypeSym = (IArrayTypeSymbol)fieldType;
                    var arrayType = GetStorageTypeName(arrTypeSym);
                    var elementType = GetArrayElemType(arrTypeSym);
                    var sizeConst = _bridge.ConstInt(arrayInit.ElementValues.Length);
                    var arrVal = _bridge.CallExtern(new StorageType(arrayType),
                        UdonAbi.ArrayConstructor(arrayType),
                        new CLeaf[] { sizeConst });
                    _bridge.Store(fieldId, arrVal);
                    for (int i = 0; i < arrayInit.ElementValues.Length; i++)
                    {
                        var elemVal = _operations.VisitExpression(arrayInit.ElementValues[i]);
                        var idxConst = _bridge.ConstInt(i);
                        var arrLoad = _bridge.Load(fieldId, new StorageType(arrayType));
                        _bridge.CallExternVoid(
                            UdonAbi.ArraySet(arrayType, elementType),
                            new CLeaf[] { arrLoad, idxConst, elemVal });
                    }
                    continue;
                }

                var valueVal = _operations.VisitExpression(initOp);
                _bridge.Store(fieldId, valueVal);
            }
            catch (NotSupportedException ex)
            {
                throw new NotSupportedException(
                    $"Field '{fieldId}' initializer is not supported and would otherwise compile as default(T): {ex.Message}",
                    ex);
            }
        }

        // Initialize _old_ variables for FieldChangeCallback fields
        foreach (var kvp in _fieldChangeCallbacks)
        {
            var fcbType = _state.Storage.GetFieldType(kvp.Key);
            if (fcbType != null)
            {
                var fieldVal = _bridge.Load(kvp.Key, fcbType.Value);
                _bridge.Store($"__old_{kvp.Key}", fieldVal);
            }
        }
    }

    // ── Static collection helpers ──

    // CA call-graph rewrite (M4): the stable, traversal-independent ordinal key. GetDocumentationCommentId
    // is a unique deterministic per-symbol id ("M:Ns.Type.Method(args)" / "T:Ns.Type"); OriginalDefinition
    // normalizes generic specs to their definition (the graph is def-keyed). Ordinal string comparison keeps
    // it culture-independent.
    internal static string StableOrdinalKey(ISymbol s)
        => s.OriginalDefinition.GetDocumentationCommentId() ?? s.OriginalDefinition.ToDisplayString();

    /// <summary>B81: the instance field-/auto-property-INITIALIZER value operations of a v1 class, in
    /// declaration order — the reach-side twin of LoweringServices.EmitInstanceFieldInitializers (which emits
    /// them at mint). Static/const fields are excluded (const folds; statics reject). Used to Phase-1-walk
    /// a minted class's initializer expressions for foreign-static / struct-member collection.</summary>
    internal IEnumerable<IOperation> EnumerateClassFieldInitOps(INamedTypeSymbol classTy)
        => EnumerateClassFieldInitializers(classTy)
            .Select(initializer => initializer.Operation);

    IReadOnlyList<IOperation> CollectConstructedClassInitializerRoots(
        ReachabilityPlan reach)
    {
        var roots = new List<IOperation>();
        var syntax = new HashSet<SyntaxNode>();
        foreach (var constructedClass in reach.ConstructedClasses)
            for (var owner = constructedClass;
                 owner != null && IsUserClass(owner);
                 owner = owner.BaseType)
                foreach (var operation in
                         EnumerateClassFieldInitOps(owner))
                    if (syntax.Add(operation.Syntax))
                        roots.Add(operation);
        return Array.AsReadOnly(roots.ToArray());
    }

    IEnumerable<(IFieldSymbol Field, IOperation Operation)>
        EnumerateClassFieldInitializers(INamedTypeSymbol classTy)
    {
        foreach (var member in classTy.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
            ISymbol initHolder = f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol prop ? prop : f;
            var syntax = initHolder.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var initializer = syntax switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd
                    => vd.Initializer,
                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd
                    => pd.Initializer,
                _ => null,
            };
            if (initializer == null) continue;
            var model = _compilation.GetSemanticModel(
                initializer.SyntaxTree);
            var initOp =
                (model.GetOperation(initializer)
                    as ISymbolInitializerOperation)?.Value
                ?? model.GetOperation(initializer.Value);
            if (initOp != null) yield return (f, initOp);
        }
    }

    // B46 (wave-14 r4): a foreign-static call whose containing type still carries an OPEN type
    // parameter (Helper<U>.Boost seen in the SHARED body of a generic struct/method, U unbound) has
    // no single monomorphization here — collecting it would register a phantom open FlatFunction, exactly
    // the shape IsCollectibleStructMember skips. The binding phase materializes its closed call site.
    // Genuinely closed foreign statics (incl.
    // non-generic Helper.Boost, or Helper<int>.Boost from a concretely-typed context) are collected.
    internal static bool IsClosedForeignStaticTarget(IMethodSymbol m)
        => !(m.ContainingType is INamedTypeSymbol ct && ct.IsGenericType
             && ct.TypeArguments.Any(ta => ta is ITypeParameterSymbol));

    // A property is auto-implemented iff the compiler synthesized a backing field associated with it.
    // Computed (expression-bodied or block-bodied) properties have no such field and must be inlined.
    internal static bool IsComputedProperty(IPropertySymbol prop)
        => !prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));

    // Feature G: a generic struct's OWN method body — walked from its single shared/original syntax
    // regardless of which instantiation is collecting it — resolves a SELF-reference (recursion, or
    // one struct member calling a sibling) to the RAW OPEN containing type (Box<T> where T is the
    // struct's own type parameter), never to any concrete spec. Collecting this phantom open-form
    // entry registers a SECOND, dead (real call sites bind to the closed spec) FlatFunction
    // that corrupts the definition-keyed
    // recursion/spill bookkeeping (VM-proven: a self-recursive generic struct method returned 0
    // instead of the CLR's 6). Skip collecting through the open form; the real call sites (outer
    // construction/invocation, always concretely typed) already reach every instantiation this
    // collector needs; internal self/sibling references are closed during bound-program construction.
    //
    // Wave-14 widening: the original check (ContainingType.IsDefinition) only catches a struct
    // referencing ITSELF. A CROSS-type reference — APart<T>'s own body doing `new BPart<T>()` /
    // `b.Pong(...)`, where the T is APart's OWN (still open) type parameter used as BPart's type
    // ARGUMENT — produces a containing type (BPart<T_APart>) that is NOT its own IsDefinition (its
    // argument isn't BPart's own type parameter) but is STILL fundamentally open: T_APart is only
    // ever closed once APart's OWN spec is known, so this is exactly the same phantom-open-form shape,
    // just one level removed. Checking "any type argument is still an open type parameter" (rather
    // than IsDefinition specifically) subsumes both shapes identically — mutual/cross recursion
    // between two generic struct types corrupted the same definition-keyed bookkeeping the original
    // fix targeted (VM-proven: BoxMutualRecurse Ping(5)+Ping(3) returned 8 instead of the CLR's 21).
    // B70 root 2 (A11): a recursive generic LF / struct method's self-call `Lf<T>(n-1)` is an open generic-
    // METHOD form (its OWN type argument is still the open T), the method-dimension twin of the open-
    // containing-type shape above. Left collectible it registers a dead second FlatFunction whose body is
    // emitted mapless (no isSpec map) → `new T[]` → bogus `TArray` (the closed Lf<int> spec is registered
    // separately while the bound program is materialized). Reject it too.
    internal static bool IsCollectibleStructMember(IMethodSymbol m)
        => m != null
            && !(m.IsImplicitlyDeclared && m.MethodKind == MethodKind.Ordinary)
            && !(m.ContainingType.IsGenericType
                && m.ContainingType.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
            && !(m.IsGenericMethod
                && m.TypeArguments.Any(ta => ta is ITypeParameterSymbol));

    // C4 retirement dedup: the ONE IsBaseInstanceMethod (InvocationHandler carried an open-coded copy
    // WITHOUT the [Y10] guard) — static, parameterized by the compiled class, so the handler, the
    // resolver, and the emitter all read the same guarded predicate.
    internal static bool IsBaseInstanceMethod(IMethodSymbol method, INamedTypeSymbol classSymbol)
    {
        if (method.IsStatic) return false;
        // Round-9 [Y10]: a LOCAL FUNCTION declared inside a base method's body has
        // ContainingType = the base class but is NOT a base instance method — registering it as an
        // eager phase-1 copy declared its param/return heap vars BEFORE any type-param map exists,
        // so an enclosing-generic 'T' in its signature stayed raw and the body emission ICEd
        // ("expected 'T', got 'SystemInt32'") on a single legal instantiation. Local functions
        // register on demand during the enclosing body's emission (declaration statement or the
        // [Y9] forward-reference arm), where the instantiation map is active.
        if (method.MethodKind == MethodKind.LocalFunction) return false;
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, classSymbol)) return false;
        if (USugarCompilerHelper.IsFrameworkNamespace(method.ContainingType.ContainingNamespace)) return false;
        if (method.ContainingType.Name == "UdonSharpBehaviour") return false;
        var bt = classSymbol.BaseType;
        while (bt != null)
        {
            if (SymbolEqualityComparer.Default.Equals(bt, method.ContainingType)) return true;
            bt = bt.BaseType;
        }
        return false;
    }

    /// <summary>The single ReachableBodies fixpoint result (design §1), built once in Emit() before
    /// CaptureScopeAnalysis and consumed by the Phase-1 registration regimes, BuildRecursionInfo roots,
    /// and CaptureScope roots. Carries each reachable DEFINITION's body fetched EXACTLY ONCE.</summary>

    // C4 retirement dedup: the ONE IsForeignStatic (InvocationHandler carried a byte-identical
    // open-coded copy) — static, parameterized by the compiled class. Extension methods: ReducedFrom
    // holds the original static definition.
    internal static bool IsForeignStatic(IMethodSymbol method, INamedTypeSymbol classSymbol)
    {
        var resolved = method.ReducedFrom ?? method;
        if (!resolved.IsStatic) return false;
        if (resolved.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        // Static methods on a user UdonSharpBehaviour subclass are inlinable (no instance ⇒ no cross-program
        // SendCustomEvent path); the syntax-less base/SDK behaviours are already excluded above.
        if (SymbolEqualityComparer.Default.Equals(resolved.ContainingType, classSymbol)) return false;
        if (USugarCompilerHelper.IsExternNamespace(resolved.ContainingType.ContainingNamespace)) return false;
        return true;
    }

    // ── Constant evaluation helpers ──

    object TryEvaluateFieldInitForHeap(IOperation initOp, ITypeSymbol fieldType)
    {
        if (initOp is IArrayCreationOperation arrayCreation)
            return TryEvalArrayCreation(arrayCreation);

        if (initOp is IArrayInitializerOperation arrayInit && fieldType is IArrayTypeSymbol arrType)
            return TryEvalArrayInitializer(arrayInit, arrType);

        return null;
    }

    object TryEvalArrayCreation(IArrayCreationOperation op)
    {
        if (op.DimensionSizes.Length != 1) return null;

        var arrTypeSym = op.Type as IArrayTypeSymbol;
        if (arrTypeSym == null) return null;

        var elemClrType = ResolveClrTypeForConst(arrTypeSym.ElementType);
        if (elemClrType == null) return null;

        int size;
        if (op.DimensionSizes[0].ConstantValue.HasValue
            && op.DimensionSizes[0].ConstantValue.Value is int constSize)
        {
            size = constSize;
        }
        else if (op.Initializer != null)
        {
            size = op.Initializer.ElementValues.Length;
        }
        else
        {
            return null;
        }

        var array = Array.CreateInstance(elemClrType, size);

        if (op.Initializer != null)
        {
            if (!TryPopulateArray(array, op.Initializer, elemClrType))
                return null;
        }

        return array;
    }

    object TryEvalArrayInitializer(IArrayInitializerOperation init, IArrayTypeSymbol arrType)
    {
        var elemClrType = ResolveClrTypeForConst(arrType.ElementType);
        if (elemClrType == null) return null;

        var array = Array.CreateInstance(elemClrType, init.ElementValues.Length);
        if (!TryPopulateArray(array, init, elemClrType))
            return null;

        return array;
    }

    static Type ResolveClrTypeForConst(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Boolean => typeof(bool),
            SpecialType.System_Byte => typeof(byte),
            SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Int16 => typeof(short),
            SpecialType.System_UInt16 => typeof(ushort),
            SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint),
            SpecialType.System_Int64 => typeof(long),
            SpecialType.System_UInt64 => typeof(ulong),
            SpecialType.System_Single => typeof(float),
            SpecialType.System_Double => typeof(double),
            SpecialType.System_String => typeof(string),
            SpecialType.System_Char => typeof(char),
            _ => null,
        };
    }

    static bool TryPopulateArray(Array array, IArrayInitializerOperation init, Type elemClrType)
    {
        for (int i = 0; i < init.ElementValues.Length; i++)
        {
            var elemOp = init.ElementValues[i];
            if (!elemOp.ConstantValue.HasValue)
                return false;
            var val = elemOp.ConstantValue.Value;
            if (val == null)
                continue;
            try
            {
                array.SetValue(Convert.ChangeType(val, elemClrType), i);
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

}
