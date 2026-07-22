using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

public partial class UasmEmitter
{
    readonly EmitContext _ctx;
    readonly ExternRegistryFacts _externRegistry;
    readonly Dictionary<OperationKind, IOperationHandler> _stmtDispatch;
    readonly Dictionary<OperationKind, IExpressionHandler> _exprDispatch;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _delegateConvention;

    public bool DumpEnabled;

    // Property shims → EmitContext
    Compilation _compilation => _ctx.Compilation;
    INamedTypeSymbol _classSymbol => _ctx.ClassSymbol;
    CModule _module => _ctx.Module;
    CoreBuilder _builder => _ctx.Builder;
    LayoutPlanner _planner => _ctx.Planner;
    Dictionary<IMethodSymbol, CFunction> _methodFunctions => _ctx.Methods.Functions;
    Dictionary<IMethodSymbol, EmitContext.MethodSlot> _methodSlots => _ctx.Methods.Slots;
    Dictionary<IMethodSymbol, ReturnSlot[]> _methodReturns => _ctx.Methods.Returns;
    Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.Methods.ParamVarIds;
    IMethodSymbol _currentMethod { get => _ctx.Methods.CurrentMethod; set => _ctx.Methods.CurrentMethod = value; }
    List<MethodContext.ClosureSpec> _pendingClosures => _ctx.Methods.PendingClosures;
    List<(IMethodSymbol Method, MethodContext.ClosureSpec Spec)> _pendingGenericSpecs => _ctx.Generics.PendingSpecs;
    IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap => _ctx.Generics.TypeParamMap;
    HashSet<IMethodSymbol> _inheritedMethods = new(SymbolEqualityComparer.Default);
    List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.Initializers.FieldInitOps;
    List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _staticFieldInitOps => _ctx.Initializers.StaticFieldInitOps;
    Dictionary<string, string> _fieldChangeCallbacks => _ctx.Initializers.FieldChangeCallbacks;
    List<EmitDiagnostic> _diagnostics => _ctx.DiagnosticState.Diagnostics;

    CodeGenResult _codeGenResult;

    public IReadOnlyList<EmitDiagnostic> Diagnostics => _diagnostics;
    public CodeGenResult CodeGenResult => _codeGenResult;

    static Dictionary<string, string> UdonEventNames => LayoutPlanner.UdonEventNames;

    // F2: true when this emitter created its OWN private planner (no shared planner was passed). A private
    // planner is safe to lazily Plan()+Freeze() in EnsurePlannerReady on any thread (nothing else reads it);
    // a SHARED planner reaching EnsurePlannerReady unfrozen is the parallel-emit race the guard rejects.
    readonly bool _ownsPlanner;

    public UasmEmitter(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner = null,
        ExternRegistryFacts externRegistry = null)
    {
        _externRegistry = externRegistry;
        _ownsPlanner = planner == null;
        _ctx = new EmitContext(compilation, classSymbol, planner ?? new LayoutPlanner(compilation));
        _bridge = new SyntheticBridgeBuilder(_ctx.Builder);
        _delegateConvention = new DelegateConventionStorage(_ctx.Storage);

        var stmtHandler = new StatementHandler(_ctx);
        var loopHandler = new LoopHandler(_ctx);
        var switchHandler = new SwitchHandler(_ctx);
        var deconstructHandler = new DeconstructionAssignmentHandler(_ctx);
        var simpleAssignHandler = new SimpleAssignmentHandler(_ctx);
        var compoundAssignHandler = new CompoundAssignmentHandler(_ctx);
        var operatorHandler = new OperatorHandler(_ctx);

        _stmtDispatch = BuildDispatch<IOperationHandler>(
            stmtHandler, loopHandler, switchHandler, deconstructHandler);
        _exprDispatch = BuildDispatch<IExpressionHandler>(
            new ExpressionHandler(_ctx),
            simpleAssignHandler,
            compoundAssignHandler,
            operatorHandler,
            new InvocationHandler(_ctx),
            new ArrayHandler(_ctx),
            new NullableHandler(_ctx));

        _ctx.InitializeDispatchers(VisitOperation, VisitExpression, operatorHandler.EmitPatternCheckImpl);
    }

    // Build one kind→handler table from each handler's declared HandledKinds. A kind claimed by two
    // handlers in the same table is a construction-time bug (throws), not a silent first-wins tie-break.
    static Dictionary<OperationKind, T> BuildDispatch<T>(params T[] handlers) where T : IHandler
    {
        var table = new Dictionary<OperationKind, T>();
        foreach (var h in handlers)
            foreach (var kind in h.HandledKinds)
            {
                if (table.TryGetValue(kind, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate handler for OperationKind.{kind}: {existing.GetType().Name} and {h.GetType().Name}");
                table[kind] = h;
            }
        return table;
    }

    // Type name resolution helper
    StorageType GetStorageType(ITypeSymbol type)
        => ExternResolver.GetStorageType(new RuntimeType(type), _typeParamMap);
    string GetStorageTypeName(ITypeSymbol type) => GetStorageType(type).Name;
    string GetArrayType(IArrayTypeSymbol arrType) => GetStorageTypeName(arrType);
    string GetArrayElemType(IArrayTypeSymbol arrType)
    {
        var t = GetArrayType(arrType);
        return t.Substring(0, t.Length - "Array".Length);
    }

    // ── CoreBuilder bridge helpers (old IrBuilder API → CoreBuilder) ──

    // ── Emit ──

    /// <summary>Access to the Core IR module for debugging and testing.</summary>
    public CModule Module => _module;

    /// <summary>Test/tooling accessors for the Stage 2 M1 CaptureScopeAnalysis (built in <see cref="Emit"/>,
    /// consumed by nothing yet — see EmitContext.CaptureScope).</summary>
    public CaptureScopeAnalysis CaptureScope => _ctx.Closures.CaptureScope;
    public Compilation Compilation => _ctx.Compilation;
    public INamedTypeSymbol ClassSymbol => _ctx.ClassSymbol;

    // C4 (M5d): the one per-class ResolvedEdgeResolver instance — the relocated classifier core plus
    // the reach cores; stateless beyond this emitter, so every consumer (reach worklist, recursion
    // walk, tail matchers, legacy oracle, tests) shares it.
    ResolvedEdgeResolver _edgeResolver;
    internal ResolvedEdgeResolver EdgeResolver => _edgeResolver ??= new ResolvedEdgeResolver(this);
    internal ResolvedEdgeResolver DebugBuildResolver() => EdgeResolver; // test entry (post-Emit state)

    // C4: the seeded-context reads the relocated CallEdge classifier consumes (null/empty before Emit
    // seeds them at the compile-plan build — the resolver fails loud on a pre-seed CallEdge call).
    internal VirtualDispatch VirtualDispatchInstance => _ctx.VirtualDispatch;
    internal ClassTypeObjectContext ClassTypes => _ctx.ClassTypes;

    // CA call-graph rewrite (M5b prerequisite): test-only accessor exposing the populated RecursionInfo
    // (all six facets: RecursionGraphNodes, per-node RecursiveCallees/CycleCallees edge sets,
    // ThisFieldTouches, ReentrantDispatchSites, TailSparedDirectCallSites) post-Emit, so
    // RecursionFacetEquivalenceTests can census the legacy BuildRecursionInfo product and diff it
    // against the worklist-produced facets before the M5b swap. Unused by production emission.
    internal RecursionInfo DebugRecursionInfo => _ctx.RecursionContext.Info;

    /// <summary>Called after handler emission, before optimization. Set for IR debugging.</summary>
    public Action<string, CModule> OnIrPass;

    public string Emit()
    {
        using var externScope = _externRegistry == null ? null : ExternResolver.UseRegistry(_externRegistry);
        using var typeFactScope = UdonTypeFacts.RecordInto(_module.TypeFacts);
        // Record types cannot work in Udon: no heap allocation for user types, no inheritance from UdonSharpBehaviour
        if (_classSymbol.IsRecord)
            throw new NotSupportedException(
                $"Record type '{_classSymbol.Name}' is not supported in UdonSharp. " +
                "Udon VM cannot allocate user-defined types. Use a regular class inheriting from UdonSharpBehaviour instead.");

        EnsurePlannerReady();
        EmitFields();
        SetReflectionValues();
        var plan = BuildClassCompilePlan();
        _plan = plan;
        _reach = plan.Reach;
        // Stage 2: closure-scope analysis feeding real codegen — EnvEmit's alloc/read/write and every
        // IsCapturingClosure call site (HandlerBase, InvocationHandler.Extern, this file) key off it.
        // Its roots are the reach definition projection (ComputeCaptureRoots); root bodies come from the
        // reach result (BodyByDef) — no re-fetch (F1).
        // C1 fix: roots = the FULL reach artifact (all provenances); field inits = the emitter's own
        // _fieldInitOps (own + base + auto-property + static, already collected + spliced by EmitFields),
        // NOT CaptureScopeAnalysis's own own-class-instance-only re-collection which missed base field and
        // auto-property initializers.
        IReadOnlyDictionary<IMethodSymbol, IOperation> captureBodies = plan.Reach.BodyByDef;
        if (plan.Reach.GenericForeignStaticBodies.Count > 0)
        {
            // SS2A: merge the supplementary bodies for the authoritative Build lookup (reach itself
            // stays untouched - registration consumers never see these).
            var mergedBodies = new Dictionary<IMethodSymbol, IOperation>(plan.Reach.BodyByDef, SymbolEqualityComparer.Default);
            foreach (var kv in plan.Reach.GenericForeignStaticBodies) mergedBodies[kv.Key] = kv.Value;
            captureBodies = mergedBodies;
        }
        _ctx.Closures.SetCaptureScope(CaptureScopeAnalysis.Build(_compilation, _classSymbol,
            plan.CaptureRoots, captureBodies, plan.FieldInitOps));
        // CA rewrite (M4): seed the typeobj registry in stable-key order (not mint-walk discovery order),
        // so typeobj alloc / is-chain / virtual-dispatch-chain byte order is traversal-independent.
        _ctx.ClassTypes.Seed(plan.Reach.MintedClasses
            .OrderBy(StableOrdinalKey, StringComparer.Ordinal)
            .ThenBy(ClassTypeObjectContext.SpecKey, StringComparer.Ordinal));
        _ctx.VirtualDispatch = new VirtualDispatch(_ctx.ClassTypes); // CA-v2b-2: virtual-call lowering
        EmitMethods(plan);
        OnIrPass?.Invoke("after-emit", _module);
        // Handlers build Core IR; the pipeline (verify/optimize/flatten) runs on Core directly.
        var result = IrPipeline.GenerateUasmFromCore(_module, DumpEnabled);
        _codeGenResult = result;
        return result.Uasm;
    }

    public uint GetHeapSize() => _codeGenResult.HeapSize;

    ClassCompilePlan BuildClassCompilePlan()
    {
        // Design §1: build the single ReachableBodies fixpoint ONCE here — after EmitFields (field
        // initializers are seeds) and before its consumers. Its projections feed Phase-1 registration,
        // BuildRecursionInfo roots, and CaptureScope roots (all in EmitMethods / injected below).
        return new CompilationPlanner(_compilation, ComputeMethods, BuildReachableBodiesViaResolver,
            () => _fieldInitOps.Select(fi => fi.initOp), GetMethodBodyOperation, EnumerateClassFieldInitOps).Build();
    }

    // CA call-graph rewrite (M5a cutover): the reach fixpoint now runs through the unified resolver-driven
    // worklist instead of the legacy 5-collector BuildReachableBodies. Byte-neutral — M4's stable ordinal
    // decouples emit order from the worklist's (different) discovery order, and the worklist reproduces every
    // ReachableBodies facet (proven by golden + DiffFuzz). The open-base-generic defs ride the reach result
    // (ReachableBodies.OpenGenericBaseDefs) and reach the recursion graph through BodyByDef, so the former
    // legacy _openGenericBaseDefs side-effect field is gone — the recursion node source is the reach result.
    ReachableBodies BuildReachableBodiesViaResolver(IMethodSymbol[] methods)
        => new ResolverDrivenReach(EdgeResolver, GetMethodBodyOperation,
            () => _fieldInitOps.Select(fi => fi.initOp), IsCollectibleStructMember, StableOrdinalKey).Build(methods);

    void SetReflectionValues()
    {
        var typeName = _classSymbol.ToDisplayString();
        long typeId = ComputeTypeId(typeName);
        _ctx.Storage.DeclareField(EmitContext.ReflTypeIdField, StorageTypes.Int64, defaultValue: typeId);
        _ctx.Storage.DeclareField(EmitContext.ReflTypeNameField, StorageTypes.String, defaultValue: typeName);

        var ancestorIds = CollectAncestorTypeIds(_classSymbol);
        if (ancestorIds.Length > 1)
            _ctx.DeclareReflTypeIds(ancestorIds);
    }

    static long[] CollectAncestorTypeIds(INamedTypeSymbol type)
    {
        var ids = new List<long>();
        var current = type;
        while (current != null && current.Name != "UdonSharpBehaviour")
        {
            ids.Add(ComputeTypeId(current.ToDisplayString()));
            current = current.BaseType;
        }
        return ids.ToArray();
    }

    internal static long ComputeTypeId(string typeName)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(typeName));
        return System.BitConverter.ToInt64(hash, 0);
    }

