using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

public class UasmEmitter
{
    readonly EmitContext _ctx;
    readonly IOperationHandler[] _stmtHandlers;
    readonly IExpressionHandler[] _exprHandlers;

    public bool DumpEnabled;

    // Property shims → EmitContext
    Compilation _compilation => _ctx.Compilation;
    INamedTypeSymbol _classSymbol => _ctx.ClassSymbol;
    CModule _module => _ctx.Module;
    CoreBuilder _builder => _ctx.Builder;
    LayoutPlanner _planner => _ctx.Planner;
    Dictionary<IMethodSymbol, CFunction> _methodFunctions => _ctx.MethodFunctions;
    Dictionary<IMethodSymbol, EmitContext.MethodSlot> _methodSlots => _ctx.MethodSlots;
    Dictionary<IMethodSymbol, ReturnSlot[]> _methodReturns => _ctx.MethodReturns;
    Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.MethodParamVarIds;
    IMethodSymbol _currentMethod { get => _ctx.CurrentMethod; set => _ctx.CurrentMethod = value; }
    List<(IMethodSymbol symbol, CFunction func)> _pendingLocalFunctions => _ctx.PendingLocalFunctions;
    List<IMethodSymbol> _pendingGenericSpecs => _ctx.PendingGenericSpecs;
    Dictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap { get => _ctx.TypeParamMap; set => _ctx.TypeParamMap = value; }
    HashSet<IMethodSymbol> _inheritedMethods = new(SymbolEqualityComparer.Default);
    List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.FieldInitOps;
    Dictionary<string, string> _fieldChangeCallbacks => _ctx.FieldChangeCallbacks;
    List<EmitDiagnostic> _diagnostics => _ctx.Diagnostics;

    CodeGenResult _codeGenResult;

    public IReadOnlyList<EmitDiagnostic> Diagnostics => _diagnostics;
    public CodeGenResult CodeGenResult => _codeGenResult;

    static Dictionary<string, string> UdonEventNames => LayoutPlanner.UdonEventNames;

    public UasmEmitter(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner = null)
    {
        _ctx = new EmitContext(compilation, classSymbol, planner ?? new LayoutPlanner(compilation));

        var stmtHandler = new StatementHandler(_ctx);
        var loopHandler = new LoopHandler(_ctx);
        var switchHandler = new SwitchHandler(_ctx);
        var deconstructHandler = new DeconstructionAssignmentHandler(_ctx);
        var simpleAssignHandler = new SimpleAssignmentHandler(_ctx);
        var compoundAssignHandler = new CompoundAssignmentHandler(_ctx);
        var operatorHandler = new OperatorHandler(_ctx);

        _stmtHandlers = new IOperationHandler[] { stmtHandler, loopHandler, switchHandler, deconstructHandler };
        _exprHandlers = new IExpressionHandler[]
        {
            new ExpressionHandler(_ctx),
            simpleAssignHandler,
            compoundAssignHandler,
            operatorHandler,
            new InvocationHandler(_ctx),
            new ArrayHandler(_ctx),
            new NullableHandler(_ctx),
        };

        _ctx.InitializeDispatchers(VisitOperation, VisitExpression, operatorHandler.EmitPatternCheckImpl,
            operatorHandler.EmitNewAggregate);
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
    public CaptureScopeAnalysis CaptureScope => _ctx.CaptureScope;
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
        // Stage 2 M1: structural analysis only — computed and unit-tested, not consumed by codegen
        // (design §11 M1: "コード生成は未接続"). Running it unconditionally here exercises it against
        // every compiled class in the existing suite/corpus as a standing hardening check.
        _ctx.CaptureScope = CaptureScopeAnalysis.Build(_compilation, _classSymbol);
        EmitMethods();
        OnIrPass?.Invoke("after-emit", _module);
        // Handlers build Core IR; the pipeline (verify/optimize/flatten) runs on Core directly.
        var result = IrPipeline.GenerateUasmFromCore(_module, DumpEnabled);
        _codeGenResult = result;
        return result.Uasm;
    }

    public uint GetHeapSize() => _codeGenResult.HeapSize;

    void SetReflectionValues()
    {
        var typeName = _classSymbol.ToDisplayString();
        long typeId = ComputeTypeId(typeName);
        _ctx.DeclareField("__refl_typeid", "SystemInt64", defaultValue: typeId);
        _ctx.DeclareField("__refl_typename", "SystemString", defaultValue: typeName);

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

        // Defense-in-depth: detect likely parallel misuse. If we're on a thread pool
        // thread and the planner isn't frozen, this is almost certainly a bug in the
        // orchestrator — planning should have been done in the serial prep phase.
        if (!System.Threading.Thread.CurrentThread.IsBackground)
        {
            // Main thread: safe to plan lazily (test / standalone path)
        }
        else
        {
            // Background thread: log a warning. The plan-and-freeze below is still
            // safe IF this emitter has its own private planner (constructor default).
            // It is NOT safe if a shared planner was passed in.
#if UNITY_EDITOR
            UnityEngine.Debug.LogWarning(
#else
            System.Diagnostics.Debug.WriteLine(
#endif
                "[USugar] EnsurePlannerReady called on a background thread with an unfrozen planner. "
              + "This is safe only if the planner is private to this emitter instance. "
              + "Callers running in parallel MUST pass a pre-frozen LayoutPlanner.");
        }

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
        }
        _planner.Freeze();
    }

    // ── EmitFields ──

