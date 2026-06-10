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
        EmitMethods();
        DetectLambdaCaptureAliasing();
        OnIrPass?.Invoke("after-emit", _module);
        // Handlers build Core IR; the pipeline (verify/optimize/flatten) runs on Core directly.
        var result = IrPipeline.GenerateUasmFromCore(_module, DumpEnabled);
        _codeGenResult = result;
        return result.Uasm;
    }

    /// <summary>
    /// Post-emit aliasing check: a captured local shared by 2+ lambdas / delegate fields
    /// aliases the same flat-heap field (Udon VM has no closure objects). Reassigning one
    /// delegate would silently overwrite the other's capture — an Error; the orchestrator's
    /// Phase-3 gate (roadmap B26) blocks asset apply on it.
    /// </summary>
    void DetectLambdaCaptureAliasing()
    {
        foreach (var kv in _ctx.AllLambdaCaptures)
        {
            if (kv.Value.Count <= 1) continue;
            var symbolName = kv.Key.Name;
            // Point the diagnostic at the captured local's declaration; list the capturing lambdas' lines.
            var span = kv.Key.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
            var lambdaLines = string.Join(", ", kv.Value
                .Select(l => l.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                .OrderBy(n => n));
            _diagnostics.Add(new EmitDiagnostic
            {
                Severity = "Error",
                Message =
                    $"Captured local '{symbolName}' is shared by {kv.Value.Count} lambdas / delegate fields (lines {lambdaLines}). " +
                    "Udon VM has no closure objects — captured locals alias a single flat-heap field, " +
                    "so reassigning one delegate overwrites the other's captured value. " +
                    "Use distinct locals per lambda, or restructure to avoid simultaneous live captures.",
                FilePath = span?.Path ?? "",
                Line = (span?.StartLinePosition.Line ?? -1) + 1,
                Character = (span?.StartLinePosition.Character ?? -1) + 1,
            });
        }
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
            var udonType = GetUdonType(member.Type);
            var flags = FieldFlags.None;
            if (member.DeclaredAccessibility == Accessibility.Public
                || member.GetAttributes().Any(a => a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute"))
                flags |= FieldFlags.Export;
            string syncMode = null;
            var syncAttr = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "UdonSyncedAttribute");
            if (syncAttr != null)
            {
                flags |= FieldFlags.Sync;
                if (syncAttr.ConstructorArguments.Length > 0 && syncAttr.ConstructorArguments[0].Value is int modeVal)
                    syncMode = modeVal switch { 2 => "linear", 3 => "smooth", _ => "none" };
                else
                    syncMode = "none";

                var syncCheckType = (member.Type is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Enum)
                    ? GetUdonType(nt.EnumUnderlyingType)
                    : udonType;
                if (!ExternResolver.IsSyncableType(syncCheckType))
                    throw new NotSupportedException(
                        $"Cannot sync field '{member.Name}': type '{member.Type}' is not supported by Udon sync");
            }

            // First-class delegate field (design §2.1): ONE SystemObjectArray heap var holding the bundle
            // reference, null-initialized in UASM data. Private fields are bundled too (assign/invoke route
            // on _delegateFields set-membership, not accessibility).
            if (member.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod != null)
            {
                DeclareDelegateField(member, delegateType);
                continue; // Skip normal field declaration
            }

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
            _ctx.DeclareField(prop.Name, udonType, flags);
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

                _ctx.DeclareField(member.Name, udonType, baseFlags, constValue);

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
                        if (isAuto)
                            _ctx.DeclareField(BaseAutoPropBackingName(prop), GetUdonType(prop.Type), FieldFlags.None);
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
                _ctx.DeclareField(prop.Name, udonType, flags);
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
    /// First-class delegate field (design §2.1/§1.6): ONE SystemObjectArray heap var holding the bundle
    /// reference, null-initialized in UASM data. Never exported (exported/synced vars must not be retyped;
    /// SetProgramVariable needs no export) — [UdonSynced] is rejected by the IsSyncableType check before
    /// this runs. An initializer (e.g. `public Action cb = M;`) always becomes runtime bundle construction
    /// at _start via _fieldInitOps, which also fixes the old silent drop of derived-class initializers.
    /// Shared by the derived-class and base-class field paths.
    /// </summary>
    void DeclareDelegateField(IFieldSymbol member, INamedTypeSymbol delegateType)
    {
        if (delegateType.DelegateInvokeMethod.ReturnType.IsTupleType)
            throw new NotSupportedException($"Tuple-return delegate field '{member.Name}' is not supported.");
        // §3.4-1: ref/out delegate signatures are rejected at the convention-var declaration side too.
        DelegateAbi.ValidateNoRefOutParams(delegateType.DelegateInvokeMethod);

        _ctx.DeclareField(member.Name, "SystemObjectArray", FieldFlags.None);
        _ctx.DelegateFields.Add(member.Name);

        // Declare the signature-keyed __dlgc_ convention vars for this delegate signature (§3.2).
        var invoke = delegateType.DelegateInvokeMethod;
        var (convArgs, convRet) = HandlerBase.GetConventionFieldNames(delegateType);
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
                foreach (var bm in inheritBase.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => (m.MethodKind == MethodKind.Ordinary
                              || m.MethodKind == MethodKind.PropertyGet
                              || m.MethodKind == MethodKind.PropertySet)
                             && !m.IsImplicitlyDeclared && !m.IsGenericMethod && !m.IsAbstract))
                {
                    if (!overriddenMethods.Contains(bm))
                        inheritedMethodsList.Add(bm);
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

        // Collect foreign static methods
        var foreignStatics = CollectForeignStaticMethods(methods);
        foreach (var fm in foreignStatics)
        {
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
        var structMethods = CollectStructMethods(methods);
        foreach (var sm in structMethods)
        {
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

        // Collect base class instance methods
        var methodSet = new HashSet<IMethodSymbol>(methods, SymbolEqualityComparer.Default);
        var baseInstanceMethods = CollectBaseInstanceMethods(methods)
            .Where(bm => !methodSet.Contains(bm))
            .ToArray();
        foreach (var bm in baseInstanceMethods)
        {
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
    }

    // ── Interface Bridges ──

    void EmitInterfaceBridges()
    {
        var bridges = _planner.ComputeBridges(_classSymbol);
        foreach (var (ifaceMethod, ifaceMl, classMl) in bridges)
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

            // Find class implementation
            var implMethod = _classSymbol.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
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

        // Get method body IOperation
        var bodySource = isGenericSpec ? method.OriginalDefinition : method;
        var syntaxRef = bodySource.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef != null)
        {
            var syntax = syntaxRef.GetSyntax();
            var tree = syntax.SyntaxTree;
            var model = _compilation.GetSemanticModel(tree);

            var bodyOp = model.GetOperation(syntax);
            PreScanGotoLabels(bodyOp);

            // Emit tail-call optimization label at function entry (jump target for TCO goto)
            _builder.EmitLabel($"__tco_{func.Name}");

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

        // Clear type param map after generic specialization emission
        if (isGenericSpec)
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

        // §2.8 round-2: pre-scan every root body + field initializer for DIRECT capturing-lambda
        // stores into fields / auto-properties / struct members (simple, coalesce, and deconstruction
        // assignment shapes — the only legal ways a capturing lambda enters a member; tainted-equivalent
        // member stores are loud rejects). Runs BEFORE body emission so the guards' member-read taint
        // (HandlerBase.IsLaunderingMemberRead) is independent of method emission order. Local-function
        // bodies are part of their root's operation tree, so walking roots covers them.
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rb))
                CollectCaptureReceivingMembers(rb);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectCaptureReceivingMembers(initOp);

        // §2.8 round-4 [K3]: pre-scan LOCAL capture taint. The emission-time taint registration
        // (GuardCaptureEscapeStore / the declaration arms) populates CapturingLambdaLocals in
        // LEXICAL order, so a read emitted before the tainting store escapes clean yet executes
        // AFTER the seed from iteration 2 onward (loop back-edge use-before-seed, VM-verified
        // wrong values; round 2 fixed exactly this order-dependence for MEMBERS). Walk every root
        // body for DIRECT capturing stores into locals / local-rooted member chains and taint the
        // root local BEFORE emission, then propagate through local-to-local copy edges to a
        // fixpoint (F4 emission-order-independent). Straight-line use-before-seed shapes over-
        // reject by design (§8-3). Runs AFTER the [A]/[K2] fixpoint above (capturing local-function
        // method groups are seeds too) — emission-time registration stays as a redundant backstop.
        var localCopyEdges = new Dictionary<ILocalSymbol, HashSet<ILocalSymbol>>(SymbolEqualityComparer.Default);
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rb3))
                CollectCaptureSeededLocals(rb3, localCopyEdges);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectCaptureSeededLocals(initOp, localCopyEdges);
        bool taintChanged = true;
        while (taintChanged)
        {
            taintChanged = false;
            foreach (var kv in localCopyEdges)
            {
                if (!_ctx.CapturingLambdaLocals.Contains(kv.Key)) continue;
                foreach (var dst in kv.Value)
                    if (_ctx.CapturingLambdaLocals.Add(dst)) taintChanged = true;
            }
        }

        // ── §4.2 graph extension: lambda nodes, EscapeSet, synthetic edges ──

        // (a) Lambda nodes. Collected from the ROOT-method bodies and the field-initializer operations
        // so each lambda is keyed in exactly one operation-tree family (local-function bodies are
        // separate GetOperation trees, so collecting from them too would yield duplicate-but-distinct
        // instances). Emit-time matching is value-based for symbols (Roslyn lambda/local-function
        // symbols compare by syntax + container) and red-syntax-based for dispatch sites.
        var lambdaNodes = new List<(IMethodSymbol Sym, IOperation Body)>();
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rootBody))
                CollectLambdaNodes(rootBody, lambdaNodes);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectLambdaNodes(initOp, lambdaNodes);

        foreach (var (sym, body) in lambdaNodes)
        {
            if (edges.ContainsKey(sym)) continue;
            bodies[sym] = body;
            var lambdaCallees = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            CollectInternalCallees(body, methodSet, lambdaCallees);
            edges[sym] = lambdaCallees;
        }

        // (b) EscapeSet E (§4.1): conservative approximation of every function whose bridge address can
        // end up inside a bundle — same-class method-group targets (incl. local functions) and lambdas.
        // MEMBERSHIP-ONLY (§1.5): never drives emission order.
        var escape = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var m in roots)
            if (bodies.TryGetValue(m, out var rootBody))
                CollectEscapedDelegateTargets(rootBody, methodSet, escape);
        foreach (var (_, initOp, _) in _fieldInitOps)
            CollectEscapedDelegateTargets(initOp, methodSet, escape);

        // (c) Synthetic edges m→E for every function m (lambdas included) containing a delegate
        // dispatch: an indirect dispatch can start any escaped function. Real call edges are unchanged;
        // the RecursiveCallees filter below self-filters synthetic edges (no named call to match), so
        // they create cycle membership — consumed by the per-site Reentrant marking — without ever
        // creating named-call spills.
        var allNodes = internalMethods.Concat(lambdaNodes.Select(l => l.Sym))
            .Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToArray();
        foreach (var node in allNodes)
        {
            if (!bodies.TryGetValue(node, out var nodeBody) || nodeBody == null) continue;
            if (!ContainsDelegateDispatch(nodeBody)) continue;
            var nodeEdges = edges[node];
            foreach (var e in escape)
                if (edges.ContainsKey(e)) nodeEdges.Add(e);
        }

        var recursive = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var reentrantSites = new HashSet<SyntaxNode>();
        foreach (var scc in TarjanScc(allNodes, edges))
        {
            var sccSet = new HashSet<IMethodSymbol>(scc, SymbolEqualityComparer.Default);
            // Non-trivial SCC (mutual cycle) OR a single method with a self-loop (direct self-recursion).
            bool isCycle = scc.Count > 1 || (scc.Count == 1 && edges[scc[0]].Contains(scc[0]));
            if (!isCycle) continue;
            foreach (var caller in scc)
            {
                bodies.TryGetValue(caller, out var callerBody);
                // Only edges with a NON-tail call need spilling: a tail call (`return Callee(..)`) reads
                // nothing after the call, so flat-heap clobbering is harmless — and spilling deep tail
                // recursion would needlessly exhaust the stack.
                var inScc = new HashSet<IMethodSymbol>(
                    edges[caller].Where(c => sccSet.Contains(c) && HasNonTailCallTo(callerBody, c)),
                    SymbolEqualityComparer.Default);
                if (inScc.Count > 0) recursive[caller] = inScc;

                // §4.3: per-site Reentrant marking — a NON-TAIL dispatch inside a cycle member can
                // re-enter its containing function via any escaped function. Keyed by red syntax node
                // (shared across semantic models); tail sites are spared (§4.4).
                if (callerBody == null) continue;
                var dispatchSites = new List<IOperation>();
                CollectDelegateDispatchSites(callerBody, dispatchSites);
                foreach (var site in dispatchSites)
                    if (site.Syntax != null && EmitContext.IsNonTailDispatchSite(callerBody, site))
                        reentrantSites.Add(site.Syntax);
            }
        }
        _ctx.RecursiveCallees = recursive;
        _ctx.ReentrantDispatchSites = reentrantSites;
    }

    // §2.8 round-2 pre-scan: record member symbols that receive a DIRECT capturing-lambda store.
    // Covers simple assignment (cb = () => v), coalesce assignment (cb ??= () => v), deconstruction
    // tuple-literal element stores ((f, g) = (() => v, M)), and object-initializer member assignments
    // (new S { f = () => v } — an ISimpleAssignmentOperation in the tree). Full descent.
    void CollectCaptureReceivingMembers(IOperation op)
    {
        if (op == null) return;
        switch (op)
        {
            case ISimpleAssignmentOperation sa:
                RecordIfCapturingMemberStore(sa.Target, sa.Value);
                break;
            case ICoalesceAssignmentOperation ca:
                RecordIfCapturingMemberStore(ca.Target, ca.Value);
                break;
            case IDeconstructionAssignmentOperation da:
            {
                var tgt = da.Target is IDeclarationExpressionOperation de ? de.Expression : da.Target;
                var val = da.Value;
                while (val is IConversionOperation c) val = c.Operand;
                if (tgt is ITupleOperation tt && val is ITupleOperation vt)
                    for (int i = 0; i < tt.Elements.Length && i < vt.Elements.Length; i++)
                        RecordIfCapturingMemberStore(tt.Elements[i], vt.Elements[i]);
                break;
            }
        }
        foreach (var child in op.Children)
            CollectCaptureReceivingMembers(child);
    }

    // Value is DIRECTLY a capturing delegate creation (conversions unwrapped): a capturing lambda,
    // or (§2.8 round-3 [A]) a capturing LOCAL FUNCTION method group — the pre-scan twin of
    // HandlerBase.IsDirectCapturingLambda, shared by the member and local pre-scans.
    bool IsPreScanCapturingValue(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (!(v is IDelegateCreationOperation dc)) return false;
        return dc.Target switch
        {
            IAnonymousFunctionOperation lambda => _ctx.CaptureAnalyzer.HasCaptures(lambda),
            IMethodReferenceOperation mr => _ctx.IsCapturingLocalFunction(mr.Method),
            _ => false,
        };
    }

    void RecordIfCapturingMemberStore(IOperation target, IOperation value)
    {
        if (!IsPreScanCapturingValue(value)) return;
        // §2.8 round-3 [B]: record the WHOLE member chain, not just the leaf — `sField.f = () => v`
        // makes the struct-typed class field `sField` an envelope carrying the bundle, so a whole-
        // struct read (`arr[i] = sField`, `return sField`) must go loud exactly like the leaf read.
        // Local/param chain roots are owned by the emit-time container taint / param-seed reject.
        // §2.8 round-5 [N2]: record the CANONICAL symbol (override-chain root / ItemN — the one
        // helper every lookup uses too). Interface members canonicalize to null and record nothing:
        // the implementing class is unknown, so the emission-time guard rejects the store loudly
        // instead (GuardCaptureEscapeStore's foreign/interface chain arm).
        var t = target;
        while (true)
        {
            if (t is IFieldReferenceOperation fr)
            {
                if (EmitContext.CanonicalMemberSymbol(fr.Field) is { } cf) _ctx.CaptureReceivingMembers.Add(cf);
                t = fr.Instance; continue;
            }
            if (t is IPropertyReferenceOperation pr)
            {
                if (EmitContext.CanonicalMemberSymbol(pr.Property) is { } cp) _ctx.CaptureReceivingMembers.Add(cp);
                t = pr.Instance; continue;
            }
            if (t is IConversionOperation tc) { t = tc.Operand; continue; }
            break;
        }
    }

    // §2.8 round-4 [K3] pre-scan: taint locals seeded with a DIRECT capturing value — bare local
    // targets, local-rooted member chains (the round-3 [B] container taint, order-independent),
    // and declarators — and collect local-to-local copy edges (`var g = f;` / `g = f;` / tuple
    // elements) for the taint-propagation fixpoint in BuildRecursionInfo. Same assignment shapes
    // as CollectCaptureReceivingMembers plus declarators; array-element and param chain roots are
    // skipped here because the emission-time guard rejects those seeds loudly ([K1]/[K4]/H6).
    void CollectCaptureSeededLocals(IOperation op, Dictionary<ILocalSymbol, HashSet<ILocalSymbol>> copyEdges)
    {
        if (op == null) return;
        switch (op)
        {
            case ISimpleAssignmentOperation sa:
                RecordIfCapturingLocalSeedOrCopy(sa.Target, sa.Value, copyEdges);
                break;
            case ICoalesceAssignmentOperation ca:
                RecordIfCapturingLocalSeedOrCopy(ca.Target, ca.Value, copyEdges);
                break;
            case IDeconstructionAssignmentOperation da:
            {
                var tgt = da.Target is IDeclarationExpressionOperation de ? de.Expression : da.Target;
                var val = da.Value;
                while (val is IConversionOperation c) val = c.Operand;
                if (tgt is ITupleOperation tt && val is ITupleOperation vt)
                    for (int i = 0; i < tt.Elements.Length && i < vt.Elements.Length; i++)
                        RecordIfCapturingLocalSeedOrCopy(tt.Elements[i], vt.Elements[i], copyEdges);
                break;
            }
            case IVariableDeclaratorOperation vd when vd.Initializer?.Value != null:
                RecordLocalSeedOrCopy(vd.Symbol, hasMemberHops: false, vd.Initializer.Value, copyEdges);
                break;
        }
        foreach (var child in op.Children)
            CollectCaptureSeededLocals(child, copyEdges);
    }

    void RecordIfCapturingLocalSeedOrCopy(IOperation target, IOperation value,
        Dictionary<ILocalSymbol, HashSet<ILocalSymbol>> copyEdges)
    {
        // Resolve the target's chain root exactly like the emission-time member-chain arm:
        // strip declaration-expression wrapping, then hop field/property/conversion links.
        var t = target is IDeclarationExpressionOperation de ? de.Expression : target;
        bool hops = false;
        while (true)
        {
            if (t is IFieldReferenceOperation fr) { hops = true; t = fr.Instance; continue; }
            if (t is IPropertyReferenceOperation pr) { hops = true; t = pr.Instance; continue; }
            if (t is IConversionOperation tc) { t = tc.Operand; continue; }
            break;
        }
        if (t is ILocalReferenceOperation lr)
            RecordLocalSeedOrCopy(lr.Local, hops, value, copyEdges);
    }

    void RecordLocalSeedOrCopy(ILocalSymbol rootLocal, bool hasMemberHops, IOperation value,
        Dictionary<ILocalSymbol, HashSet<ILocalSymbol>> copyEdges)
    {
        if (rootLocal == null || value == null) return;
        if (IsPreScanCapturingValue(value))
        {
            _ctx.CapturingLambdaLocals.Add(rootLocal);
            return;
        }
        // Copy edges feed the taint fixpoint; targets must be BARE locals (member-chain targets
        // receiving a tainted value are loud rejects at emission, so edges through them never
        // carry taint).
        if (hasMemberHops) return;
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (v is ILocalReferenceOperation src)
        {
            AddCopyEdge(copyEdges, src.Local, rootLocal);
            return;
        }
        // §2.8 round-5 [N4]: MEMBER-READ copy edges — `g = s.f` acquired taint only at emission
        // time (lexical order), so `fs[i] = g; … s.f = () => w; g = s.f;` in a loop escaped clean
        // yet executed seeded from iteration 2 on (VM-verified, the round-4 [K3] documented
        // residual). Mirror IsLaunderingMemberRead order-independently, gated on a delegate-capable
        // member type (identity resolver: the pre-scan walks definition trees, so an unresolved T
        // is conservatively capable, §8-3): a capture-receiving / interface / foreign-class member
        // anywhere in the chain taints the target local directly (recipient sets are already
        // complete — the member pre-scan runs first); a local chain root adds a copy edge for the
        // fixpoint (container taint propagates); a param chain root taints directly (the callee is
        // blind to what the caller packed — mirrors the emission-time param-rooted read taint).
        if (v is IFieldReferenceOperation or IPropertyReferenceOperation)
        {
            var leafType = v is IFieldReferenceOperation lf ? lf.Field.Type
                : ((IPropertyReferenceOperation)v).Property.Type;
            if (!EmitContext.IsNonObjectDelegateCapableType(leafType, null)) return;
            var chain = v;
            while (true)
            {
                ISymbol memberSym;
                IOperation instance;
                if (chain is IFieldReferenceOperation cf) { memberSym = cf.Field; instance = cf.Instance; }
                else if (chain is IPropertyReferenceOperation cp) { memberSym = cp.Property; instance = cp.Instance; }
                else if (chain is IConversionOperation cc) { chain = cc.Operand; continue; }
                else break;
                var canonical = EmitContext.CanonicalMemberSymbol(memberSym);
                if (canonical == null // interface member — unknown implementing class, conservative
                    || EmitContext.IsForeignOrInterfaceMember(memberSym, _classSymbol)
                    || _ctx.CaptureReceivingMembers.Contains(canonical))
                {
                    _ctx.CapturingLambdaLocals.Add(rootLocal);
                    return;
                }
                chain = instance;
            }
            if (chain is ILocalReferenceOperation containerLocal)
                AddCopyEdge(copyEdges, containerLocal.Local, rootLocal);
            else if (chain is IParameterReferenceOperation)
                _ctx.CapturingLambdaLocals.Add(rootLocal);
        }
    }

    static void AddCopyEdge(Dictionary<ILocalSymbol, HashSet<ILocalSymbol>> copyEdges,
        ILocalSymbol from, ILocalSymbol to)
    {
        if (!copyEdges.TryGetValue(from, out var dsts))
            copyEdges[from] = dsts = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        dsts.Add(to);
    }

    // Collect every lambda (anonymous function) with its body — each becomes its own SCC node (§4.2).
    // Descends everywhere (nested lambdas / lambdas inside local functions are nodes too).
    static void CollectLambdaNodes(IOperation op, List<(IMethodSymbol Sym, IOperation Body)> result)
    {
        if (op == null) return;
        if (op is IAnonymousFunctionOperation af && af.Symbol != null && af.Body != null)
            result.Add((af.Symbol, af.Body));
        foreach (var child in op.Children)
            CollectLambdaNodes(child, result);
    }

    // EscapeSet collection (§4.1): targets of every IDelegateCreationOperation that resolve to an
    // internal function — same-class method groups (incl. local functions) and lambdas. Full descent.
    static void CollectEscapedDelegateTargets(IOperation op, HashSet<IMethodSymbol> internalMethods, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is IDelegateCreationOperation dc)
        {
            if (dc.Target is IMethodReferenceOperation mr && mr.Method != null)
            {
                var t = mr.Method.OriginalDefinition;
                if (internalMethods.Contains(t)) result.Add(t);
            }
            else if (dc.Target is IAnonymousFunctionOperation af && af.Symbol != null)
                result.Add(af.Symbol);
        }
        foreach (var child in op.Children)
            CollectEscapedDelegateTargets(child, internalMethods, result);
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

    // True if the caller body contains a call to callee that is NOT in tail position (its result is used
    // by something after the call, so the caller's live values would be clobbered by a recursive re-entry).
    static bool HasNonTailCallTo(IOperation op, IMethodSymbol callee)
    {
        if (op == null) return false;
        // `return Callee(args)` — the call itself is tail, but its argument/instance subexpressions are not.
        if (op is IReturnOperation ret && IsInternalCallTo(ret.ReturnedValue, callee, out var tailInv))
        {
            var tailArgs = (tailInv as IInvocationOperation)?.Arguments
                           ?? (tailInv as IObjectCreationOperation)?.Arguments
                           ?? System.Collections.Immutable.ImmutableArray<IArgumentOperation>.Empty;
            foreach (var arg in tailArgs)
                if (HasNonTailCallTo(arg, callee)) return true;
            return HasNonTailCallTo((tailInv as IInvocationOperation)?.Instance, callee);
        }
        if (IsInternalCallTo(op, callee, out _)) return true; // call not in tail position
        foreach (var child in op.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue; // own nodes
            if (HasNonTailCallTo(child, callee)) return true;
        }
        return false;
    }

    static bool IsInternalCallTo(IOperation op, IMethodSymbol callee, out IOperation call)
    {
        call = null;
        IMethodSymbol target = op switch
        {
            IInvocationOperation inv => inv.TargetMethod.OriginalDefinition,
            IObjectCreationOperation oc => oc.Constructor?.OriginalDefinition,
            _ => null,
        };
        if (target != null && SymbolEqualityComparer.Default.Equals(target, callee)) { call = op; return true; }
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

    // Collect call targets that resolve to a registered internal method (same program, JUMP-based).
    // Nested local functions are skipped — each is analysed as its own graph node, so their internal
    // calls are not attributed to the enclosing method.
    static void CollectInternalCallees(IOperation op, HashSet<IMethodSymbol> internalMethods, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is IInvocationOperation inv)
        {
            var t = inv.TargetMethod.OriginalDefinition;
            if (internalMethods.Contains(t)) result.Add(t);
        }
        if (op is IObjectCreationOperation oc && oc.Constructor != null)
        {
            var c = oc.Constructor.OriginalDefinition;
            if (internalMethods.Contains(c)) result.Add(c);
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

    IMethodSymbol[] CollectBaseInstanceMethods(IMethodSymbol[] classMethods)
    {
        var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var method in classMethods)
            CollectBaseInstanceCallsInOperation(GetMethodBodyOperation(method), result);
        // Transitive closure: a discovered base method's OWN body may call a further base method (a
        // `base.M` chain across 3+ levels), and a base property accessor may reference another base member.
        // Keep scanning newly discovered base methods' bodies until a fixpoint (else the deepest target is
        // never registered → its call falls through to a bogus extern).
        var queue = new Queue<IMethodSymbol>(result);
        while (queue.Count > 0)
        {
            var discovered = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            CollectBaseInstanceCallsInOperation(GetMethodBodyOperation(queue.Dequeue()), discovered);
            foreach (var d in discovered)
                if (result.Add(d)) queue.Enqueue(d);
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
        if (op is IInvocationOperation inv && IsBaseInstanceMethod(inv.TargetMethod))
            result.Add(inv.TargetMethod);
        // base.Prop / base[i]: a property/indexer reference invokes an accessor implicitly (it is not an
        // IInvocationOperation), so collect the base accessor too — else the read/write handler emits a
        // bogus SystemX.__get_Prop__ extern instead of a JUMP to the registered base getter/setter.
        if (op is IPropertyReferenceOperation pr)
        {
            if (pr.Property.GetMethod is { } g && IsBaseInstanceMethod(g)) result.Add(g);
            if (pr.Property.SetMethod is { } s && IsBaseInstanceMethod(s)) result.Add(s);
        }
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