    /// <summary>
    /// Ensure the LayoutPlanner is planned and frozen before emission begins.
    ///
    /// IMPORTANT: During parallel emit (Phase 2 of USugarCompilationOrchestrator),
    /// the caller MUST pass a pre-frozen planner. This lazy path is only safe for
    /// single-threaded use (e.g., tests, standalone compilation). If an unfrozen
    /// planner is detected, it is planned and frozen here — but this is NOT
    /// thread-safe if the same planner instance is shared across threads.
    /// </summary>
    void EnsurePlannerReady()
    {
        if (_planner.IsFrozen) return;

        // F2: Phase-2 emit runs in parallel (USugarCompilationOrchestrator's Parallel.ForEach) over a
        // SHARED planner that Phase-1 must have Plan()'d and Freeze()'d (orchestrator freezes at line ~212
        // before the ForEach at ~227). A SHARED planner reaching here UNFROZEN means that freeze contract
        // was violated — lazily planning it now would MUTATE a planner other emitter threads read
        // concurrently. Fail loudly instead of racing. (The discriminator is planner OWNERSHIP, not thread
        // type: xUnit runs tests on background thread-pool threads, so a thread check would reject the
        // legitimate test/standalone path. A PRIVATE planner is unshared and safe to plan lazily anywhere.)
        if (!_ownsPlanner)
            throw new System.InvalidOperationException(
                "EnsurePlannerReady: a SHARED LayoutPlanner reached emit unfrozen. Phase-1 must "
              + "Plan() and Freeze() every layout before Phase-2's Parallel.ForEach — mutating a shared "
              + "planner during parallel emit would race concurrent emitters. Pass a pre-frozen planner, "
              + "or omit the planner argument to use a private one (the test/standalone lazy path).");

        // Private (emitter-owned) planner, unfrozen: the documented test/standalone path — plan, then freeze.
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var classDecl in root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                if (symbol == null || !ExternResolver.IsUdonSharpBehaviour(symbol)) continue;
                _planner.Plan(symbol);
                foreach (var iface in symbol.AllInterfaces)
                    _planner.Plan(iface);
            }
            foreach (var ifaceDecl in root.DescendantNodes()
                .OfType<InterfaceDeclarationSyntax>())
            {
                var ifaceSymbol = model.GetDeclaredSymbol(ifaceDecl) as INamedTypeSymbol;
                if (ifaceSymbol != null)
                    _planner.Plan(ifaceSymbol);
            }
            // Wave-14 r3: record every interface a user STRUCT implements (see LayoutPlanner's
            // InterfaceHasStructImplementor doc comment) — a separate walk since structs aren't classes.
            foreach (var structDecl in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            {
                var structSymbol = model.GetDeclaredSymbol(structDecl) as INamedTypeSymbol;
                if (structSymbol == null) continue;
                foreach (var iface in structSymbol.AllInterfaces)
                    _planner.RegisterStructImplementedInterface(iface);
            }
        }
        _planner.Freeze();
    }

    // ── EmitFields ──

    void EmitFields()
    {
        foreach (var member in _classSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsImplicitlyDeclared) continue;
            if (member.IsStatic)
            {
                EmitStaticReadonlyField(member);
                continue;
            }

            // First-class delegate field (design §2.1): ONE SystemObjectArray heap var holding the bundle
            // reference, null-initialized in UASM data. Private fields are bundled too (assign/invoke route
            // on _delegateFields set-membership, not accessibility). Intercepted BEFORE the generic
            // sync/flags block (M4 [T2]) so a [UdonSynced] delegate field hits the delegate-specific
            // reject in DeclareDelegateField — the single choke point shared with the base-class path.
            // Flags/syncMode were never used for delegate fields, so this reorder changes no output.
            if (member.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod != null)
            {
                DeclareDelegateField(member, delegateType);
                continue; // Skip normal field declaration
            }

            // CA-M1 §2-1: a v1 class value is a program-local object[] bundle, so a class-carrying field must
            // not be a cross-program surface. A public / [SerializeField] / [UdonSynced] field is exposed via
            // GetProgramVariable, network sync, or the Inspector — none can carry the bundle. A PRIVATE,
            // non-synced class field stays legal (in-program storage, e.g. a linked-list root).
            if (EmitPolicy.ContainsUserClassType(member.Type))
            {
                bool exported = member.DeclaredAccessibility == Accessibility.Public
                    || member.GetAttributes().Any(a => a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute");
                bool synced = member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute");
                if (exported || synced)
                    throw new NotSupportedException(
                        $"Field '{member.Name}' carries a v1 user class and is "
                        + (synced ? "[UdonSynced]" : "public/[SerializeField]")
                        + ": a class value is a program-local object[] bundle that cannot cross the "
                        + "GetProgramVariable / network-sync / Inspector surface. Make the field private, or "
                        + "store plain data.");
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
                            _fieldInitOps.Add((member.Name, initOp, member.Type));
                    }
                }
            }
            _ctx.Storage.DeclareField(member.Name, new StorageType(udonType), flags, constValue, syncMode);

            // Aggregate (struct/tuple) field with NO explicit initializer → C# default-initializes it to a
            // zeroed struct. In the object[] emulation that requires a fresh default array; without it the heap
            // var stays null and `f.x = …` faults (NRE on __Set__). Reference-type/array fields stay null (correct).
            if (syntaxRef?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: not null }
                && member.Type is INamedTypeSymbol aggFieldType && TypeClassifier.IsAggregateValue(aggFieldType))
            {
                _ctx.Aggregates.FieldDefaults.Add((member.Name, aggFieldType));
            }

            // Detect [FieldChangeCallback("PropertyName")]
            var fcbAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
            if (fcbAttr != null && fcbAttr.ConstructorArguments.Length > 0
                && fcbAttr.ConstructorArguments[0].Value is string propName)
            {
                _fieldChangeCallbacks[member.Name] = propName;
                _ctx.Storage.DeclareField($"__old_{member.Name}", new StorageType(udonType));
            }
        }

        // Field-like events (design §2.1, A-M2): materialize each as a private multicast delegate field
        // via the SAME DeclareDelegateField choke point plain delegate fields use (heap var = event
        // name, DelegateFields registration, __dlgc_{sig} conv globals, sync/NetworkCallable/tuple-
        // return/ref-out reject all inherited for free). The compiler-synthesized backing IFieldSymbol
        // stays IsImplicitlyDeclared and is skipped by the field loop above — materialize here instead,
        // so it never double-declares.
        foreach (var evt in _classSymbol.GetMembers().OfType<IEventSymbol>())
            DeclareEvent(evt);

        // Properties → declare as heap variables
        foreach (var prop in _classSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.IsImplicitlyDeclared) continue;
            // Auto-property iff it has a compiler-generated backing field (its accessors have empty bodies).
            // The old DeclaringSyntaxReferences.IsEmpty check was always false for source `{ get; set; }`
            // accessors, so a PRIVATE auto-property was never detected and its backing field went undeclared.
            var isAuto = prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
                .Any(f => f.IsImplicitlyDeclared && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));
            // An explicit interface implementation auto-property's metadata name is DOTTED
            // ("IFoo.P"), which is not a valid UASM identifier — the backing-var declaration used to
            // crash the assembler (UAssemblyParser ParseException, an ICE). Loud diagnostic instead.
            if (isAuto && prop.ExplicitInterfaceImplementations.Length > 0)
                throw new NotSupportedException(ExplicitInterfaceAutoPropError(prop));
            if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
            var udonType = GetStorageTypeName(prop.Type);
            var flags = FieldFlags.None;
            if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            _ctx.Storage.DeclareField(prop.Name, new StorageType(udonType), flags,
                isAuto ? ResolveAutoPropInitializer(prop.Name, prop) : null);
        }

        // Record count of derived-class field init ops; base class init ops added below
        // must be reordered to come first (C# spec: base → derived initializer order).
        int derivedFieldInitCount = _fieldInitOps.Count;
        int derivedStaticFieldInitCount = _staticFieldInitOps.Count; // §3.6: static tier gets the same treatment
        var baseClassInitBoundaries = new List<int>(); // track boundaries per base class
        var baseStaticInitBoundaries = new List<int>();

        // Collect declared member SYMBOLS (name → derived-most declaration). A base member whose
        // name matches is either (a) part of one override chain — legal, one virtual slot — or
        // (b) `new`-style shadowing, where C# has TWO storages but this emitter's name-keyed heap
        // model would collapse both symbols onto ONE heap var (VM-verified silent state corruption:
        // SetBase/GetBase through the base symbol read the derived symbol's writes, and a
        // type-conflicting shadow halts the VM with HeapTypeMismatchException at runtime).
        // Storage collision is never acceptable → loud reject per design §8-3 (predates fcd-stage1).
        // Non-const, non-mutable static readonly fields are tracked here too (feature B materializes
        // them into a heap var by the same bare name) — a static MUTABLE field gets no storage, so
        // reusing its name lower in the hierarchy collides with nothing and is left untracked.
        // Field-like events materialize storage under their bare name too (DeclareEvent/DeclareDelegateField)
        // — tracked here so a base field/prop/event of the same name collides loudly (design §8 item 6).
        var declaredMemberSyms = new Dictionary<string, ISymbol>();
        foreach (var m in _classSymbol.GetMembers())
            if (m is IFieldSymbol or IPropertySymbol or IEventSymbol && !m.IsImplicitlyDeclared
                && (!m.IsStatic || (m is IFieldSymbol { IsReadOnly: true, HasConstantValue: false }))
                && !declaredMemberSyms.ContainsKey(m.Name))
                declaredMemberSyms[m.Name] = m;

        // Inherited fields and properties from user-defined base classes
        var baseType = _classSymbol.BaseType;
        while (baseType != null)
        {
            if (USugarCompilerHelper.IsFrameworkNamespace(baseType.ContainingNamespace) || baseType.Name == "UdonSharpBehaviour") break;
            baseClassInitBoundaries.Add(_fieldInitOps.Count);
            baseStaticInitBoundaries.Add(_staticFieldInitOps.Count);
            foreach (var member in baseType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsImplicitlyDeclared) continue;
                // A FIELD can never be overridden, so any name match with a nearer declaration is
                // `new`-style shadowing — two distinct symbols, one heap var. Loud. (Materialized static
                // readonly fields are name-keyed heap vars too, so this applies to them identically;
                // static MUTABLE fields carry no storage and were never tracked into declaredMemberSyms.)
                if (declaredMemberSyms.TryGetValue(member.Name, out var fieldShadower))
                    throw new NotSupportedException(ShadowedStorageError(member, fieldShadower));
                if (member.IsStatic)
                {
                    EmitStaticReadonlyField(member);
                    if (!member.IsConst && member.IsReadOnly) declaredMemberSyms[member.Name] = member;
                    continue;
                }

                // Delegate field from a base class → same single-SystemObjectArray declaration as the derived
                // path (private bundled too). Must intercept BEFORE the generic initializer scan below, which
                // would otherwise also enqueue the init op (the helper routes it via _fieldInitOps itself) —
                // this fixes the old base-path store into a never-declared variable.
                if (member.Type is INamedTypeSymbol baseDelegateType && baseDelegateType.DelegateInvokeMethod != null)
                {
                    DeclareDelegateField(member, baseDelegateType);
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
                                _fieldInitOps.Add((member.Name, initOp, member.Type));
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

                _ctx.Storage.DeclareField(member.Name, new StorageType(udonType), baseFlags, constValue, baseSyncMode);

                var baseFcbAttr = member.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
                if (baseFcbAttr != null && baseFcbAttr.ConstructorArguments.Length > 0
                    && baseFcbAttr.ConstructorArguments[0].Value is string basePropName)
                {
                    _fieldChangeCallbacks[member.Name] = basePropName;
                    _ctx.Storage.DeclareField($"__old_{member.Name}", new StorageType(udonType));
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
                if (prop.IsStatic || prop.IsImplicitlyDeclared) continue;
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
                            _ctx.Storage.DeclareField(BaseAutoPropBackingName(prop), GetStorageType(prop.Type), FieldFlags.None,
                                ResolveAutoPropInitializer(BaseAutoPropBackingName(prop), prop));
                        continue;
                    }
                    // A storage-BEARING base member (auto-prop) hidden without an override relation
                    // is the heap-var collision — loud. A MANUAL base property has no storage (its
                    // accessors are real functions; the planner already disambiguates their export
                    // names on collision), so `new`-shadowing it stays legal (wave-7 pinned).
                    if (isAuto)
                        throw new NotSupportedException(ShadowedStorageError(prop, propShadower));
                    continue;
                }
                if (isAuto && prop.ExplicitInterfaceImplementations.Length > 0)
                    throw new NotSupportedException(ExplicitInterfaceAutoPropError(prop));
                if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
                var udonType = GetStorageTypeName(prop.Type);
                var flags = FieldFlags.None;
                if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
                declaredMemberSyms[prop.Name] = prop;
                _ctx.Storage.DeclareField(prop.Name, new StorageType(udonType), flags,
                    isAuto ? ResolveAutoPropInitializer(prop.Name, prop) : null);
            }
            baseType = baseType.BaseType;
        }

        // Reorder field init ops: base class initializers must run before derived (C# spec).
        // Base classes were walked nearest-parent-first, so reverse class-level order
        // while preserving field order within each class.
        ReorderBaseFirst(_fieldInitOps, baseClassInitBoundaries, derivedFieldInitCount);

        // §3.6 (feature B): the static TIER gets the identical base-first reorder, independently of
        // the instance tier above (they were collected into separate lists), then splices in FRONT of
        // it — base static → derived static → base instance → derived instance.
        ReorderBaseFirst(_staticFieldInitOps, baseStaticInitBoundaries, derivedStaticFieldInitCount);
        if (_staticFieldInitOps.Count > 0)
            _fieldInitOps.InsertRange(0, _staticFieldInitOps);
    }

    /// <summary>Base-first reorder shared by the instance and static field-initializer tiers (§3.6):
    /// <paramref name="ops"/> was collected derived-first (indices [0, derivedCount) ) then base-class
    /// groups nearest-parent-first (each group's start boundary recorded in <paramref name="boundaries"/>);
    /// rewrites it to outermost-base ... nearest-base ... derived (C# spec order). A no-op when no base
    /// class contributed any ops.</summary>
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
        return syncMode;
    }

    /// <summary>
    /// Static field declaration branch (design §3, feature B), shared verbatim by the own-class and
    /// base-class field walks. `const` fields and `static readonly` fields whose initializer folds to
    /// a compile-time constant get NO storage here — ExpressionHandler's existing read-time fold
    /// (byte-invariant, §6 gate) keeps handling them exactly as before this feature. Static MUTABLE
    /// fields also get no storage (reject stays at the read/write use site, §3.7/R8). A non-const,
    /// non-foldable `static readonly` (array / struct / tuple / delegate / computed-but-pure value) is
    /// PER-PROGRAM INSTANCE MATERIALIZED: declared exactly like an instance field (reusing
    /// TryEvaluateFieldInitForHeap for the same constant-array/struct heap-default fast path), its
    /// initializer enqueued to the STATIC TIER of _staticFieldInitOps (run before the instance tier at
    /// _start — §3.6) so each behaviour instance builds its own independent copy. An impure initializer
    /// is loud-rejected (§3.4, R6) before any storage is declared — purity is what makes running it once
    /// per instance observationally identical to C#'s once-per-domain. Returns true iff a heap var was
    /// declared (materialized), so callers can track the name for cross-hierarchy shadow-collision
    /// detection the same way instance fields already are.
    /// </summary>
    bool EmitStaticReadonlyField(IFieldSymbol member)
    {
        if (member.HasConstantValue) return false;    // `const` → existing fold path, no storage
        if (!member.IsReadOnly) return false;         // static mutable → no storage; reject at use site

        var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
        IOperation initOp = null;
        if (syntaxRef?.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } declarator)
        {
            var model = _compilation.GetSemanticModel(declarator.SyntaxTree);
            initOp = model.GetOperation(declarator.Initializer.Value);
        }

        // Compile-time-constant initializer (`static readonly int X = 1 + 2;`) → the EXISTING fold
        // path (ExpressionHandler.VisitFieldReference), byte-invariant — must stay storage-free exactly
        // as before this feature.
        if (initOp != null && initOp.ConstantValue.HasValue && initOp.ConstantValue.Value != null) return false;
        if (initOp != null && EmitPolicy.TryGetConstFieldInitializer(_compilation, member, out _)) return false;

        if (initOp != null && !EmitPolicy.IsPureStaticReadonlyInitializer(initOp))
            throw new NotSupportedException(
                $"a static readonly initializer must be pure (composed of constants and value construction); "
                + $"'{member.Name}' calls a method or reads mutable state, which would run once per behaviour "
                + "instance rather than once. Compute it in an instance field initializer or Start().");

        // Nothing to synchronize: each instance already materializes its own immutable copy at Start.
        if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute"))
            throw new NotSupportedException(
                $"[UdonSynced] static readonly field '{member.Name}' cannot be synced: each behaviour "
                + "instance already materializes its own immutable copy at Start, so there is nothing to "
                + "synchronize. Remove [UdonSynced].");

        if (member.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod != null)
        {
            DeclareDelegateField(member, delegateType);
            return true;
        }

        var udonType = GetStorageTypeName(member.Type);
        object constValue = null;
        if (initOp != null)
        {
            constValue = TryEvaluateFieldInitForHeap(initOp, member.Type);
            if (constValue == null)
                _staticFieldInitOps.Add((member.Name, initOp, member.Type)); // static tier — §3.6
        }
        _ctx.Storage.DeclareField(member.Name, new StorageType(udonType), FieldFlags.None, constValue);

        if (initOp == null && member.Type is INamedTypeSymbol aggFieldType && TypeClassifier.IsAggregateValue(aggFieldType))
            _ctx.Aggregates.FieldDefaults.Add((member.Name, aggFieldType));

        return true;
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
    void DeclareDelegateField(ISymbol member, INamedTypeSymbol delegateType)
    {
        // M4 [T2]: Udon sync cannot carry the object[] bundle (the on-game security filter over
        // synced vars is the unverified design §8-7 risk) — loud on BOTH declaration paths.
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
        DelegateAbi.ValidateNoRefOutParams(delegateType.DelegateInvokeMethod);

        _ctx.Storage.DeclareField(member.Name, StorageTypes.ObjectArray, FieldFlags.None);
        _ctx.Synthetics.DelegateFields.Add(member.Name);

        // Declare the signature-keyed __dlgc_ convention vars for this delegate signature (§3.2).
        var invoke = delegateType.DelegateInvokeMethod;
        // envName is intentionally ignored here: a delegate FIELD declaration is not a dispatch site
        // or capturing bridge, so declaring __dlgc_{sig}__env unconditionally would break the
        // capture-free byte invariant (§1.3). It is declared on-first-use at the dispatch/bridge site.
        var (convArgs, convRet, _) = HandlerBase.GetConventionFieldNames(delegateType);
        for (int ci = 0; ci < convArgs.Length; ci++)
            _ctx.Storage.TryDeclareVar(convArgs[ci], ExternResolver.GetStorageType(new RuntimeType(invoke.Parameters[ci].Type), _typeParamMap));
        if (convRet != null)
            _ctx.Storage.TryDeclareVar(convRet, ExternResolver.GetStorageType(new RuntimeType(invoke.ReturnType), _typeParamMap));

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
                _fieldInitOps.Add((member.Name, initOp, delegateType)); // delegateType == member.Type (caller-supplied)
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
            throw new NotSupportedException(
                $"Static event '{evt.Name}' is not supported: the Udon VM has no shared static storage "
                + "(same reason static mutable fields are unsupported). Use an instance event.");
        // Field-like events get compiler-synthesized (IsImplicitlyDeclared) add/remove accessors; a
        // custom accessor body means the user wrote add{...}/remove{...} explicitly.
        if (evt.AddMethod == null || !evt.AddMethod.IsImplicitlyDeclared
            || evt.RemoveMethod == null || !evt.RemoveMethod.IsImplicitlyDeclared)
            throw new NotSupportedException(
                $"Custom-accessor event '{evt.Name}' (add{{...}}/remove{{...}}) is not supported; only "
                + "field-like events ('event Action Foo;') are, since a custom accessor has no "
                + "well-defined backing storage to materialize.");
        if (evt.Type is not INamedTypeSymbol delegateType || delegateType.DelegateInvokeMethod == null)
            throw new NotSupportedException($"Event '{evt.Name}' has a non-delegate type.");
        DeclareDelegateField(evt, delegateType);
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
        return autoProp.Name;
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
        _fieldInitOps.Add((backingVar, initOp, prop.Type));
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

    static string ExplicitInterfaceAutoPropError(IPropertySymbol prop)
        => $"Explicit interface implementation auto-property '{prop.Name}' is not supported in "
         + "the current storage ABI: its backing storage name contains '.' and is not a valid Udon identifier. "
         + "Implement the property implicitly (public auto-property) or with manual accessors.";

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
    /// class). Runs after EnsurePlannerReady, so the planner is frozen and this class is planned.</summary>
    IMethodSymbol[] ComputeMethods()
    {
        var planned = _planner.GetLayout(_classSymbol).Methods.Keys.ToArray();
        _inheritedMethods = new HashSet<IMethodSymbol>(
            planned.Where(m => !SymbolEqualityComparer.Default.Equals(m.ContainingType, _classSymbol)),
            SymbolEqualityComparer.Default);

        // Emitter-only projection: own generic user-method DEFINITIONS (see summary).
        var ownGenerics = _classSymbol.GetMembers().OfType<IMethodSymbol>().Where(IsOwnGenericSeed);

        return planned.Concat(ownGenerics).ToArray();
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

    void EmitMethods(ClassCompilePlan plan)
    {
        var registration = RegisterProgram(plan);
        BuildRecursionInfo();
        EmitRegisteredBodies(plan, registration);
    }

    ProgramRegistration RegisterProgram(ClassCompilePlan plan)
    {
        var methods = plan.Methods;
        var typeLayout = _planner.GetLayout(_classSymbol);

        // First pass: create IrFunctions, assign params, return vars (skip generic definitions)
        _ctx.Methods.NextMethodIndex = 0;
        foreach (var method in methods)
        {
            EmitPolicy.RejectInParameters(method); // round-7 follow-up [Q3], declaration-side
            EmitPolicy.RejectNetworkCallableDelegates(method); // M4 [T1], declaration-side
            if (method.IsGenericMethod) continue;

            var ml = typeLayout.Methods[method];
            var exportName = ml.ExportName;
            var slot = _ctx.Methods.Register(method, _ => exportName);
            var idx = slot.Index;

            // Determine if this method should be exported
            bool isOwnOrInherited = SymbolEqualityComparer.Default.Equals(method.ContainingType, _classSymbol)
                || _inheritedMethods.Contains(method);

            string fcbFieldName = null;
            if (method.MethodKind == MethodKind.PropertySet
                && method.AssociatedSymbol is IPropertySymbol setProp)
            {
                foreach (var kvp in _fieldChangeCallbacks)
                    if (kvp.Value == setProp.Name) { fcbFieldName = kvp.Key; break; }
            }

            bool shouldExport = !method.IsGenericMethod
                && isOwnOrInherited
                && (method.MethodKind == MethodKind.Ordinary
                    || method.MethodKind == MethodKind.PropertyGet
                    || method.MethodKind == MethodKind.PropertySet)
                && (method.DeclaredAccessibility == Accessibility.Public
                    || UdonEventNames.ContainsKey(method.Name)
                    || fcbFieldName != null);

            // Create CFunction with or without ExportName
            var func = _module.AddFunction(exportName, shouldExport ? exportName : null);
            _methodFunctions[method] = func;

            // Declare params using LayoutPlanner IDs (delegate-typed params are SystemObjectArray bundle
            // references via the type-map delegate arm — design §2.1).
            var paramVarIds = new string[method.Parameters.Length];
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                _ctx.Storage.DeclareVar(ml.ParamIds[i], GetStorageType(method.Parameters[i].Type));
                paramVarIds[i] = ml.ParamIds[i];
            }
            _methodParamVarIds[method] = paramVarIds;
            foreach (var pid in paramVarIds) func.ParamFieldNames.Add(pid);

            // Declare return var(s) from unified Returns
            if (ml.Returns.Count > 0)
            {
                foreach (var ret in ml.Returns)
                    _ctx.Storage.DeclareVar(ret.Id, ret.StorageType);

                if (ml.Returns.Count == 1)
                    func.ReturnType = ml.Returns[0].StorageType;
                else
                    func.ReturnType = StorageTypes.Void; // tuple: no single return value

                foreach (var ret in ml.Returns)
                    func.ReturnSlots.Add(ret);

                _methodReturns[method] = ml.Returns.ToArray();
            }
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
        var baseInstanceMethods = plan.BaseInstanceMethods;
        var structMethods = plan.StructMethods;
        // C4 retirement (the C2-incidental duplicate): a static LOCAL FUNCTION declared inside a foreign
        // static classifies as a foreign static itself (IsForeignStatic has no MethodKind filter — its
        // reach leg seeding BodyByDef is the C2-proven recursion-node arm and stays), but local functions
        // register on demand at their declaration statement (or the [Y9] forward-reference arm), which
        // overwrote this eager Phase-1 copy in _methodFunctions and left it emitted-but-unreachable (a
        // dead __N_ duplicate body + heap vars, probe-proven __2_Twice/__3_Twice). Gate the REGISTRATION
        // projection only.
        var foreignStatics = plan.ForeignStatics.Where(fm => fm.MethodKind != MethodKind.LocalFunction).ToArray();
        foreach (var fm in foreignStatics)
        {
            EmitPolicy.RejectInParameters(fm); // round-7 follow-up [Q3]

            // B70 root 1 (A14/A15): a static method on a CLOSED generic struct (GS14<bool>.Run) is registered
            // here, but this loop — unlike the struct-instance and base-instance loops — never seeded
            // FirstGenericSpec. A nested LF then could not reach the enclosing struct's closed T (the
            // closureBindings walk at EmitMethod misses the owner), so `new T[]` emitted a bogus TArray. Seed
            // it the same way the struct-methods loop does (including the two-instantiation aliasing guard,
            // which GS15<int>/GS15<string> exercises).
            var slot = _ctx.Methods.Register(fm, i => i.ToString());
            var idx = slot.Index;
            var funcName = $"__{idx}_{SanitizeId(fm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[fm] = func;

            var fmParamIds = new string[fm.Parameters.Length];
            for (int pi = 0; pi < fm.Parameters.Length; pi++)
            {
                var param = fm.Parameters[pi];
                var paramId = NameAllocator.ParamId(param.Name, idx);
                _ctx.Storage.DeclareVar(paramId, GetStorageType(param.Type));
                fmParamIds[pi] = paramId;
            }
            _methodParamVarIds[fm] = fmParamIds;
            foreach (var pid in fmParamIds) func.ParamFieldNames.Add(pid);

            if (!fm.ReturnsVoid)
            {
                var retType = GetStorageTypeName(fm.ReturnType);
                var retId = NameAllocator.RetId(SanitizeId(fm.Name), idx);
                    func.ReturnType = new StorageType(retType);
                func.ReturnSlots.Add(new ReturnSlot(retId, new StorageType(retType)));
                _methodReturns[fm] = new[] { new ReturnSlot(retId, new StorageType(retType)) };
            }
        }

        // Register user-struct constructors + instance methods (object[]-emulated; synthetic receiver = param0).
        // structMethods was collected above (before the foreign-static scan, which it also seeds).
        foreach (var sm in structMethods)
        {
            EmitPolicy.RejectInParameters(sm); // round-7 follow-up [Q3]

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
                var containingArgPart = string.Join("_", sm.ContainingType.TypeArguments.Select(ExternResolver.GetUdonTypeName));
                var methodArgPart = sm.IsGenericMethod
                    ? "_" + string.Join("_", sm.TypeArguments.Select(ExternResolver.GetUdonTypeName))
                    : "";
                typeArgSuffix = $"_{containingArgPart}{methodArgPart}";
            }

            var slot = _ctx.Methods.Register(sm, i => i.ToString());
            var idx = slot.Index;
            var isCtor = sm.MethodKind == MethodKind.Constructor;
            var funcName = isCtor
                ? $"__{idx}_{SanitizeId(sm.ContainingType.Name)}__ctor{typeArgSuffix}"
                : $"__{idx}_{SanitizeId(sm.Name)}{typeArgSuffix}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[sm] = func;

            // param0 = receiver object[] for instance methods/ctors (passed uncloned so in-place mutation
            // reflects back to the caller's local). Static operator methods have no receiver.
            if (!sm.IsStatic)
            {
                var receiverId = NameAllocator.ParamId("this", idx);
                _ctx.Storage.DeclareVar(receiverId, StorageTypes.ObjectArray);
                func.ParamFieldNames.Add(receiverId);
            }

            var smParamIds = new string[sm.Parameters.Length];
            for (int pi = 0; pi < sm.Parameters.Length; pi++)
            {
                var p = sm.Parameters[pi];
                var pid = NameAllocator.ParamId(p.Name, idx);
                _ctx.Storage.DeclareVar(pid, GetStorageType(p.Type));
                smParamIds[pi] = pid;
                func.ParamFieldNames.Add(pid);
            }
            _methodParamVarIds[sm] = smParamIds; // Ordinal-indexed; receiver tracked separately

            if (!sm.ReturnsVoid) // ctors are void (mutate in place); instance methods may return
            {
                var retType = GetStorageTypeName(sm.ReturnType);
                var retId = NameAllocator.RetId(SanitizeId(sm.Name), idx);
                func.ReturnType = new StorageType(retType);
                func.ReturnSlots.Add(new ReturnSlot(retId, new StorageType(retType)));
                _methodReturns[sm] = new[] { new ReturnSlot(retId, new StorageType(retType)) };
            }
        }

        // Register base class instance copies (collected above, before the [X5] collector seeds).
        foreach (var bm in baseInstanceMethods)
        {
            EmitPolicy.RejectInParameters(bm); // round-7 follow-up [Q3]
            // Wave-9 round-8 [Y10]: an INHERITED generic method's call-site-constructed copy is the
            // de-facto specialization this path emits (EmitMethod sets the type-param map from it),
            // but it bypassed RegisterGenericSpecialization — so FirstGenericSpec never learned it
            // and a hoisted closure inside the base generic body could not resolve the enclosing
            // method's params (loud "Cannot resolve parameter") or its type-param map. Seed it here,
            // with the same second-distinct-instantiation guard ([X6] r5, widened in round 8).
            var slot = _ctx.Methods.Register(bm, i => i.ToString());
            var idx = slot.Index;
            var funcName = $"__{idx}_{SanitizeId(bm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[bm] = func;

            var bmParamIds = new string[bm.Parameters.Length];
            for (int pi = 0; pi < bm.Parameters.Length; pi++)
            {
                var param = bm.Parameters[pi];
                var paramId = NameAllocator.ParamId(param.Name, idx);
                _ctx.Storage.DeclareVar(paramId, GetStorageType(param.Type));
                bmParamIds[pi] = paramId;
            }
            _methodParamVarIds[bm] = bmParamIds;
            foreach (var pid in bmParamIds) func.ParamFieldNames.Add(pid);

            if (!bm.ReturnsVoid)
            {
                var retType = GetStorageTypeName(bm.ReturnType);
                var retId = NameAllocator.RetId(SanitizeId(bm.Name), idx);
                func.ReturnType = new StorageType(retType);
                func.ReturnSlots.Add(new ReturnSlot(retId, new StorageType(retType)));
                _methodReturns[bm] = new[] { new ReturnSlot(retId, new StorageType(retType)) };
            }
        }

        return new ProgramRegistration(foreignStatics, structMethods, baseInstanceMethods);
    }

    void EmitRegisteredBodies(ClassCompilePlan plan, ProgramRegistration registration)
    {
        var methods = plan.Methods;
        var foreignStatics = registration.ForeignStatics;
        var structMethods = registration.StructMethods;
        var baseInstanceMethods = registration.BaseInstanceMethods;

        // Second pass: emit bodies (skip generic definitions)
        foreach (var method in methods)
        {
            if (method.IsGenericMethod) continue;
            EmitMethod(method);
        }

        // Emit foreign static method bodies
        foreach (var fm in foreignStatics)
            EmitMethod(fm);

        // Emit user-struct constructor + instance method bodies
        foreach (var sm in structMethods)
            EmitMethod(sm);

        // Emit base class instance method bodies
        foreach (var bm in baseInstanceMethods)
            EmitMethod(bm);

        // Synthesize _start if there are field initializers, FCB fields, or default-init aggregate
        // fields but no user-defined Start(). This MUST run BEFORE the bridge emission and the
        // pending-local-function/generic-spec drains below — mirroring the user-Start path, where
        // EmitFieldInitializers runs during the body pass — so an initializer that hoists a lambda
        // (delegate-field initializer) gets its CFunction body and __dlg_ bridge emitted instead of
        // landing in never-drained pending lists (CoreToUasm 'CFuncRef references unknown function').
        if ((_fieldInitOps.Count > 0 || _fieldChangeCallbacks.Count > 0 || _ctx.Aggregates.FieldDefaults.Count > 0)
            && !methods.Any(m => UdonEventNames.TryGetValue(m.Name, out var en) && en == "_start"))
        {
            var startFunc = _module.AddFunction("_start", "_start");
            _builder.SetFunction(startFunc);
            EmitFieldInitializers();
            _builder.EmitReturn();
        }

        // Emit interface bridge exports
        new InterfaceBridgeEmitter(_ctx, _bridge).Emit();

        // Emit delegate bridge exports
        new DelegateBridgeEmitter(_ctx, _bridge, _delegateConvention).EmitLayoutBridges();

        // Emit pending local functions and generic specializations (may chain)
        while (_pendingClosures.Count > 0 || _pendingGenericSpecs.Count > 0)
        {
            if (_pendingClosures.Count > 0)
            {
                var batch = _pendingClosures.ToList();
                _pendingClosures.Clear();
                foreach (var closureSpec in batch)
                    EmitMethod(closureSpec.Def, closureSpec);
            }
            if (_pendingGenericSpecs.Count > 0)
            {
                var batch = _pendingGenericSpecs.ToList();
                _pendingGenericSpecs.Clear();
                foreach (var (specSym, specRecord) in batch)
                    EmitMethod(specSym, specRecord);
            }
        }

        // Emit pending delegate bridges for hoisted lambdas/local functions
        new DelegateBridgeEmitter(_ctx, _bridge, _delegateConvention).EmitPending();
        new ReceiverBridgeEmitter(_ctx, _bridge, _delegateConvention).EmitPending();

        // Variance design (2026-07-04 §2.2/§2.3) T-M2: sig adapters (B-1) + wrapper-with-payload
        // bridges (B-2), for every variant method-group binding / third-party-variant hinge / variant
        // delegate-value conversion registered in this class. A class with no variance emits neither —
        // single-cast golden untouched (§5 gate).
        new WrapperBridgeEmitter(_ctx, _bridge, _delegateConvention).EmitPending();

        // Multicast design (2026-07-03 §1) A-M1: per-sig synthetic combine/remove helpers + fan-out
        // bridge, for every sig a `+=`/`-=` site registered in this class (RegisterMulticastSig). A
        // class with no delegate compound assignment emits none of this — single-cast golden is
        // untouched (§6 gate). Reentrancy graph-node registration for the fan-out is A-M3 scope (§1.6),
        // deliberately not wired here.
        new MulticastDelegateEmitter(_ctx, _bridge, _delegateConvention).EmitPending();
        new EnumToStringSyntheticEmitter(_ctx, _bridge).Emit();

        // §5.5 (graft #2): now that every capturing bridge is registered, assert each has a graph node.
        VerifyBridgeTargetsAreNodes();
    }

    static string SanitizeId(string name) => NameAllocator.Sanitize(name);

    static bool IsHoistedClosureMethod(IMethodSymbol method)
        => method.MethodKind is MethodKind.LocalFunction
            or MethodKind.LambdaMethod or MethodKind.AnonymousFunction;

    // ── EmitMethod ──

    void EmitMethod(IMethodSymbol method, MethodContext.ClosureSpec closureSpec = null)
    {
        _currentMethod = method;
        _ctx.Methods.CurrentClosureSpec = closureSpec;
        var func = closureSpec?.Func ?? _methodFunctions[method];

        // Struct instance methods/ctors carry the receiver object[] as synthetic param0; make `this`
        // resolve to it for the body. Static (operator) struct methods have no receiver. B44: a hoisted
        // lambda/local function declared INSIDE a struct method also reports ContainingType == the
        // struct (Roslyn resolves a closure's ContainingType up to the nearest named type), but it was
        // registered via RegisterLocalFunction (envp-based, no receiver param0) — C# itself forbids a
        // struct closure from referencing `this`'s members (CS1673), so it never needs the receiver;
        // indexing ParamFieldNames[0] for it read past an empty list.
        // CA-M1: a v1 class instance member uses the SAME param0 object[] receiver as a user struct member
        // (reference semantics — no clone; the bundle flows through by reference).
        _ctx.Methods.CurrentStructReceiverParamId =
            (method.ContainingType is INamedTypeSymbol structCt && TypeClassifier.IsObjectArrayEmulated(structCt) && !method.IsStatic
                && method.MethodKind is not (MethodKind.LambdaMethod or MethodKind.LocalFunction))
                ? func.ParamFieldNames[0] : null;

        // A "spec" is any constructed (non-definition) method symbol — a generic method instantiation,
        // a member of a constructed generic struct (feature G, method itself need not be generic), or
        // both. IsGenericMethod alone under-fires for the containing-type-generic case (Box<T>.Get()
        // is not itself a generic method), which is exactly the G-M0-4 gap this predicate closes.
        bool isSpec = !method.IsDefinition;

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

        // Emit field initializers at the start of _start
        var exportName = (closureSpec?.Slot ?? _methodSlots[method]).VarPrefix;
        if (exportName == "_start")
            EmitFieldInitializers();

        // Set up type param map for generic specializations. Feature G: compose the method's OWN
        // generic-method type args (if the method itself is generic) with its ContainingType's type
        // args (if the containing type is generic — a generic-struct member); a method that is itself
        // generic ON a generic struct (Box<T>.Map<U>()) merges both.
        // Compose this method's type-param map locally, then open ONE scope for it just before body
        // emission (below). Nothing between here and the scope reads the ambient map.
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap = null;
        if (isSpec)
            typeMap = TypeEnvironment.ForMethod(method);

        // SS2B ambient owner chain: closure key composition during THIS emission resolves its
        // lexical owners' args against this chain (ComposeClosureKeyArgs).
        _ctx.Methods.CurrentOwnerSpecs = closureSpec?.OwnerSpecs
            ?? (isSpec ? System.Collections.Immutable.ImmutableArray.Create(method)
                       : System.Collections.Immutable.ImmutableArray<IMethodSymbol>.Empty);

        // Wave-9 round-8 [Y2]: a hoisted closure (lambda / local function) declared inside a GENERIC
        // method body — its operation tree is the generic DEFINITION's, so T-typed expressions need
        // the instantiation's type-param map during body emission (registration already substituted
        // the signature types while the enclosing spec's map was active; without the map here the
        // body type-checks as 'T' and CoreVerify ICEs on a single legal instantiation). A closure
        // whose semantics depend on T pins its generic to ONE instantiation (the [X6] r5 reject,
        // widened in round 8 to type-param-referencing closures), so FirstGenericSpec is the exact
        // owner. Walk up through enclosing closures to (possibly nested) generic owners.
        // Round-9 [Y8]: also runs for a generic LOCAL FUNCTION spec (isSpec) nested in a
        // generic method — the spec map above holds only the LF's OWN type params, so the
        // enclosing generic's params are MERGED in (never replacing the spec map).
        if (IsHoistedClosureMethod(method))
        {
            List<(IReadOnlyList<ITypeParameterSymbol>, IReadOnlyList<ITypeSymbol>)> closureBindings = null;
            // SS2B: a per-spec closure composes from ITS OWN registration-time owner-spec chain — the
            // the old first-wins fallback was leg-B's silent first-spec-T bake. Owners not in
            // the record's chain (an outer generic beyond the registration ambient — M2b bound) still
            // fall through to the legacy walk below, which SKIPS owners the record already covered.
            var coveredOwners = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            if (closureSpec != null)
                foreach (var ownerSpec in closureSpec.OwnerSpecs)
                {
                    coveredOwners.Add(ownerSpec.OriginalDefinition);
                    closureBindings ??= new();
                    closureBindings.Add((ownerSpec.OriginalDefinition.TypeParameters, ownerSpec.TypeArguments));
                    if (ownerSpec.ContainingType.IsGenericType)
                        closureBindings.Add((ownerSpec.ContainingType.OriginalDefinition.TypeParameters,
                            ownerSpec.ContainingType.TypeArguments));
                }
            for (var s = method.ContainingSymbol; s is IMethodSymbol enclosing; s = enclosing.ContainingSymbol)
            {
                if (coveredOwners.Contains(enclosing.OriginalDefinition)) continue;
                // No IsGenericMethod pre-filter: FirstGenericSpec is keyed by OriginalDefinition
                // regardless of WHY a method is a spec (generic method, generic-struct member, or
                // both — feature G), so the dictionary lookup alone is the correct, sufficient gate.
                if (enclosing.OriginalDefinition.TypeParameters.Length > 0
                    || enclosing.ContainingType is { IsGenericType: true })
                    throw new InvalidOperationException(
                        $"Closure '{method.ToDisplayString()}' was registered without its lexical owner "
                        + $"specialization '{enclosing.OriginalDefinition.ToDisplayString()}'.");
            }
            // Inherit the owner generic's args but let this method's own map keep colliding keys
            // (newWins:false = add-if-missing, mirroring the former merge).
            if (closureBindings != null)
                typeMap = TypeParamScope.Compose(typeMap, newWins: false, closureBindings);
        }

        // Get method body IOperation
        var bodySource = isSpec ? method.OriginalDefinition : method;
        var syntaxRef = bodySource.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef != null)
        {
            var syntax = syntaxRef.GetSyntax();
            var tree = syntax.SyntaxTree;
            var model = _compilation.GetSemanticModel(tree);

            var bodyOp = model.GetOperation(syntax);

            // The former [Y8]/B51/B70 rekey block (re-binding the map under each walk's fresh
            // type-parameter symbols) is retired: TypeParamScope composes its maps with
            // TypeParamIdComparer, so per-walk twins of one declared parameter hit one key directly
            // (design 2026-07-10 symbol-intern v2, T1 — red-proofed: disabling the comparer-era
            // compensation reproduced the 15 rekey-class failures before this landed).

            // Open the depth-1 scope now that the map is fully composed; Dispose (at block end) is the
            // sole clear, running even if body emission throws. Non-generic methods carry a null map
            // and open no scope. A closure/spec emitted later recomposes its own map at its own entry.
            using var _typeScope = typeMap != null ? _ctx.EnterTypeParamScope(typeMap) : null;

            PreScanGotoLabels(bodyOp);

            // Emit tail-call optimization label at function entry (jump target for TCO goto)
            _builder.EmitLabel($"__tco_{func.Name}");

            // Stage 2 M2 (design §3.0 INV-2): the MethodEntry-scope EnvAlloc lowers AFTER the __tco_
            // label so a self-tail loopback re-runs it every logical activation (per-activation env
            // freshness). A closure reaches its own body scope via ClosureScopes (its MethodEntry
            // Node is the lambda/LF body, not this bodyOp); a root method via ScopeFor(bodyOp).
            // No-ops on a null / non-capture-bearing scope, so the call is unconditional.
            CaptureScope entryScope = null;
            if (_ctx.Closures.CaptureScope != null)
            {
                if (IsHoistedClosureMethod(method))
                    _ctx.Closures.CaptureScope.ClosureScopes.TryGetValue(method.OriginalDefinition, out entryScope);
                else
                    entryScope = _ctx.Closures.CaptureScope.ScopeFor(bodyOp, CaptureScopeKind.MethodEntry);
            }
            EnvEmit.Alloc(_builder, _ctx, entryScope);

            // Consume every captured PARAMETER of this method out of its flat param field into its env
            // cell (the arg arrived positionally in the flat field; all body reads route through env).
            var entryParamIds = closureSpec?.ParamVarIds;
            if (entryParamIds == null) _methodParamVarIds.TryGetValue(method, out entryParamIds);
            if (_ctx.Closures.CaptureScope != null && entryParamIds != null)
                foreach (var p in method.Parameters)
                    if (p.Ordinal < entryParamIds.Length && _ctx.Closures.TryGetEnvBinding(p, out _))
                        EnvEmit.Write(_builder, _ctx, p,
                            _bridge.Load(entryParamIds[p.Ordinal], GetStorageType(p.Type)));

            // Class receiver capture (design 2026-07-10 v2 §1.3): consume the receiver param0 into its
            // env cell exactly like a captured parameter — after __tco_ + EnvAlloc, so a self-tail
            // loopback re-seeds each logical activation's fresh env. Null CurrentStructReceiverParamId
            // (behaviour methods, hoisted closures) and an uncaptured receiver both skip.
            if (_ctx.Closures.CaptureScope != null
                && _ctx.Methods.CurrentStructReceiverParamId is { } rcvParamId
                && LambdaCaptureAnalyzer.ReceiverCaptureKey(method) is { } rcvKey
                && _ctx.Closures.TryGetEnvBinding(rcvKey, out _))
                EnvEmit.Write(_builder, _ctx, rcvKey, _bridge.Load(rcvParamId, new StorageType(AggregateAbi.ArrayType)));

            if (bodyOp is IMethodBodyOperation methodBody)
            {
                if (methodBody.BlockBody != null)
                    VisitOperation(methodBody.BlockBody);
                else if (methodBody.ExpressionBody != null)
                    VisitOperation(methodBody.ExpressionBody);
            }
            else if (bodyOp is ILocalFunctionOperation localFuncOp)
            {
                if (localFuncOp.Body != null)
                    VisitOperation(localFuncOp.Body);
            }
            else if (bodyOp is IConstructorBodyOperation ctorBodyOp)
            {
                // CA-v2 M1: a v1 CLASS ctor orchestrates its own chain (charter #6, field inits + base
                // call, in InvocationHandler which owns EmitCallToMethod/ResolveStructMember). A STRUCT
                // ctor has no base — just its body.
                if (method.ContainingType is INamedTypeSymbol cctClass && TypeClassifier.IsUserClass(cctClass)
                    && _ctx.Methods.CurrentStructReceiverParamId != null)
                    new InvocationHandler(_ctx).EmitClassCtorPrologue(method, ctorBodyOp,
                        _ctx.Methods.CurrentStructReceiverParamId);
                if (ctorBodyOp.BlockBody != null)
                    VisitOperation(ctorBodyOp.BlockBody);
            }
            else if (bodyOp is IAnonymousFunctionOperation anonFunc)
            {
                if (anonFunc.Body is IBlockOperation anonBlock)
                    VisitOperation(anonBlock);
                else if (anonFunc.Body != null)
                {
                    var lambdaRets = closureSpec?.ReturnSlots;
                    if (lambdaRets == null) _methodReturns.TryGetValue(method, out lambdaRets);
                    if (lambdaRets is { Length: 1 })
                    {
                        var resultVal = VisitExpression(anonFunc.Body);
                        _bridge.Store(lambdaRets[0].Id, resultVal);
                    }
                }
            }
            else if (bodyOp is IBlockOperation block)
                VisitOperation(block);
            // Expression-bodied property: int X => expr;
            else if (syntax is PropertyDeclarationSyntax propDecl
                     && propDecl.ExpressionBody != null)
            {
                var exprOp = model.GetOperation(propDecl.ExpressionBody.Expression);
                if (exprOp != null && _methodReturns.TryGetValue(method, out var propRets) && propRets.Length == 1)
                {
                    var resultVal = VisitExpression(exprOp);
                    _bridge.Store(propRets[0].Id, resultVal);
                }
            }
            // Block-bodied property accessor: int X { get { return expr; } }
            else if (syntax is AccessorDeclarationSyntax accessorDecl)
            {
                if (accessorDecl.Body == null && accessorDecl.ExpressionBody == null
                    && method.AssociatedSymbol is IPropertySymbol autoProp)
                {
                    // Auto-property accessor: synthesize body (get → load field, set → store field).
                    // Per-DECLARATION backing (AutoPropBackingVar): the chain leaf owns the bare
                    // name; an overridden base declaration's copies use their own storage (base.P).
                    var backingVar = AutoPropBackingVar(autoProp);
                    var propType = GetStorageTypeName(autoProp.Type);
                    if (method.MethodKind == MethodKind.PropertyGet
                        && _methodReturns.TryGetValue(method, out var autoRets) && autoRets.Length == 1)
                    {
                        _bridge.Store(autoRets[0].Id, _bridge.Load(backingVar, new StorageType(propType)));
                    }
                    else if (method.MethodKind == MethodKind.PropertySet
                        && _methodParamVarIds.TryGetValue(method, out var paramIds) && paramIds.Length > 0)
                    {
                        _bridge.Store(backingVar, _bridge.Load(paramIds[0], new StorageType(propType)));
                    }
                }
                else
                {
                    var accessorOp = model.GetOperation(accessorDecl);
                    if (accessorOp is IMethodBodyOperation accessorBody)
                    {
                        if (accessorBody.BlockBody != null)
                            VisitOperation(accessorBody.BlockBody);
                        else if (accessorBody.ExpressionBody != null)
                            VisitOperation(accessorBody.ExpressionBody);
                    }
                    else if (accessorOp is IBlockOperation accessorBlock)
                        VisitOperation(accessorBlock);
                }
            }
        }

        // FieldChangeCallback epilogue: update _old_ to current value
        if (fcbFieldName != null)
        {
            var curVal = _bridge.Load(fcbFieldName, new StorageType(fcbFieldType));
            _bridge.Store($"__old_{fcbFieldName}", curVal);
        }

        // Method epilogue: return
        _builder.EmitReturn();
        _currentMethod = null;
    }

    // ── Field Initializers ──

    void EmitFieldInitializers()
    {
        // 2026-07-11 audit: field-initializer expressions belong to the CLASS context — never to
        // whatever spec/closure happened to emit last (the synthesized-_start path runs outside any
        // EmitMethod, so the ambient would otherwise be stale). A delegate-field initializer lambda
        // registers against this clean ambient.
        _ctx.Methods.CurrentClosureSpec = null;
        _ctx.Methods.CurrentOwnerSpecs = System.Collections.Immutable.ImmutableArray<IMethodSymbol>.Empty;
        // CA-v2b-1 (charter #6): allocate each minted class's per-program typeobj BEFORE field inits and
        // any instance mint, so bundle[0] can point at it. A typeobj is a fresh object[1] whose reference
        // identity distinguishes the runtime type (v2b-2 will size it to the vtable and back-patch slots).
        foreach (var mc in _ctx.ClassTypes.MintedClasses)
        {
            var tv = _ctx.ClassTypes.TryGetTypeObjVar(mc);
            _ctx.Storage.TryDeclareVar(tv, new StorageType(AggregateAbi.ArrayType));
            _bridge.Store(tv, AggregateAbi.Allocate(_builder, 1));
        }
        // Default-init aggregate (struct/tuple) fields with no explicit initializer FIRST, so any explicit
        // initializer that references one sees a non-null backing array (C# default-then-initializer order).
        foreach (var (fieldId, aggType) in _ctx.Aggregates.FieldDefaults)
            _bridge.Store(fieldId, AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout(aggType),
                _ctx.Aggregates.GetLayout, GetStorageTypeName));

        foreach (var (fieldId, initOp, fieldType) in _fieldInitOps)
        {
            try
            {
                // Bare array initializer { 1, 2, 3 } → synthesize array creation + element Set
                if (initOp is IArrayInitializerOperation arrayInit)
                {
                    var arrTypeSym = (IArrayTypeSymbol)fieldType;
                    var arrayType = GetStorageTypeName(arrTypeSym);
                    var elementType = GetArrayElemType(arrTypeSym);
                    var sizeConst = _bridge.ConstInt(arrayInit.ElementValues.Length);
                    var arrVal = _bridge.CallExtern(new StorageType(arrayType),
                        ExternResolver.BuildArrayCtorSignature(arrayType),
                        new CLeaf[] { sizeConst });
                    _bridge.Store(fieldId, arrVal);
                    for (int i = 0; i < arrayInit.ElementValues.Length; i++)
                    {
                        var elemVal = VisitExpression(arrayInit.ElementValues[i]);
                        var idxConst = _bridge.ConstInt(i);
                        var arrLoad = _bridge.Load(fieldId, new StorageType(arrayType));
                        _bridge.CallExternVoid(
                            ExternResolver.BuildArraySetSignature(arrayType, elementType),
                            new CLeaf[] { arrLoad, idxConst, elemVal });
                    }
                    continue;
                }

                var valueVal = VisitExpression(initOp);

                // Type conversion for numeric type mismatch (e.g. int literal 0 → float field)
                if (initOp.Type != null && fieldType != null
                    && !SymbolEqualityComparer.Default.Equals(initOp.Type, fieldType)
                    && ExternResolver.IsNumericType(initOp.Type)
                    && ExternResolver.IsNumericType(fieldType))
                {
                    var methodName = ExternResolver.GetConvertMethodName(fieldType);
                    if (methodName != null)
                    {
                        var srcType = GetStorageTypeName(initOp.Type);
                        var dstType = GetStorageTypeName(fieldType);
                        var converted = _bridge.CallExtern(new StorageType(dstType),
                            $"SystemConvert.__{methodName}__{srcType}__{dstType}",
                            new CLeaf[] { valueVal });
                        _bridge.Store(fieldId, converted);
                        continue;
                    }
                }

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
            var fcbType = _ctx.Storage.GetFieldType(kvp.Key);
            if (fcbType != null)
            {
                var fieldVal = _bridge.Load(kvp.Key, fcbType.Value);
                _bridge.Store($"__old_{kvp.Key}", fieldVal);
            }
        }
    }

    // ── IOperation visitor (facade — delegates to handlers) ──

    void VisitOperation(IOperation op)
    {
        if (op == null)
            throw new NotSupportedException("VisitOperation called with null operation");
        // Unwrap parenthesized expressions in statement context
        while (op is IParenthesizedOperation paren) op = paren.Operand;
        if (_stmtDispatch.TryGetValue(op.Kind, out var h))
            try { h.Handle(op); return; } catch (System.Exception ex) { throw TagLocation(ex, op); }
        throw new NotSupportedException($"Unsupported operation: {op.Kind} ({op.GetType().Name})");
    }

    void PreScanGotoLabels(IOperation op)
    {
        // No-op: the Core IR uses string-based CGoto/CLabel instead of IrBlock targets.
    }

    // ── Expression visitor (facade — delegates to handlers) ──

    CLeaf VisitExpression(IOperation op)
    {
        if (op == null)
            throw new NotSupportedException("VisitExpression called with null operation");
        // Unwrap parenthesized expressions (transparent wrapper)
        while (op is IParenthesizedOperation paren) op = paren.Operand;
        if (_exprDispatch.TryGetValue(op.Kind, out var h))
            try { return h.Handle(op); } catch (System.Exception ex) { throw TagLocation(ex, op); }
        throw new NotSupportedException(
            $"Unsupported expression: {op.Kind} ({op.GetType().Name})");
    }

    // Augment an emit-time exception with the source location + a snippet of the OFFENDING operation, so a
    // failure deep in a child (e.g. "VisitExpression called with null operation") is reported at the nearest
    // enclosing construct that has syntax, not at the context-free throw site. Tags only once (innermost frame)
    // via Exception.Data so outer dispatch frames re-throw the located exception unchanged.
    static System.Exception TagLocation(System.Exception ex, IOperation op)
    {
        if (ex.Data.Contains("usugar_located") || op?.Syntax == null) return ex;
        var span = op.Syntax.GetLocation().GetLineSpan();
        var where = $"{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}";
        var snippet = op.Syntax.ToString().Replace("\r", " ").Replace("\n", " ");
        if (snippet.Length > 100) snippet = snippet.Substring(0, 100) + "…";
        var wrapped = new NotSupportedException($"{ex.Message}  [at ({where}) {op.Kind}: `{snippet}`]", ex);
        wrapped.Data["usugar_located"] = true;
        return wrapped;
    }

    // ── Static collection helpers ──

    // CA call-graph rewrite (M4): the stable, traversal-independent ordinal key. GetDocumentationCommentId
    // is a unique deterministic per-symbol id ("M:Ns.Type.Method(args)" / "T:Ns.Type"); OriginalDefinition
    // normalizes generic specs to their definition (the graph is def-keyed). Ordinal string comparison keeps
    // it culture-independent.
    internal static string StableOrdinalKey(ISymbol s)
        => s.OriginalDefinition.GetDocumentationCommentId() ?? s.OriginalDefinition.ToDisplayString();

    /// <summary>B81: the instance field-/auto-property-INITIALIZER value operations of a v1 class, in
    /// declaration order — the reach-side twin of HandlerBase.EmitInstanceFieldInitializers (which emits
    /// them at mint). Static/const fields are excluded (const folds; statics reject). Used to Phase-1-walk
    /// a minted class's initializer expressions for foreign-static / struct-member collection.</summary>
    internal IEnumerable<IOperation> EnumerateClassFieldInitOps(INamedTypeSymbol classTy)
    {
        foreach (var member in classTy.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
            ISymbol initHolder = f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol prop ? prop : f;
            var syntax = initHolder.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var initValue = syntax switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd => vd.Initializer?.Value,
                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd => pd.Initializer?.Value,
                _ => null,
            };
            if (initValue == null) continue;
            var initOp = _compilation.GetSemanticModel(initValue.SyntaxTree).GetOperation(initValue);
            if (initOp != null) yield return initOp;
        }
    }

    // B46 (wave-14 r4): a foreign-static call whose containing type still carries an OPEN type
    // parameter (Helper<U>.Boost seen in the SHARED body of a generic struct/method, U unbound) has
    // no single monomorphization here — collecting it would register a phantom open CFunction, exactly
    // the shape IsCollectibleStructMember skips. It is registered on demand at its closed call site
    // (InvocationHandler's foreign-static-on-generic arm). Genuinely closed foreign statics (incl.
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
    // entry registers a SECOND, dead (never actually dispatched — SubstituteMethodTypeArgs always
    // re-closes real call sites to the live spec) CFunction that corrupts the definition-keyed
    // recursion/spill bookkeeping (VM-proven: a self-recursive generic struct method returned 0
    // instead of the CLR's 6). Skip collecting through the open form; the real call sites (outer
    // construction/invocation, always concretely typed) already reach every instantiation this
    // collector needs (an internal-only self/sibling reference is instead resolved on demand via
    // HandlerBase.ResolveStructMember, wave-14).
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
    // containing-type shape above. Left collectible it registers a dead second CFunction whose body is
    // emitted mapless (no isSpec map) → `new T[]` → bogus `TArray` (the closed Lf<int> spec is registered
    // separately via SubstituteMethodTypeArgs + on-demand RegisterGenericSpecialization). Reject it too.
    internal static bool IsCollectibleStructMember(IMethodSymbol m)
        => m != null
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
    ClassCompilePlan _plan;
    ReachableBodies _reach = new();

    IOperation GetMethodBodyOperation(IMethodSymbol method)
    {
        var syntaxRef = method?.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;
        var syntax = syntaxRef.GetSyntax();
        return _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
    }

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
        if (IsExternNamespace(resolved.ContainingType.ContainingNamespace)) return false;
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

    static bool IsExternNamespace(INamespaceSymbol ns)
    {
        if (ns == null || ns.IsGlobalNamespace) return false;
        var root = ns;
        while (root.ContainingNamespace != null && !root.ContainingNamespace.IsGlobalNamespace)
            root = root.ContainingNamespace;
        return root.Name is "UnityEngine" or "VRC" or "TMPro" or "System";
    }
}
