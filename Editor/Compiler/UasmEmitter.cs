using System;
using System.Collections.Generic;
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
    Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> _delegateParamConventions => _ctx.DelegateParamConventions;
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

        _ctx.InitializeDispatchers(VisitOperation, VisitExpression, operatorHandler.EmitPatternCheckImpl);
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

    CValue BridgeLoad(string fieldName, string type) => _builder.LoadField(fieldName, type);
    void BridgeStore(string fieldName, CValue value) => _builder.EmitStoreField(fieldName, value);
    CValue BridgeCallExtern(string retType, string sig, CValue[] args)
        => _builder.ExternCall(sig, new List<CValue>(args), retType);
    void BridgeCallExternVoid(string sig, CValue[] args)
        => _builder.EmitExternVoid(sig, new List<CValue>(args));
    CValue BridgeCallInternal(CFunction func, CValue[] args)
    {
        var retType = func.ReturnType ?? "SystemVoid";
        var call = _builder.InternalCall(func.Name, new List<CValue>(args), retType);
        if (retType == "SystemVoid") { _builder.EmitExprStmt(call); return null; }
        return call;
    }
    CValue BridgeConstInt(int value) => _builder.Const(value, "SystemInt32");

    // ── Emit ──

    /// <summary>Access to the HIR module for debugging and testing.</summary>
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
    /// delegate would silently overwrite the other's capture — was a Warning in v2.1, now Error.
    /// </summary>
    void DetectLambdaCaptureAliasing()
    {
        foreach (var kv in _ctx.AllLambdaCaptures)
        {
            if (kv.Value.Count <= 1) continue;
            var symbolName = kv.Key.Name;
            _diagnostics.Add(new EmitDiagnostic
            {
                Severity = "Error",
                Message =
                    $"Captured local '{symbolName}' is shared by {kv.Value.Count} lambdas / delegate fields. " +
                    "Udon VM has no closure objects — captured locals alias a single flat-heap field, " +
                    "so reassigning one delegate overwrites the other's captured value. " +
                    "Use distinct locals per lambda, or restructure to avoid simultaneous live captures.",
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

            // Public delegate field → expand to 3-variable bundle (target, method, addr)
            // Private delegate fields remain as SystemUInt32 (same-behaviour function pointers)
            if (member.DeclaredAccessibility == Accessibility.Public
                && member.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod != null)
            {
                if (delegateType.DelegateInvokeMethod.ReturnType.IsTupleType)
                    throw new NotSupportedException($"Tuple-return delegate field '{member.Name}' is not supported.");

                var bundle = new DelegateBundle(member.Name);
                _ctx.DeclareField(bundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver", flags);
                _ctx.DeclareField(bundle.Method, "SystemString");
                _ctx.DeclareField(bundle.Addr, "SystemUInt32");
                _ctx.DelegateFields.Add(member.Name);

                // Declare convention fields for this delegate signature
                var (convArgs, convRet) = HandlerBase.GetConventionFieldNames(delegateType);
                for (int ci = 0; ci < convArgs.Length; ci++)
                    _ctx.TryDeclareVar(convArgs[ci], NormalizeDelegateParamType(delegateType.DelegateInvokeMethod.Parameters[ci].Type));
                if (convRet != null)
                    _ctx.TryDeclareVar(convRet, ExternResolver.GetUdonTypeName(delegateType.DelegateInvokeMethod.ReturnType));

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
            var isAuto = prop.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true || prop.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
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

        // Collect declared member names to skip overridden/shadowed members in base classes
        var declaredMemberNames = new HashSet<string>(
            _classSymbol.GetMembers()
                .Where(m => m is IFieldSymbol or IPropertySymbol && !m.IsStatic && !m.IsImplicitlyDeclared)
                .Select(m => m.Name));

        // Inherited fields and properties from user-defined base classes
        var baseType = _classSymbol.BaseType;
        while (baseType != null)
        {
            if (USugarCompilerHelper.IsFrameworkNamespace(baseType.ContainingNamespace) || baseType.Name == "UdonSharpBehaviour") break;
            baseClassInitBoundaries.Add(_fieldInitOps.Count);
            foreach (var member in baseType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared) continue;
                if (declaredMemberNames.Contains(member.Name)) continue;
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
                declaredMemberNames.Add(member.Name);
                var baseFlags = FieldFlags.None;
                if (member.DeclaredAccessibility == Accessibility.Public
                    || member.GetAttributes().Any(a => a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute"))
                    baseFlags |= FieldFlags.Export;

                // Public delegate field from base class → expand to 3-variable bundle
                if (member.DeclaredAccessibility == Accessibility.Public
                    && member.Type is INamedTypeSymbol baseDelegateType && baseDelegateType.DelegateInvokeMethod != null)
                {
                    if (baseDelegateType.DelegateInvokeMethod.ReturnType.IsTupleType)
                        throw new NotSupportedException($"Tuple-return delegate field '{member.Name}' is not supported.");

                    var bundle = new DelegateBundle(member.Name);
                    _ctx.DeclareField(bundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver", baseFlags);
                    _ctx.DeclareField(bundle.Method, "SystemString");
                    _ctx.DeclareField(bundle.Addr, "SystemUInt32");
                    _ctx.DelegateFields.Add(member.Name);

                    // Declare convention fields for this delegate signature
                    var (baseConvArgs, baseConvRet) = HandlerBase.GetConventionFieldNames(baseDelegateType);
                    for (int ci = 0; ci < baseConvArgs.Length; ci++)
                        _ctx.TryDeclareVar(baseConvArgs[ci], NormalizeDelegateParamType(baseDelegateType.DelegateInvokeMethod.Parameters[ci].Type));
                    if (baseConvRet != null)
                        _ctx.TryDeclareVar(baseConvRet, ExternResolver.GetUdonTypeName(baseDelegateType.DelegateInvokeMethod.ReturnType));
                }
                else
                {
                    _ctx.DeclareField(member.Name, udonType, baseFlags, constValue);
                }

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
                if (declaredMemberNames.Contains(prop.Name)) continue;
                var isAuto = prop.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true || prop.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
                if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
                var udonType = GetUdonType(prop.Type);
                var flags = FieldFlags.None;
                if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
                declaredMemberNames.Add(prop.Name);
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

            // Declare params using LayoutPlanner IDs
            var paramVarIds = new string[method.Parameters.Length];
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var param = method.Parameters[i];
                var isDelegateParam = param.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null;
                var udonType = isDelegateParam ? "SystemUInt32" : GetUdonType(param.Type);
                _ctx.DeclareVar(ml.ParamIds[i], udonType);
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

            DeclareDelegateConventionVars(method, idx);
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
                var isDelegateParam = param.Type is INamedTypeSymbol nt3 && nt3.DelegateInvokeMethod != null;
                var udonType = isDelegateParam ? "SystemUInt32" : GetUdonType(param.Type);
                var paramId = $"__{idx}_{param.Name}__param";
                _ctx.DeclareVar(paramId, udonType);
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
                var isDelegateParam = param.Type is INamedTypeSymbol nt4 && nt4.DelegateInvokeMethod != null;
                var udonType = isDelegateParam ? "SystemUInt32" : GetUdonType(param.Type);
                var paramId = $"__{idx}_{param.Name}__param";
                _ctx.DeclareVar(paramId, udonType);
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

        // Synthesize _start if there are field initializers or FCB fields but no user-defined Start()
        if ((_fieldInitOps.Count > 0 || _fieldChangeCallbacks.Count > 0)
            && !methods.Any(m => UdonEventNames.TryGetValue(m.Name, out var en) && en == "_start"))
        {
            var startFunc = _module.AddFunction("_start", "_start");
            _builder.SetFunction(startFunc);
            EmitFieldInitializers();
            _builder.EmitReturn();
        }
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

            // Create bridge function with unique name (avoid __body label collision with class method)
            var bridgeFunc = _module.AddFunction($"__bridge_{ifaceMl.ExportName}", ifaceMl.ExportName);
            _builder.SetFunction(bridgeFunc);

            // Find class implementation
            var implMethod = _classSymbol.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
            if (implMethod == null || !_methodFunctions.TryGetValue(implMethod, out var classFunc))
                throw new InvalidOperationException(
                    $"Interface bridge for '{ifaceMl.ExportName}': "
                  + $"no function found for implementation of '{ifaceMethod.Name}'.");

            // Load interface params
            var args = new List<CValue>();
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
            var ml = bridge.RealMethodLayout;

            // Build canonical convention key using shared helper
            var sigPart = BuildBridgeSigPart(method);

            // Declare convention fields (if not already declared)
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = NormalizeDelegateParamType(method.Parameters[i].Type);
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
            var callArgs = new List<CValue>();
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = NormalizeDelegateParamType(method.Parameters[i].Type);
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

            // Use the saved type param snapshot instead of _typeParamMap (which may be cleared)
            var sigPart = BuildBridgeSigPart(method, resolvedMap);

            // Declare convention fields (if not already declared)
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = NormalizeDelegateParamType(method.Parameters[i].Type, resolvedMap);
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
            var callArgs = new List<CValue>();
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var argType = NormalizeDelegateParamType(method.Parameters[i].Type, resolvedMap);
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

    // ── Delegate convention vars ──

    void DeclareDelegateConventionVars(IMethodSymbol method, int idx)
    {
        foreach (var param in method.Parameters)
        {
            if (param.Type is not INamedTypeSymbol namedType || namedType.DelegateInvokeMethod == null)
                continue;

            var invoke = namedType.DelegateInvokeMethod;
            var argVarIds = new string[invoke.Parameters.Length];
            for (int j = 0; j < invoke.Parameters.Length; j++)
            {
                var argType = GetUdonType(invoke.Parameters[j].Type);
                argVarIds[j] = _ctx.DeclareVar($"__dlg_{idx}_{param.Name}_a{j}", argType);
            }
            string retVarId = null;
            if (!invoke.ReturnsVoid)
            {
                var retType = GetUdonType(invoke.ReturnType);
                retVarId = _ctx.DeclareVar($"__dlg_{idx}_{param.Name}_ret", retType);
            }
            _delegateParamConventions[(idx, param.Ordinal)] = new DelegateConvention
            {
                ArgVarIds = argVarIds, RetVarId = retVarId
            };
        }
    }

    static string SanitizeId(string name) => name.Replace('.', '_');

    /// <summary>Normalize param type: delegate-typed params become SystemUInt32 (JUMP addresses).</summary>
    static string NormalizeDelegateParamType(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        if (type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
            return "SystemUInt32";
        return ExternResolver.GetUdonTypeName(type, typeParamMap);
    }

    /// <summary>Build canonical convention sig part for a bridge method, matching HandlerBase.BuildConventionSigPart.</summary>
    static string BuildBridgeSigPart(IMethodSymbol method, Dictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var paramParts = method.Parameters.Select(p =>
        {
            if (p.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
                return "SystemUInt32";
            return ExternResolver.GetUdonTypeName(p.Type, typeParamMap);
        });
        var retPart = method.ReturnsVoid ? "Void" : ExternResolver.GetUdonTypeName(method.ReturnType, typeParamMap);
        var paramStr = string.Join("_", paramParts);
        if (paramStr == "") paramStr = "Void";
        return $"{paramStr}__{retPart}";
    }

    // ── EmitMethod ──

    void EmitMethod(IMethodSymbol method)
    {
        _currentMethod = method;
        var func = _methodFunctions[method];
        var idx = _methodSlots[method].Index;

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
            BridgeCallInternal(func, new CValue[] { newVal });
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
                    // Auto-property accessor: synthesize body (get → load field, set → store field)
                    var propType = GetUdonType(autoProp.Type);
                    if (method.MethodKind == MethodKind.PropertyGet
                        && _methodReturns.TryGetValue(method, out var autoRets) && autoRets.Length == 1)
                    {
                        BridgeStore(autoRets[0].Id, BridgeLoad(autoProp.Name, propType));
                    }
                    else if (method.MethodKind == MethodKind.PropertySet
                        && _methodParamVarIds.TryGetValue(method, out var paramIds) && paramIds.Length > 0)
                    {
                        BridgeStore(autoProp.Name, BridgeLoad(paramIds[0], propType));
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
                        new CValue[] { sizeConst });
                    BridgeStore(fieldId, arrVal);
                    for (int i = 0; i < arrayInit.ElementValues.Length; i++)
                    {
                        var elemVal = VisitExpression(arrayInit.ElementValues[i]);
                        var idxConst = BridgeConstInt(i);
                        var arrLoad = BridgeLoad(fieldId, arrayType);
                        BridgeCallExternVoid(
                            $"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                            new CValue[] { arrLoad, idxConst, elemVal });
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
                            new CValue[] { valueVal });
                        BridgeStore(fieldId, converted);
                        continue;
                    }
                }

                BridgeStore(fieldId, valueVal);
            }
            catch (NotSupportedException ex)
            {
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
        // Unwrap parenthesized expressions in statement context
        while (op is IParenthesizedOperation paren) op = paren.Operand;
        foreach (var h in _stmtHandlers)
            if (h.CanHandle(op)) { h.Handle(op); return; }
        throw new NotSupportedException($"Unsupported operation: {op.Kind} ({op.GetType().Name})");
    }

    void PreScanGotoLabels(IOperation op)
    {
        // No-op: HIR uses string-based CGoto/CLabel instead of IrBlock targets.
    }

    // ── Expression visitor (facade — delegates to handlers) ──

    CValue VisitExpression(IOperation op)
    {
        if (op == null)
            throw new NotSupportedException("VisitExpression called with null operation");
        // Unwrap parenthesized expressions (transparent wrapper)
        while (op is IParenthesizedOperation paren) op = paren.Operand;
        foreach (var h in _exprHandlers)
            if (h.CanHandle(op)) return h.Handle(op);
        throw new NotSupportedException(
            $"Unsupported expression: {op.Kind} ({op.GetType().Name})");
    }

    // ── Recursion-cycle analysis ──

    // Build the internal-call graph over all registered methods and mark, for each method, the callees
    // that lie in its strongly-connected component (Tarjan). A call along such an edge can re-enter the
    // caller, so the caller's live values must be spilled to the software stack around the call (Udon's
    // flat heap shares param/local slots across frames). Includes direct self-recursion (self-loops).
    void BuildRecursionInfo()
    {
        var roots = _methodFunctions.Keys
            .Select(m => m.OriginalDefinition)
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

        var recursive = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var scc in TarjanScc(internalMethods, edges))
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
            }
        }
        _ctx.RecursiveCallees = recursive;
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
            if (child is ILocalFunctionOperation) continue; // analysed as its own node
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
            if (child is ILocalFunctionOperation) continue; // analysed as its own node
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
        // Computed (non-auto) user-struct property getter: v.Prop. Auto-properties read their backing-field
        // slot directly (no method), but a computed getter must be inlined as a struct instance method.
        if (op is IPropertyReferenceOperation { Property.IsIndexer: false } pr
            && pr.Property is { IsStatic: false, GetMethod: { } pgm }
            && pr.Property.ContainingType is INamedTypeSymbol pit && EmitContext.IsUserStruct(pit)
            && IsComputedProperty(pr.Property))
            result.Add(pgm.OriginalDefinition);
        // User-struct operator: v1 + v2, -v (static operator methods).
        var opMethod = (op as IBinaryOperation)?.OperatorMethod ?? (op as IUnaryOperation)?.OperatorMethod;
        if (opMethod is { MethodKind: MethodKind.UserDefinedOperator }
            && opMethod.ContainingType is INamedTypeSymbol ot && EmitContext.IsUserStruct(ot))
            result.Add(opMethod.OriginalDefinition);
        foreach (var child in op.Children)
            CollectStructMethodsInOperation(child, result);
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
        {
            var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null) continue;
            var syntax = syntaxRef.GetSyntax();
            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var bodyOp = model.GetOperation(syntax);
            CollectBaseInstanceCallsInOperation(bodyOp, result);
        }
        return result.ToArray();
    }

    void CollectBaseInstanceCallsInOperation(IOperation op, HashSet<IMethodSymbol> result)
    {
        if (op == null) return;
        if (op is IInvocationOperation inv && IsBaseInstanceMethod(inv.TargetMethod))
            result.Add(inv.TargetMethod);
        foreach (var child in op.Children)
            CollectBaseInstanceCallsInOperation(child, result);
    }

    bool IsForeignStatic(IMethodSymbol method)
    {
        var resolved = method.ReducedFrom ?? method;
        if (!resolved.IsStatic) return false;
        if (resolved.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        if (ExternResolver.IsUdonSharpBehaviour(resolved.ContainingType)) return false;
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
