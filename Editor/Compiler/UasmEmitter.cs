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

    /// <summary>When true, write IR dump files during code generation.</summary>
    public bool DumpEnabled;

    // Property shims → EmitContext
    Compilation _compilation => _ctx.Compilation;
    INamedTypeSymbol _classSymbol => _ctx.ClassSymbol;
    HModule _hirModule => _ctx.HirModule;
    HirBuilder _builder => _ctx.Builder;
    LayoutPlanner _planner => _ctx.Planner;
    Dictionary<IMethodSymbol, HFunction> _methodFunctions => _ctx.MethodFunctions;
    Dictionary<IMethodSymbol, int> _methodIndices => _ctx.MethodIndices;
    Dictionary<IMethodSymbol, string> _methodVarPrefix => _ctx.MethodVarPrefix;
    Dictionary<IMethodSymbol, string> _methodRetVars => _ctx.MethodRetVars;
    Dictionary<IMethodSymbol, string> _methodRetTypes => _ctx.MethodRetTypes;
    Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.MethodParamVarIds;
    IMethodSymbol _currentMethod { get => _ctx.CurrentMethod; set => _ctx.CurrentMethod = value; }
    int _nextMethodIndex { get => _ctx.NextMethodIndex; set => _ctx.NextMethodIndex = value; }
    List<(IMethodSymbol symbol, HFunction func)> _pendingLocalFunctions => _ctx.PendingLocalFunctions;
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
        var assignHandler = new AssignmentHandler(_ctx);
        var operatorHandler = new OperatorHandler(_ctx);

        _stmtHandlers = new IOperationHandler[] { stmtHandler, loopHandler, assignHandler };
        _exprHandlers = new IExpressionHandler[]
        {
            new ExpressionHandler(_ctx),
            (IExpressionHandler)assignHandler,
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

    // ── HirBuilder bridge helpers (old IrBuilder API → HirBuilder) ──

    HExpr BridgeLoad(string fieldName, string type) => _builder.LoadField(fieldName, type);
    void BridgeStore(string fieldName, HExpr value) => _builder.EmitStoreField(fieldName, value);
    HExpr BridgeCallExtern(string retType, string sig, HExpr[] args)
        => _builder.ExternCall(sig, new List<HExpr>(args), retType);
    void BridgeCallExternVoid(string sig, HExpr[] args)
        => _builder.EmitExternVoid(sig, new List<HExpr>(args));
    HExpr BridgeCallInternal(HFunction func, HExpr[] args)
    {
        var retType = func.ReturnType ?? "SystemVoid";
        var call = _builder.InternalCall(func.Name, new List<HExpr>(args), retType);
        if (retType == "SystemVoid") { _builder.EmitExprStmt(call); return null; }
        return call;
    }
    HExpr BridgeConstInt(int value) => _builder.Const(value, "SystemInt32");

    // ── Emit ──

    /// <summary>Access to the HIR module for debugging and testing.</summary>
    public HModule HirModule => _hirModule;

    /// <summary>Called after handler emission, before optimization. Set for IR debugging.</summary>
    public Action<string, HModule> OnIrPass;

    public string Emit()
    {
        EnsurePlannerReady();
        EmitFields();
        SetReflectionValues();
        EmitMethods();
        OnIrPass?.Invoke("after-emit", _hirModule);
        var result = IrPipeline.GenerateUasmFromHir(_hirModule, DumpEnabled);
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

    void EnsurePlannerReady()
    {
        if (_planner.IsFrozen) return;
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
            var isAuto = prop.GetMethod?.IsImplicitlyDeclared == true || prop.SetMethod?.IsImplicitlyDeclared == true;
            if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
            var udonType = GetUdonType(prop.Type);
            var flags = FieldFlags.None;
            if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
            _ctx.DeclareField(prop.Name, udonType, flags);
        }

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
                if (declaredMemberNames.Contains(prop.Name)) continue;
                var isAuto = prop.GetMethod?.IsImplicitlyDeclared == true || prop.SetMethod?.IsImplicitlyDeclared == true;
                if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
                var udonType = GetUdonType(prop.Type);
                var flags = FieldFlags.None;
                if (prop.DeclaredAccessibility == Accessibility.Public) flags |= FieldFlags.Export;
                declaredMemberNames.Add(prop.Name);
                _ctx.DeclareField(prop.Name, udonType, flags);
            }
            baseType = baseType.BaseType;
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
        _nextMethodIndex = 0;
        foreach (var method in methods)
        {
            if (method.IsGenericMethod) continue;

            var idx = _nextMethodIndex++;
            _methodIndices[method] = idx;

            var ml = typeLayout.Methods[method];
            var exportName = ml.ExportName;
            _methodVarPrefix[method] = exportName;

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

            // Create HFunction with or without ExportName
            var func = _hirModule.AddFunction(exportName, shouldExport ? exportName : null);
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

            // Declare return var
            if (!method.ReturnsVoid && ml.ReturnId != null)
            {
                var retType = GetUdonType(method.ReturnType);
                func.ReturnType = retType;
                func.ReturnFieldName = ml.ReturnId;
                _methodRetVars[method] = ml.ReturnId;
                _methodRetTypes[method] = retType;
            }

            DeclareDelegateConventionVars(method, idx);
        }

        // Collect foreign static methods
        var foreignStatics = CollectForeignStaticMethods(methods);
        foreach (var fm in foreignStatics)
        {
            var idx = _nextMethodIndex++;
            _methodIndices[fm] = idx;
            _methodVarPrefix[fm] = idx.ToString();
            var funcName = $"__{idx}_{SanitizeId(fm.Name)}";
            var func = _hirModule.AddFunction(funcName);
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
                func.ReturnFieldName = retId;
                _methodRetVars[fm] = retId;
                _methodRetTypes[fm] = retType;
            }
        }

        // Collect base class instance methods
        var methodSet = new HashSet<IMethodSymbol>(methods, SymbolEqualityComparer.Default);
        var baseInstanceMethods = CollectBaseInstanceMethods(methods)
            .Where(bm => !methodSet.Contains(bm))
            .ToArray();
        foreach (var bm in baseInstanceMethods)
        {
            var idx = _nextMethodIndex++;
            _methodIndices[bm] = idx;
            _methodVarPrefix[bm] = idx.ToString();
            var funcName = $"__{idx}_{SanitizeId(bm.Name)}";
            var func = _hirModule.AddFunction(funcName);
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
                func.ReturnFieldName = retId;
                _methodRetVars[bm] = retId;
                _methodRetTypes[bm] = retType;
            }
        }

        // Second pass: emit bodies (skip generic definitions)
        foreach (var method in methods)
        {
            if (method.IsGenericMethod) continue;
            EmitMethod(method);
        }

        // Emit foreign static method bodies
        foreach (var fm in foreignStatics)
            EmitMethod(fm);

        // Emit base class instance method bodies
        foreach (var bm in baseInstanceMethods)
            EmitMethod(bm);

        // Emit interface bridge exports
        EmitInterfaceBridges();

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

        // Synthesize _start if there are field initializers or FCB fields but no user-defined Start()
        if ((_fieldInitOps.Count > 0 || _fieldChangeCallbacks.Count > 0)
            && !methods.Any(m => UdonEventNames.TryGetValue(m.Name, out var en) && en == "_start"))
        {
            var startFunc = _hirModule.AddFunction("_start", "_start");
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
            var bridgeFunc = _hirModule.AddFunction($"__bridge_{ifaceMl.ExportName}", ifaceMl.ExportName);
            _builder.SetFunction(bridgeFunc);

            // Find class implementation
            var implMethod = _classSymbol.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
            if (implMethod == null || !_methodFunctions.TryGetValue(implMethod, out var classFunc))
                throw new InvalidOperationException(
                    $"Interface bridge for '{ifaceMl.ExportName}': "
                  + $"no function found for implementation of '{ifaceMethod.Name}'.");

            // Load interface params
            var args = new List<HExpr>();
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

    // ── EmitMethod ──

    void EmitMethod(IMethodSymbol method)
    {
        _currentMethod = method;
        var func = _methodFunctions[method];
        var idx = _methodIndices[method];

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
            var varChangeFunc = _hirModule.AddFunction(varChangeName, varChangeName);
            _builder.SetFunction(varChangeFunc);

            // Preamble: read new value from field, restore old value to field
            var newVal = BridgeLoad(fcbFieldName, fcbFieldType);
            var oldVal = BridgeLoad($"__old_{fcbFieldName}", fcbFieldType);
            BridgeStore(fcbFieldName, oldVal);

            // Call setter with new value
            BridgeCallInternal(func, new HExpr[] { newVal });
            _builder.EmitReturn();
        }

        // Switch to the method's function for body emission
        _builder.SetFunction(func);

        // Emit field initializers at the start of _start
        var exportName = _methodVarPrefix[method];
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
            else if (bodyOp is IAnonymousFunctionOperation anonFunc)
            {
                if (anonFunc.Body is IBlockOperation anonBlock)
                    VisitOperation(anonBlock);
                else if (anonFunc.Body != null && _methodRetVars.TryGetValue(method, out var lambdaRetId))
                {
                    var resultVal = VisitExpression(anonFunc.Body);
                    BridgeStore(lambdaRetId, resultVal);
                }
            }
            else if (bodyOp is IBlockOperation block)
                VisitOperation(block);
            // Expression-bodied property: int X => expr;
            else if (syntax is PropertyDeclarationSyntax propDecl
                     && propDecl.ExpressionBody != null)
            {
                var exprOp = model.GetOperation(propDecl.ExpressionBody.Expression);
                if (exprOp != null && _methodRetVars.TryGetValue(method, out var retId))
                {
                    var resultVal = VisitExpression(exprOp);
                    BridgeStore(retId, resultVal);
                }
            }
            // Block-bodied property accessor: int X { get { return expr; } }
            else if (syntax is AccessorDeclarationSyntax accessorDecl)
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
                        new HExpr[] { sizeConst });
                    BridgeStore(fieldId, arrVal);
                    for (int i = 0; i < arrayInit.ElementValues.Length; i++)
                    {
                        var elemVal = VisitExpression(arrayInit.ElementValues[i]);
                        var idxConst = BridgeConstInt(i);
                        var arrLoad = BridgeLoad(fieldId, arrayType);
                        BridgeCallExternVoid(
                            $"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                            new HExpr[] { arrLoad, idxConst, elemVal });
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
                            new HExpr[] { valueVal });
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
        foreach (var h in _stmtHandlers)
            if (h.CanHandle(op)) { h.Handle(op); return; }
        throw new NotSupportedException($"Unsupported operation: {op.Kind} ({op.GetType().Name})");
    }

    void PreScanGotoLabels(IOperation op)
    {
        // No-op: HIR uses string-based HGoto/HLabelStmt instead of IrBlock targets.
    }

    // ── Expression visitor (facade — delegates to handlers) ──

    HExpr VisitExpression(IOperation op)
    {
        if (op == null)
            throw new NotSupportedException("VisitExpression called with null operation");
        foreach (var h in _exprHandlers)
            if (h.CanHandle(op)) return h.Handle(op);
        throw new NotSupportedException(
            $"Unsupported expression: {op.Kind} ({op.GetType().Name})");
    }

    bool HasNonTailSelfRecursiveCall(IMethodSymbol method)
    {
        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return false;
        var syntax = syntaxRef.GetSyntax();
        var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
        var bodyOp = model.GetOperation(syntax);
        return ContainsNonTailSelfCall(bodyOp, method);
    }

    static bool ContainsNonTailSelfCall(IOperation op, IMethodSymbol method)
    {
        if (op == null) return false;

        if (op is IReturnOperation ret && ret.ReturnedValue is IInvocationOperation retInv
            && SymbolEqualityComparer.Default.Equals(retInv.TargetMethod, method))
        {
            foreach (var arg in retInv.Arguments)
                if (ContainsNonTailSelfCall(arg, method))
                    return true;
            return false;
        }

        if (op is IInvocationOperation inv
            && SymbolEqualityComparer.Default.Equals(inv.TargetMethod, method))
            return true;
        foreach (var child in op.Children)
            if (ContainsNonTailSelfCall(child, method))
                return true;
        return false;
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
