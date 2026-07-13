using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

public class UasmEmitter
{
    readonly EmitContext _ctx;
    readonly Dictionary<OperationKind, IOperationHandler> _stmtDispatch;
    readonly Dictionary<OperationKind, IExpressionHandler> _exprDispatch;

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

    public UasmEmitter(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner = null)
    {
        _ownsPlanner = planner == null;
        _ctx = new EmitContext(compilation, classSymbol, planner ?? new LayoutPlanner(compilation));

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
    string GetUdonType(ITypeSymbol type) => ExternResolver.GetUdonTypeName(type, _typeParamMap);
    string GetArrayType(IArrayTypeSymbol arrType) => GetUdonType(arrType);
    string GetArrayElemType(IArrayTypeSymbol arrType)
    {
        var t = GetArrayType(arrType);
        return t.Substring(0, t.Length - "Array".Length);
    }

    // ── CoreBuilder bridge helpers (old IrBuilder API → CoreBuilder) ──

    CLeaf BridgeLoad(string fieldName, string type) => _builder.LoadField(fieldName, type);
    void BridgeStore(string fieldName, CLeaf value) => _builder.EmitStoreField(fieldName, value);
    CLeaf BridgeCallExtern(string retType, string sig, CLeaf[] args)
        => _builder.ExternCall(sig, new List<CLeaf>(args), retType);
    void BridgeCallExternVoid(string sig, CLeaf[] args)
        => _builder.EmitExternVoid(sig, new List<CLeaf>(args));
    CLeaf BridgeCallInternal(CFunction func, CLeaf[] args)
    {
        var retType = func.ReturnType ?? "SystemVoid";
        var call = _builder.InternalCall(func.Name, new List<CLeaf>(args), retType);
        if (retType == "SystemVoid") { _builder.EmitExprStmt(call); return null; }
        return call;
    }
    CLeaf BridgeConstInt(int value) => _builder.Const(value, "SystemInt32");

    // ── Emit ──

    /// <summary>Access to the Core IR module for debugging and testing.</summary>
    public CModule Module => _module;

    /// <summary>Test/tooling accessors for the Stage 2 M1 CaptureScopeAnalysis (built in <see cref="Emit"/>,
    /// consumed by nothing yet — see EmitContext.CaptureScope).</summary>
    public CaptureScopeAnalysis CaptureScope => _ctx.Closures.CaptureScope;
    public Compilation Compilation => _ctx.Compilation;
    public INamedTypeSymbol ClassSymbol => _ctx.ClassSymbol;

    /// <summary>Called after handler emission, before optimization. Set for IR debugging.</summary>
    public Action<string, CModule> OnIrPass;

    public string Emit()
    {
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
        _ctx.ClassTypes.Seed(plan.Reach.MintedClasses); // CA-v2b-1: typeobj registry
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
        return new ClassCompilePlanBuilder(
            ComputeMethods,
            BuildReachableBodies,
            () => _fieldInitOps.Select(fi => fi.initOp)).Build();
    }

    void SetReflectionValues()
    {
        var typeName = _classSymbol.ToDisplayString();
        long typeId = ComputeTypeId(typeName);
        _ctx.Storage.DeclareField(EmitContext.ReflTypeIdField, "SystemInt64", defaultValue: typeId);
        _ctx.Storage.DeclareField(EmitContext.ReflTypeNameField, "SystemString", defaultValue: typeName);

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

            var udonType = GetUdonType(member.Type);
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
            _ctx.Storage.DeclareField(member.Name, udonType, flags, constValue, syncMode);

            // Aggregate (struct/tuple) field with NO explicit initializer → C# default-initializes it to a
            // zeroed struct. In the object[] emulation that requires a fresh default array; without it the heap
            // var stays null and `f.x = …` faults (NRE on __Set__). Reference-type/array fields stay null (correct).
            if (syntaxRef?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: not null }
                && member.Type is INamedTypeSymbol aggFieldType && EmitPolicy.IsAggregateType(aggFieldType))
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
                _ctx.Storage.DeclareField($"__old_{member.Name}", udonType);
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
            var udonType = GetUdonType(prop.Type);
            var flags = FieldFlags.None;
            if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            _ctx.Storage.DeclareField(prop.Name, udonType, flags,
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

                var udonType = GetUdonType(member.Type);
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

                _ctx.Storage.DeclareField(member.Name, udonType, baseFlags, constValue, baseSyncMode);

                var baseFcbAttr = member.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
                if (baseFcbAttr != null && baseFcbAttr.ConstructorArguments.Length > 0
                    && baseFcbAttr.ConstructorArguments[0].Value is string basePropName)
                {
                    _fieldChangeCallbacks[member.Name] = basePropName;
                    _ctx.Storage.DeclareField($"__old_{member.Name}", udonType);
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
                            _ctx.Storage.DeclareField(BaseAutoPropBackingName(prop), GetUdonType(prop.Type), FieldFlags.None,
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
                var udonType = GetUdonType(prop.Type);
                var flags = FieldFlags.None;
                if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
                declaredMemberSyms[prop.Name] = prop;
                _ctx.Storage.DeclareField(prop.Name, udonType, flags,
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
            ? GetUdonType(nt.EnumUnderlyingType)
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

        var udonType = GetUdonType(member.Type);
        object constValue = null;
        if (initOp != null)
        {
            constValue = TryEvaluateFieldInitForHeap(initOp, member.Type);
            if (constValue == null)
                _staticFieldInitOps.Add((member.Name, initOp, member.Type)); // static tier — §3.6
        }
        _ctx.Storage.DeclareField(member.Name, udonType, FieldFlags.None, constValue);

        if (initOp == null && member.Type is INamedTypeSymbol aggFieldType && EmitPolicy.IsAggregateType(aggFieldType))
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

        _ctx.Storage.DeclareField(member.Name, "SystemObjectArray", FieldFlags.None);
        _ctx.Synthetics.DelegateFields.Add(member.Name);

        // Declare the signature-keyed __dlgc_ convention vars for this delegate signature (§3.2).
        var invoke = delegateType.DelegateInvokeMethod;
        // envName is intentionally ignored here: a delegate FIELD declaration is not a dispatch site
        // or capturing bridge, so declaring __dlgc_{sig}__env unconditionally would break the
        // capture-free byte invariant (§1.3). It is declared on-first-use at the dispatch/bridge site.
        var (convArgs, convRet, _) = HandlerBase.GetConventionFieldNames(delegateType);
        for (int ci = 0; ci < convArgs.Length; ci++)
            _ctx.Storage.TryDeclareVar(convArgs[ci], ExternResolver.GetUdonTypeName(invoke.Parameters[ci].Type));
        if (convRet != null)
            _ctx.Storage.TryDeclareVar(convRet, ExternResolver.GetUdonTypeName(invoke.ReturnType));

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
                _ctx.Storage.DeclareVar(ml.ParamIds[i], GetUdonType(method.Parameters[i].Type));
                paramVarIds[i] = ml.ParamIds[i];
            }
            _methodParamVarIds[method] = paramVarIds;
            foreach (var pid in paramVarIds) func.ParamFieldNames.Add(pid);

            // Declare return var(s) from unified Returns
            if (ml.Returns.Count > 0)
            {
                foreach (var ret in ml.Returns)
                    _ctx.Storage.DeclareVar(ret.Id, ret.UdonType);

                if (ml.Returns.Count == 1)
                    func.ReturnType = ml.Returns[0].UdonType;
                else
                    func.ReturnType = "SystemVoid"; // tuple: no single return value

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
        var foreignStatics = plan.ForeignStatics;
        foreach (var fm in foreignStatics)
        {
            EmitPolicy.RejectInParameters(fm); // round-7 follow-up [Q3]

            // B70 root 1 (A14/A15): a static method on a CLOSED generic struct (GS14<bool>.Run) is registered
            // here, but this loop — unlike the struct-instance and base-instance loops — never seeded
            // FirstGenericSpec. A nested LF then could not reach the enclosing struct's closed T (the
            // closureBindings walk at EmitMethod misses the owner), so `new T[]` emitted a bogus TArray. Seed
            // it the same way the struct-methods loop does (including the two-instantiation aliasing guard,
            // which GS15<int>/GS15<string> exercises).
            if (fm.ContainingType.IsGenericType && !fm.IsDefinition)
            {
                var fmGenericDef = fm.OriginalDefinition;
                if (!_ctx.Generics.FirstSpecByDefinition.ContainsKey(fmGenericDef))
                    _ctx.Generics.FirstSpecByDefinition[fmGenericDef] = fm;
            }

            var slot = _ctx.Methods.Register(fm, i => i.ToString());
            var idx = slot.Index;
            var funcName = $"__{idx}_{SanitizeId(fm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[fm] = func;

            var fmParamIds = new string[fm.Parameters.Length];
            for (int pi = 0; pi < fm.Parameters.Length; pi++)
            {
                var param = fm.Parameters[pi];
                var paramId = $"__{idx}_{param.Name}__param";
                _ctx.Storage.DeclareVar(paramId, GetUdonType(param.Type));
                fmParamIds[pi] = paramId;
            }
            _methodParamVarIds[fm] = fmParamIds;
            foreach (var pid in fmParamIds) func.ParamFieldNames.Add(pid);

            if (!fm.ReturnsVoid)
            {
                var retType = GetUdonType(fm.ReturnType);
                var retId = $"__{idx}_{SanitizeId(fm.Name)}__ret";
                func.ReturnType = retType;
                func.ReturnSlots.Add(new ReturnSlot(retId, retType));
                _methodReturns[fm] = new[] { new ReturnSlot(retId, retType) };
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
                var genericDef = sm.OriginalDefinition;
                if (!_ctx.Generics.FirstSpecByDefinition.ContainsKey(genericDef))
                    _ctx.Generics.FirstSpecByDefinition[genericDef] = sm;

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
                var receiverId = $"__{idx}_this__param";
                _ctx.Storage.DeclareVar(receiverId, "SystemObjectArray");
                func.ParamFieldNames.Add(receiverId);
            }

            var smParamIds = new string[sm.Parameters.Length];
            for (int pi = 0; pi < sm.Parameters.Length; pi++)
            {
                var p = sm.Parameters[pi];
                var pid = $"__{idx}_{p.Name}__param";
                _ctx.Storage.DeclareVar(pid, GetUdonType(p.Type));
                smParamIds[pi] = pid;
                func.ParamFieldNames.Add(pid);
            }
            _methodParamVarIds[sm] = smParamIds; // Ordinal-indexed; receiver tracked separately

            if (!sm.ReturnsVoid) // ctors are void (mutate in place); instance methods may return
            {
                var retType = GetUdonType(sm.ReturnType);
                var retId = $"__{idx}_{SanitizeId(sm.Name)}__ret";
                func.ReturnType = retType;
                func.ReturnSlots.Add(new ReturnSlot(retId, retType));
                _methodReturns[sm] = new[] { new ReturnSlot(retId, retType) };
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
            if (bm.IsGenericMethod && !bm.IsDefinition)
            {
                var bmDef = bm.OriginalDefinition;
                if (!_ctx.Generics.FirstSpecByDefinition.ContainsKey(bmDef))
                    _ctx.Generics.FirstSpecByDefinition[bmDef] = bm;
            }
            var slot = _ctx.Methods.Register(bm, i => i.ToString());
            var idx = slot.Index;
            var funcName = $"__{idx}_{SanitizeId(bm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[bm] = func;

            var bmParamIds = new string[bm.Parameters.Length];
            for (int pi = 0; pi < bm.Parameters.Length; pi++)
            {
                var param = bm.Parameters[pi];
                var paramId = $"__{idx}_{param.Name}__param";
                _ctx.Storage.DeclareVar(paramId, GetUdonType(param.Type));
                bmParamIds[pi] = paramId;
            }
            _methodParamVarIds[bm] = bmParamIds;
            foreach (var pid in bmParamIds) func.ParamFieldNames.Add(pid);

            if (!bm.ReturnsVoid)
            {
                var retType = GetUdonType(bm.ReturnType);
                var retId = $"__{idx}_{SanitizeId(bm.Name)}__ret";
                func.ReturnType = retType;
                func.ReturnSlots.Add(new ReturnSlot(retId, retType));
                _methodReturns[bm] = new[] { new ReturnSlot(retId, retType) };
            }
        }

        // Analyze the internal-call graph for recursion cycles (after all methods are registered).
        BuildRecursionInfo();

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
        EmitInterfaceBridges();

        // Emit delegate bridge exports
        EmitDelegateBridges();

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
        EmitPendingDelegateBridges();
        EmitPendingReceiverBridges();

        // Variance design (2026-07-04 §2.2/§2.3) T-M2: sig adapters (B-1) + wrapper-with-payload
        // bridges (B-2), for every variant method-group binding / third-party-variant hinge / variant
        // delegate-value conversion registered in this class. A class with no variance emits neither —
        // single-cast golden untouched (§5 gate).
        EmitPendingSigAdapterBridges();
        EmitPendingWrapperBridges();

        // Multicast design (2026-07-03 §1) A-M1: per-sig synthetic combine/remove helpers + fan-out
        // bridge, for every sig a `+=`/`-=` site registered in this class (RegisterMulticastSig). A
        // class with no delegate compound assignment emits none of this — single-cast golden is
        // untouched (§6 gate). Reentrancy graph-node registration for the fan-out is A-M3 scope (§1.6),
        // deliberately not wired here.
        EmitMulticastSynthetics();
        EmitEnumToStringSynthetics();

        // §5.5 (graft #2): now that every capturing bridge is registered, assert each has a graph node.
        VerifyBridgeTargetsAreNodes();
    }

    // ── Interface Bridges ──

    void EmitInterfaceBridges()
    {
        var bridges = _planner.ComputeBridges(_classSymbol);
        foreach (var (ifaceMethod, ifaceMl, implMethod, classMl) in bridges)
        {
            // Declare interface param/return variables
            for (int i = 0; i < ifaceMethod.Parameters.Length; i++)
            {
                if (ifaceMl.ParamIds[i] != classMl.ParamIds[i])
                {
                    var udonType = GetUdonType(ifaceMethod.Parameters[i].Type);
                    _ctx.Storage.TryDeclareVar(ifaceMl.ParamIds[i], udonType);
                }
            }
            if (ifaceMl.ReturnId != null && ifaceMl.ReturnId != classMl.ReturnId)
            {
                var retType = GetUdonType(ifaceMethod.ReturnType);
                _ctx.Storage.TryDeclareVar(ifaceMl.ReturnId, retType);
            }

            // Export the bridge under the canonical interface-qualified name (unique vs class methods and
            // other bridges); the function name carries it too so each bridge gets a distinct __body label.
            var bridgeName = LayoutPlanner.InterfaceDispatchName(ifaceMethod, ifaceMl);
            var bridgeFunc = _module.AddFunction($"__bridge_{bridgeName}", bridgeName);
            _builder.SetFunction(bridgeFunc);

            // Class implementation: the planner already resolved it through the override chain
            // (wave-9 round-2 [W5] — FindImplementationForInterfaceMember returns the chain ROOT,
            // which the [W4] folding removes from both the layout and _methodFunctions, so a
            // re-resolution here would miss the registered chain leaf and throw).
            if (implMethod == null || !_methodFunctions.TryGetValue(implMethod, out var classFunc))
                throw new InvalidOperationException(
                    $"Interface bridge for '{ifaceMl.ExportName}': "
                  + $"no function found for implementation of '{ifaceMethod.Name}'.");

            // Load interface params
            var args = new List<CLeaf>();
            for (int i = 0; i < ifaceMethod.Parameters.Length; i++)
            {
                var paramType = GetUdonType(ifaceMethod.Parameters[i].Type);
                args.Add(BridgeLoad(ifaceMl.ParamIds[i], paramType));
            }

            // Call class implementation
            var result = BridgeCallInternal(classFunc, args.ToArray());

            // Copy return value to interface return field if needed
            if (result != null && ifaceMl.ReturnId != null
                && classMl.ReturnId != null && ifaceMl.ReturnId != classMl.ReturnId)
            {
                BridgeStore(ifaceMl.ReturnId, result);
            }

            _builder.EmitReturn();
        }
    }

    // ── Delegate Bridge Exports ──

    void EmitDelegateBridges()
    {
        var classLayout = _planner.GetLayout(_classSymbol);
        foreach (var (method, bridge) in classLayout.DelegateBridges)
        {
            if (!_methodFunctions.TryGetValue(method, out var realFunc)) continue;
            // Tuple returns (design 2026-07-04 §1.2): the bridge InternalCalls the real method and
            // stores its result straight into conv-ret — no special casing, since a tuple return is
            // already the same single SystemObjectArray aggregate slot a struct return uses.

            // §3.4-1 NOTE: ValidateNoRefOutParams deliberately does NOT run here. This loop emits a
            // speculative bridge for EVERY non-event user method (planner DelegateBridges), so a throw
            // would reject any class merely CONTAINING a ref/out method, and a skip would change the
            // struct_ref_param byte-identity sentinel. A ref/out method's bridge is unreachable as a
            // delegate target: ValidateDelegateBinding rejects every creation and EmitDelegateDispatch
            // re-validates at every dispatch site, so no USugar-built bundle can ever name it.

            // Build canonical convention key using the unified ABI builder (design §3.2)
            var sigPart = DelegateAbi.BuildSigPart(method);

            // Declare convention fields (if not already declared)
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = ExternResolver.GetUdonTypeName(method.Parameters[i].Type);
                _ctx.Storage.TryDeclareVar(DelegateAbi.ConvArgName(sigPart, i), argType);
            }
            if (!method.ReturnsVoid)
            {
                var retType = ExternResolver.GetUdonTypeName(method.ReturnType);
                _ctx.Storage.TryDeclareVar(DelegateAbi.ConvRetName(sigPart), retType);
            }

            // Build bridge function
            var bridgeFunc = _module.AddFunction(bridge.BridgeExportName, bridge.BridgeExportName);

            var prevFunc = _builder.CurrentFunction;
            _builder.SetFunction(bridgeFunc);

            // Copy convention fields → real param fields, then call real method
            var callArgs = new List<CLeaf>();
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = ExternResolver.GetUdonTypeName(method.Parameters[i].Type);
                var convName = DelegateAbi.ConvArgName(sigPart, i);
                callArgs.Add(BridgeLoad(convName, argType));
            }

            var retTypeStr = method.ReturnsVoid ? "SystemVoid" : ExternResolver.GetUdonTypeName(method.ReturnType);
            var callResult = _builder.InternalCall(realFunc.Name, callArgs, retTypeStr);

            if (!method.ReturnsVoid)
            {
                var convRet = DelegateAbi.ConvRetName(sigPart);
                BridgeStore(convRet, callResult);
            }
            else
            {
                _builder.EmitExprStmt(callResult);
            }

            _builder.EmitReturn();

            if (prevFunc != null)
                _builder.SetFunction(prevFunc);
        }
    }

    // ── Pending Delegate Bridges (for hoisted lambdas/local functions) ──

    /// <summary>Declare a sig's `__dlgc_` conv arg/ret fields — the preamble shared by every delegate
    /// bridge flavor (plain bridge, sig adapter, wrapper, fan-out). Returns the ret Udon type string,
    /// or null when the sig is void.</summary>
    string DeclareConvSigFields(string sigPart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
        => DeclareConvSigFields(sigPart, invoke, typeParamMap, out _);

    /// <summary>Overload exposing the per-parameter Udon type array (fan-out/wrapper need it afterward
    /// to allocate typed argument slots) alongside the same declaration preamble.</summary>
    string DeclareConvSigFields(string sigPart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap, out string[] argTypes)
    {
        argTypes = new string[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
        {
            argTypes[i] = ExternResolver.GetUdonTypeName(invoke.Parameters[i].Type, typeParamMap);
            _ctx.Storage.TryDeclareVar(DelegateAbi.ConvArgName(sigPart, i), argTypes[i]);
        }
        string retType = invoke.ReturnsVoid ? null : ExternResolver.GetUdonTypeName(invoke.ReturnType, typeParamMap);
        if (retType != null) _ctx.Storage.TryDeclareVar(DelegateAbi.ConvRetName(sigPart), retType);
        return retType;
    }

    void EmitPendingDelegateBridges()
    {
        var emitted = new HashSet<string>();
        foreach (var (method, bridgeExportName, resolvedMap) in _ctx.Synthetics.DelegateBridges)
        {
            if (!emitted.Add(bridgeExportName)) continue;
            // SS2B: per-spec closure bridges resolve by bridge name (a bare symbol cannot name a spec).
            if (!_ctx.Synthetics.ClosureBridgeFuncs.TryGetValue(bridgeExportName, out var realFunc)
                && !_methodFunctions.TryGetValue(method, out realFunc)) continue;

            // §3.4-1 conv-var declaration side check. Pending bridges are delegate-originated by
            // construction (creation already validated), but a future registration path must stay loud.
            DelegateAbi.ValidateNoRefOutParams(method);

            var targetRetTypeStr = method.ReturnsVoid ? "SystemVoid" : ExternResolver.GetUdonTypeName(method.ReturnType, resolvedMap);
            EmitDelegateBridgeBody(bridgeExportName, method, resolvedMap, realFunc, targetRetTypeStr, method);
        }
    }

    // ── Variance Sig Adapter Bridges (design 2026-07-04 §2.2, B-1) ──
    //
    // A same-program variant method-group binding mints one of these instead of the plain bridge:
    // reads sig-S conv args (the DELEGATE's own declared types), InternalCalls the real target (zero
    // conversion — C# only permits reference-conversion variance here, P2-verified), stores the result
    // into the sig-S conv-ret. Shares EmitDelegateBridgeBody with EmitPendingDelegateBridges; the ONLY
    // differences are which signature drives the conv-var names/types (sig-S/delegateInvoke here, the
    // target method's own sig there — the same symbol as the plain bridge's target) and the synthesized
    // bridge name (DelegateAbi.SigAdapterName).

    // MG auto-wrap (design 2026-07-11 v2): receiver-bridge drain. A receiver-bridge is the 5th
    // bridge flavor — it reads the member's conv args PLUS the staged conv env as the RECEIVER
    // (leading param0), guarded only by env-null (a class aggregate's slot 0 is reserved/null, so
    // the closure bridge's KindTag check is inapplicable). Null receiver = LogError + default —
    // C# throws NRE at BIND time, USugar reports at DISPATCH time (documented timing deviation;
    // Udon has no exceptions).
    void EmitPendingReceiverBridges()
    {
        var emitted = new HashSet<string>();
        foreach (var (member, bridgeName) in _ctx.Synthetics.ReceiverBridges)
        {
            if (!emitted.Add(bridgeName)) continue;
            DelegateAbi.ValidateNoRefOutParams(member);
            var func = _ctx.Methods.Functions[member];
            var retTypeStr = member.ReturnsVoid ? "SystemVoid" : GetUdonType(member.ReturnType);
            EmitReceiverBridgeBody(bridgeName, member, func, retTypeStr);
        }
    }

    void EmitReceiverBridgeBody(string bridgeName, IMethodSymbol member, CFunction targetFunc, string targetRetTypeStr)
    {
        var sigPart = DelegateAbi.BuildSigPart(member, null);
        var retType = DeclareConvSigFields(sigPart, member, null);

        var bridgeFunc = _module.AddFunction(bridgeName, bridgeName);
        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(bridgeFunc);

        var envConv = DelegateAbi.ConvEnvName(sigPart);
        _ctx.Storage.TryDeclareVar(envConv, EnvEmit.EnvType);
        var recvLeaf = BridgeLoad(envConv, EnvEmit.EnvType);

        var callArgs = new List<CLeaf> { recvLeaf }; // CA-M1: receiver is the member's param0
        for (int i = 0; i < member.Parameters.Length; i++)
            callArgs.Add(BridgeLoad(DelegateAbi.ConvArgName(sigPart, i),
                ExternResolver.GetUdonTypeName(member.Parameters[i].Type)));

        var convRet = retType != null ? DelegateAbi.ConvRetName(sigPart) : null;
        var recvOk = BridgeCallExtern("SystemBoolean",
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            new[] { recvLeaf, (CLeaf)_builder.Const(null, "SystemObject") });
        _builder.EmitIf(recvOk,
            _ =>
            {
                var callResult = _builder.InternalCall(targetFunc.Name, callArgs, targetRetTypeStr);
                if (convRet != null) BridgeStore(convRet, callResult);
                else _builder.EmitExprStmt(callResult);
            },
            _ =>
            {
                BridgeCallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                    new[] { (CLeaf)_builder.Const(
                        $"USugar: null receiver — invoked a method-group delegate whose receiver is null ({member.ContainingType.Name}.{member.Name})",
                        "SystemString") });
                if (convRet != null)
                    BridgeStore(convRet, InvocationHandler.DefaultConst(_builder, retType));
            });

        _builder.EmitReturn();
        _builder.SetFunction(prevFunc);
    }

    void EmitPendingSigAdapterBridges()
    {
        var emitted = new HashSet<string>();
        foreach (var (targetMethod, delegateInvoke, adapterName, resolvedMap) in _ctx.Synthetics.SigAdapterBridges)
        {
            if (!emitted.Add(adapterName)) continue;
            // SS2B: per-spec closure adapters resolve by adapter name (a bare symbol cannot name a spec).
            if (!_ctx.Synthetics.ClosureBridgeFuncs.TryGetValue(adapterName, out var realFunc)
                && !_methodFunctions.TryGetValue(targetMethod, out realFunc)) continue;

            DelegateAbi.ValidateNoRefOutParams(targetMethod);

            var targetRetTypeStr = targetMethod.ReturnsVoid
                ? "SystemVoid" : ExternResolver.GetUdonTypeName(targetMethod.ReturnType, resolvedMap);
            EmitDelegateBridgeBody(adapterName, delegateInvoke, resolvedMap, realFunc, targetRetTypeStr, targetMethod);
        }
    }

    /// <summary>Shared protocol skeleton for a delegate bridge body (plain bridge and sig adapter
    /// alike, design 2026-07-04 §2.2): declare the sig's conv arg/ret fields, stage them into
    /// <paramref name="targetFunc"/>'s InternalCall, store the result back into the conv-ret — with
    /// the capturing-closure __envp forwarding + env-null guard when <paramref
    /// name="closureCheckMethod"/> is a capturing closure. <paramref name="sigMethod"/> drives the
    /// conv-var names/types (the delegate's OWN declared signature for a sig adapter; the target
    /// method's own signature — the same symbol as <paramref name="closureCheckMethod"/> — for a plain
    /// bridge). <paramref name="targetRetTypeStr"/> is the actual InternalCall's return type, which can
    /// differ from sigMethod's for a sig adapter (Stage 1.75 §2's reference-variance guarantee is what
    /// makes that safe to forward with zero conversion).</summary>
    void EmitDelegateBridgeBody(string bridgeName, IMethodSymbol sigMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> resolvedMap, CFunction targetFunc,
        string targetRetTypeStr, IMethodSymbol closureCheckMethod)
    {
        var sigPart = DelegateAbi.BuildSigPart(sigMethod, resolvedMap);
        var retType = DeclareConvSigFields(sigPart, sigMethod, resolvedMap);

        var bridgeFunc = _module.AddFunction(bridgeName, bridgeName);
        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(bridgeFunc);

        // Copy convention fields → real param fields, then call real method
        var callArgs = new List<CLeaf>();
        for (int i = 0; i < sigMethod.Parameters.Length; i++)
        {
            var argType = ExternResolver.GetUdonTypeName(sigMethod.Parameters[i].Type, resolvedMap);
            callArgs.Add(BridgeLoad(DelegateAbi.ConvArgName(sigPart, i), argType));
        }

        var convRet = retType != null ? DelegateAbi.ConvRetName(sigPart) : null;

        void EmitBridgeCall(List<CLeaf> args)
        {
            var callResult = _builder.InternalCall(targetFunc.Name, args, targetRetTypeStr);
            if (convRet != null) BridgeStore(convRet, callResult);
            else _builder.EmitExprStmt(callResult);
        }

        // Stage 2 §5.1: a CAPTURING target's bridge consumes the staged env global as the trailing
        // arg (positional copy-in binds it to the real function's __envp param field) under env
        // null/tag guards. A hand-rolled object[] or mismatched delegate bundle must LogError +
        // default, not fault or silently read garbage.
        if (_ctx.Closures.CaptureScope != null && _ctx.Closures.CaptureScope.IsCapturingClosure(closureCheckMethod))
        {
            var envConv = DelegateAbi.ConvEnvName(sigPart);
            _ctx.Storage.TryDeclareVar(envConv, EnvEmit.EnvType);
            var envLeaf = BridgeLoad(envConv, EnvEmit.EnvType);
            callArgs.Add(envLeaf);
            var envOk = BridgeCallExtern("SystemBoolean",
                "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                new[] { envLeaf, _builder.Const(null, "SystemObject") });
            _builder.EmitIf(envOk,
                _ =>
                {
                    var envKind = BridgeCallExtern("SystemString",
                        ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                        new[] { envLeaf, _builder.Const(EnvAbi.Kind, "SystemInt32") });
                    var envKindOk = BridgeCallExtern("SystemBoolean",
                        "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
                        new[] { envKind, _builder.Const(EnvAbi.KindTag, "SystemString") });
                    _builder.EmitIf(envKindOk,
                        _ => EmitBridgeCall(callArgs),
                        _ =>
                        {
                            BridgeCallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                                new[] { (CLeaf)_builder.Const(
                                    $"USugar: invalid closure environment — invoked a captured delegate with a non-env payload ({closureCheckMethod.Name})",
                                    "SystemString") });
                            if (convRet != null)
                                BridgeStore(convRet, InvocationHandler.DefaultConst(_builder, retType));
                        });
                },
                _ =>
                {
                    BridgeCallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                        new[] { (CLeaf)_builder.Const(
                            $"USugar: missing closure environment — invoked a captured delegate whose bundle carries no env ({closureCheckMethod.Name})",
                            "SystemString") });
                    if (convRet != null)
                        BridgeStore(convRet, InvocationHandler.DefaultConst(_builder, retType));
                });
        }
        else
        {
            EmitBridgeCall(callArgs);
        }

        _builder.EmitReturn();

        if (prevFunc != null)
            _builder.SetFunction(prevFunc);
    }

    // ── Wrapper-with-Payload Bridges (design 2026-07-04 §2.3, B-2) ──
    //
    // Per sig registered via RegisterWrapperSig: a sig-S bridge that receives an INNER bundle via
    // slot[3] (bridge-private payload — same principle as a capturing bridge's env record or a
    // multicast fan-out's invocation list) and fires it through the EXISTING unified dispatch (the
    // fan-out's one-element form, InvocationHandler.EmitFanoutElementDispatch — unconditionally
    // Reentrant, A-M3 inheritance, §2.3). INV-A (§2.5/§6): sig-S conv args snapshot to LOCAL SLOTS
    // before the inner dispatch; conv-ret stores from the LOCAL result, never a post-call conv reread.

    void EmitPendingWrapperBridges()
    {
        foreach (var (wrapperName, (outerInvoke, innerInvoke, typeParamMap)) in _ctx.Synthetics.WrapperSigs)
            EmitWrapperBridge(wrapperName, outerInvoke, innerInvoke, typeParamMap);
    }

    void EmitWrapperBridge(string wrapperName, IMethodSymbol outerInvoke, IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        // OUTER protocol (sig-S, outerInvoke): what callers holding the declared delegate type stage
        // into / read out of — the wrapper's OWN conv-var contract. INNER protocol (sig-T, innerInvoke):
        // the wrapped bundle's OWN native signature (never sig-S, unless they happen to coincide) — the
        // inner EmitFanoutElementDispatch call derives ITS OWN conv-var names from innerInvoke, distinct
        // from the outer ones read/written here.
        var outerSigPart = DelegateAbi.BuildSigPart(outerInvoke, typeParamMap);

        // §1.6/A-M3 reentrancy: unconditionally reentrant, same reasoning as the fan-out bridge (any
        // sig-S bundle's [1]/[2] can point at this wrapper by construction).
        _ctx.Storage.EnsureRecursionStack();

        var retType = DeclareConvSigFields(outerSigPart, outerInvoke, typeParamMap, out var argTypes);
        _ctx.Storage.TryDeclareVar(DelegateAbi.ConvEnvName(outerSigPart), EnvEmit.EnvType);

        var wrapperFunc = _module.AddFunction(wrapperName, wrapperName);
        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(wrapperFunc);

        // INV-A: snapshot the inner bundle + every OUTER conv arg to LOCAL SLOTS before dispatching.
        var innerSlot = _ctx.Builder.AllocScratch("SystemObjectArray");
        _builder.EmitAssign(innerSlot, BridgeLoad(DelegateAbi.ConvEnvName(outerSigPart), "SystemObjectArray"));

        var argSlots = new int[outerInvoke.Parameters.Length];
        for (int i = 0; i < outerInvoke.Parameters.Length; i++)
        {
            argSlots[i] = _ctx.Builder.AllocScratch(argTypes[i]);
            _builder.EmitAssign(argSlots[i], BridgeLoad(DelegateAbi.ConvArgName(outerSigPart, i), argTypes[i]));
        }
        var argLeaves = new CLeaf[argSlots.Length];
        for (int i = 0; i < argSlots.Length; i++) argLeaves[i] = _builder.SlotRef(argSlots[i]);

        // Dispatch the INNER bundle using ITS OWN protocol (innerInvoke) — EmitFanoutElementDispatch
        // derives sig-T's conv names from innerInvoke directly (Stage 1.75 §2.3 fix to
        // GetConventionFieldNames' IMethodSymbol overload), matching whatever bridge the inner bundle's
        // Method/Addr actually names. Unlike the fan-out (whose caller pre-declares the SAME sig's
        // conv vars for both outer and inner use, since they coincide), sig-T here is DIFFERENT from
        // sig-S — EmitFanoutElementDispatch assumes its conv vars are already declared (mirroring every
        // other dispatch site's declare-on-first-use discipline), so declare sig-T's here explicitly:
        // THIS program is the "caller" for the inner dispatch (stages args / reads ret), regardless of
        // whether any method of sig-T exists locally.
        var innerSigPart = DelegateAbi.BuildSigPart(innerInvoke, typeParamMap);
        DeclareConvSigFields(innerSigPart, innerInvoke, typeParamMap);
        _ctx.Storage.TryDeclareVar(DelegateAbi.ConvEnvName(innerSigPart), EnvEmit.EnvType);

        var dispatch = new InvocationHandler(_ctx);
        var innerRet = dispatch.EmitFanoutElementDispatch(_builder.SlotRef(innerSlot), innerInvoke, typeParamMap, argLeaves);

        if (retType != null && innerRet != null)
            BridgeStore(DelegateAbi.ConvRetName(outerSigPart), innerRet);

        _builder.EmitReturn();
        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }

    // ── Multicast Delegate Synthetics (design 2026-07-03 §1, A-M1) ──
    //
    // Per sig registered via RegisterMulticastSig (a `+=`/`-=` site in THIS class), emit: the fan-out
    // bridge (§1.2, dispatches invocation-list elements through the existing unified dispatch) and the
    // combine/remove helpers (§1.4, immutable invocation-list rebuild matching Delegate.Combine/Remove).
    // All three are per-CLASS synthetic functions, same emission tier as EmitPendingDelegateBridges —
    // nothing here is a recursion-graph node (§1.6 defers fan-out reentrancy registration to A-M3).

    static readonly string MulticastArrGet = ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject");
    static readonly string MulticastArrSet = ExternResolver.BuildArraySetSignature("SystemObjectArray", "SystemObject");
    static readonly string MulticastArrCtor = ExternResolver.BuildArrayCtorSignature("SystemObjectArray");

    /// <summary>Element-by-element object[] blit (§1.4/§9 open item 1, A-M0 finding): the registry lists
    /// `SystemArray.__Copy` by name, but the real Udon VM assembler this harness targets does not resolve
    /// it (ExternMissing on-device) — the design's own documented fallback ("使えなければ手 loop") applies.
    /// Same semantics as Array.Copy(src, srcStart, dst, dstStart, len), one extra loop per copy.</summary>
    void EmitMulticastArrayBlit(int srcSlot, CLeaf srcStart, int dstSlot, CLeaf dstStart, int lenSlot)
    {
        var kSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitFor(
            _ => _builder.EmitAssign(kSlot, BridgeConstInt(0)),
            () => BridgeCallExtern("SystemBoolean", "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new CLeaf[] { _builder.SlotRef(kSlot), _builder.SlotRef(lenSlot) }),
            _ => _builder.EmitAssign(kSlot, BridgeCallExtern("SystemInt32",
                "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new CLeaf[] { _builder.SlotRef(kSlot), BridgeConstInt(1) })),
            _ =>
            {
                var srcIdx = BridgeCallExtern("SystemInt32", "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new CLeaf[] { srcStart, _builder.SlotRef(kSlot) });
                var dstIdx = BridgeCallExtern("SystemInt32", "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new CLeaf[] { dstStart, _builder.SlotRef(kSlot) });
                var elem = BridgeCallExtern("SystemObject", MulticastArrGet, new CLeaf[] { _builder.SlotRef(srcSlot), srcIdx });
                BridgeCallExternVoid(MulticastArrSet, new CLeaf[] { _builder.SlotRef(dstSlot), dstIdx, elem });
            });
    }

    void EmitMulticastSynthetics()
    {
        foreach (var (sigPart, (invoke, typeParamMap)) in _ctx.Synthetics.MulticastSigs)
        {
            EmitMulticastCombineHelper(sigPart);
            EmitMulticastRemoveHelper(sigPart);
            EmitMulticastFanoutBridge(sigPart, invoke, typeParamMap);
        }
    }

    // B67: one value→name helper per user enum whose ToString/concat/interpolation was reached. The Udon tag
    // of a user enum is its bare underlying integer, so ToString on it prints the number — C# prints the
    // member name. The member list is compile-time known, so emit `string __enumstr_{Enum}(underlying v)`
    // as a value→name chain; the default arm is the underlying .ToString(), which matches C# for a value
    // with no defined member (e.g. (Suit)99 → "99").
    void EmitEnumToStringSynthetics()
    {
        foreach (var enumType in _ctx.Synthetics.EnumToString)
        {
            var helperName = HandlerBase.EnumToStringHelperName(enumType);
            var underlyingUdon = ExternResolver.GetUdonTypeName(enumType.EnumUnderlyingType);
            var vId = $"{helperName}__v";
            var retId = $"{helperName}__ret";
            _ctx.Storage.TryDeclareVar(vId, underlyingUdon);
            _ctx.Storage.TryDeclareVar(retId, "SystemString");

            var func = _module.AddFunction(helperName);
            func.ParamFieldNames.Add(vId);
            func.ReturnType = "SystemString";
            func.ReturnSlots.Add(new ReturnSlot(retId, "SystemString"));

            var prevFunc = _builder.CurrentFunction;
            _builder.SetFunction(func);

            var vLeaf = BridgeLoad(vId, underlyingUdon);
            var eqExtern = $"{underlyingUdon}.__op_Equality__{underlyingUdon}_{underlyingUdon}__SystemBoolean";
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (!member.HasConstantValue) continue;
                var constLeaf = _builder.Const(
                    EmitPolicy.ParseConstValue(underlyingUdon, System.Convert.ToString(
                        member.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)), underlyingUdon);
                var isMatch = BridgeCallExtern("SystemBoolean", eqExtern, new CLeaf[] { vLeaf, constLeaf });
                _builder.EmitIf(isMatch, _ => _builder.EmitReturn(_builder.Const(member.Name, "SystemString")));
            }
            // Default: an undefined value formats as the underlying number (C#-parity).
            _builder.EmitReturn(BridgeCallExtern("SystemString", $"{underlyingUdon}.__ToString__SystemString",
                new CLeaf[] { vLeaf }));

            if (prevFunc != null) _builder.SetFunction(prevFunc);
        }
    }

    /// <summary>Mint a fresh multicast bundle (§1.1/§1.4): a tagged delegate ABI bundle with
    /// Target=this, Method=this sig's fan-out export name, Addr=that bridge's funcaddr
    /// (back-patched CFuncRef, §1.3 addr discipline), Env=the given invocation list.</summary>
    CLeaf EmitMulticastMintBundle(string sigPart, CLeaf listLeaf)
    {
        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);
        var mSlot = _ctx.Builder.AllocScratch("SystemObjectArray");
        var thisType = ExternResolver.GetUdonTypeName(_classSymbol);
        return DelegateAbi.EmitBundleMintToSlot(_builder, mSlot,
            () => BridgeLoad(_ctx.Storage.DeclareThisOnce(thisType), thisType),
            _builder.Const(fanoutName, "SystemString"),
            _builder.FuncRef(fanoutName),
            listLeaf);
    }

    /// <summary>Multicast combine/remove shared operand normalization (§1.4): a multicast operand
    /// unwraps to its invocation list (DelegateAbi.Env); a single-cast operand wraps as its own 1-element
    /// list. The multicast test is a compile-time constant string compare against DelegateAbi.Method — the only
    /// test allowed to distinguish a multicast bundle from a single-cast one (the __dlg_fanout_ prefix
    /// is reserved and can never collide with a real user method/bridge export name).</summary>
    void EmitMulticastFlattenOperand(CLeaf operand, string fanoutName, out int listSlot, out int lenSlot)
    {
        var tag = DelegateAbi.ReadSlot(_builder, operand, DelegateAbi.Method, "SystemString");
        var isMulticast = BridgeCallExtern("SystemBoolean",
            "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
            new CLeaf[] { tag, _builder.Const(fanoutName, "SystemString") });

        var lSlot = _ctx.Builder.AllocScratch("SystemObjectArray");
        var nSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitIf(isMulticast,
            _ =>
            {
                _builder.EmitAssign(lSlot, DelegateAbi.ReadSlot(_builder, operand, DelegateAbi.Env, "SystemObjectArray"));
                _builder.EmitAssign(nSlot, BridgeCallExtern("SystemInt32", "SystemArray.__get_Length__SystemInt32",
                    new CLeaf[] { _builder.SlotRef(lSlot) }));
            },
            _ =>
            {
                _builder.EmitAssign(lSlot, BridgeCallExtern("SystemObjectArray", MulticastArrCtor,
                    new CLeaf[] { BridgeConstInt(1) }));
                BridgeCallExternVoid(MulticastArrSet, new CLeaf[] { _builder.SlotRef(lSlot), BridgeConstInt(0), operand });
                _builder.EmitAssign(nSlot, BridgeConstInt(1));
            });
        listSlot = lSlot;
        lenSlot = nSlot;
    }

    /// <summary>__dlg_combine_{sig}(x, y) (§1.4): null legs, else flatten both operands and concatenate
    /// (two non-null operands always yield |cat| >= 2 → always mints a multicast).</summary>
    void EmitMulticastCombineHelper(string sigPart)
    {
        var helperName = DelegateAbi.MulticastCombineName(sigPart);
        var xId = $"{helperName}__x"; var yId = $"{helperName}__y"; var retId = $"{helperName}__ret";
        _ctx.Storage.TryDeclareVar(xId, "SystemObjectArray");
        _ctx.Storage.TryDeclareVar(yId, "SystemObjectArray");
        _ctx.Storage.TryDeclareVar(retId, "SystemObjectArray");

        var func = _module.AddFunction(helperName);
        func.ParamFieldNames.Add(xId);
        func.ParamFieldNames.Add(yId);
        func.ReturnType = "SystemObjectArray";
        func.ReturnSlots.Add(new ReturnSlot(retId, "SystemObjectArray"));

        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(func);

        var xLeaf = BridgeLoad(xId, "SystemObjectArray");
        var yLeaf = BridgeLoad(yId, "SystemObjectArray");

        var xNull = BridgeCallExtern("SystemBoolean", "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new CLeaf[] { xLeaf, _builder.Const(null, "SystemObject") });
        _builder.EmitIf(xNull, _ => _builder.EmitReturn(yLeaf)); // null + y = y

        var yNull = BridgeCallExtern("SystemBoolean", "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new CLeaf[] { yLeaf, _builder.Const(null, "SystemObject") });
        _builder.EmitIf(yNull, _ => _builder.EmitReturn(xLeaf)); // x + null = x

        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);
        EmitMulticastFlattenOperand(xLeaf, fanoutName, out var lxSlot, out var lenLxSlot);
        EmitMulticastFlattenOperand(yLeaf, fanoutName, out var lySlot, out var lenLySlot);

        var catLenSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(catLenSlot, BridgeCallExtern("SystemInt32",
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(lenLySlot) }));

        var catSlot = _ctx.Builder.AllocScratch("SystemObjectArray");
        _builder.EmitAssign(catSlot, BridgeCallExtern("SystemObjectArray", MulticastArrCtor,
            new CLeaf[] { _builder.SlotRef(catLenSlot) }));
        EmitMulticastArrayBlit(lxSlot, BridgeConstInt(0), catSlot, BridgeConstInt(0), lenLxSlot);
        EmitMulticastArrayBlit(lySlot, BridgeConstInt(0), catSlot, _builder.SlotRef(lenLxSlot), lenLySlot);

        _builder.EmitReturn(EmitMulticastMintBundle(sigPart, _builder.SlotRef(catSlot)));

        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }

    /// <summary>__dlg_remove_{sig}(x, y) (§1.4): null legs, else flatten both operands and delete the
    /// LAST contiguous run of lx that elementwise-matches ly (element equality reuses the existing
    /// CompareDelegates leg via InvocationHandler.EmitDelegateElementEquals — never re-derived, §1.4).
    /// No match → x unchanged. Full removal → null. Single survivor → the bare bundle (not re-wrapped).</summary>
    void EmitMulticastRemoveHelper(string sigPart)
    {
        var helperName = DelegateAbi.MulticastRemoveName(sigPart);
        var xId = $"{helperName}__x"; var yId = $"{helperName}__y"; var retId = $"{helperName}__ret";
        _ctx.Storage.TryDeclareVar(xId, "SystemObjectArray");
        _ctx.Storage.TryDeclareVar(yId, "SystemObjectArray");
        _ctx.Storage.TryDeclareVar(retId, "SystemObjectArray");

        var func = _module.AddFunction(helperName);
        func.ParamFieldNames.Add(xId);
        func.ParamFieldNames.Add(yId);
        func.ReturnType = "SystemObjectArray";
        func.ReturnSlots.Add(new ReturnSlot(retId, "SystemObjectArray"));

        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(func);

        var xLeaf = BridgeLoad(xId, "SystemObjectArray");
        var yLeaf = BridgeLoad(yId, "SystemObjectArray");

        var xNull = BridgeCallExtern("SystemBoolean", "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new CLeaf[] { xLeaf, _builder.Const(null, "SystemObject") });
        _builder.EmitIf(xNull, _ => _builder.EmitReturn(_builder.Const(null, "SystemObjectArray"))); // null - y = null

        var yNull = BridgeCallExtern("SystemBoolean", "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new CLeaf[] { yLeaf, _builder.Const(null, "SystemObject") });
        _builder.EmitIf(yNull, _ => _builder.EmitReturn(xLeaf)); // x - null = x

        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);
        EmitMulticastFlattenOperand(xLeaf, fanoutName, out var lxSlot, out var lenLxSlot);
        EmitMulticastFlattenOperand(yLeaf, fanoutName, out var lySlot, out var lenLySlot);

        var elementEquals = new InvocationHandler(_ctx);

        // LastContiguousMatch: search the candidate start index DOWNWARD from (lenLx-lenLy) to 0 — the
        // first full match found this way is the RIGHTMOST (= last) one, per Delegate.Remove semantics.
        var startSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(startSlot, BridgeCallExtern("SystemInt32",
            "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(lenLySlot) }));
        var foundSlot = _ctx.Builder.AllocScratch("SystemBoolean");
        _builder.EmitAssign(foundSlot, _builder.Const(false, "SystemBoolean"));
        var matchIdxSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(matchIdxSlot, BridgeConstInt(-1));

        _builder.EmitWhile(() =>
            {
                var notFound = BridgeCallExtern("SystemBoolean", "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new CLeaf[] { _builder.SlotRef(foundSlot) });
                var startOk = BridgeCallExtern("SystemBoolean",
                    "SystemInt32.__op_GreaterThanOrEqual__SystemInt32_SystemInt32__SystemBoolean",
                    new CLeaf[] { _builder.SlotRef(startSlot), BridgeConstInt(0) });
                return BridgeCallExtern("SystemBoolean", "SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                    new CLeaf[] { notFound, startOk });
            },
            _ =>
            {
                var allMatchSlot = _ctx.Builder.AllocScratch("SystemBoolean");
                _builder.EmitAssign(allMatchSlot, _builder.Const(true, "SystemBoolean"));
                var kSlot = _ctx.Builder.AllocScratch("SystemInt32");
                _builder.EmitAssign(kSlot, BridgeConstInt(0));

                _builder.EmitWhile(() =>
                    {
                        var kOk = BridgeCallExtern("SystemBoolean", "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                            new CLeaf[] { _builder.SlotRef(kSlot), _builder.SlotRef(lenLySlot) });
                        return BridgeCallExtern("SystemBoolean", "SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                            new CLeaf[] { _builder.SlotRef(allMatchSlot), kOk });
                    },
                    _ =>
                    {
                        var lxIdx = BridgeCallExtern("SystemInt32", "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                            new CLeaf[] { _builder.SlotRef(startSlot), _builder.SlotRef(kSlot) });
                        var lxElem = BridgeCallExtern("SystemObjectArray", MulticastArrGet,
                            new CLeaf[] { _builder.SlotRef(lxSlot), lxIdx });
                        var lyElem = BridgeCallExtern("SystemObjectArray", MulticastArrGet,
                            new CLeaf[] { _builder.SlotRef(lySlot), _builder.SlotRef(kSlot) });
                        var eq = elementEquals.EmitDelegateElementEquals(lxElem, lyElem);
                        var notEq = BridgeCallExtern("SystemBoolean", "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                            new CLeaf[] { eq });
                        _builder.EmitIf(notEq, _ => _builder.EmitAssign(allMatchSlot, _builder.Const(false, "SystemBoolean")));
                        _builder.EmitAssign(kSlot, BridgeCallExtern("SystemInt32",
                            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                            new CLeaf[] { _builder.SlotRef(kSlot), BridgeConstInt(1) }));
                    });

                _builder.EmitIf(_builder.SlotRef(allMatchSlot),
                    _ =>
                    {
                        _builder.EmitAssign(foundSlot, _builder.Const(true, "SystemBoolean"));
                        _builder.EmitAssign(matchIdxSlot, _builder.SlotRef(startSlot));
                    },
                    _ => _builder.EmitAssign(startSlot, BridgeCallExtern("SystemInt32",
                        "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
                        new CLeaf[] { _builder.SlotRef(startSlot), BridgeConstInt(1) })));
            });

        _builder.EmitIf(_builder.SlotRef(foundSlot), null, _ => _builder.EmitReturn(xLeaf)); // no match → x unchanged

        var rLenSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(rLenSlot, BridgeCallExtern("SystemInt32",
            "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(lenLySlot) }));

        var rLenIsZero = BridgeCallExtern("SystemBoolean", "SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean",
            new CLeaf[] { _builder.SlotRef(rLenSlot), BridgeConstInt(0) });
        _builder.EmitIf(rLenIsZero, _ => _builder.EmitReturn(_builder.Const(null, "SystemObjectArray"))); // full removal → null

        var rSlot = _ctx.Builder.AllocScratch("SystemObjectArray");
        _builder.EmitAssign(rSlot, BridgeCallExtern("SystemObjectArray", MulticastArrCtor, new CLeaf[] { _builder.SlotRef(rLenSlot) }));
        EmitMulticastArrayBlit(lxSlot, BridgeConstInt(0), rSlot, BridgeConstInt(0), matchIdxSlot);
        var tailStartSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(tailStartSlot, BridgeCallExtern("SystemInt32", "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new CLeaf[] { _builder.SlotRef(matchIdxSlot), _builder.SlotRef(lenLySlot) }));
        var tailLenSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(tailLenSlot, BridgeCallExtern("SystemInt32", "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            new CLeaf[] { _builder.SlotRef(lenLxSlot), _builder.SlotRef(tailStartSlot) }));
        EmitMulticastArrayBlit(lxSlot, _builder.SlotRef(tailStartSlot), rSlot, _builder.SlotRef(matchIdxSlot), tailLenSlot);

        var rLenIsOne = BridgeCallExtern("SystemBoolean", "SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean",
            new CLeaf[] { _builder.SlotRef(rLenSlot), BridgeConstInt(1) });
        _builder.EmitIf(rLenIsOne, _ => _builder.EmitReturn( // single collapse → bare bundle, not re-wrapped
            BridgeCallExtern("SystemObjectArray", MulticastArrGet, new CLeaf[] { _builder.SlotRef(rSlot), BridgeConstInt(0) })));

        _builder.EmitReturn(EmitMulticastMintBundle(sigPart, _builder.SlotRef(rSlot)));

        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }

    /// <summary>__dlg_fanout_{sig} (§1.2): bridge protocol entry (conv args/env arrive via the standard
    /// __dlgc_{sig}__* convention fields, same as any delegate bridge), snapshots them into fan-out
    /// locals, then loops the invocation list dispatching each element through the EXISTING unified
    /// dispatch (InvocationHandler.EmitFanoutElementDispatch) — args are re-staged from the snapshot
    /// each iteration so a prior element's cross-dispatch clobber never leaks into the next. Last
    /// element's return value wins (§1.5, matches C# Invoke semantics); empty/null list → default.
    /// §1.6/A-M3: the element-dispatch call site is UNCONDITIONALLY Reentrant-flagged — see the
    /// reentrant-decision comment below.</summary>
    void EmitMulticastFanoutBridge(string sigPart, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        var fanoutName = DelegateAbi.MulticastFanoutName(sigPart);

        // §1.6/A-M3 reentrancy decision: UNCONDITIONALLY reentrant. fan-out(sigPart) is always its own
        // sig-S escape target (any sig-S bundle's [1]/[2] can point at it, by construction), so a
        // sig-matched escape-set membership check only ever narrows the false case to a multicast
        // composed ENTIRELY from foreign-received bundles (no local delegate-creation of this sig) —
        // an under-approximation hole, not a real safety margin: a cross element dispatch can still
        // SendCustomEvent into a foreign handler that redispatches this sig and SCEs back into this
        // fan-out mid-iteration. Unconditional closes that hole pre-emptively and needs no snapshot of
        // BuildRecursionInfo's escape set (which cannot be computed before this synthetic function
        // exists anyway — see the design doc §1.6 note). Strictly conservative, matching the §8-3
        // "extra edges only ever over-spill" direction used throughout BuildRecursionInfo.
        // MarkReentrantDispatch (the normal path for a Reentrant-flagged site) declares __recurStack/
        // __recurSp on first use; this path bypasses that helper entirely (no IMethodSymbol to key
        // AccumulateRecursionSpillFields off), so declare the software stack here directly. No named
        // spill fields are added for the fan-out itself — every one of its locals (i/n/list/args-
        // snapshot/ret) is a plain scratch slot, spilled by InsertRecursionSpillsFunc's generic
        // post-coalesce liveness pass, not by the named-field mechanism.
        _ctx.Storage.EnsureRecursionStack();

        var retType = DeclareConvSigFields(sigPart, invoke, typeParamMap, out var argTypes);
        _ctx.Storage.TryDeclareVar(DelegateAbi.ConvEnvName(sigPart), EnvEmit.EnvType);

        var fanoutFunc = _module.AddFunction(fanoutName, fanoutName);
        var prevFunc = _builder.CurrentFunction;
        _builder.SetFunction(fanoutFunc);

        var listSlot = _ctx.Builder.AllocScratch(EnvEmit.EnvType);
        _builder.EmitAssign(listSlot, BridgeLoad(DelegateAbi.ConvEnvName(sigPart), EnvEmit.EnvType));

        var argSlots = new int[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
        {
            argSlots[i] = _ctx.Builder.AllocScratch(argTypes[i]);
            _builder.EmitAssign(argSlots[i], BridgeLoad(DelegateAbi.ConvArgName(sigPart, i), argTypes[i]));
        }

        int retSlot = -1;
        if (retType != null)
        {
            retSlot = _ctx.Builder.AllocScratch(retType);
            _builder.EmitAssign(retSlot, InvocationHandler.DefaultConst(_builder, retType));
        }

        var nSlot = _ctx.Builder.AllocScratch("SystemInt32");
        _builder.EmitAssign(nSlot, BridgeCallExtern("SystemInt32", "SystemArray.__get_Length__SystemInt32",
            new CLeaf[] { _builder.SlotRef(listSlot) }));

        var iSlot = _ctx.Builder.AllocScratch("SystemInt32");
        var dispatch = new InvocationHandler(_ctx);

        _builder.EmitFor(
            _ => _builder.EmitAssign(iSlot, BridgeConstInt(0)),
            () => BridgeCallExtern("SystemBoolean", "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new CLeaf[] { _builder.SlotRef(iSlot), _builder.SlotRef(nSlot) }),
            _ => _builder.EmitAssign(iSlot, BridgeCallExtern("SystemInt32",
                "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                new CLeaf[] { _builder.SlotRef(iSlot), BridgeConstInt(1) })),
            _ =>
            {
                var elemSlot = _ctx.Builder.AllocScratch("SystemObjectArray");
                _builder.EmitAssign(elemSlot, BridgeCallExtern("SystemObjectArray", MulticastArrGet,
                    new CLeaf[] { _builder.SlotRef(listSlot), _builder.SlotRef(iSlot) }));

                var argLeaves = new CLeaf[argSlots.Length];
                for (int k = 0; k < argSlots.Length; k++) argLeaves[k] = _builder.SlotRef(argSlots[k]);

                var elemRet = dispatch.EmitFanoutElementDispatch(_builder.SlotRef(elemSlot), invoke, typeParamMap, argLeaves);
                if (retSlot >= 0 && elemRet != null)
                    _builder.EmitAssign(retSlot, elemRet);
            });

        if (retSlot >= 0)
            BridgeStore(DelegateAbi.ConvRetName(sigPart), _builder.SlotRef(retSlot));

        _builder.EmitReturn();
        if (prevFunc != null) _builder.SetFunction(prevFunc);
    }

    static string SanitizeId(string name) => name.Replace('.', '_');

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
            (method.ContainingType is INamedTypeSymbol structCt && EmitPolicy.IsObjectArrayEmulated(structCt) && !method.IsStatic
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
                    fcbFieldType = GetUdonType(setterProp.Type);
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
            var newVal = BridgeLoad(fcbFieldName, fcbFieldType);
            var oldVal = BridgeLoad($"__old_{fcbFieldName}", fcbFieldType);
            BridgeStore(fcbFieldName, oldVal);

            // Call setter with new value
            BridgeCallInternal(func, new CLeaf[] { newVal });
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
        {
            var bindings = new List<(IReadOnlyList<ITypeParameterSymbol>, IReadOnlyList<ITypeSymbol>)>(2);
            if (method.IsGenericMethod)
                bindings.Add((method.OriginalDefinition.TypeParameters, method.TypeArguments));
            if (method.ContainingType.IsGenericType)
                bindings.Add((method.ContainingType.OriginalDefinition.TypeParameters, method.ContainingType.TypeArguments));
            typeMap = TypeParamScope.Compose(null, newWins: true, bindings);
        }

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
            // first-wins FirstSpecByDefinition read was leg-B's silent first-spec-T bake. Owners not in
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
                if (_ctx.Generics.FirstSpecByDefinition.TryGetValue(enclosing.OriginalDefinition, out var ownerSpec))
                {
                    closureBindings ??= new();
                    closureBindings.Add((ownerSpec.OriginalDefinition.TypeParameters, ownerSpec.TypeArguments));
                    if (ownerSpec.ContainingType.IsGenericType)
                        closureBindings.Add((ownerSpec.ContainingType.OriginalDefinition.TypeParameters,
                            ownerSpec.ContainingType.TypeArguments));
                }
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
                            BridgeLoad(entryParamIds[p.Ordinal], GetUdonType(p.Type)));

            // Class receiver capture (design 2026-07-10 v2 §1.3): consume the receiver param0 into its
            // env cell exactly like a captured parameter — after __tco_ + EnvAlloc, so a self-tail
            // loopback re-seeds each logical activation's fresh env. Null CurrentStructReceiverParamId
            // (behaviour methods, hoisted closures) and an uncaptured receiver both skip.
            if (_ctx.Closures.CaptureScope != null
                && _ctx.Methods.CurrentStructReceiverParamId is { } rcvParamId
                && LambdaCaptureAnalyzer.ReceiverCaptureKey(method) is { } rcvKey
                && _ctx.Closures.TryGetEnvBinding(rcvKey, out _))
                EnvEmit.Write(_builder, _ctx, rcvKey, BridgeLoad(rcvParamId, AggregateAbi.ArrayType));

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
                if (method.ContainingType is INamedTypeSymbol cctClass && EmitPolicy.IsUserClassType(cctClass)
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
                        BridgeStore(lambdaRets[0].Id, resultVal);
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
                    BridgeStore(propRets[0].Id, resultVal);
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
                    var propType = GetUdonType(autoProp.Type);
                    if (method.MethodKind == MethodKind.PropertyGet
                        && _methodReturns.TryGetValue(method, out var autoRets) && autoRets.Length == 1)
                    {
                        BridgeStore(autoRets[0].Id, BridgeLoad(backingVar, propType));
                    }
                    else if (method.MethodKind == MethodKind.PropertySet
                        && _methodParamVarIds.TryGetValue(method, out var paramIds) && paramIds.Length > 0)
                    {
                        BridgeStore(backingVar, BridgeLoad(paramIds[0], propType));
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
            var curVal = BridgeLoad(fcbFieldName, fcbFieldType);
            BridgeStore($"__old_{fcbFieldName}", curVal);
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
            _ctx.Storage.TryDeclareVar(tv, AggregateAbi.ArrayType);
            BridgeStore(tv, AggregateAbi.Allocate(_builder, 1));
        }
        // Default-init aggregate (struct/tuple) fields with no explicit initializer FIRST, so any explicit
        // initializer that references one sees a non-null backing array (C# default-then-initializer order).
        foreach (var (fieldId, aggType) in _ctx.Aggregates.FieldDefaults)
            BridgeStore(fieldId, AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout(aggType),
                _ctx.Aggregates.GetLayout, GetUdonType));

        foreach (var (fieldId, initOp, fieldType) in _fieldInitOps)
        {
            try
            {
                // Bare array initializer { 1, 2, 3 } → synthesize array creation + element Set
                if (initOp is IArrayInitializerOperation arrayInit)
                {
                    var arrTypeSym = (IArrayTypeSymbol)fieldType;
                    var arrayType = GetUdonType(arrTypeSym);
                    var elementType = GetArrayElemType(arrTypeSym);
                    var sizeConst = BridgeConstInt(arrayInit.ElementValues.Length);
                    var arrVal = BridgeCallExtern(arrayType,
                        ExternResolver.BuildArrayCtorSignature(arrayType),
                        new CLeaf[] { sizeConst });
                    BridgeStore(fieldId, arrVal);
                    for (int i = 0; i < arrayInit.ElementValues.Length; i++)
                    {
                        var elemVal = VisitExpression(arrayInit.ElementValues[i]);
                        var idxConst = BridgeConstInt(i);
                        var arrLoad = BridgeLoad(fieldId, arrayType);
                        BridgeCallExternVoid(
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
                        var srcType = GetUdonType(initOp.Type);
                        var dstType = GetUdonType(fieldType);
                        var converted = BridgeCallExtern(dstType,
                            $"SystemConvert.__{methodName}__{srcType}__{dstType}",
                            new CLeaf[] { valueVal });
                        BridgeStore(fieldId, converted);
                        continue;
                    }
                }

                BridgeStore(fieldId, valueVal);
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
                var fieldVal = BridgeLoad(kvp.Key, fcbType);
                BridgeStore($"__old_{kvp.Key}", fieldVal);
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

    // ── Recursion-cycle analysis ──

    // Build the internal-call graph over all registered methods and mark, for each method, the callees
    // that lie in its strongly-connected component (Tarjan). A call along such an edge can re-enter the
    // caller, so the caller's live values must be spilled to the software stack around the call (Udon's
    // flat heap shares param/local slots across frames). Includes direct self-recursion (self-loops).
    //
    // First-class-delegate extension (design §4): lambdas are graph nodes too; an EscapeSet E collects
    // every function whose bridge address can be minted into a bundle (same-class method groups, local
    // functions, lambdas); every function containing a delegate dispatch gets synthetic edges m→E
    // (an indirect dispatch can start any escaped function). Cycle members' NON-TAIL dispatch sites are
    // recorded syntax-keyed in EmitContext.Recursion.ReentrantDispatchSites for the §4.3 Reentrant-flag marking;
    // tail dispatch sites are spared so bundle-driven deep tail recursion never spills (§4.4).
    void BuildRecursionInfo()
    {
        // Generic method definitions are monomorphized per call-site and thus skipped in registration, so
        // they are absent from _methodFunctions. Add them explicitly — otherwise a recursive generic method
        // (e.g. `int Fact<T>(int n) => n * Fact<T>(n-1)`) has no graph node and its frame is never spilled.
        var roots = _methodFunctions.Keys
            .Select(m => m.OriginalDefinition)
            // C3: SAME own-generic MethodKind set as ComputeMethods's reach seed (IsOwnGenericSeed) — a
            // recursive generic method of any user kind needs a graph node or its frame is never spilled.
            .Concat(_classSymbol.GetMembers().OfType<IMethodSymbol>()
                .Where(IsOwnGenericSeed)
                .Select(m => (IMethodSymbol)m.OriginalDefinition))
            // Wave-9 round-9 [Y5]: base-declared generic definitions called with OPEN type args —
            // their on-demand specs (round-8 [Y11]) emit the base definition's body, so the
            // definition needs a graph node exactly like a same-class generic definition; without
            // it a self-recursive inherited generic had no self-edge and never spilled (VM-proven
            // 63 where the CLR gives 234 — live locals clobbered per frame).
            .Concat(_openGenericBaseDefs)
            // SS2A/M3 (F8): generic foreign-static definitions — emitted on demand like open base
            // generics, so they need graph nodes the same way (armor + self-recursion spill).
            .Concat(_reach.GenericForeignStaticBodies.Keys)
            .Where(m => m.DeclaringSyntaxReferences.Length > 0)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<IMethodSymbol>()
            .ToList();

        // Wave-14: a generic struct member reached ONLY via internal self/cross-struct-method
        // reference (Box<T>'s own body, or mutual recursion between two DIFFERENT generic struct
        // types — APart<T> <-> BPart<T>) is registered ON DEMAND during Phase-2 body emission
        // (HandlerBase.ResolveStructMember/RegisterGenericSpecialization), which runs AFTER this
        // analysis — so it has no graph node yet and _methodFunctions above never sees it (Phase-1's
        // struct collector walks the SAME open/shared body and, correctly, skips exactly this
        // open-form reference to avoid a ghost CFunction — IsCollectibleStructMember). Without a node,
        // IsRecursiveEdge silently returns false for the edge and the software-stack spill never wraps
        // it (VM-proven: mutual recursion between two generic struct types clobbered shared frame
        // fields — 7 instead of the CLR's 21).
        //
        // ReachableBodies definition projection (design §1, consumer 2): the ungated struct-member
        // DEFINITION set from the single reach fixpoint (_reachStructMemberDefs) replaces this method's
        // former private structDefRoots BFS — the same "every user-struct member DEFINITION transitively
        // reachable" set (both propagate via CollectStructMemberDefinitions, ignoring open/closed so
        // instantiations collapse onto one node), with the duplicate semantic-model body walk removed.
        roots = roots.Concat(_reach.StructMemberDefs)
            .Where(m => m.DeclaringSyntaxReferences.Length > 0)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<IMethodSymbol>()
            .ToList();

        // Local functions are registered lazily during emission (after this pass), so discover them now by
        // walking the bodies — otherwise a recursive local function would not be detected and would corrupt
        // the flat heap. Transitive: a local function may contain nested local functions. F1: every root is
        // a reach definition, so its body comes from the reach result (BodyByDef) — no re-fetch.
        var localFuncs = new List<IMethodSymbol>();
        foreach (var m in roots)
        {
            var op = ReachRootBody(m); // C2: a root's body is authoritative in BodyByDef (loud on miss)
            if (op != null) CollectLocalFunctions(op, localFuncs);
        }

        var internalMethods = roots.Concat(localFuncs)
            .Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToArray();
        var methodSet = new HashSet<IMethodSymbol>(internalMethods, SymbolEqualityComparer.Default);
        var localFuncSet = new HashSet<IMethodSymbol>(localFuncs, SymbolEqualityComparer.Default);

        var bodies = new Dictionary<IMethodSymbol, IOperation>(SymbolEqualityComparer.Default);
        var edges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var m in internalMethods)
        {
            var callees = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            // C2: a reach root's body is authoritative in BodyByDef (LOUD on miss — a missing root is a
            // fixpoint invariant violation, not a fetch to paper over). A LOCAL FUNCTION is discovered
            // during THIS analysis and is legitimately NOT a reach entry — that is the ONE explicit fetch
            // arm. Every internalMethod has syntax, so it becomes a graph node UNCONDITIONALLY — an
            // auto-property accessor's operation is a bodyless null yet must still be a node
            // (CollectInternalCallees no-ops on null); dropping null-body nodes would lose them from
            // RecursionGraphNodes.
            IOperation op = _reach.BodyByDef.TryGetValue(m, out var cached) ? cached
                : _reach.GenericForeignStaticBodies.TryGetValue(m, out var suppCached) ? suppCached
                : localFuncSet.Contains(m) ? GetMethodBodyOperation(m)
                : throw ReachMiss(m);
            var body = (op as ILocalFunctionOperation)?.Body ?? op;
            bodies[m] = body;
            CollectInternalCallees(body, methodSet, callees);
            edges[m] = callees;
        }

        // ── §4.2 graph extension: lambda nodes, EscapeSet, synthetic edges ──

        // (a) Lambda nodes. Collected from the ROOT-method bodies and the field-initializer operations
        // so each lambda is keyed in exactly one operation-tree family (local-function bodies are
        // separate GetOperation trees, so collecting from them too would yield duplicate-but-distinct
        // instances). Emit-time matching is value-based for symbols (Roslyn lambda/local-function
        // symbols compare by syntax + container) and red-syntax-based for dispatch sites.
        var lambdaNodes = new List<(IMethodSymbol Sym, IOperation Body, IAnonymousFunctionOperation Op)>();
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rootBody))
                CollectLambdaNodes(rootBody, lambdaNodes);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectLambdaNodes(initOp, lambdaNodes);

        var lambdaOps = new Dictionary<IMethodSymbol, IAnonymousFunctionOperation>(SymbolEqualityComparer.Default);
        foreach (var (sym, body, lambdaOp) in lambdaNodes)
        {
            if (edges.ContainsKey(sym)) continue;
            bodies[sym] = body;
            lambdaOps[sym] = lambdaOp;
            var lambdaCallees = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            CollectInternalCallees(body, methodSet, lambdaCallees);
            edges[sym] = lambdaCallees;
        }

        // (b) EscapeSet E (§4.1 + §5.4 widening): conservative approximation of every function whose
        // bridge address can end up inside a dispatched bundle. Two sources:
        //   1. Same-class delegate-creation targets (method groups incl. local functions, and lambdas).
        //   2. §5.4 widening — every BRIDGE-BEARING method. A bundle can be minted in ANOTHER program
        //      (foreign-wired self-callback, fcd47 form; or SetProgramVariable-delivered) whose creation
        //      site is invisible to CollectEscapedDelegateTargets, yet dispatched here re-enters THIS
        //      program's method. The planner emits a speculative bridge per non-event user method, and
        //      each such method is already a graph node (a root), so it is an escape target too. The
        //      resulting SCC growth is contained by the sig-filter on the synthetic edges (c): a typed
        //      dispatch can only enter a bridge of the SAME signature. Variance (Stage 1.75 §2.2) keeps
        //      this sound WITHOUT rejecting the binding: a variant method-group target is escaped under
        //      its ADAPTER's protocol sig (sig-S), not its own — see the variantEscapeSigs collection
        //      below (was previously "sound only while variance is rejected," the tracked coupling pin
        //      SigFilterCoupledToVarianceReject; that pin now asserts the widened-not-rejected form).
        // MEMBERSHIP-ONLY (§1.5): never drives emission order.
        var escape = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rootBody))
                CollectEscapedDelegateTargets(rootBody, methodSet, escape);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectEscapedDelegateTargets(initOp, methodSet, escape);
        foreach (var m in _planner.GetLayout(_classSymbol).DelegateBridges.Keys)
        {
            var def = m.OriginalDefinition;
            if (bodies.ContainsKey(def)) escape.Add(def);
        }

        // Variance design (2026-07-04 §2.2): a target reached ONLY via a sig adapter is escaped under
        // the adapter's protocol sig (sig-S), which can differ from the target's OWN sig — collected
        // separately since a single target may be BOTH an exact-sig escape target (elsewhere) AND a
        // variant one (multiple entries per method, hence a list rather than the single-valued dict
        // below used for the exact-sig case).
        var variantEscapeSigs = new List<(IMethodSymbol Method, string Sig)>();
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rootBody))
                CollectVariantEscapeSigs(rootBody, methodSet, variantEscapeSigs);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectVariantEscapeSigs(initOp, methodSet, variantEscapeSigs);

        // (c) Synthetic SIG-FILTERED edges m→{e ∈ E : sig(e) == sig(one of m's dispatches)}: an indirect
        // dispatch of a delegate type T can only start an escaped function whose signature matches T's
        // Invoke method (§5.4). Real call edges are unchanged; the RecursiveCallees filter below
        // self-filters synthetic edges (no named call to match), so they create cycle membership —
        // consumed by the per-site Reentrant marking — without ever creating named-call spills. Signature
        // matching (SigsMatch) uses the concrete definition-level BuildSigPart, with a wildcard escape
        // hatch for type-param-involving signatures (see the escapeSig comment below).
        var allNodes = internalMethods.Concat(lambdaNodes.Select(l => l.Sym))
            .Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToArray();
        // sig(e) = concrete open-definition BuildSigPart, or WILDCARD (null) when the signature
        // involves a type parameter. At analysis time there is no type-param map, so a generic escape
        // target (e.g. an inherited `FreeG<T>` dispatched as a monomorphized spec) and a concrete
        // dispatch (`Func<int,int>`) cannot be reliably matched by string — the OPEN sig of a generic
        // never equals the CONCRETE dispatch sig. Treating either side as wildcard when it involves a
        // type param restores the pre-widening connect-all behaviour for generic-involved dispatches
        // (sound: conservative), while keeping the exact sig-filter for the concrete common case
        // (contains the §5.4 widening). SigsMatch: equal, or either is wildcard. A method may appear
        // MULTIPLE times (once under its own exact sig, again under each sig-S it's variant-adapted to)
        // — hence a list, not a single-valued dict (Stage 1.75 §2.2).
        var escapeSig = new List<(IMethodSymbol Method, string Sig)>();
        foreach (var e in escape)
            if (edges.ContainsKey(e)) escapeSig.Add((e, DispatchSigOrWildcard(e)));
        foreach (var (vm, vSig) in variantEscapeSigs)
            if (edges.ContainsKey(vm)) escapeSig.Add((vm, vSig));
        // Wave-12 [V1]: sites whose bundle provenance is exact (see TryResolvePreciseDispatchTargets)
        // contribute edges to their KNOWN targets only, instead of sig-matching against the whole
        // widened escape set; every other site keeps the §5.4 blanket treatment. Keyed by operation
        // reference — the reentrant-marking loop below re-collects sites from the same shared bodies.
        var preciseDispatchTargets = new Dictionary<IOperation, HashSet<IMethodSymbol>>();
        foreach (var node in allNodes)
        {
            if (!bodies.TryGetValue(node, out var nodeBody) || nodeBody == null) continue;
            var dispatchSites = new List<IOperation>();
            CollectDelegateDispatchSites(nodeBody, dispatchSites);
            if (dispatchSites.Count == 0) continue;
            var nodeSigs = new List<string>();
            var nodeEdges = edges[node];
            foreach (var site in dispatchSites)
            {
                if (site is not IInvocationOperation dinv || dinv.TargetMethod == null) continue;
                if (TryResolvePreciseDispatchTargets(nodeBody, dinv, out var preciseTargets))
                {
                    preciseDispatchTargets[site] = preciseTargets;
                    foreach (var t in preciseTargets)
                        if (edges.ContainsKey(t)) nodeEdges.Add(t);
                }
                else
                    nodeSigs.Add(DispatchSigOrWildcard(dinv.TargetMethod));
            }
            if (nodeSigs.Count == 0) continue;
            foreach (var (escMethod, escSig) in escapeSig)
                foreach (var ds in nodeSigs)
                    if (SigsMatch(ds, escSig)) { nodeEdges.Add(escMethod); break; }
        }

        // Round-7 follow-up [Q5]: per-node this-FIELD touch sets for the ref/out-argument alias
        // guard (see EmitContext.Recursion.ThisFieldTouches). Direct touches are collected per node;
        // this-property references add accessor edges (a callee reading a manual property whose
        // getter touches the field is the same alias one hop deeper); the closure runs over the
        // same `edges` graph — synthetic dispatch edges included, conservative per §8-3.
        var thisTouches = new Dictionary<IMethodSymbol, HashSet<IFieldSymbol>>(SymbolEqualityComparer.Default);
        var accessorEdges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var node in allNodes)
        {
            var touch = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
            var acc = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            if (bodies.TryGetValue(node, out var touchBody) && touchBody != null)
                CollectThisFieldTouches(touchBody, touch, acc);
            thisTouches[node] = touch;
            accessorEdges[node] = acc;
        }
        bool touchChanged = true;
        while (touchChanged)
        {
            touchChanged = false;
            foreach (var node in allNodes)
            {
                var mySet = thisTouches[node];
                foreach (var callee in edges[node])
                    if (thisTouches.TryGetValue(callee, out var calleeSet)
                        && !ReferenceEquals(calleeSet, mySet))
                        foreach (var f in calleeSet)
                            if (mySet.Add(f)) touchChanged = true;
                foreach (var callee in accessorEdges[node])
                    if (thisTouches.TryGetValue(callee, out var accSet)
                        && !ReferenceEquals(accSet, mySet))
                        foreach (var f in accSet)
                            if (mySet.Add(f)) touchChanged = true;
            }
        }
        var recursive = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var cycleEdges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var reentrantSites = new HashSet<SyntaxNode>();
        var tailSparedSites = new HashSet<SyntaxNode>();

        // Wave-9 round-9 [Y4]: forward reachability from each escaped function (over the same
        // `edges` graph, synthetic edges included), memoized across SCCs. A dispatch can only
        // START an escaped function, so it can only RE-ENTER its containing function when some
        // escaped function reaches that function's SCC.
        var escapeReach = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        HashSet<IMethodSymbol> ReachFrom(IMethodSymbol e)
        {
            if (escapeReach.TryGetValue(e, out var cached)) return cached;
            var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { e };
            var work = new Stack<IMethodSymbol>();
            work.Push(e);
            while (work.Count > 0)
            {
                var cur = work.Pop();
                if (!edges.TryGetValue(cur, out var succ)) continue;
                foreach (var s in succ)
                    if (seen.Add(s)) work.Push(s);
            }
            escapeReach[e] = seen;
            return seen;
        }

        foreach (var scc in TarjanScc(allNodes, edges))
        {
            var sccSet = new HashSet<IMethodSymbol>(scc, SymbolEqualityComparer.Default);
            // Non-trivial SCC (mutual cycle) OR a single method with a self-loop (direct self-recursion).
            bool isCycle = scc.Count > 1 || (scc.Count == 1 && edges[scc[0]].Contains(scc[0]));
            if (!isCycle) continue;
            // Wave-9 round-9 [Y4]: gate the per-site Reentrant marking on actual re-enterability.
            // When NO escaped function reaches this SCC, a dispatch inside it can never re-enter the
            // caller — and the spurious spill/reload (VM-proven) DISCARDED a same-environment write
            // made by a dispatched non-cycle closure: the lambda's write to its captured cell never
            // reached the declarer's post-dispatch read (acc=1 where the CLR gives 6 at depth 1).
            // Direct-call spills (RecursiveCallees) are real edges and stay ungated.
            // §5.4 sig-filter applied to the reachability gate (NOT only the synthetic edges): the set
            // of delegate signatures that can re-enter this SCC — an escaped function of signature S
            // reaches the SCC. A dispatch site is Reentrant only when ITS OWN signature is in this set.
            // Widening E (§5.4) can add a same-SCC method of a DIFFERENT signature (e.g. `M(int)` reaches
            // its own SCC); without the per-site sig gate that would spuriously mark a Void→Void `bump()`
            // dispatch in M reentrant even though a bundle of M's signature can never flow to it. Variant
            // targets contribute their ADAPTER's sig-S here (via escapeSig's variantEscapeSigs entries),
            // not their own — SigFilterCoupledToVarianceReject now pins the widened-not-rejected form.
            var reenterSigs = new List<string>();   // may hold WILDCARD (null) entries
            foreach (var (escMethod, escSig) in escapeSig)
                if (ReachFrom(escMethod).Overlaps(sccSet)) reenterSigs.Add(escSig);
            bool sccReenterable = reenterSigs.Count > 0;
            foreach (var caller in scc)
            {
                bodies.TryGetValue(caller, out var callerBody);
                // Wave-9 round-8 [Y3]: the UNFILTERED in-SCC edge set feeds the ref/out re-chain
                // guard (IsCycleEdge) — a tail `return M(m-1, ref w);` re-chain corrupts exactly
                // like the non-tail statement form, so the guard must not ride the tail filter.
                var allInScc = new HashSet<IMethodSymbol>(
                    edges[caller].Where(c => sccSet.Contains(c)), SymbolEqualityComparer.Default);
                if (allInScc.Count > 0) cycleEdges[caller] = allInScc;
                // Only edges with a NON-tail call need spilling: a tail call (`return Callee(..)`) reads
                // nothing after the call, so flat-heap clobbering is harmless — and spilling deep tail
                // recursion would needlessly exhaust the stack.
                var inScc = new HashSet<IMethodSymbol>(
                    edges[caller].Where(c => sccSet.Contains(c) && HasNonTailCallTo(callerBody, c)),
                    SymbolEqualityComparer.Default);
                if (inScc.Count > 0) recursive[caller] = inScc;

                if (callerBody == null) continue;

                // Wave-9 round-9 [Y3]: per-SITE tail classification for DIRECT calls on the
                // recursive (non-tail-carrying) edges above. The spill map gates per callee NAME,
                // so a callee with ONE non-tail site used to spill at EVERY site — tail sites of a
                // mixed tail/non-tail callee are recorded here (syntax-keyed, exactly like the
                // dispatch arm's per-site marking) and EmitCallToMethod flags them TailSpared.
                if (inScc.Count > 0)
                {
                    var directSites = new List<IOperation>();
                    CollectInvocationSites(callerBody, directSites);
                    foreach (var site in directSites)
                    {
                        if (site.Syntax == null) continue;
                        bool toRecursiveCallee = false;
                        foreach (var c in inScc)
                            if (IsInternalCallTo(site, c, out var matched) && ReferenceEquals(matched, site))
                            { toRecursiveCallee = true; break; }
                        if (toRecursiveCallee && !EmitPolicy.IsNonTailDispatchSite(callerBody, site))
                            tailSparedSites.Add(site.Syntax);
                    }
                }

                // §4.3: per-site Reentrant marking — a NON-TAIL dispatch inside a cycle member can
                // re-enter its containing function via any escaped function that reaches this SCC
                // (round-9 [Y4]: unreachable SCCs skip the marking entirely, see sccReenterable).
                // Keyed by red syntax node (shared across semantic models); tail sites are spared (§4.4).
                if (sccReenterable)
                {
                    var dispatchSites = new List<IOperation>();
                    CollectDelegateDispatchSites(callerBody, dispatchSites);
                    foreach (var site in dispatchSites)
                        if (site.Syntax != null && EmitPolicy.IsNonTailDispatchSite(callerBody, site)
                            && site is IInvocationOperation dsInv && dsInv.TargetMethod != null)
                        {
                            // Wave-12 [V1]: a provenance-exact site is Reentrant only when one of
                            // ITS OWN possible callees reaches this SCC (through that callee's full
                            // edge set, blanket edges included — a captured-field dispatch inside
                            // the callee still re-enters and still spills, FP5B4 form). The sig
                            // match against the whole widened escape set stays for every site whose
                            // bundle can be foreign-minted.
                            if (preciseDispatchTargets.TryGetValue(site, out var preciseTargets))
                            {
                                foreach (var t in preciseTargets)
                                    if (ReachFrom(t).Overlaps(sccSet)) { reentrantSites.Add(site.Syntax); break; }
                                continue;
                            }
                            var dsSig = DispatchSigOrWildcard(dsInv.TargetMethod);
                            foreach (var rs in reenterSigs)
                                if (SigsMatch(rs, dsSig)) { reentrantSites.Add(site.Syntax); break; }
                        }
                }

            }
        }
        // Write-once populate of every analysis artifact at the tail (ThisFieldTouches was computed
        // above; the rest just now). §5.5 (graft #2): RecursionGraphNodes is the definition-keyed
        // graph-node set (bodies.Keys = roots, local functions, lambdas) the post-emission armor reads.
        _ctx.RecursionContext.Info.Populate(recursive, cycleEdges, thisTouches, reentrantSites, tailSparedSites,
            new HashSet<IMethodSymbol>(bodies.Keys, SymbolEqualityComparer.Default));
    }

    // §5.5 (graft #2): VerifyBridgeTargetsAreNodes — the wave-10 [Z1]-class emit-time-registration
    // hole detector. A CAPTURING delegate bridge carries an env and MUST have its frame protected
    // across reentrant dispatch; that protection is driven by its recursion-graph node (BuildRecursionInfo
    // above). PendingDelegateBridges is populated DURING body emission (after BuildRecursionInfo), so
    // this runs AFTER EmitPendingDelegateBridges — the design's "end of BuildRecursionInfo" intent, at
    // the only point where the full capturing-bridge set exists. A capturing target with no node means a
    // future registration path escaped the reentrancy analysis: fail loud at compile time, never emit
    // silently-unprotected. Non-capturing bridges (named methods, capture-free lambdas) carry no env and
    // are intentionally skipped — they have no reentrancy-sensitive frame state to lose.
    void VerifyBridgeTargetsAreNodes()
    {
        if (_ctx.Closures.CaptureScope == null || _ctx.RecursionContext.Info.RecursionGraphNodes == null) return;
        foreach (var (method, bridgeExportName, _) in _ctx.Synthetics.DelegateBridges)
        {
            var def = method.OriginalDefinition;
            if (!_ctx.Closures.CaptureScope.IsCapturingClosure(def)) continue;
            if (!_ctx.RecursionContext.Info.RecursionGraphNodes.Contains(def))
                throw new InvalidOperationException(
                    $"USugar internal error (§5.5 bridge-target armor): capturing delegate bridge "
                  + $"'{bridgeExportName}' targets '{def}', which has no recursion-graph node — its "
                  + "reentrancy spill protection would be silently missing. A registration path added a "
                  + "capturing bridge without seeding the recursion analysis (wave-10 [Z1] class).");
        }
        // Variance design (2026-07-04 §2.2): a sig adapter's target is exactly as reachable via
        // dispatch as a plain bridge's — same armor requirement.
        foreach (var (targetMethod, _, adapterName, _) in _ctx.Synthetics.SigAdapterBridges)
        {
            var def = targetMethod.OriginalDefinition;
            if (!_ctx.Closures.CaptureScope.IsCapturingClosure(def)) continue;
            if (!_ctx.RecursionContext.Info.RecursionGraphNodes.Contains(def))
                throw new InvalidOperationException(
                    $"USugar internal error (§5.5 bridge-target armor): capturing sig adapter "
                  + $"'{adapterName}' targets '{def}', which has no recursion-graph node — its "
                  + "reentrancy spill protection would be silently missing.");
        }
    }

    // §5.4 sig-filter helpers. The delegate signature key is BuildSigPart, but only reliable when the
    // signature is CONCRETE. When it involves a type parameter (own generic method, or a param/return
    // referencing an enclosing generic's T), it has no analysis-time concrete form — return WILDCARD
    // (null) so it conservatively matches every dispatch (pre-widening connect-all for generics).
    static string DispatchSigOrWildcard(IMethodSymbol m)
        => SigInvolvesTypeParam(m) ? null : DelegateAbi.BuildSigPart(m);

    // Two signatures match if equal, or either is WILDCARD (a type-param-involving sig matches anything).
    static bool SigsMatch(string a, string b) => a == null || b == null || a == b;

    static bool SigInvolvesTypeParam(IMethodSymbol m)
    {
        if (m.IsGenericMethod) return true;
        static bool Has(ITypeSymbol t) => t switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol a => Has(a.ElementType),
            INamedTypeSymbol n => n.IsGenericType && n.TypeArguments.Any(Has),
            _ => false,
        };
        if (Has(m.ReturnType)) return true;
        foreach (var p in m.Parameters)
            if (Has(p.Type)) return true;
        return false;
    }

    // Collect every lambda (anonymous function) with its body — each becomes its own SCC node (§4.2).
    // Descends everywhere (nested lambdas / lambdas inside local functions are nodes too). The
    // operation itself is carried so callers can ask the capture analyzer (GetCaptures is keyed by
    // IAnonymousFunctionOperation).
    static void CollectLambdaNodes(IOperation op,
        List<(IMethodSymbol Sym, IOperation Body, IAnonymousFunctionOperation Op)> result)
    {
        if (op == null) return;
        if (op is IAnonymousFunctionOperation af && af.Symbol != null && af.Body != null)
            result.Add((af.Symbol, af.Body, af));
        foreach (var child in op.ChildOps())
            CollectLambdaNodes(child, result);
    }

    // EscapeSet collection (§4.1): targets of every IDelegateCreationOperation that resolve to an
    // internal function — same-class method groups (incl. local functions) and lambdas. Full descent.
    // Wave-9 round-5 [X1]: a this-receiver VIRTUAL method-group conversion in an inherited base body
    // statically binds the BASE declaration, but the planner's bridge layout normalizes to the
    // chain-root export whose body is the LEAF override (ResolveDelegateBridge) — so the escape set
    // must contain the leaf's definition too, or a delegate-dispatch cycle through the override never
    // gets a synthetic edge and its frames are never spilled (VM-proven 66 vs 6).
    void CollectEscapedDelegateTargets(IOperation op, HashSet<IMethodSymbol> internalMethods, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is IDelegateCreationOperation dc)
        {
            if (dc.Target is IMethodReferenceOperation mr && mr.Method != null)
            {
                var t = mr.Method.OriginalDefinition;
                if (internalMethods.Contains(t)) result.Add(t);
                if (LeafMethodRefTarget(mr) is { } leafT && internalMethods.Contains(leafT))
                    result.Add(leafT);
            }
            else if (dc.Target is IAnonymousFunctionOperation af && af.Symbol != null)
                result.Add(af.Symbol);
        }
        foreach (var child in op.ChildOps())
            CollectEscapedDelegateTargets(child, internalMethods, result);
    }

    /// <summary>Variance design (2026-07-04 §2.2): collect (target, declared sig-S) pairs for every
    /// VARIANT method-group delegate-creation site — a target reached through a sig adapter is escaped
    /// under the ADAPTER's protocol sig (sig-S = the delegate type's OWN Invoke signature), not its own,
    /// so the widened synthetic edge / SCC-reentrancy check must key on sig-S here (§5.4
    /// SigFilterCoupledToVarianceReject). Mirrors CollectEscapedDelegateTargets's method-group arm but
    /// omits the base-override leaf-target resolution (base.M variance is an unexercised compounding
    /// edge case, not part of this design's tested scope) and the lambda arm (a lambda's sig is inferred
    /// from the delegate type, so it can never be variant).</summary>
    void CollectVariantEscapeSigs(IOperation op, HashSet<IMethodSymbol> internalMethods,
        List<(IMethodSymbol Method, string Sig)> result)
    {
        if (op == null) return;
        if (op is IDelegateCreationOperation { Target: IMethodReferenceOperation { Method: { } mr } } variantDc
            && variantDc.Type is INamedTypeSymbol vDlgType && vDlgType.DelegateInvokeMethod is { } vInvoke)
        {
            var t = mr.OriginalDefinition;
            if (internalMethods.Contains(t))
            {
                var sigS = DelegateAbi.BuildSigPart(vInvoke);
                if (sigS != DelegateAbi.BuildSigPart(t))
                    result.Add((t, sigS));
            }
        }
        foreach (var child in op.ChildOps())
            CollectVariantEscapeSigs(child, internalMethods, result);
    }

    // ── Wave-12 [V1]: per-site dispatch-target provenance ──
    // A dispatch that reads a LOCAL whose every write (declaration initializer / simple assignment,
    // anywhere in the body tree, nested closures included) is a delegate CREATION has a provably
    // exact callee set: locals are not foreign-writable through the sanctioned surface
    // (SetProgramVariable targets symbols by name only via the documented accepted-risk raw boundary,
    // ref locals and delegate-typed ref/out params are rejected), so the bundle can only be one the
    // scanned creations minted. The §5.4 same-signature widening — sound and required for
    // foreign-writable storage (fields, params, elements, foreign receivers) — over-approximated
    // these sites too: every same-sig bridge-bearing method joined the callee set, so a per-frame
    // closure-helper dispatch inside a recursion cycle was marked Reentrant and spilled the whole
    // frame at EVERY iteration's dispatch, overflowing the 512-entry __recurStack ~20% earlier than
    // the equivalent plain-call recursion (VM-proven VmFault at 102 frames on legal code, ErD_D100).
    // Precise iff: the dispatch instance (conversions unwrapped) is a local reference; the local's
    // DECLARATOR is inside this node's own body (a local declared in an enclosing method and
    // dispatched inside a hoisted closure keeps the blanket treatment — its defs live outside this
    // tree); no ref/out use, no compound/increment/deconstruction target anywhere; at least one
    // write exists; and every write's RHS resolves to a delegate creation (or null). Targets mirror
    // CollectEscapedDelegateTargets' mapping (OriginalDefinition + the [X1] leaf override).
    bool TryResolvePreciseDispatchTargets(IOperation callerBody, IInvocationOperation site,
        out HashSet<IMethodSymbol> targets)
    {
        targets = null;
        var instance = site.Instance;
        while (instance is IConversionOperation conv) instance = conv.Operand;
        if (instance is not ILocalReferenceOperation locRef || locRef.Local is not { } local)
            return false;

        var found = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        bool declFound = false, poisoned = false; int writeCount = 0;

        bool RhsIsCreation(IOperation rhs)
        {
            while (rhs is IConversionOperation c) rhs = c.Operand;
            switch (rhs)
            {
                case IDelegateCreationOperation dc:
                    return RhsIsCreation(dc.Target);
                case IAnonymousFunctionOperation af when af.Symbol != null:
                    found.Add(af.Symbol);
                    return true;
                case IMethodReferenceOperation mr when mr.Method != null:
                    found.Add(mr.Method.OriginalDefinition);
                    if (LeafMethodRefTarget(mr) is { } leafT) found.Add(leafT);
                    return true;
                default:
                    // null / default contribute no callee; anything else breaks provenance.
                    return rhs.ConstantValue is { HasValue: true, Value: null }
                        || rhs is IDefaultValueOperation;
            }
        }

        void Walk(IOperation op)
        {
            if (op == null || poisoned) return;
            switch (op)
            {
                case IVariableDeclaratorOperation vd when SymbolEqualityComparer.Default.Equals(vd.Symbol, local):
                    declFound = true;
                    if (vd.Initializer?.Value is { } init)
                    {
                        writeCount++;
                        if (!RhsIsCreation(init)) { poisoned = true; return; }
                    }
                    break;
                case ISimpleAssignmentOperation sa
                    when sa.Target is ILocalReferenceOperation t && SymbolEqualityComparer.Default.Equals(t.Local, local):
                    writeCount++;
                    if (!RhsIsCreation(sa.Value)) { poisoned = true; return; }
                    Walk(sa.Value); // still scan the RHS subtree (a creation may nest another write)
                    return;
                case ISimpleAssignmentOperation sa2 when SubtreeReferencesLocal(sa2.Target):
                case IDeconstructionAssignmentOperation da when SubtreeReferencesLocal(da.Target):
                case ICompoundAssignmentOperation ca when SubtreeReferencesLocal(ca.Target):
                case IIncrementOrDecrementOperation io when SubtreeReferencesLocal(io.Target):
                case IArgumentOperation { Parameter: { RefKind: not RefKind.None } } arg when SubtreeReferencesLocal(arg.Value):
                    poisoned = true;
                    return;
            }
            foreach (var child in op.ChildOps())
                Walk(child);
        }

        bool SubtreeReferencesLocal(IOperation op)
        {
            if (op == null) return false;
            if (op is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local))
                return true;
            foreach (var child in op.ChildOps())
                if (SubtreeReferencesLocal(child)) return true;
            return false;
        }

        Walk(callerBody);
        if (poisoned || !declFound || writeCount == 0)
            return false;
        targets = found;
        return true;
    }

    // Collect the delegate-dispatch invocations attributed to THIS function (hoisted children skipped).
    static void CollectDelegateDispatchSites(IOperation op, List<IOperation> result)
    {
        if (op == null) return;
        if (EmitPolicy.IsDelegateDispatch(op)) result.Add(op);
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            CollectDelegateDispatchSites(child, result);
        }
    }

    // Wave-9 round-9 [Y3]: collect every invocation operation attributed to THIS function
    // (hoisted children skipped — same attribution rule as the dispatch-site collector above).
    static void CollectInvocationSites(IOperation op, List<IOperation> result)
    {
        if (op == null) return;
        if (op is IInvocationOperation) result.Add(op);
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            CollectInvocationSites(child, result);
        }
    }

    // ── Wave-9 round-3 [W1]/[W2]/[W3]: emission-faithful leaf-override resolution for the graph ──
    // Emission resolves a this-receiver virtual call to the most-derived override visible from the
    // compiled class (HandlerBase.ResolveMostDerivedOverride / ResolveDispatchProperty, sharing this
    // file's HandlerBase.FindOverrideMethodInChain/FindOverridePropertyInChain walkers), but the
    // recursion graph recorded only the STATIC binding — so a runtime cycle closed through an
    // override (base body's virtual call/property read dispatching the leaf, or an override calling
    // base.M whose body virtual-calls back) had no static counterpart and its frames were never spilled
    // (VM-proven: 305 where the CLR gives 605; override<->base-copy 14 vs 12; fb=base.M bundle 17 vs 21).
    // These mirrors return the leaf's ORIGINAL DEFINITION (graph nodes are definition-keyed) or null
    // when the site keeps its static binding (no override visible / base. receiver / non-virtual).

    IMethodSymbol LeafCallTarget(IInvocationOperation inv)
    {
        var tm = inv.TargetMethod;
        if (!(tm.IsVirtual || tm.IsOverride || tm.IsAbstract) || tm.MethodKind != MethodKind.Ordinary)
            return null;
        if (inv.Instance is not IInstanceReferenceOperation iref
            || iref.Syntax is BaseExpressionSyntax) return null;
        var def = tm.OriginalDefinition;
        var leaf = ResolveLeafOverrideDef(def);
        return SymbolEqualityComparer.Default.Equals(leaf, def) ? null : leaf;
    }

    /// <summary>Wave-9 round-5 [X1]: leaf resolution for a this-receiver virtual METHOD-GROUP
    /// conversion (delegate creation), gated identically to LeafCallTarget — emission's bridge
    /// resolves these to the chain-root export running the leaf body, so the escape set mirrors it.</summary>
    IMethodSymbol LeafMethodRefTarget(IMethodReferenceOperation mr)
    {
        var m = mr.Method;
        if (m == null || !(m.IsVirtual || m.IsOverride || m.IsAbstract) || m.MethodKind != MethodKind.Ordinary)
            return null;
        if (mr.Instance is not IInstanceReferenceOperation iref
            || iref.Syntax is BaseExpressionSyntax) return null;
        var def = m.OriginalDefinition;
        var leaf = ResolveLeafOverrideDef(def);
        return SymbolEqualityComparer.Default.Equals(leaf, def) ? null : leaf;
    }

    IPropertySymbol LeafPropertyTarget(IPropertyReferenceOperation pr)
    {
        var p = pr.Property;
        if (!(p.IsVirtual || p.IsOverride || p.IsAbstract)) return null;
        if (pr.Instance is not IInstanceReferenceOperation iref
            || iref.Syntax is BaseExpressionSyntax) return null;
        var def = p.OriginalDefinition;
        var cand = HandlerBase.FindOverridePropertyInChain(_classSymbol, def, p.Name);
        if (cand == null) return null;
        return SymbolEqualityComparer.Default.Equals(cand.OriginalDefinition, def) ? null : cand.OriginalDefinition;
    }

    // Definition-keyed twin of HandlerBase.ResolveMostDerivedOverride, sharing its
    // FindOverrideMethodInChain walker: the graph is keyed by OriginalDefinition, so unlike the emission
    // side there is no generic re-Construct here — just normalize the found override to its definition.
    IMethodSymbol ResolveLeafOverrideDef(IMethodSymbol def)
        => HandlerBase.FindOverrideMethodInChain(_classSymbol, def, def.Name)?.OriginalDefinition ?? def;

    // ── Wave-12 r2 [V1]: cross-dispatch landing target for the recursion graph ──
    // A method/accessor dispatched through a VARIABLE receiver (same-typed field/local, base-typed
    // reference) or an INTERFACE-typed receiver emits SetProgramVariable + SendCustomEvent — and when
    // the receiver holds `this` at runtime, the event re-enters THIS program synchronously, exactly
    // like a direct recursive call. These edges were invisible to the SCC analysis (the interface
    // flavor entirely; the class flavor had the static edge but no spill site at emission), so a
    // live local/param after the reentrant self-call was silently clobbered (VM-proven ref=36 vs 0
    // field/local/base flavors, 180 vs 0 interface, 75 vs 60 property accessor, 69 vs 27 mutual).
    // Returns the ORIGINAL DEFINITION of the local method the dispatch lands on when the receiver is
    // this program (the class family's most-derived override — mirroring the chain-root export
    // normalization the emission dispatches), or null when it can never land here (foreign class,
    // unimplemented interface, static). HandlerBase.CrossDispatchLocalCallee mirrors this for the
    // per-site Reentrant marking at emission.
    IMethodSymbol CrossDispatchLocalTarget(IMethodSymbol target)
    {
        if (target == null || target.IsStatic) return null;
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            var impl = (_classSymbol.FindImplementationForInterfaceMember(target)
                        ?? _classSymbol.FindImplementationForInterfaceMember(target.OriginalDefinition))
                       as IMethodSymbol;
            // FindImplementationForInterfaceMember returns the chain ROOT ([W5]) — the dispatch runs
            // the receiver program's most-derived override, so leaf-resolve like the class flavor.
            return impl == null ? null : ResolveLeafOverrideDef(impl.OriginalDefinition);
        }
        for (var t = _classSymbol; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target.ContainingType))
                return ResolveLeafOverrideDef(target.OriginalDefinition);
        return null;
    }

    /// <summary>[V1] arm shared by CollectInternalCallees / IsInternalCallTo / PropertyAccessorMatches:
    /// true when <paramref name="op"/> is a variable-receiver (or interface-typed) member access whose
    /// cross dispatch can land back on this program. Interface members dispatch cross for EVERY
    /// receiver shape (a `(IFace)this` cast wraps the instance reference in a conversion).</summary>
    static bool IsCrossDispatchReceiver(IOperation instance, ISymbol member)
        => instance != null
           && (instance is not IInstanceReferenceOperation
               || member.ContainingType?.TypeKind == TypeKind.Interface);

    // True if the caller body contains a call to callee that is NOT in tail position (its result is used
    // by something after the call, so the caller's live values would be clobbered by a recursive re-entry).
    // The walk itself lives in TailCallAnalysis (shared with EmitPolicy.IsNonTailDispatchSite); this is
    // the named-callee matcher's parameterization of it — `checkReturnInstanceLeg: true` and
    // `ternaryPreciseReturn: false` reproduce this classifier's own return-position behavior exactly
    // (see TailCallAnalysis's file header for what those two differences from the dispatch-site
    // classifier actually are).
    bool HasNonTailCallTo(IOperation op, IMethodSymbol callee)
        => TailCallAnalysis.HasNonTailCall(op,
            (IOperation o, out IOperation matched) => IsInternalCallTo(o, callee, out matched),
            (pr, getter) => PropertyAccessorMatches(pr, callee, getter),
            checkReturnInstanceLeg: true,
            ternaryPreciseReturn: false);

    // [Y5]/[Y6]/[Y13]: accessor-SPECIFIC twin of IsInternalCallTo's property arm — true when the
    // chosen accessor (static binding OR the emission-faithful leaf override) IS the callee. The
    // either-accessor match is correct for simple SET (only the setter runs) but too coarse for
    // compound/inc-dec, where the getter runs first and its result is read afterwards.
    bool PropertyAccessorMatches(IPropertyReferenceOperation pr, IMethodSymbol callee, bool getter)
    {
        var acc = getter ? pr.Property.GetMethod : pr.Property.SetMethod;
        // Wave-12 r2 [V1]: variable-receiver / interface accessor dispatch — match the local method
        // the cross dispatch can land on (same rationale as IsInternalCallTo's cross arms).
        if (IsCrossDispatchReceiver(pr.Instance, pr.Property))
            return CrossDispatchLocalTarget(acc) is { } xacc
                && SymbolEqualityComparer.Default.Equals(xacc, callee);
        // Wave-14 r4: struct accessor on a fresh instance (a `next[d-1] += ..` / `next.P--` compound or
        // inc-dec through a struct-typed local) — the specific get/set accessor on a user-struct receiver
        // is the callee, independent of a `this` receiver (mirrors the IsInternalCallTo struct arm).
        if (pr.Property is { IsStatic: false } && pr.Property.ContainingType is INamedTypeSymbol saCt
            && EmitPolicy.IsObjectArrayEmulated(saCt)
            && acc != null && SymbolEqualityComparer.Default.Equals(acc.OriginalDefinition, callee))
            return true;
        if (pr.Instance is not IInstanceReferenceOperation) return false;
        if (acc != null && SymbolEqualityComparer.Default.Equals(acc.OriginalDefinition, callee))
            return true;
        if (LeafPropertyTarget(pr) is { } lp)
        {
            var leafAcc = getter ? lp.GetMethod : lp.SetMethod;
            if (leafAcc != null && SymbolEqualityComparer.Default.Equals(leafAcc.OriginalDefinition, callee))
                return true;
        }
        return false;
    }

    /// <summary>[V1 unification] The per-NODE call-target classifier shared by the recursion-graph edge
    /// walk (<see cref="CollectInternalCallees"/>) and the per-site non-tail classifier
    /// (<see cref="IsInternalCallTo"/>). Yields every method (OriginalDefinition, or the emission-faithful
    /// leaf/cross target) that a SINGLE operation node can dispatch to: an invocation's static /
    /// this-virtual-leaf-override / variable-or-interface-cross targets; a ctor; and a property or indexer
    /// reference's this / leaf-override / variable-or-interface-cross / user-struct accessor pairs (both
    /// get and set, conservatively — a write-position reference yielding the getter only over-spills,
    /// §8-3, never corrupts). Each arm was VM-proven necessary (wave-9 r2/r3 [W1..W3], wave-12 r2 [V1],
    /// wave-14 r4): recursion threaded through leaf-override / variable-receiver / fresh-struct-instance
    /// accessors was invisible to the SCC analysis and the accessor frame never spilled (e.g. 5 vs CLR 11,
    /// 305 vs 605, computed-property factorial 1 vs 120). Extracting the two formerly hand-mirrored switches
    /// into ONE enumerator removes the drift that caused those wave-14 r4 miscompiles — the arms can no
    /// longer fall out of lockstep. Includes the user-defined OPERATOR edge (binary / unary /
    /// compound-assignment / increment-decrement forms all carry an OperatorMethod) — B49: it formerly
    /// lived only in CollectInternalCallees, so IsInternalCallTo could not see it and a recursive struct
    /// operator was never frame-spilled (VM-proven ref=15/usugar=0); routing it through here makes both
    /// consumers agree and fixes the spill.</summary>
    IEnumerable<IMethodSymbol> EnumerateInternalCallTargets(IOperation op)
    {
        switch (op)
        {
            case IInvocationOperation inv:
                yield return inv.TargetMethod.OriginalDefinition;
                if (LeafCallTarget(inv) is { } leafT) yield return leafT;
                if (IsCrossDispatchReceiver(inv.Instance, inv.TargetMethod)
                    && CrossDispatchLocalTarget(inv.TargetMethod) is { } crossT)
                    yield return crossT;
                // CA-v2b-2: a base-typed polymorphic call (not `this`/`base`, handled by LeafCallTarget)
                // dispatches to EVERY override in its closed-world set — yield each so the recursion-graph
                // edge walk AND the per-site non-tail spill classifier (both read this one enumerator) see a
                // recursive override (Branch.Sum → Branch.Sum). Over-yield only ever over-spills (sound).
                if (VirtualDispatch.IsVirtualCall(inv.TargetMethod)
                    && inv.Instance is not IInstanceReferenceOperation
                    && inv.Instance?.Type is INamedTypeSymbol vrecv && EmitPolicy.IsUserClassType(vrecv))
                    foreach (var vt in _ctx.VirtualDispatch.ResolveTargets(vrecv, inv.TargetMethod))
                        yield return vt.Impl.OriginalDefinition;
                break;
            case IObjectCreationOperation { Constructor: { } ctor }:
                yield return ctor.OriginalDefinition;
                break;
            case IPropertyReferenceOperation pr:
                // this-receiver accessor call (both accessors + the emission-faithful leaf override).
                if (pr.Instance is IInstanceReferenceOperation)
                {
                    if (pr.Property.GetMethod is { } pg) yield return pg.OriginalDefinition;
                    if (pr.Property.SetMethod is { } ps) yield return ps.OriginalDefinition;
                    if (LeafPropertyTarget(pr) is { } lp)
                    {
                        if (lp.GetMethod is { } lg) yield return lg.OriginalDefinition;
                        if (lp.SetMethod is { } ls) yield return ls.OriginalDefinition;
                    }
                }
                // variable-receiver / interface-typed accessor dispatch that can land back on this program.
                if (IsCrossDispatchReceiver(pr.Instance, pr.Property))
                {
                    if (CrossDispatchLocalTarget(pr.Property.GetMethod) is { } cg) yield return cg;
                    if (CrossDispatchLocalTarget(pr.Property.SetMethod) is { } cs) yield return cs;
                }
                // computed property / indexer on a USER-STRUCT receiver — `this` OR a fresh struct
                // instance (structs compile into this program's accessor functions).
                if (pr.Property is { IsStatic: false } sprop
                    && sprop.ContainingType is INamedTypeSymbol sprct && EmitPolicy.IsObjectArrayEmulated(sprct))
                {
                    if (sprop.GetMethod is { } sg) yield return sg.OriginalDefinition;
                    if (sprop.SetMethod is { } ss) yield return ss.OriginalDefinition;
                }
                break;
        }
        // User-defined operator call — every form that resolves one carries the OperatorMethod: a plain
        // `a + b` (IBinaryOperation) / `-a` (IUnaryOperation), a `a += b` (ICompoundAssignmentOperation),
        // and a `a++`/`--a` (IIncrementOrDecrementOperation). A BCL operator has a null OperatorMethod and
        // is naturally excluded; the consumers' internalMethods / callee filter restricts to registered
        // struct operators. (B49 — see the summary above.)
        var opMethod = (op as IBinaryOperation)?.OperatorMethod
            ?? (op as IUnaryOperation)?.OperatorMethod
            ?? (op as ICompoundAssignmentOperation)?.OperatorMethod
            ?? (op as IIncrementOrDecrementOperation)?.OperatorMethod;
        if (opMethod != null) yield return opMethod.OriginalDefinition;
    }

    bool IsInternalCallTo(IOperation op, IMethodSymbol callee, out IOperation call)
    {
        call = null;
        foreach (var t in EnumerateInternalCallTargets(op))
            if (SymbolEqualityComparer.Default.Equals(t, callee)) { call = op; return true; }
        return false;
    }

    // Collect every local function declared anywhere in an operation tree (transitive: nested too).
    static void CollectLocalFunctions(IOperation op, List<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is ILocalFunctionOperation lf && lf.Symbol != null)
            result.Add(lf.Symbol.OriginalDefinition);
        foreach (var child in op.ChildOps())
            CollectLocalFunctions(child, result);
    }

    // Round-7 follow-up [Q5]: direct this-FIELD touches (field reference through an implicit/
    // explicit this/base receiver) + this-property ACCESSOR edges of one graph node. Nested local
    // functions / lambdas are skipped like CollectInternalCallees — each is its own node, and the
    // touch closure unions callee sets over the call graph (real + accessor + synthetic edges).
    static void CollectThisFieldTouches(IOperation op, HashSet<IFieldSymbol> touch, HashSet<IMethodSymbol> accessorEdges)
    {
        if (op == null) return;
        switch (op)
        {
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fr when !fr.Field.IsStatic:
                touch.Add(fr.Field.OriginalDefinition);
                break;
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } pr:
                if (pr.Property.GetMethod != null) accessorEdges.Add(pr.Property.GetMethod.OriginalDefinition);
                if (pr.Property.SetMethod != null) accessorEdges.Add(pr.Property.SetMethod.OriginalDefinition);
                break;
        }
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue; // own nodes
            CollectThisFieldTouches(child, touch, accessorEdges);
        }
    }

    // Collect call targets that resolve to a registered internal method (same program, JUMP-based).
    // Nested local functions are skipped — each is analysed as its own graph node, so their internal
    // calls are not attributed to the enclosing method.
    void CollectInternalCallees(IOperation op, HashSet<IMethodSymbol> internalMethods, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        // Every call-target shape (invocation static/leaf/cross, ctor, property this/leaf/cross/user-struct,
        // and the user-defined operator) is enumerated by the shared classifier, so this walk and
        // IsInternalCallTo cannot drift (see EnumerateInternalCallTargets).
        foreach (var t in EnumerateInternalCallTargets(op))
            if (internalMethods.Contains(t)) result.Add(t);
        foreach (var child in op.ChildOps())
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue; // own nodes
            CollectInternalCallees(child, internalMethods, result);
        }
    }

    // Tarjan's strongly-connected-components algorithm (iterative, to avoid deep recursion on large graphs).
    static List<List<IMethodSymbol>> TarjanScc(IMethodSymbol[] nodes, Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> edges)
    {
        var index = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var lowlink = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var onStack = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var stack = new Stack<IMethodSymbol>();
        var sccs = new List<List<IMethodSymbol>>();
        int counter = 0;

        foreach (var start in nodes)
        {
            if (index.ContainsKey(start)) continue;
            // Iterative DFS: frame = (node, enumerator over its successors)
            var work = new Stack<(IMethodSymbol node, IEnumerator<IMethodSymbol> succ)>();
            index[start] = lowlink[start] = counter++;
            stack.Push(start); onStack.Add(start);
            work.Push((start, edges[start].GetEnumerator()));
            while (work.Count > 0)
            {
                var (node, succ) = work.Peek();
                bool descended = false;
                while (succ.MoveNext())
                {
                    var w = succ.Current;
                    if (!index.ContainsKey(w))
                    {
                        index[w] = lowlink[w] = counter++;
                        stack.Push(w); onStack.Add(w);
                        work.Push((w, edges[w].GetEnumerator()));
                        descended = true;
                        break;
                    }
                    if (onStack.Contains(w))
                        lowlink[node] = Math.Min(lowlink[node], index[w]);
                }
                if (descended) continue;
                // All successors processed: node is done.
                work.Pop();
                if (work.Count > 0)
                {
                    var parent = work.Peek().node;
                    lowlink[parent] = Math.Min(lowlink[parent], lowlink[node]);
                }
                if (lowlink[node] == index[node])
                {
                    var comp = new List<IMethodSymbol>();
                    IMethodSymbol w;
                    do { w = stack.Pop(); onStack.Remove(w); comp.Add(w); }
                    while (!SymbolEqualityComparer.Default.Equals(w, node));
                    sccs.Add(comp);
                }
            }
        }
        return sccs;
    }

    // ── Static collection helpers ──

    /// <summary>Design §1: the single provenance-tagged ReachableBodies fixpoint. ONE queue+visited
    /// (keyed by definition) and ONE GetOperation per body replace the three separate Phase-1 collector
    /// fixpoints (CollectForeignStaticMethods / CollectStructMethods / CollectBaseInstanceMethods) and the
    /// duplicated body fetches. Seeds = own+inherited method bodies (<paramref name="methods"/>) + field
    /// initializers (instance + static). Transitions = the UNION of the three current per-operation rules,
    /// reusing the existing collectors verbatim (no new shape-switch): CollectForeignStaticCallsInOperation,
    /// CollectStructMethodsInOperation, CollectBaseInstanceCallsInOperation, plus the ungated
    /// CollectStructMemberDefinitions. Propagation is UNGATED (walks open self/cross-struct bodies too, per
    /// the shared classifier), so the recursion/capture DEFINITION projection (StructMemberDefs) subsumes
    /// the former BuildRecursionInfo.structDefRoots and CaptureScope.structQueue expansions; the REGISTRATION
    /// projections keep their existing gates (IsCollectibleStructMember / IsClosedForeignStaticTarget). The
    /// union-of-rules can widen reach vs the former staged seeding (design §5-3) — that surfaces in the census.
    /// F1 (R-M3): each definition's body is fetched ONCE and retained in the result (BodyByDef), so
    /// BuildRecursionInfo and CaptureScopeAnalysis read bodies from here instead of re-fetching.</summary>
    ReachableBodies BuildReachableBodies(IMethodSymbol[] methods)
    {
        var result = new ReachableBodies();
        var foreignStatics = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var structMembers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var baseCopies = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var structMemberDefs = result.StructMemberDefs;

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default); // definitions walked
        var queue = new Queue<IMethodSymbol>();                                    // definitions to walk

        var mintWalked = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        var suppCaptureDefs = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        void Walk(IOperation body)
        {
            if (body == null) return;
            CollectForeignStaticCallsInOperation(body, foreignStatics, suppCaptureDefs); // gated: closed, non-generic
            CollectStructMethodsInOperation(body, structMembers);       // gated: IsCollectibleStructMember
            CollectBaseInstanceCallsInOperation(body, baseCopies);      // + populates _openGenericBaseDefs
            CollectStructMemberDefinitions(body, structMemberDefs);     // ungated: definition-keyed
            CollectClassMintReach(body);                                // B81: v1-class field-inits + ctor bodies
        }

        // B81 (B50's foreign-static twin on the reach side): a v1 class's field-INITIALIZER expressions and
        // ctor BODY run at `new C()` mint, so any foreign-static / struct-member they call must be Phase-1
        // registered too — but they live in the class declaration, not in the walked body tree, so the
        // collectors never reach them. On seeing a minted v1 class (deduped by constructed type), walk its
        // field-init ops and ctor body through the same Walk (recurses into nested mints; the parameterless
        // ctor the struct-member collector skips at Arguments.Length==0 is covered here directly).
        void CollectClassMintReach(IOperation op)
        {
            if (op == null) return;
            if (op is IObjectCreationOperation oc && oc.Type is INamedTypeSymbol ct
                && EmitPolicy.IsUserClassType(ct) && mintWalked.Add(ct))
            {
                result.MintedClasses.Add(ct); // CA-v2b-1: this concrete class needs a typeobj
                foreach (var initOp in EnumerateClassFieldInitOps(ct)) Walk(initOp);
                if (oc.Constructor is { IsImplicitlyDeclared: false } ctor)
                    Walk(GetMethodBodyOperation(ctor.OriginalDefinition));
                // CA-v2b-2: a virtual/override method of a minted class is a virtual-dispatch target reached
                // ONLY through the inline typeobj chain — the invocation collector sees the abstract/static
                // slot method, never this concrete override, so it would register on demand in Phase-2 AFTER
                // BuildRecursionInfo (no graph node → polymorphic recursion under-spills, the MG-arm twin at
                // EnumerateStructMemberRefs). Seed each here as a struct-member reach root + node.
                foreach (var vm in ct.GetMembers().OfType<IMethodSymbol>())
                    if ((vm.IsVirtual || vm.IsOverride) && !vm.IsAbstract
                        && vm.MethodKind == MethodKind.Ordinary && IsCollectibleStructMember(vm))
                    {
                        structMemberDefs.Add(vm.OriginalDefinition); // recursion-root set (feeds BuildRecursionInfo)
                        if (structMembers.Add(vm))                   // emit-registration set + body walk
                            Walk(GetMethodBodyOperation(vm.OriginalDefinition));
                    }
            }
            foreach (var child in op.ChildOps()) CollectClassMintReach(child);
        }

        void EnqueueDiscovered()
        {
            foreach (var m in foreignStatics) TryEnqueue(m);
            foreach (var m in structMembers) TryEnqueue(m);
            foreach (var m in baseCopies) TryEnqueue(m);
            foreach (var m in _openGenericBaseDefs) TryEnqueue(m);
            foreach (var m in structMemberDefs) TryEnqueue(m);
        }
        void TryEnqueue(IMethodSymbol m)
        {
            if (m.DeclaringSyntaxReferences.Length > 0 && visited.Add(m.OriginalDefinition))
                queue.Enqueue(m.OriginalDefinition);
        }

        foreach (var m in methods) TryEnqueue(m);
        // F1 double-seed fix: EmitFields has already spliced _staticFieldInitOps into the FRONT of
        // _fieldInitOps (§3.6, runs before this in Emit()), so _fieldInitOps covers BOTH tiers — walk it
        // once. (Walking _staticFieldInitOps as well only re-discovered the same reach, deduped by visited.)
        foreach (var (_, initOp, _) in _fieldInitOps) Walk(initOp);
        EnqueueDiscovered();

        while (queue.Count > 0)
        {
            var def = queue.Dequeue();
            var body = GetMethodBodyOperation(def);
            result.BodyByDef[def] = body; // fetch once, retained for every consumer
            Walk(body);
            EnqueueDiscovered();
        }

        // Design 2026-07-10 v3 SS2A (B89 leg A), widened 2026-07-11 (pre-fuzz audit): SUPPLEMENTARY
        // fixpoint over dropped generic-foreign-static definitions. The generic DEFINITIONS themselves
        // stay registration-free (GenericForeignStaticBodies feeds capture analysis + recursion nodes
        // only — F1/F2), but their bodies are walked with the REAL collectors and alternate with the
        // main queue until both dry: a foreign static / struct member / base copy / class mint
        // reachable ONLY through a generic-foreign-static body registers like any other reach (it
        // previously reached emission with no CFunction — loud W17D reject; the struct-member leg was
        // the same hole). Byte-neutral for programs without the shape: they have no supp-only members,
        // so the registration sets are unchanged.
        var suppQueue = new Queue<IMethodSymbol>();
        void EnqueueSupp()
        {
            foreach (var d in suppCaptureDefs)
                if (d.DeclaringSyntaxReferences.Length > 0
                    && !visited.Contains(d)
                    && !result.GenericForeignStaticBodies.ContainsKey(d))
                    suppQueue.Enqueue(d);
            suppCaptureDefs.Clear();
        }
        EnqueueSupp();
        while (suppQueue.Count > 0)
        {
            var def = suppQueue.Dequeue();
            if (result.GenericForeignStaticBodies.ContainsKey(def)) continue;
            var suppBody = GetMethodBodyOperation(def);
            result.GenericForeignStaticBodies[def] = suppBody;
            Walk(suppBody);
            EnqueueDiscovered();
            while (queue.Count > 0)
            {
                var mainDef = queue.Dequeue();
                var mainBody = GetMethodBodyOperation(mainDef);
                result.BodyByDef[mainDef] = mainBody;
                Walk(mainBody);
                EnqueueDiscovered();
            }
            EnqueueSupp();
        }

        result.ForeignStatics = foreignStatics.ToArray();
        result.StructMembers = structMembers.ToArray();
        result.BaseCopies = baseCopies.ToArray();
        return result;
    }

    /// <summary>B81: the instance field-/auto-property-INITIALIZER value operations of a v1 class, in
    /// declaration order — the reach-side twin of HandlerBase.EmitInstanceFieldInitializers (which emits
    /// them at mint). Static/const fields are excluded (const folds; statics reject). Used to Phase-1-walk
    /// a minted class's initializer expressions for foreign-static / struct-member collection.</summary>
    IEnumerable<IOperation> EnumerateClassFieldInitOps(INamedTypeSymbol classTy)
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
    static bool IsClosedForeignStaticTarget(IMethodSymbol m)
        => !(m.ContainingType is INamedTypeSymbol ct && ct.IsGenericType
             && ct.TypeArguments.Any(ta => ta is ITypeParameterSymbol));

    void CollectForeignStaticCallsInOperation(IOperation op, HashSet<IMethodSymbol> result,
        HashSet<IMethodSymbol> suppCaptureDefs = null)
    {
        if (op == null) return;
        // Design 2026-07-10 v3 SS2A (B89 leg A): the arms below deliberately DROP generic /
        // open-container foreign statics from the registration set - but their closures still need
        // capture analysis. Report the dropped definitions to the supplementary capture-roots set.
        void SuppDef(IMethodSymbol original)
            => suppCaptureDefs?.Add(original.OriginalDefinition);
        if (op is IInvocationOperation inv && IsForeignStatic(inv.TargetMethod))
        {
            var original = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
            if (!original.IsGenericMethod && IsClosedForeignStaticTarget(original))
                result.Add(original);
            else
                SuppDef(original);
        }
        // wave-13 staticro lens (2026-07-04): a delegate/method-group reference to a foreign static
        // method (`Func<int,int> f = Helper.M;`) is itself a call site the collector must see — the
        // ONLY prior collection route was IInvocationOperation, so a method referenced exclusively via
        // delegate creation never got a CFunction registered at all, and ResolveDelegateBridge's
        // fallback to the frozen LayoutPlanner (which never pre-plans non-UdonSharpBehaviour types)
        // crashed with "was not pre-planned" on legal C#.
        if (op is IMethodReferenceOperation mref && IsForeignStatic(mref.Method))
        {
            var original = mref.Method.ReducedFrom ?? mref.Method;
            if (!original.IsGenericMethod && IsClosedForeignStaticTarget(original))
                result.Add(original);
            else
                SuppDef(original);
        }
        // B47 (wave-14 r6): a STATIC COMPUTED property on a user struct/class (StaticPropHelper<T>.Doubled)
        // is referenced as an IPropertyReferenceOperation, never an invocation/method-ref, so the two arms
        // above never saw it — its accessor fell through to a bogus SystemObjectArray.__get_Doubled__
        // extern (the B46 shape, one node kind over). Collect the computed accessor(s) as foreign statics
        // (they ARE static "methods" — get_X/set_X); auto-properties (backed by a field) are excluded by
        // IsComputedProperty, BCL statics by IsForeignStatic's extern-namespace filter, and an open
        // generic containing type by the closed guard (registered on demand at its closed call site).
        if (op is IPropertyReferenceOperation spr && spr.Property.IsStatic && IsComputedProperty(spr.Property))
        {
            if (spr.Property.GetMethod is { } sg && IsForeignStatic(sg))
            {
                if (!sg.IsGenericMethod && IsClosedForeignStaticTarget(sg)) result.Add(sg);
                else SuppDef(sg);
            }
            if (spr.Property.SetMethod is { } ss && IsForeignStatic(ss))
            {
                if (!ss.IsGenericMethod && IsClosedForeignStaticTarget(ss)) result.Add(ss);
                else SuppDef(ss);
            }
        }
        foreach (var child in op.ChildOps())
            CollectForeignStaticCallsInOperation(child, result, suppCaptureDefs);
    }

    // A property is auto-implemented iff the compiler synthesized a backing field associated with it.
    // Computed (expression-bodied or block-bodied) properties have no such field and must be inlined.
    static bool IsComputedProperty(IPropertySymbol prop)
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
    static bool IsCollectibleStructMember(IMethodSymbol m)
        => m != null
            && !(m.ContainingType.IsGenericType
                && m.ContainingType.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
            && !(m.IsGenericMethod
                && m.TypeArguments.Any(ta => ta is ITypeParameterSymbol));

    /// <summary>The six non-using node shapes referencing a user-struct member (ctor/instance-method/
    /// computed-property/subpattern-property/operator/conversion), yielded as-observed (constructed
    /// symbol, ungated) — shared by CollectStructMethodsInOperation (registration: gates with
    /// IsCollectibleStructMember, keeps the constructed symbol) and CollectStructMemberDefinitions
    /// (recursion/capture-scope root expansion: ungated, projects .OriginalDefinition). `using`-resource
    /// dispose is NOT included here — the two callers handle it with different resource shapes (see
    /// each caller) and must not be merged.</summary>
    static IEnumerable<IMethodSymbol> EnumerateStructMemberRefs(IOperation op)
    {
        // Parameterized user-struct / v1-class constructor: new V(...) / new C(...).
        if (op is IObjectCreationOperation oc && oc.Constructor != null
            && oc.Type is INamedTypeSymbol nt && EmitPolicy.IsObjectArrayEmulated(nt)
            && oc.Arguments.Length > 0 && !oc.Constructor.IsImplicitlyDeclared)
            yield return oc.Constructor;
        // User-struct instance method: v.Method(...). Feature G: yield the CONSTRUCTED symbol
        // (roadmap B36 residue — a struct declaring its OWN type parameter used to be collected by
        // OriginalDefinition and rejected loudly here; now the receiver's concrete T is carried
        // through, mirroring RegisterGenericSpecialization's per-spec discipline for the
        // containing-type dimension). Non-generic structs are unaffected: tm == tm.OriginalDefinition
        // there, so this is byte-identical for them.
        if (op is IInvocationOperation inv && inv.TargetMethod is { IsStatic: false } tm
            && tm.MethodKind == MethodKind.Ordinary && !tm.IsImplicitlyDeclared
            && tm.ContainingType is INamedTypeSymbol it && EmitPolicy.IsObjectArrayEmulated(it))
            yield return tm;
        // CA-v2 M1: a `: base(...)` / `: this(...)` ctor initializer is an IInvocationOperation whose
        // target is a CONSTRUCTOR (MethodKind.Constructor, missed by the Ordinary arm above). The base
        // ctor function is otherwise never registered at Phase 1 -> its on-demand emission from the
        // derived ctor prologue lands mid-drain but its reach/recursion node is absent (VM-faulted:
        // the derived ctor jumped to an unemitted base ctor). Collect the explicit-ctor target here.
        if (op is IInvocationOperation cinv && cinv.TargetMethod is { MethodKind: MethodKind.Constructor } ctm
            && !ctm.IsImplicitlyDeclared
            && ctm.ContainingType is INamedTypeSymbol cit && EmitPolicy.IsObjectArrayEmulated(cit))
            yield return ctm;
        // MG auto-wrap (2026-07-11 wave-lite): a class/struct instance member reached ONLY as a METHOD
        // GROUP (`o.M` -> a receiver-bridge delegate) is otherwise invisible to this collector (which
        // sees invocations), so it registered on demand at emit time AFTER BuildRecursionInfo -> no
        // graph node, no reentrancy spill when the bundle re-enters it ([Z1]/[Y7] class, VM-proven
        // under-spill: a self-recursive class MG returned 25025 vs the CLR's 40025). Seed it here so
        // it becomes a reach root + graph node + escape target.
        if (op is IMethodReferenceOperation mgr && mgr.Method is { IsStatic: false } mgm
            && mgm.MethodKind == MethodKind.Ordinary && !mgm.IsImplicitlyDeclared
            && mgm.ContainingType is INamedTypeSymbol mgit && EmitPolicy.IsObjectArrayEmulated(mgit))
            yield return mgm;
        // Computed (non-auto) user-struct property: v.Prop (read) or v.Prop = x (write). Auto-properties use
        // their backing-field slot directly (no method), but a computed accessor must be inlined as a struct
        // instance method. Yield both accessors (the reference alone doesn't reveal read-vs-write context).
        // A user-struct indexer (s[i]) is just a parameterized computed property (never auto-backed), so it
        // is collected the same way — its accessors carry the index args after the synthetic receiver.
        if (op is IPropertyReferenceOperation pr
            && pr.Property is { IsStatic: false } prop
            && pr.Property.ContainingType is INamedTypeSymbol pit && EmitPolicy.IsObjectArrayEmulated(pit)
            && IsComputedProperty(prop))
        {
            if (prop.GetMethod != null) yield return prop.GetMethod;
            if (prop.SetMethod != null) yield return prop.SetMethod;
        }
        // Property-pattern subpattern: `p is { Doubled: ... }` reads Doubled via an IMPLICIT getter call,
        // not an explicit IPropertyReferenceOperation, so yield a computed user-struct property's getter
        // here too — else the pattern lowering emits a bogus accessor extern for an unregistered getter.
        if (op is IPropertySubpatternOperation sub && sub.Member is IPropertyReferenceOperation spr
            && spr.Property is { IsStatic: false } sprop
            && spr.Property.ContainingType is INamedTypeSymbol spit && EmitPolicy.IsObjectArrayEmulated(spit)
            && IsComputedProperty(sprop) && sprop.GetMethod != null)
            yield return sprop.GetMethod;
        // User-struct operator: v1 + v2, -v, s += t, c++ (static operator methods). Compound-assignment and
        // increment/decrement carry their operator method too, so yield those so the emit side can JUMP to
        // the user operator instead of a bogus SystemObjectArray.__op_* extern.
        var opMethod = (op as IBinaryOperation)?.OperatorMethod
            ?? (op as IUnaryOperation)?.OperatorMethod
            ?? (op as ICompoundAssignmentOperation)?.OperatorMethod
            ?? (op as IIncrementOrDecrementOperation)?.OperatorMethod;
        if (opMethod is { MethodKind: MethodKind.UserDefinedOperator }
            && opMethod.ContainingType is INamedTypeSymbol ot && EmitPolicy.IsObjectArrayEmulated(ot))
            yield return opMethod;
        // User-struct CONVERSION operator (implicit/explicit). MethodKind is Conversion (not UserDefinedOperator),
        // so it needs its own arm — invoked implicitly by an IConversionOperation, routed to the method on emit.
        if (op is IConversionOperation convOp && convOp.OperatorMethod is { MethodKind: MethodKind.Conversion } convM
            && convM.ContainingType is INamedTypeSymbol convCt && EmitPolicy.IsObjectArrayEmulated(convCt))
            yield return convM;
    }

    void CollectStructMethodsInOperation(IOperation op, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        foreach (var m in EnumerateStructMemberRefs(op))
            if (IsCollectibleStructMember(m)) result.Add(m);
        // `using` resource: the Dispose() is invoked IMPLICITLY (no IInvocationOperation in the tree), so
        // collect a user-struct disposable's Dispose so it is registered as a struct method and the using
        // lowering can JUMP to it instead of emitting a non-existent SystemObjectArray.__Dispose__ extern.
        if (op is IUsingOperation uo) CollectUsingDispose(uo.Resources, result);
        if (op is IUsingDeclarationOperation ud) CollectUsingDispose(ud.DeclarationGroup, result);
        foreach (var child in op.ChildOps())
            CollectStructMethodsInOperation(child, result);
    }

    /// <summary>The ungated definition-side counterpart of CollectStructMethodsInOperation — same node
    /// shapes (ctor/instance-method/computed-property/operator/conversion on a user struct), but collects
    /// the method's OriginalDEFINITION unfiltered (no IsCollectibleStructMember gate) instead of a
    /// constructed CFunction-ready symbol. Definition-keyed regardless of instantiation (mirroring
    /// "_methodFunctions.Keys.Select(OriginalDefinition)"), so it discovers a struct member that Phase-1
    /// registration deliberately skipped (the open self/cross-struct-method form) but that the emitted
    /// program can still recursively re-enter or capture into.</summary>
    // internal: the ungated struct-member DEFINITION transition of the single ReachableBodies fixpoint
    // (BuildReachableBodies). Its StructMemberDefs projection feeds BOTH BuildRecursionInfo roots and
    // CaptureScope roots — one set, one definition, one walk (design §1, consumers 2 & 3).
    internal static void CollectStructMemberDefinitions(IOperation op, HashSet<IMethodSymbol> defs)
    {
        if (op == null) return;
        foreach (var m in EnumerateStructMemberRefs(op))
            defs.Add(m.OriginalDefinition);
        if (op is IUsingOperation or IUsingDeclarationOperation)
        {
            var resources = op is IUsingOperation uo2 ? uo2.Resources : ((IUsingDeclarationOperation)op).DeclarationGroup;
            if (resources is IVariableDeclarationGroupOperation g)
                foreach (var decl in g.Declarations)
                    foreach (var declarator in decl.Declarators)
                        if (declarator.Symbol.Type is INamedTypeSymbol dnt
                            && EmitPolicy.FindStructDisposeMethod(dnt) is { } dispose)
                            defs.Add(dispose.OriginalDefinition);
        }
        foreach (var child in op.ChildOps())
            CollectStructMemberDefinitions(child, defs);
    }

    static void CollectUsingDispose(IOperation resources, HashSet<IMethodSymbol> result)
    {
        if (resources is IVariableDeclarationGroupOperation g)
        {
            foreach (var decl in g.Declarations)
                foreach (var declarator in decl.Declarators)
                    AddStructDispose(declarator.Symbol.Type, result);
        }
        else if (resources != null)
        {
            AddStructDispose(resources.Type, result);
        }
    }

    static void AddStructDispose(ITypeSymbol type, HashSet<IMethodSymbol> result)
    {
        if (type is INamedTypeSymbol nt && EmitPolicy.IsUserStruct(nt)
            && EmitPolicy.FindStructDisposeMethod(nt) is { } dispose
            && IsCollectibleStructMember(dispose))
            result.Add(dispose);
    }

    bool IsBaseInstanceMethod(IMethodSymbol method)
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
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, _classSymbol)) return false;
        if (USugarCompilerHelper.IsFrameworkNamespace(method.ContainingType.ContainingNamespace)) return false;
        if (method.ContainingType.Name == "UdonSharpBehaviour") return false;
        var bt = _classSymbol.BaseType;
        while (bt != null)
        {
            if (SymbolEqualityComparer.Default.Equals(bt, method.ContainingType)) return true;
            bt = bt.BaseType;
        }
        return false;
    }

    /// <summary>Wave-9 round-9 [Y5]/[Y6]: definitions of base-declared generic methods called with
    /// OPEN type args (the round-8 [Y11] on-demand-spec family). Their bodies are emitted on demand
    /// per closed call site, so the DEFINITION seeds the collectors and the recursion graph exactly
    /// like an eagerly registered base copy. Populated by CollectBaseInstanceCallsInOperation.</summary>
    readonly HashSet<IMethodSymbol> _openGenericBaseDefs = new(SymbolEqualityComparer.Default);

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

    /// <summary>C2: the authoritative body of a REACH definition — from BodyByDef, fetched once by the
    /// fixpoint. A miss is an invariant violation (the fixpoint was supposed to walk every reach root),
    /// so it throws rather than silently re-fetching. The only legitimate non-reach body is a local
    /// function discovered during recursion analysis, handled by its own explicit arm in BuildRecursionInfo.</summary>
    IOperation ReachRootBody(IMethodSymbol root)
        => root != null && _reach.BodyByDef.TryGetValue(root, out var body) ? body
         : root != null && _reach.GenericForeignStaticBodies.TryGetValue(root, out var suppBody) ? suppBody
         : throw ReachMiss(root);

    static System.InvalidOperationException ReachMiss(IMethodSymbol def)
        => new($"ReachableBodies.BodyByDef has no entry for reach definition '{def?.ToDisplayString()}' — "
             + "the reach fixpoint did not walk it (BodyByDef authoritativeness invariant violation).");

    void CollectBaseInstanceCallsInOperation(IOperation op, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        // Wave-9 round-8 [Y11]: skip OPEN-constructed generic targets (`P2<T>(x)` inside a generic
        // body — the type args are the ENCLOSING method's type params). The copy registration would
        // be keyed by a symbol no emission-time lookup ever produces (the invocation handler
        // substitutes the enclosing spec's map first), leaving a dead function whose param/return
        // types cannot resolve; the closed specialization registers lazily at the call site instead.
        // Wave-9 round-9 [Y5]/[Y6]: the skipped target's DEFINITION is still tracked — the on-demand
        // spec emits that definition's body, so it must seed the foreign-static/struct collectors
        // (using/Dispose inside it ICEd "No CFunction registered for method 'Dispose'") and join
        // BuildRecursionInfo's roots (a self-recursive base generic had no graph node, no self-edge,
        // and no spills — live locals clobbered per frame, VM-proven 63 where the CLR gives 234).
        if (op is IInvocationOperation inv && IsBaseInstanceMethod(inv.TargetMethod))
        {
            if (!(inv.TargetMethod.IsGenericMethod
                  && inv.TargetMethod.TypeArguments.Any(ta => ta is ITypeParameterSymbol)))
                result.Add(inv.TargetMethod);
            else
                _openGenericBaseDefs.Add(inv.TargetMethod.OriginalDefinition);
        }
        // base.Prop / base[i]: a property/indexer reference invokes an accessor implicitly (it is not an
        // IInvocationOperation), so collect the base accessor too — else the read/write handler emits a
        // bogus SystemX.__get_Prop__ extern instead of a JUMP to the registered base getter/setter.
        // ONLY actual `base.` receivers (round 7): a this/implicit reference to an OVERRIDDEN base
        // accessor must dispatch the chain-leaf override (ResolveDispatchProperty at the lookup sites),
        // not a base-instance copy — registering it here made the this-path lookups direct-call the
        // copy, which runs the base accessor body (manual, pre-chain-dispatch behavior) or reads the base
        // declaration's `__basebk` storage (auto, post-917d99c). Non-overridden base accessors are
        // already registered as inherited methods and never needed this arm.
        if (op is IPropertyReferenceOperation pr
            && pr.Instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax })
        {
            if (pr.Property.GetMethod is { } g && IsBaseInstanceMethod(g)) result.Add(g);
            if (pr.Property.SetMethod is { } s && IsBaseInstanceMethod(s)) result.Add(s);
        }
        // Wave-9 [W3]: a `base.M` METHOD GROUP (delegate conversion) is a non-virtual binding of the
        // BASE implementation, exactly like a `base.M()` call (C# ldftn semantics) — register the
        // base-instance copy so ResolveDelegateBridge can bridge the base BODY instead of the
        // chain-root export (which is the most-derived override in this program, VM-proven 6 vs 103).
        if (op is IMethodReferenceOperation mref
            && mref.Instance is IInstanceReferenceOperation { Syntax: BaseExpressionSyntax }
            && IsBaseInstanceMethod(mref.Method))
            result.Add(mref.Method);
        // Wave-10 round-10 [Z1]: a method GROUP of an INHERITED generic method through `this`
        // (implicit or explicit). The [Y7] bridge registers the constructed spec at EMIT time —
        // AFTER BuildRecursionInfo ran — so a self-recursive base generic referenced ONLY through
        // a delegate had no graph node, no self-edge, and no spill machinery (VM-proven r=17 where
        // the CLR gives 47: every unwinding frame replayed the innermost frame's local). Seed the
        // DEFINITION exactly like an open-T call site ([Y5] — closed and open MG flavors alike,
        // the spec lookup reduces both ends to OriginalDefinition); a non-recursive body gains a
        // trivially cycle-free node. `base.` and variable receivers stay uncollected — the [Y7]
        // creation gate rejects them loudly.
        if (op is IMethodReferenceOperation gmref && gmref.Method.IsGenericMethod
            && IsBaseInstanceMethod(gmref.Method)
            && (gmref.Instance == null
                || gmref.Instance is IInstanceReferenceOperation { Syntax: not BaseExpressionSyntax }))
            _openGenericBaseDefs.Add(gmref.Method.OriginalDefinition);
        foreach (var child in op.ChildOps())
            CollectBaseInstanceCallsInOperation(child, result);
    }

    bool IsForeignStatic(IMethodSymbol method)
    {
        var resolved = method.ReducedFrom ?? method;
        if (!resolved.IsStatic) return false;
        if (resolved.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        // Static methods on a user UdonSharpBehaviour subclass are inlinable (no instance ⇒ no cross-program
        // SendCustomEvent path); the syntax-less base/SDK behaviours are already excluded above.
        if (SymbolEqualityComparer.Default.Equals(resolved.ContainingType, _classSymbol)) return false;
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