    void EmitFields()
    {
        foreach (var member in _classSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;

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
            _ctx.DeclareField(member.Name, udonType, flags, constValue, syncMode);

            // Aggregate (struct/tuple) field with NO explicit initializer → C# default-initializes it to a
            // zeroed struct. In the object[] emulation that requires a fresh default array; without it the heap
            // var stays null and `f.x = …` faults (NRE on __Set__). Reference-type/array fields stay null (correct).
            if (syntaxRef?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: not null }
                && member.Type is INamedTypeSymbol aggFieldType && EmitContext.IsAggregateType(aggFieldType))
            {
                _ctx.AggregateFieldDefaults.Add((member.Name, aggFieldType));
            }

            // Detect [FieldChangeCallback("PropertyName")]
            var fcbAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
            if (fcbAttr != null && fcbAttr.ConstructorArguments.Length > 0
                && fcbAttr.ConstructorArguments[0].Value is string propName)
            {
                _fieldChangeCallbacks[member.Name] = propName;
                _ctx.DeclareField($"__old_{member.Name}", udonType);
            }
        }

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
            _ctx.DeclareField(prop.Name, udonType, flags,
                isAuto ? ResolveAutoPropInitializer(prop.Name, prop) : null);
        }

        // Record count of derived-class field init ops; base class init ops added below
        // must be reordered to come first (C# spec: base → derived initializer order).
        int derivedFieldInitCount = _fieldInitOps.Count;
        var baseClassInitBoundaries = new List<int>(); // track boundaries per base class

        // Collect declared member SYMBOLS (name → derived-most declaration). A base member whose
        // name matches is either (a) part of one override chain — legal, one virtual slot — or
        // (b) `new`-style shadowing, where C# has TWO storages but this emitter's name-keyed heap
        // model would collapse both symbols onto ONE heap var (VM-verified silent state corruption:
        // SetBase/GetBase through the base symbol read the derived symbol's writes, and a
        // type-conflicting shadow halts the VM with HeapTypeMismatchException at runtime).
        // Storage collision is never acceptable → loud reject per design §8-3 (predates fcd-stage1).
        var declaredMemberSyms = new Dictionary<string, ISymbol>();
        foreach (var m in _classSymbol.GetMembers())
            if (m is IFieldSymbol or IPropertySymbol && !m.IsStatic && !m.IsImplicitlyDeclared
                && !declaredMemberSyms.ContainsKey(m.Name))
                declaredMemberSyms[m.Name] = m;

        // Inherited fields and properties from user-defined base classes
        var baseType = _classSymbol.BaseType;
        while (baseType != null)
        {
            if (USugarCompilerHelper.IsFrameworkNamespace(baseType.ContainingNamespace) || baseType.Name == "UdonSharpBehaviour") break;
            baseClassInitBoundaries.Add(_fieldInitOps.Count);
            foreach (var member in baseType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared) continue;
                // A FIELD can never be overridden, so any name match with a nearer declaration is
                // `new`-style shadowing — two distinct symbols, one heap var. Loud.
                if (declaredMemberSyms.TryGetValue(member.Name, out var fieldShadower))
                    throw new NotSupportedException(ShadowedStorageError(member, fieldShadower));

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

                _ctx.DeclareField(member.Name, udonType, baseFlags, constValue, baseSyncMode);

                var baseFcbAttr = member.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "FieldChangeCallbackAttribute");
                if (baseFcbAttr != null && baseFcbAttr.ConstructorArguments.Length > 0
                    && baseFcbAttr.ConstructorArguments[0].Value is string basePropName)
                {
                    _fieldChangeCallbacks[member.Name] = basePropName;
                    _ctx.DeclareField($"__old_{member.Name}", udonType);
                }
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
                            _ctx.DeclareField(BaseAutoPropBackingName(prop), GetUdonType(prop.Type), FieldFlags.None,
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
                _ctx.DeclareField(prop.Name, udonType, flags,
                    isAuto ? ResolveAutoPropInitializer(prop.Name, prop) : null);
            }
            baseType = baseType.BaseType;
        }

        // Reorder field init ops: base class initializers must run before derived (C# spec).
        // Base classes were walked nearest-parent-first, so reverse class-level order
        // while preserving field order within each class.
        if (_fieldInitOps.Count > derivedFieldInitCount)
        {
            baseClassInitBoundaries.Add(_fieldInitOps.Count); // sentinel
            var reordered = new List<(string, IOperation, ITypeSymbol)>();
            // Reverse iterate base class groups (outermost base first)
            for (int i = baseClassInitBoundaries.Count - 2; i >= 0; i--)
            {
                int start = baseClassInitBoundaries[i];
                int end = baseClassInitBoundaries[i + 1];
                for (int j = start; j < end; j++)
                    reordered.Add(_fieldInitOps[j]);
            }
            // Append derived class init ops
            for (int j = 0; j < derivedFieldInitCount; j++)
                reordered.Add(_fieldInitOps[j]);
            // Replace
            _fieldInitOps.Clear();
            _fieldInitOps.AddRange(reordered);
        }
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
    /// First-class delegate field (design §2.1/§1.6): ONE SystemObjectArray heap var holding the bundle
    /// reference, null-initialized in UASM data. Never exported (exported/synced vars must not be retyped;
    /// SetProgramVariable needs no export) — [UdonSynced] is rejected HERE (M4 [T2], single choke point):
    /// pre-fix the own-class path threw the generic IsSyncableType message while the base-class path
    /// (which reads no sync attributes at all) compiled the synced delegate field CLEAN with no .sync
    /// directive. An initializer (e.g. `public Action cb = M;`) always becomes runtime bundle construction
    /// at _start via _fieldInitOps, which also fixes the old silent drop of derived-class initializers.
    /// Shared by the derived-class and base-class field paths.
    /// </summary>
    void DeclareDelegateField(IFieldSymbol member, INamedTypeSymbol delegateType)
    {
        // M4 [T2]: Udon sync cannot carry the object[] bundle (the on-device security filter over
        // synced vars is the unverified design §8-7 risk) — loud on BOTH declaration paths.
        if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "UdonSyncedAttribute"))
            throw new NotSupportedException(
                $"[UdonSynced] delegate field '{member.Name}' cannot be synced: a delegate value is a "
                + "runtime object[] bundle (target/method/addr), which Udon sync cannot carry. Sync "
                + "plain data instead and re-create the delegate locally.");
        if (delegateType.DelegateInvokeMethod.ReturnType.IsTupleType)
            throw new NotSupportedException($"Tuple-return delegate field '{member.Name}' is not supported.");
        // §3.4-1: ref/out delegate signatures are rejected at the convention-var declaration side too.
        DelegateAbi.ValidateNoRefOutParams(delegateType.DelegateInvokeMethod);

        _ctx.DeclareField(member.Name, "SystemObjectArray", FieldFlags.None);
        _ctx.DelegateFields.Add(member.Name);

        // Declare the signature-keyed __dlgc_ convention vars for this delegate signature (§3.2).
        var invoke = delegateType.DelegateInvokeMethod;
        // envName is intentionally ignored here: a delegate FIELD declaration is not a dispatch site
        // or capturing bridge, so declaring __dlgc_{sig}__env unconditionally would break the
        // capture-free byte invariant (§1.3). It is declared on-first-use at the dispatch/bridge site.
        var (convArgs, convRet, _) = HandlerBase.GetConventionFieldNames(delegateType);
        for (int ci = 0; ci < convArgs.Length; ci++)
            _ctx.TryDeclareVar(convArgs[ci], ExternResolver.GetUdonTypeName(invoke.Parameters[ci].Type));
        if (convRet != null)
            _ctx.TryDeclareVar(convRet, ExternResolver.GetUdonTypeName(invoke.ReturnType));

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
                _fieldInitOps.Add((member.Name, initOp, member.Type));
        }
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
         + "shadows). Shadowing an inherited field/property is not supported in v2.x — rename the "
         + "member, or use virtual/override.";

    static string ExplicitInterfaceAutoPropError(IPropertySymbol prop)
        => $"Explicit interface implementation auto-property '{prop.Name}' is not supported in "
         + "v2.x: its backing storage name contains '.' and is not a valid Udon identifier. "
         + "Implement the property implicitly (public auto-property) or with manual accessors.";

    // ── EmitMethods ──

    void EmitMethods()
    {
        var directMethods = _classSymbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => (m.MethodKind == MethodKind.Ordinary
                      || m.MethodKind == MethodKind.ExplicitInterfaceImplementation
                      || m.MethodKind == MethodKind.PropertyGet
                      || m.MethodKind == MethodKind.PropertySet)
                     && !m.IsImplicitlyDeclared)
            .ToArray();

        // Collect inherited methods from user-defined base classes
        var overriddenMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var m in directMethods)
        {
            var cur = m.OverriddenMethod;
            while (cur != null)
            {
                overriddenMethods.Add(cur);
                cur = cur.OverriddenMethod;
            }
        }
        var inheritedMethodsList = new List<IMethodSymbol>();
        var inheritBase = _classSymbol.BaseType;
        while (inheritBase != null && inheritBase.Name != "UdonSharpBehaviour")
        {
            if (!inheritBase.DeclaringSyntaxReferences.IsEmpty)
            {
                // Round-8 [R3]: inherit ExplicitInterfaceImplementation methods too, mirroring the
                // planner's inherit loop — the derived program must emit and export the __iface_*
                // bridge for a base class's explicit implementation (pre-fix: never-exported
                // dispatch name, silent no-op).
                foreach (var bm in inheritBase.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => (m.MethodKind == MethodKind.Ordinary
                              || m.MethodKind == MethodKind.ExplicitInterfaceImplementation
                              || m.MethodKind == MethodKind.PropertyGet
                              || m.MethodKind == MethodKind.PropertySet)
                             && !m.IsImplicitlyDeclared && !m.IsGenericMethod && !m.IsAbstract))
                {
                    if (!overriddenMethods.Contains(bm))
                    {
                        inheritedMethodsList.Add(bm);
                        // Wave-9 [W4] (mirrors the planner's inherit loop): an inherited override owns
                        // its chain's virtual slot — never emit the overridden ROOT declaration as a
                        // second standalone function (its auto-prop body reads the dead __basebk
                        // storage / its method body is the base body, and a root-typed receiver
                        // dispatch bound that stale function instead of the exported override).
                        for (var cur = bm.OverriddenMethod; cur != null; cur = cur.OverriddenMethod)
                            overriddenMethods.Add(cur);
                    }
                }
            }
            inheritBase = inheritBase.BaseType;
        }
        _inheritedMethods = new HashSet<IMethodSymbol>(inheritedMethodsList, SymbolEqualityComparer.Default);
        var methods = directMethods.Concat(inheritedMethodsList).ToArray();

        var typeLayout = _planner.GetLayout(_classSymbol);

        // First pass: create IrFunctions, assign params, return vars (skip generic definitions)
        _ctx.NextMethodIndex = 0;
        foreach (var method in methods)
        {
            EmitContext.RejectInParameters(method); // round-7 follow-up [Q3], declaration-side
            EmitContext.RejectNetworkCallableDelegates(method); // M4 [T1], declaration-side
            if (method.IsGenericMethod) continue;

            var ml = typeLayout.Methods[method];
            var exportName = ml.ExportName;
            var slot = _ctx.RegisterMethod(method, _ => exportName);
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
                _ctx.DeclareVar(ml.ParamIds[i], GetUdonType(method.Parameters[i].Type));
                paramVarIds[i] = ml.ParamIds[i];
            }
            _methodParamVarIds[method] = paramVarIds;
            foreach (var pid in paramVarIds) func.ParamFieldNames.Add(pid);

            // Declare return var(s) from unified Returns
            if (ml.Returns.Count > 0)
            {
                foreach (var ret in ml.Returns)
                    _ctx.DeclareVar(ret.Id, ret.UdonType);

                if (ml.Returns.Count == 1)
                    func.ReturnType = ml.Returns[0].UdonType;
                else
                    func.ReturnType = "SystemVoid"; // tuple: no single return value

                foreach (var ret in ml.Returns)
                    func.ReturnSlots.Add(ret);

                _methodReturns[method] = ml.Returns.ToArray();
            }
        }

        // Wave-9 round-5 [X5]: base-instance copies are computed BEFORE the foreign-static/struct
        // collectors so their BODIES seed both scans — a using/Dispose (or any struct/foreign call)
        // inside a base virtual body reached via base.M previously had no CFunction registered
        // ("No CFunction registered for method 'Dispose'" ICE on legal C#). Registration ORDER below
        // is unchanged (foreign statics → struct methods → base copies) for name-index stability.
        var methodSet = new HashSet<IMethodSymbol>(methods, SymbolEqualityComparer.Default);
        var baseInstanceMethods = CollectBaseInstanceMethods(methods)
            .Where(bm => !methodSet.Contains(bm))
            .ToArray();
        // Wave-9 round-9 [Y6]: open-generic base definitions seed the collectors too — their bodies
        // emit on demand (round-8 [Y11]) and a struct/foreign-static call inside them (e.g. a
        // using/Dispose) previously had no CFunction registered (loud ICE on legal C#). They are
        // NOT registered as copies (no single monomorphization) — seeds only.
        var collectorSeeds = methods.Concat(baseInstanceMethods)
            .Concat(_openGenericBaseDefs.Where(d => !methodSet.Contains(d)))
            .ToArray();

        // Collect foreign static methods
        var foreignStatics = CollectForeignStaticMethods(collectorSeeds);
        foreach (var fm in foreignStatics)
        {
            EmitContext.RejectInParameters(fm); // round-7 follow-up [Q3]
            var slot = _ctx.RegisterMethod(fm, i => i.ToString());
            var idx = slot.Index;
            var funcName = $"__{idx}_{SanitizeId(fm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[fm] = func;

            var fmParamIds = new string[fm.Parameters.Length];
            for (int pi = 0; pi < fm.Parameters.Length; pi++)
            {
                var param = fm.Parameters[pi];
                var paramId = $"__{idx}_{param.Name}__param";
                _ctx.DeclareVar(paramId, GetUdonType(param.Type));
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
        var structMethods = CollectStructMethods(collectorSeeds);
        foreach (var sm in structMethods)
        {
            EmitContext.RejectInParameters(sm); // round-7 follow-up [Q3]
            var slot = _ctx.RegisterMethod(sm, i => i.ToString());
            var idx = slot.Index;
            var isCtor = sm.MethodKind == MethodKind.Constructor;
            var funcName = isCtor
                ? $"__{idx}_{SanitizeId(sm.ContainingType.Name)}__ctor"
                : $"__{idx}_{SanitizeId(sm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[sm] = func;

            // param0 = receiver object[] for instance methods/ctors (passed uncloned so in-place mutation
            // reflects back to the caller's local). Static operator methods have no receiver.
            if (!sm.IsStatic)
            {
                var receiverId = $"__{idx}_this__param";
                _ctx.DeclareVar(receiverId, "SystemObjectArray");
                func.ParamFieldNames.Add(receiverId);
            }

            var smParamIds = new string[sm.Parameters.Length];
            for (int pi = 0; pi < sm.Parameters.Length; pi++)
            {
                var p = sm.Parameters[pi];
                var pid = $"__{idx}_{p.Name}__param";
                _ctx.DeclareVar(pid, GetUdonType(p.Type));
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
            EmitContext.RejectInParameters(bm); // round-7 follow-up [Q3]
            // Wave-9 round-8 [Y10]: an INHERITED generic method's call-site-constructed copy is the
            // de-facto specialization this path emits (EmitMethod sets the type-param map from it),
            // but it bypassed RegisterGenericSpecialization — so FirstGenericSpec never learned it
            // and a hoisted closure inside the base generic body could not resolve the enclosing
            // method's params (loud "Cannot resolve parameter") or its type-param map. Seed it here,
            // with the same second-distinct-instantiation guard ([X6] r5, widened in round 8).
            if (bm.IsGenericMethod && !bm.IsDefinition)
            {
                var bmDef = bm.OriginalDefinition;
                if (_ctx.FirstGenericSpec.TryGetValue(bmDef, out var bmFirst))
                {
                    if (!SymbolEqualityComparer.Default.Equals(bmFirst, bm))
                        EmitContext.ThrowIfClosurePinsInstantiation(
                            _ctx.GenericBodyClosurePin(_compilation, bmDef), bm.Name);
                }
                else
                    _ctx.FirstGenericSpec[bmDef] = bm;
            }
            var slot = _ctx.RegisterMethod(bm, i => i.ToString());
            var idx = slot.Index;
            var funcName = $"__{idx}_{SanitizeId(bm.Name)}";
            var func = _module.AddFunction(funcName);
            _methodFunctions[bm] = func;

            var bmParamIds = new string[bm.Parameters.Length];
            for (int pi = 0; pi < bm.Parameters.Length; pi++)
            {
                var param = bm.Parameters[pi];
                var paramId = $"__{idx}_{param.Name}__param";
                _ctx.DeclareVar(paramId, GetUdonType(param.Type));
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
        if ((_fieldInitOps.Count > 0 || _fieldChangeCallbacks.Count > 0 || _ctx.AggregateFieldDefaults.Count > 0)
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
        while (_pendingLocalFunctions.Count > 0 || _pendingGenericSpecs.Count > 0)
        {
            if (_pendingLocalFunctions.Count > 0)
            {
                var batch = _pendingLocalFunctions.ToList();
                _pendingLocalFunctions.Clear();
                foreach (var (sym, _) in batch)
                    EmitMethod(sym);
            }
            if (_pendingGenericSpecs.Count > 0)
            {
                var batch = _pendingGenericSpecs.ToList();
                _pendingGenericSpecs.Clear();
                foreach (var spec in batch)
                    EmitMethod(spec);
            }
        }

        // Emit pending delegate bridges for hoisted lambdas/local functions
        EmitPendingDelegateBridges();

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
                    _ctx.TryDeclareVar(ifaceMl.ParamIds[i], udonType);
                }
            }
            if (ifaceMl.ReturnId != null && ifaceMl.ReturnId != classMl.ReturnId)
            {
                var retType = GetUdonType(ifaceMethod.ReturnType);
                _ctx.TryDeclareVar(ifaceMl.ReturnId, retType);
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
            // Skip methods with tuple returns (not supported as delegate targets)
            if (!method.ReturnsVoid && method.ReturnType.IsTupleType) continue;

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
                _ctx.TryDeclareVar($"__dlgc_{sigPart}__a{i}", argType);
            }
            if (!method.ReturnsVoid)
            {
                var retType = ExternResolver.GetUdonTypeName(method.ReturnType);
                _ctx.TryDeclareVar($"__dlgc_{sigPart}__ret", retType);
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
                var convName = $"__dlgc_{sigPart}__a{i}";
                callArgs.Add(BridgeLoad(convName, argType));
            }

            var retTypeStr = method.ReturnsVoid ? "SystemVoid" : ExternResolver.GetUdonTypeName(method.ReturnType);
            var callResult = _builder.InternalCall(realFunc.Name, callArgs, retTypeStr);

            if (!method.ReturnsVoid)
            {
                var convRet = $"__dlgc_{sigPart}__ret";
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

    void EmitPendingDelegateBridges()
    {
        var emitted = new HashSet<string>();
        foreach (var (method, bridgeExportName, resolvedMap) in _ctx.PendingDelegateBridges)
        {
            if (!emitted.Add(bridgeExportName)) continue;
            if (!_methodFunctions.TryGetValue(method, out var realFunc)) continue;
            if (!method.ReturnsVoid && method.ReturnType.IsTupleType) continue;

            // §3.4-1 conv-var declaration side check. Pending bridges are delegate-originated by
            // construction (creation already validated), but a future registration path must stay loud.
            DelegateAbi.ValidateNoRefOutParams(method);

            // Use the saved type param snapshot instead of _typeParamMap (which may be cleared)
            var sigPart = DelegateAbi.BuildSigPart(method, resolvedMap);

            // Declare convention fields (if not already declared)
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = ExternResolver.GetUdonTypeName(method.Parameters[i].Type, resolvedMap);
                _ctx.TryDeclareVar($"__dlgc_{sigPart}__a{i}", argType);
            }
            if (!method.ReturnsVoid)
            {
                var retType = ExternResolver.GetUdonTypeName(method.ReturnType, resolvedMap);
                _ctx.TryDeclareVar($"__dlgc_{sigPart}__ret", retType);
            }

            // Build bridge function
            var bridgeFunc = _module.AddFunction(bridgeExportName, bridgeExportName);

            var prevFunc = _builder.CurrentFunction;
            _builder.SetFunction(bridgeFunc);

            // Copy convention fields → real param fields, then call real method
            var callArgs = new List<CLeaf>();
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = ExternResolver.GetUdonTypeName(method.Parameters[i].Type, resolvedMap);
                var convName = $"__dlgc_{sigPart}__a{i}";
                callArgs.Add(BridgeLoad(convName, argType));
            }

            var retTypeStr = method.ReturnsVoid ? "SystemVoid" : ExternResolver.GetUdonTypeName(method.ReturnType, resolvedMap);
            var convRet = method.ReturnsVoid ? null : $"__dlgc_{sigPart}__ret";

            void EmitBridgeCall(List<CLeaf> args)
            {
                var callResult = _builder.InternalCall(realFunc.Name, args, retTypeStr);
                if (!method.ReturnsVoid) BridgeStore(convRet, callResult);
                else _builder.EmitExprStmt(callResult);
            }

            // Stage 2 §5.1: a CAPTURING target's bridge consumes the staged env global as the trailing
            // arg (positional copy-in binds it to the real function's __envp param field) under an
            // env-null LOUD guard — a hand-rolled object[] or a mixed-world old dispatcher leaves the
            // env unset, which must LogError + default, not fault or silently read garbage. Non-
            // capturing bridges (named methods, capture-free lambdas) are byte-unchanged.
            if (_ctx.CaptureScope != null && _ctx.CaptureScope.IsCapturingClosure(method))
            {
                var envConv = $"__dlgc_{sigPart}__env";
                _ctx.TryDeclareVar(envConv, EnvEmit.EnvType);
                callArgs.Add(BridgeLoad(envConv, EnvEmit.EnvType));
                var envOk = BridgeCallExtern("SystemBoolean",
                    "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                    new[] { BridgeLoad(envConv, EnvEmit.EnvType), _builder.Const(null, "SystemObject") });
                _builder.EmitIf(envOk,
                    _ => EmitBridgeCall(callArgs),
                    _ =>
                    {
                        BridgeCallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                            new[] { (CLeaf)_builder.Const(
                                $"USugar: missing closure environment — invoked a captured delegate whose bundle carries no env ({method.Name})",
                                "SystemString") });
                        if (!method.ReturnsVoid)
                            BridgeStore(convRet, InvocationHandler.DefaultConst(_builder, retTypeStr));
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
    }

    static string SanitizeId(string name) => name.Replace('.', '_');

    // ── EmitMethod ──

    void EmitMethod(IMethodSymbol method)
    {
        _currentMethod = method;
        var func = _methodFunctions[method];

        // Struct instance methods/ctors carry the receiver object[] as synthetic param0; make `this`
        // resolve to it for the body. Static (operator) struct methods have no receiver.
        _ctx.CurrentStructReceiverParamId =
            (method.ContainingType is INamedTypeSymbol structCt && EmitContext.IsUserStruct(structCt) && !method.IsStatic)
                ? func.ParamFieldNames[0] : null;

        bool isGenericSpec = method.IsGenericMethod && !method.IsDefinition;

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
        var exportName = _methodSlots[method].VarPrefix;
        if (exportName == "_start")
            EmitFieldInitializers();

        // Set up type param map for generic specializations
        if (isGenericSpec)
        {
            var orig = method.OriginalDefinition;
            var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            for (int i = 0; i < orig.TypeParameters.Length; i++)
                map[orig.TypeParameters[i]] = method.TypeArguments[i];
            _typeParamMap = map;
        }

        // Wave-9 round-8 [Y2]: a hoisted closure (lambda / local function) declared inside a GENERIC
        // method body — its operation tree is the generic DEFINITION's, so T-typed expressions need
        // the instantiation's type-param map during body emission (registration already substituted
        // the signature types while the enclosing spec's map was active; without the map here the
        // body type-checks as 'T' and CoreVerify ICEs on a single legal instantiation). A closure
        // whose semantics depend on T pins its generic to ONE instantiation (the [X6] r5 reject,
        // widened in round 8 to type-param-referencing closures), so FirstGenericSpec is the exact
        // owner. Walk up through enclosing closures to (possibly nested) generic owners.
        // Round-9 [Y8]: also runs for a generic LOCAL FUNCTION spec (isGenericSpec) nested in a
        // generic method — the spec map above holds only the LF's OWN type params, so the
        // enclosing generic's params are MERGED in (never replacing the spec map).
        bool closureMapSet = false;
        if (method.MethodKind is MethodKind.LocalFunction
            or MethodKind.LambdaMethod or MethodKind.AnonymousFunction)
        {
            Dictionary<ITypeParameterSymbol, ITypeSymbol> closureMap = null;
            for (var s = method.ContainingSymbol; s is IMethodSymbol enclosing; s = enclosing.ContainingSymbol)
            {
                if (enclosing.IsGenericMethod
                    && _ctx.FirstGenericSpec.TryGetValue(enclosing.OriginalDefinition, out var ownerSpec))
                {
                    var ownerDef = ownerSpec.OriginalDefinition;
                    closureMap ??= new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
                    for (int i = 0; i < ownerDef.TypeParameters.Length; i++)
                        closureMap[ownerDef.TypeParameters[i]] = ownerSpec.TypeArguments[i];
                }
            }
            if (closureMap != null && _typeParamMap == null)
            {
                _typeParamMap = closureMap;
                closureMapSet = true;
            }
            else if (closureMap != null)
            {
                foreach (var kv in closureMap)
                    if (!_typeParamMap.ContainsKey(kv.Key))
                        _typeParamMap[kv.Key] = kv.Value;
            }
        }

        // Get method body IOperation
        var bodySource = isGenericSpec ? method.OriginalDefinition : method;
        var syntaxRef = bodySource.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef != null)
        {
            var syntax = syntaxRef.GetSyntax();
            var tree = syntax.SyntaxTree;
            var model = _compilation.GetSemanticModel(tree);

            var bodyOp = model.GetOperation(syntax);

            // Round-9 [Y8]: a LOCAL FUNCTION's method symbol compares EQUAL across operation-tree
            // walks, but its TYPE PARAMETER symbols do NOT (fresh per-walk instances with
            // reference equality) — so the spec map keyed by the CALL SITE walk's T misses every
            // body-walk reference and the body type-checks as raw 'T' (CReturn ICE on a single
            // legal instantiation). Re-key the instantiation map with the body symbol's own
            // type parameters; class-level generic methods share symbols and are unaffected.
            if (isGenericSpec && bodyOp is ILocalFunctionOperation lfDefOp
                && lfDefOp.Symbol.TypeParameters.Length == method.TypeArguments.Length)
            {
                for (int i = 0; i < lfDefOp.Symbol.TypeParameters.Length; i++)
                    _typeParamMap[lfDefOp.Symbol.TypeParameters[i]] = method.TypeArguments[i];
            }

            PreScanGotoLabels(bodyOp);

            // Emit tail-call optimization label at function entry (jump target for TCO goto)
            _builder.EmitLabel($"__tco_{func.Name}");

            // Stage 2 M2 (design §3.0 INV-2): the MethodEntry-scope EnvAlloc lowers AFTER the __tco_
            // label so a self-tail loopback re-runs it every logical activation (per-activation env
            // freshness). A closure reaches its own body scope via ClosureScopes (its MethodEntry
            // Node is the lambda/LF body, not this bodyOp); a root method via ScopeFor(bodyOp).
            // No-ops on a null / non-capture-bearing scope, so the call is unconditional.
            CaptureScope entryScope = null;
            if (_ctx.CaptureScope != null)
            {
                if (method.MethodKind is MethodKind.LocalFunction
                    or MethodKind.LambdaMethod or MethodKind.AnonymousFunction)
                    _ctx.CaptureScope.ClosureScopes.TryGetValue(method.OriginalDefinition, out entryScope);
                else
                    entryScope = _ctx.CaptureScope.ScopeFor(bodyOp, CaptureScopeKind.MethodEntry);
            }
            EnvEmit.Alloc(_builder, _ctx, entryScope);

            // Consume every captured PARAMETER of this method out of its flat param field into its env
            // cell (the arg arrived positionally in the flat field; all body reads route through env).
            if (_ctx.CaptureScope != null && _methodParamVarIds.TryGetValue(method, out var entryParamIds))
                foreach (var p in method.Parameters)
                    if (p.Ordinal < entryParamIds.Length && _ctx.TryGetEnvBinding(p, out _))
                        EnvEmit.Write(_builder, _ctx, p,
                            BridgeLoad(entryParamIds[p.Ordinal], GetUdonType(p.Type)));

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
                // Struct ctor: body mutates the receiver object[] (this.field = …) in place.
                if (ctorBodyOp.BlockBody != null)
                    VisitOperation(ctorBodyOp.BlockBody);
            }
            else if (bodyOp is IAnonymousFunctionOperation anonFunc)
            {
                if (anonFunc.Body is IBlockOperation anonBlock)
                    VisitOperation(anonBlock);
                else if (anonFunc.Body != null && _methodReturns.TryGetValue(method, out var lambdaRets) && lambdaRets.Length == 1)
                {
                    var resultVal = VisitExpression(anonFunc.Body);
                    BridgeStore(lambdaRets[0].Id, resultVal);
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

        // Clear type param map after generic specialization / generic-body closure emission
        if (isGenericSpec || closureMapSet)
            _typeParamMap = null;

        // Method epilogue: return
        _builder.EmitReturn();
        _currentMethod = null;
    }

    // ── Field Initializers ──

    void EmitFieldInitializers()
    {
        // Default-init aggregate (struct/tuple) fields with no explicit initializer FIRST, so any explicit
        // initializer that references one sees a non-null backing array (C# default-then-initializer order).
        foreach (var (fieldId, aggType) in _ctx.AggregateFieldDefaults)
            BridgeStore(fieldId, _ctx.EmitNewAggregate(aggType));

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
                        $"{arrayType}.__ctor__SystemInt32__{arrayType}",
                        new CLeaf[] { sizeConst });
                    BridgeStore(fieldId, arrVal);
                    for (int i = 0; i < arrayInit.ElementValues.Length; i++)
                    {
                        var elemVal = VisitExpression(arrayInit.ElementValues[i]);
                        var idxConst = BridgeConstInt(i);
                        var arrLoad = BridgeLoad(fieldId, arrayType);
                        BridgeCallExternVoid(
                            $"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
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
                // Loud-or-correct: a delegate field whose initializer fails to lower must BLOCK the
                // compile — demoting to a Warning ships a silently-null bundle (compile-clean wrong
                // value). Other field types keep the legacy Warning demotion.
                if (_ctx.DelegateFields.Contains(fieldId)) throw;
                var loc = initOp.Syntax?.GetLocation()?.GetLineSpan();
                _diagnostics.Add(new EmitDiagnostic
                {
                    Severity = "Warning",
                    Message = $"Field '{fieldId}' initializer not supported, will be default(T) at runtime: {ex.Message}",
                    FilePath = loc?.Path ?? "",
                    Line = (loc?.StartLinePosition.Line ?? -1) + 1,
                    Character = (loc?.StartLinePosition.Character ?? -1) + 1,
                });
            }
        }

        // Initialize _old_ variables for FieldChangeCallback fields
        foreach (var kvp in _fieldChangeCallbacks)
        {
            var fcbType = _ctx.GetFieldType(kvp.Key);
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
        foreach (var h in _stmtHandlers)
            if (h.CanHandle(op)) { try { h.Handle(op); return; } catch (System.Exception ex) { throw TagLocation(ex, op); } }
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
        foreach (var h in _exprHandlers)
            if (h.CanHandle(op)) { try { return h.Handle(op); } catch (System.Exception ex) { throw TagLocation(ex, op); } }
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
    // recorded syntax-keyed in EmitContext.ReentrantDispatchSites for the §4.3 Reentrant-flag marking;
    // tail dispatch sites are spared so bundle-driven deep tail recursion never spills (§4.4).
    void BuildRecursionInfo()
    {
        // Generic method definitions are monomorphized per call-site and thus skipped in registration, so
        // they are absent from _methodFunctions. Add them explicitly — otherwise a recursive generic method
        // (e.g. `int Fact<T>(int n) => n * Fact<T>(n-1)`) has no graph node and its frame is never spilled.
        var roots = _methodFunctions.Keys
            .Select(m => m.OriginalDefinition)
            .Concat(_classSymbol.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.IsGenericMethod && m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared)
                .Select(m => (IMethodSymbol)m.OriginalDefinition))
            // Wave-9 round-9 [Y5]: base-declared generic definitions called with OPEN type args —
            // their on-demand specs (round-8 [Y11]) emit the base definition's body, so the
            // definition needs a graph node exactly like a same-class generic definition; without
            // it a self-recursive inherited generic had no self-edge and never spilled (VM-proven
            // 63 where the CLR gives 234 — live locals clobbered per frame).
            .Concat(_openGenericBaseDefs)
            .Where(m => m.DeclaringSyntaxReferences.Length > 0)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<IMethodSymbol>()
            .ToList();

        // Local functions are registered lazily during emission (after this pass), so discover them now by
        // walking the bodies — otherwise a recursive local function would not be detected and would corrupt
        // the flat heap. Transitive: a local function may contain nested local functions.
        var localFuncs = new List<IMethodSymbol>();
        foreach (var m in roots)
        {
            var sr = m.DeclaringSyntaxReferences.FirstOrDefault();
            if (sr != null)
                CollectLocalFunctions(_compilation.GetSemanticModel(sr.SyntaxTree).GetOperation(sr.GetSyntax()), localFuncs);
        }

        var internalMethods = roots.Concat(localFuncs)
            .Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToArray();
        var methodSet = new HashSet<IMethodSymbol>(internalMethods, SymbolEqualityComparer.Default);

        var bodies = new Dictionary<IMethodSymbol, IOperation>(SymbolEqualityComparer.Default);
        var edges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var m in internalMethods)
        {
            var callees = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var syntaxRef = m.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef != null)
            {
                var op = _compilation.GetSemanticModel(syntaxRef.SyntaxTree).GetOperation(syntaxRef.GetSyntax());
                // A local function's analysable body is the block inside its ILocalFunctionOperation.
                var body = (op as ILocalFunctionOperation)?.Body ?? op;
                bodies[m] = body;
                CollectInternalCallees(body, methodSet, callees);
            }
            edges[m] = callees;
        }

        // §2.8 round-3 [A] + round-4 [K2]: compute local-function capture sets BEFORE the recipient
        // pre-scan and any emission — a capturing local function converted to a method group is a
        // closure exactly like a capturing lambda, so the guards (and the pre-scans below) treat it
        // as capturing-lambda-equivalent via EmitContext.CapturingLocalFunctions (membership-only,
        // §1.5). Capture-ness is TRANSITIVE over the local-function call graph ([K2]: a wrapper
        // `Outer(){return Inner();}` — or any longer chain — is the same closure judged capture-free
        // by the direct walk, VM-verified laundering), so run a fixpoint unioning callee capture
        // sets, each hop filtered against the caller's own `inside` set (a callee capturing only the
        // CALLER's locals runs entirely in the caller's activation and stays non-capturing).
        var lfCaptures = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var lfInside = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var lfRefs = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var m in internalMethods)
        {
            if (m.MethodKind != MethodKind.LocalFunction) continue;
            if (!bodies.TryGetValue(m, out var lfBody) || lfBody == null) continue;
            var direct = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var insideSet = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var refs = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            LambdaCaptureAnalyzer.AnalyzeLocalFunction(m, lfBody, direct, insideSet, refs);
            lfCaptures[m] = direct;
            lfInside[m] = insideSet;
            lfRefs[m] = refs;
        }
        bool lfChanged = true;
        while (lfChanged)
        {
            lfChanged = false;
            foreach (var kv in lfRefs)
            {
                var mySet = lfCaptures[kv.Key];
                var myInside = lfInside[kv.Key];
                foreach (var callee in kv.Value)
                {
                    if (!lfCaptures.TryGetValue(callee, out var calleeSet)) continue;
                    if (ReferenceEquals(calleeSet, mySet)) continue; // self-recursion adds nothing
                    foreach (var s in calleeSet)
                        if (!myInside.Contains(s) && mySet.Add(s)) lfChanged = true;
                }
            }
        }
        var lfFinal = new Dictionary<IMethodSymbol, ImmutableArray<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var kv in lfCaptures)
        {
            if (kv.Value.Count == 0) continue;
            _ctx.CapturingLocalFunctions.Add(kv.Key);
            lfFinal[kv.Key] = kv.Value.ToImmutableArray();
        }
        // [K2] lambda side: wrapper LAMBDAS over capturing local functions are the same hole —
        // hand the transitive sets to the analyzer (consulted by GetCaptures' invocation/method-
        // reference cases) BEFORE the first GetCaptures caller below pins the per-lambda cache.
        _ctx.CaptureAnalyzer.SetLocalFunctionCaptures(lfFinal);

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
        //      dispatch can only enter a bridge of the SAME signature. That filter is sound ONLY while
        //      variance is rejected at creation (DelegateProtocol.ValidateDelegateBinding forces the
        //      delegate-Invoke sig to equal the target-method sig) — if that reject is ever relaxed, this
        //      filter must be revisited (see the tracked coupling pin SigFilterCoupledToVarianceReject).
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
        // (contains the §5.4 widening). SigsMatch: equal, or either is wildcard.
        var escapeSig = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var e in escape)
            if (edges.ContainsKey(e)) escapeSig[e] = DispatchSigOrWildcard(e);
        // Wave-12 [V1]: sites whose bundle provenance is exact (see TryResolvePreciseDispatchTargets)
        // contribute edges to their KNOWN targets only, instead of sig-matching against the whole
        // widened escape set; every other site keeps the §5.4 blanket treatment. Keyed by operation
        // reference — the reentrant-marking loop below re-collects sites from the same shared bodies.
        var preciseDispatchTargets = new Dictionary<IOperation, HashSet<IMethodSymbol>>();
        foreach (var node in allNodes)
        {
            if (!bodies.TryGetValue(node, out var nodeBody) || nodeBody == null) continue;
            if (!ContainsDelegateDispatch(nodeBody)) continue;
            var dispatchSites = new List<IOperation>();
            CollectDelegateDispatchSites(nodeBody, dispatchSites);
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
            foreach (var kv in escapeSig)
                foreach (var ds in nodeSigs)
                    if (SigsMatch(ds, kv.Value)) { nodeEdges.Add(kv.Key); break; }
        }

        // Round-7 follow-up [Q5]: per-node this-FIELD touch sets for the ref/out-argument alias
        // guard (see EmitContext.ThisFieldTouches). Direct touches are collected per node;
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
        _ctx.ThisFieldTouches = thisTouches;

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
            // dispatch in M reentrant even though a bundle of M's signature can never flow to it. Sound
            // while variance is rejected at creation (SigFilterCoupledToVarianceReject).
            var reenterSigs = new List<string>();   // may hold WILDCARD (null) entries
            foreach (var kv in escapeSig)
                if (ReachFrom(kv.Key).Overlaps(sccSet)) reenterSigs.Add(kv.Value);
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
                        if (toRecursiveCallee && !EmitContext.IsNonTailDispatchSite(callerBody, site))
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
                        if (site.Syntax != null && EmitContext.IsNonTailDispatchSite(callerBody, site)
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
        _ctx.RecursiveCallees = recursive;
        _ctx.CycleCallees = cycleEdges;
        _ctx.ReentrantDispatchSites = reentrantSites;
        _ctx.TailSparedDirectCallSites = tailSparedSites;
        // §5.5 (graft #2): snapshot the graph-node set (definition-keyed) so the post-emission armor
        // can verify every capturing delegate bridge target reached the reentrancy analysis. Bodies is
        // keyed by every node that got an edge set (roots, local functions, lambdas).
        _ctx.RecursionGraphNodes = new HashSet<IMethodSymbol>(bodies.Keys, SymbolEqualityComparer.Default);
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
        if (_ctx.CaptureScope == null || _ctx.RecursionGraphNodes == null) return;
        foreach (var (method, bridgeExportName, _) in _ctx.PendingDelegateBridges)
        {
            var def = method.OriginalDefinition;
            if (!_ctx.CaptureScope.IsCapturingClosure(def)) continue;
            if (!_ctx.RecursionGraphNodes.Contains(def))
                throw new InvalidOperationException(
                    $"USugar internal error (§5.5 bridge-target armor): capturing delegate bridge "
                  + $"'{bridgeExportName}' targets '{def}', which has no recursion-graph node — its "
                  + "reentrancy spill protection would be silently missing. A registration path added a "
                  + "capturing bridge without seeding the recursion analysis (wave-10 [Z1] class).");
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
        foreach (var child in op.Children)
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
        foreach (var child in op.Children)
            CollectEscapedDelegateTargets(child, internalMethods, result);
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
            foreach (var child in op.Children)
                Walk(child);
        }

        bool SubtreeReferencesLocal(IOperation op)
        {
            if (op == null) return false;
            if (op is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local))
                return true;
            foreach (var child in op.Children)
                if (SubtreeReferencesLocal(child)) return true;
            return false;
        }

        Walk(callerBody);
        if (poisoned || !declFound || writeCount == 0)
            return false;
        targets = found;
        return true;
    }

    // True if the body contains a delegate dispatch attributed to THIS function (hoisted children —
    // local functions and lambdas — are their own nodes and are skipped).
    static bool ContainsDelegateDispatch(IOperation op)
    {
        if (op == null) return false;
        if (EmitContext.IsDelegateDispatch(op)) return true;
        foreach (var child in op.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (ContainsDelegateDispatch(child)) return true;
        }
        return false;
    }

    // Collect the delegate-dispatch invocations attributed to THIS function (hoisted children skipped).
    static void CollectDelegateDispatchSites(IOperation op, List<IOperation> result)
    {
        if (op == null) return;
        if (EmitContext.IsDelegateDispatch(op)) result.Add(op);
        foreach (var child in op.Children)
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
        foreach (var child in op.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            CollectInvocationSites(child, result);
        }
    }

    // ── Wave-9 round-3 [W1]/[W2]/[W3]: emission-faithful leaf-override resolution for the graph ──
    // Emission resolves a this-receiver virtual call to the most-derived override visible from the
    // compiled class (InvocationHandler.ResolveMostDerivedOverride / HandlerBase.ResolveDispatchProperty),
    // but the recursion graph recorded only the STATIC binding — so a runtime cycle closed through an
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
        for (var t = _classSymbol; t != null; t = t.BaseType)
            foreach (var cand in t.GetMembers(p.Name).OfType<IPropertySymbol>())
                for (var o = cand; o != null; o = o.OverriddenProperty)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, def))
                        return SymbolEqualityComparer.Default.Equals(cand.OriginalDefinition, def)
                            ? null : cand.OriginalDefinition;
        return null;
    }

    IMethodSymbol ResolveLeafOverrideDef(IMethodSymbol def)
    {
        for (var t = _classSymbol; t != null; t = t.BaseType)
            foreach (var m in t.GetMembers(def.Name).OfType<IMethodSymbol>())
                for (var o = m; o != null; o = o.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, def))
                        return m.OriginalDefinition;
        return def;
    }

    // True if the caller body contains a call to callee that is NOT in tail position (its result is used
    // by something after the call, so the caller's live values would be clobbered by a recursive re-entry).
    // The walk itself lives in TailCallAnalysis (shared with EmitContext.IsNonTailDispatchSite); this is
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
        if (pr.Instance is not IInstanceReferenceOperation) return false;
        var acc = getter ? pr.Property.GetMethod : pr.Property.SetMethod;
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

    bool IsInternalCallTo(IOperation op, IMethodSymbol callee, out IOperation call)
    {
        call = null;
        IMethodSymbol target = op switch
        {
            IInvocationOperation inv => inv.TargetMethod.OriginalDefinition,
            IObjectCreationOperation oc => oc.Constructor?.OriginalDefinition,
            _ => null,
        };
        if (target != null && SymbolEqualityComparer.Default.Equals(target, callee)) { call = op; return true; }
        // Wave-9 round-3: a this-receiver virtual call site emits against the LEAF override —
        // match the emission-faithful target too, else the leaf edge added by CollectInternalCallees
        // is filtered as "no named call" and the spill never reaches the call site.
        if (op is IInvocationOperation linv && LeafCallTarget(linv) is { } leaf
            && SymbolEqualityComparer.Default.Equals(leaf, callee))
        { call = op; return true; }
        // Wave-9 round-2 [W2]: a this-receiver property/indexer reference is an accessor CALL —
        // matched against either accessor (conservative: a write-position reference matching the
        // getter only ever classifies an extra site non-tail, which over-spills, never corrupts).
        if (op is IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } pr)
        {
            if ((pr.Property.GetMethod is { } pg && SymbolEqualityComparer.Default.Equals(pg.OriginalDefinition, callee))
             || (pr.Property.SetMethod is { } ps && SymbolEqualityComparer.Default.Equals(ps.OriginalDefinition, callee)))
            { call = op; return true; }
            // Wave-9 round-3: leaf-override accessors (the getter/setter emission dispatches via
            // ResolveDispatchProperty) — same emission-faithful matching as the invocation arm.
            if (LeafPropertyTarget(pr) is { } lp
                && ((lp.GetMethod is { } lg && SymbolEqualityComparer.Default.Equals(lg.OriginalDefinition, callee))
                 || (lp.SetMethod is { } ls && SymbolEqualityComparer.Default.Equals(ls.OriginalDefinition, callee))))
            { call = op; return true; }
        }
        return false;
    }

    // Collect every local function declared anywhere in an operation tree (transitive: nested too).
    static void CollectLocalFunctions(IOperation op, List<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is ILocalFunctionOperation lf && lf.Symbol != null)
            result.Add(lf.Symbol.OriginalDefinition);
        foreach (var child in op.Children)
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
        foreach (var child in op.Children)
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
        if (op is IInvocationOperation inv)
        {
            var t = inv.TargetMethod.OriginalDefinition;
            if (internalMethods.Contains(t)) result.Add(t);
            // Wave-9 round-3 [W1]/[W2]/[W3]: a this-receiver virtual call dispatches the LEAF
            // override at emission, so the leaf is a call-graph edge too. Without it a runtime
            // cycle closed through the override relation (base body virtual-calling back into the
            // override, or an override's base.M call whose base body recurses virtually) was
            // invisible to the SCC analysis and no frame ever spilled (VM-proven 305 vs CLR 605 /
            // 14 vs 12 / 17 vs 21). The static edge stays — extra edges only ever over-spill (§8-3).
            if (LeafCallTarget(inv) is { } leafT && internalMethods.Contains(leafT)) result.Add(leafT);
        }
        if (op is IObjectCreationOperation oc && oc.Constructor != null)
        {
            var c = oc.Constructor.OriginalDefinition;
            if (internalMethods.Contains(c)) result.Add(c);
        }
        // Wave-9 round-2 [W2]: a this-receiver property/indexer reference is a CALL to its manual
        // accessor (this-path reads/writes direct-JUMP the registered accessor function), so it is a
        // call-graph edge — without it, recursion threaded through any accessor was invisible to the
        // SCC analysis and accessor frames were never spilled (VM-proven: every frame's locals
        // clobbered to the deepest value, 5 where the CLR gives 11). Both accessors are added
        // conservatively (read/write/compound positions all route here; an extra edge only ever
        // over-spills, §8-3 direction). Auto accessors have no body op and thus no outgoing edges —
        // they can never close a cycle, so the extra edges are inert for them.
        if (op is IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } pr)
        {
            if (pr.Property.GetMethod is { } pg && internalMethods.Contains(pg.OriginalDefinition))
                result.Add(pg.OriginalDefinition);
            if (pr.Property.SetMethod is { } ps && internalMethods.Contains(ps.OriginalDefinition))
                result.Add(ps.OriginalDefinition);
            // Wave-9 round-3: this-receiver virtual accessor references dispatch the LEAF override's
            // accessors at emission (ResolveDispatchProperty) — add those edges too, same rationale
            // as the invocation arm above (getter-recursion flavor VM-proven 305 vs CLR 605).
            if (LeafPropertyTarget(pr) is { } leafP)
            {
                if (leafP.GetMethod is { } lg && internalMethods.Contains(lg.OriginalDefinition))
                    result.Add(lg.OriginalDefinition);
                if (leafP.SetMethod is { } ls && internalMethods.Contains(ls.OriginalDefinition))
                    result.Add(ls.OriginalDefinition);
            }
        }
        var opMethod = (op as IBinaryOperation)?.OperatorMethod ?? (op as IUnaryOperation)?.OperatorMethod;
        if (opMethod != null && internalMethods.Contains(opMethod.OriginalDefinition))
            result.Add(opMethod.OriginalDefinition);
        foreach (var child in op.Children)
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

    IMethodSymbol[] CollectForeignStaticMethods(IMethodSymbol[] classMethods)
    {
        var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in classMethods)
        {
            var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null) continue;
            var syntax = syntaxRef.GetSyntax();
            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var bodyOp = model.GetOperation(syntax);
            CollectForeignStaticCallsInOperation(bodyOp, result);
        }
        var visited = new HashSet<IMethodSymbol>(result, SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>(result);
        while (queue.Count > 0)
        {
            var fm = queue.Dequeue();
            var syntaxRef = fm.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null) continue;
            var syntax = syntaxRef.GetSyntax();
            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var bodyOp = model.GetOperation(syntax);
            CollectForeignStaticCallsInOperation(bodyOp, result);
            foreach (var newMethod in result.Except(visited))
            {
                visited.Add(newMethod);
                queue.Enqueue(newMethod);
            }
        }
        return result.ToArray();
    }

    void CollectForeignStaticCallsInOperation(IOperation op, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is IInvocationOperation inv && IsForeignStatic(inv.TargetMethod))
        {
            var original = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
            if (!original.IsGenericMethod)
                result.Add(original);
        }
        foreach (var child in op.Children)
            CollectForeignStaticCallsInOperation(child, result);
    }

    // User-struct parameterized constructors + instance methods reachable from the class (transitive).
    IMethodSymbol[] CollectStructMethods(IMethodSymbol[] classMethods)
    {
        var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in classMethods)
        {
            var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null) continue;
            var syntax = syntaxRef.GetSyntax();
            CollectStructMethodsInOperation(_compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax), result);
        }
        var visited = new HashSet<IMethodSymbol>(result, SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>(result);
        while (queue.Count > 0)
        {
            var sm = queue.Dequeue();
            var syntaxRef = sm.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null) continue;
            var syntax = syntaxRef.GetSyntax();
            CollectStructMethodsInOperation(_compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax), result);
            foreach (var nc in result.Except(visited)) { visited.Add(nc); queue.Enqueue(nc); }
        }
        return result.ToArray();
    }

    // A property is auto-implemented iff the compiler synthesized a backing field associated with it.
    // Computed (expression-bodied or block-bodied) properties have no such field and must be inlined.
    static bool IsComputedProperty(IPropertySymbol prop)
        => !prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));

    void CollectStructMethodsInOperation(IOperation op, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        // Parameterized user-struct constructor: new V(...).
        if (op is IObjectCreationOperation oc && oc.Constructor != null
            && oc.Type is INamedTypeSymbol nt && EmitContext.IsUserStruct(nt)
            && oc.Arguments.Length > 0 && !oc.Constructor.IsImplicitlyDeclared)
            result.Add(oc.Constructor);
        // User-struct instance method: v.Method(...).
        if (op is IInvocationOperation inv && inv.TargetMethod is { IsStatic: false } tm
            && tm.MethodKind == MethodKind.Ordinary && !tm.IsImplicitlyDeclared
            && tm.ContainingType is INamedTypeSymbol it && EmitContext.IsUserStruct(it))
            result.Add(tm.OriginalDefinition);
        // Computed (non-auto) user-struct property: v.Prop (read) or v.Prop = x (write). Auto-properties use
        // their backing-field slot directly (no method), but a computed accessor must be inlined as a struct
        // instance method. Register both accessors (the reference alone doesn't reveal read-vs-write context).
        // A user-struct indexer (s[i]) is just a parameterized computed property (never auto-backed), so it
        // is collected the same way — its accessors carry the index args after the synthetic receiver.
        if (op is IPropertyReferenceOperation pr
            && pr.Property is { IsStatic: false } prop
            && pr.Property.ContainingType is INamedTypeSymbol pit && EmitContext.IsUserStruct(pit)
            && IsComputedProperty(prop))
        {
            if (prop.GetMethod != null) result.Add(prop.GetMethod.OriginalDefinition);
            if (prop.SetMethod != null) result.Add(prop.SetMethod.OriginalDefinition);
        }
        // Property-pattern subpattern: `p is { Doubled: ... }` reads Doubled via an IMPLICIT getter call,
        // not an explicit IPropertyReferenceOperation, so collect a computed user-struct property's getter
        // here too — else the pattern lowering emits a bogus accessor extern for an unregistered getter.
        if (op is IPropertySubpatternOperation sub && sub.Member is IPropertyReferenceOperation spr
            && spr.Property is { IsStatic: false } sprop
            && spr.Property.ContainingType is INamedTypeSymbol spit && EmitContext.IsUserStruct(spit)
            && IsComputedProperty(sprop) && sprop.GetMethod != null)
            result.Add(sprop.GetMethod.OriginalDefinition);
        // User-struct operator: v1 + v2, -v, s += t, c++ (static operator methods). Compound-assignment and
        // increment/decrement carry their operator method too, so collect those so the emit side can JUMP to
        // the user operator instead of a bogus SystemObjectArray.__op_* extern.
        var opMethod = (op as IBinaryOperation)?.OperatorMethod
            ?? (op as IUnaryOperation)?.OperatorMethod
            ?? (op as ICompoundAssignmentOperation)?.OperatorMethod
            ?? (op as IIncrementOrDecrementOperation)?.OperatorMethod;
        if (opMethod is { MethodKind: MethodKind.UserDefinedOperator }
            && opMethod.ContainingType is INamedTypeSymbol ot && EmitContext.IsUserStruct(ot))
            result.Add(opMethod.OriginalDefinition);
        // User-struct CONVERSION operator (implicit/explicit). MethodKind is Conversion (not UserDefinedOperator),
        // so it needs its own arm — invoked implicitly by an IConversionOperation, routed to the method on emit.
        if (op is IConversionOperation convOp && convOp.OperatorMethod is { MethodKind: MethodKind.Conversion } convM
            && convM.ContainingType is INamedTypeSymbol convCt && EmitContext.IsUserStruct(convCt))
            result.Add(convM.OriginalDefinition);
        // `using` resource: the Dispose() is invoked IMPLICITLY (no IInvocationOperation in the tree), so
        // collect a user-struct disposable's Dispose so it is registered as a struct method and the using
        // lowering can JUMP to it instead of emitting a non-existent SystemObjectArray.__Dispose__ extern.
        if (op is IUsingOperation uo) CollectUsingDispose(uo.Resources, result);
        if (op is IUsingDeclarationOperation ud) CollectUsingDispose(ud.DeclarationGroup, result);
        foreach (var child in op.Children)
            CollectStructMethodsInOperation(child, result);
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
        if (type is INamedTypeSymbol nt && EmitContext.IsUserStruct(nt)
            && EmitContext.FindStructDisposeMethod(nt) is { } dispose)
            result.Add(dispose.OriginalDefinition);
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

    IMethodSymbol[] CollectBaseInstanceMethods(IMethodSymbol[] classMethods)
    {
        var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in classMethods)
            CollectBaseInstanceCallsInOperation(GetMethodBodyOperation(method), result);
        // Transitive closure: a discovered base method's OWN body may call a further base method (a
        // `base.M` chain across 3+ levels), and a base property accessor may reference another base member.
        // Keep scanning newly discovered base methods' bodies until a fixpoint (else the deepest target is
        // never registered → its call falls through to a bogus extern). Round-9 [Y5]/[Y6]: open-generic
        // base definitions are scanned too — their bodies emit on demand and may reach further base
        // members (or more open generics).
        var scanned = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>(result.Concat(_openGenericBaseDefs));
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!scanned.Add(next)) continue;
            var discovered = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            CollectBaseInstanceCallsInOperation(GetMethodBodyOperation(next), discovered);
            foreach (var d in discovered)
                if (result.Add(d)) queue.Enqueue(d);
            foreach (var od in _openGenericBaseDefs)
                if (!scanned.Contains(od)) queue.Enqueue(od);
        }
        return result.ToArray();
    }

    IOperation GetMethodBodyOperation(IMethodSymbol method)
    {
        var syntaxRef = method?.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;
        var syntax = syntaxRef.GetSyntax();
        return _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
    }

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
        // copy, which runs the base accessor body (manual, pre-existing v2.x) or reads the base
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
        foreach (var child in op.Children)
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
