using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public abstract class HandlerBase
{
    protected readonly EmitContext _ctx;

    protected HandlerBase(EmitContext ctx) => _ctx = ctx;

    // ── Property shims to EmitContext ──
    protected Compilation _compilation => _ctx.Compilation;
    protected INamedTypeSymbol _classSymbol => _ctx.ClassSymbol;
    protected HModule _hirModule => _ctx.HirModule;
    protected HirBuilder _builder => _ctx.Builder;
    protected LayoutPlanner _planner => _ctx.Planner;
    protected Dictionary<IMethodSymbol, HFunction> _methodFunctions => _ctx.MethodFunctions;
    protected Dictionary<IMethodSymbol, EmitContext.MethodSlot> _methodSlots => _ctx.MethodSlots;
    protected Dictionary<IMethodSymbol, ReturnSlot[]> _methodReturns => _ctx.MethodReturns;
    protected Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.MethodParamVarIds;
    protected IMethodSymbol _currentMethod { get => _ctx.CurrentMethod; set => _ctx.CurrentMethod = value; }
    protected List<(IMethodSymbol symbol, HFunction func)> _pendingLocalFunctions => _ctx.PendingLocalFunctions;
    protected Dictionary<ILocalSymbol, IMethodSymbol> _delegateVarMap => _ctx.DelegateVarMap;
    protected List<IMethodSymbol> _pendingGenericSpecs => _ctx.PendingGenericSpecs;
    protected Dictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap { get => _ctx.TypeParamMap; set => _ctx.TypeParamMap = value; }
    protected Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> _delegateParamConventions => _ctx.DelegateParamConventions;
    protected Dictionary<IMethodSymbol, DelegateConvention> _lambdaConventionOverrides => _ctx.LambdaConventionOverrides;
    protected Dictionary<ILocalSymbol, EmitContext.LocalBinding> _localBindings => _ctx.LocalBindings;
    protected List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.FieldInitOps;
    protected Dictionary<string, string> _fieldChangeCallbacks => _ctx.FieldChangeCallbacks;
    protected Stack<(HExpr Target, string DelegateFieldName)> _conditionalAccessStack => _ctx.ConditionalAccessStack;
    protected Stack<List<(HExpr val, ITypeSymbol type)>> _usingDisposableStack => _ctx.UsingDisposableStack;
    protected HashSet<string> _delegateFields => _ctx.DelegateFields;
    protected List<EmitDiagnostic> _diagnostics => _ctx.Diagnostics;

    // ── Dispatch (recursive descent into other handlers via UasmEmitter facade) ──
    protected void VisitOperation(IOperation op) => _ctx.VisitOperation(op);
    protected HExpr VisitExpression(IOperation op) => _ctx.VisitExpression(op);
    protected HExpr EmitPatternCheck(HExpr value, ITypeSymbol valueType, IPatternOperation pattern)
        => _ctx.EmitPatternCheck(value, valueType, pattern);

    // ── Type resolution ──
    protected string GetUdonType(ITypeSymbol type) => ExternResolver.GetUdonTypeName(type, _ctx.TypeParamMap);
    protected ITypeSymbol ResolveType(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol tp && _ctx.TypeParamMap != null && _ctx.TypeParamMap.TryGetValue(tp, out var resolved))
            return resolved;
        return type;
    }
    protected string GetArrayType(IArrayTypeSymbol arrType) => GetUdonType(arrType);
    protected string GetArrayElemType(IArrayTypeSymbol arrType)
    {
        var t = GetArrayType(arrType);
        return t.Substring(0, t.Length - "Array".Length);
    }

    // ── HIR convenience methods ──

    /// <summary>Emit: slot = expr</summary>
    protected void EmitAssign(int destSlot, HExpr value) => _builder.EmitAssign(destSlot, value);

    /// <summary>Emit: fieldName = expr</summary>
    protected void EmitStoreField(string fieldName, HExpr value) => _builder.EmitStoreField(fieldName, value);

    /// <summary>Emit: return [value]</summary>
    protected void EmitReturn(HExpr value = null) => _builder.EmitReturn(value);

    /// <summary>Create a constant.</summary>
    protected HConst Const(object value, string type) => _builder.Const(value, type);

    /// <summary>Create a slot reference expression.</summary>
    protected HSlotRef SlotRef(int slotId) => _builder.SlotRef(slotId);

    /// <summary>Create a field load expression.</summary>
    protected HLoadField LoadField(string fieldName, string type) => _builder.LoadField(fieldName, type);

    /// <summary>Create a field address reference (for extern out/ref).</summary>
    protected HFieldAddr FieldAddr(string fieldName, string type) => _builder.FieldAddr(fieldName, type);

    /// <summary>Create an extern call expression.</summary>
    protected HExternCall ExternCall(string sig, List<HExpr> args, string retType)
        => _builder.ExternCall(ResolveExtern(sig), args, retType);

    /// <summary>Emit a void extern call as a statement.</summary>
    protected void EmitExternVoid(string sig, List<HExpr> args)
        => _builder.EmitExternVoid(ResolveExtern(sig), args);

    /// <summary>Create an internal call expression.</summary>
    protected HInternalCall InternalCall(string funcName, List<HExpr> args, string retType)
        => _builder.InternalCall(funcName, args, retType);

    /// <summary>Create a select (ternary) expression.</summary>
    protected HSelect Select(HExpr cond, HExpr trueVal, HExpr falseVal, string type)
        => _builder.Select(cond, trueVal, falseVal, type);

    /// <summary>Create a function reference (for delegate/JUMP_INDIRECT).</summary>
    protected HFuncRef FuncRef(string funcName) => _builder.FuncRef(funcName);

    /// <summary>Emit a statement.</summary>
    protected void Emit(HStmt stmt) => _builder.Emit(stmt);

    /// <summary>Emit an expression as a statement (side-effecting calls).</summary>
    protected void EmitExprStmt(HExpr expr) => _builder.EmitExprStmt(expr);

    // ── Extern resolution ──

    static readonly string[] FallbackBaseTypes = new[]
    {
        "UnityEngineComponent", "UnityEngineBehaviour",
        "UnityEngineMonoBehaviour", "UnityEngineObject",
    };

    static string ResolveExtern(string externSig)
    {
        var isValid = ExternResolver.IsExternValid;
        if (isValid == null || isValid(externSig))
            return externSig;
        var dotIdx = externSig.IndexOf(".__");
        if (dotIdx < 0) return externSig;
        var containingType = externSig.Substring(0, dotIdx);
        var rest = externSig.Substring(dotIdx);
        foreach (var baseType in FallbackBaseTypes)
        {
            if (baseType == containingType) continue;
            var alt = baseType + rest;
            if (isValid(alt))
                return alt;
        }
        return externSig;
    }

    protected static IOperation UnwrapConversions(IOperation op)
    {
        while (op is IConversionOperation conv) op = conv.Operand;
        return op;
    }

    protected static string SanitizeId(string name) => name.Replace('.', '_');
    protected static string ToInvariantString(object value)
        => value is IFormattable fmt ? fmt.ToString(null, CultureInfo.InvariantCulture)
         : value?.ToString() ?? "null";

    // ── Shared helpers (used by multiple handlers) ──

    protected string GetParamVarId(IParameterSymbol param)
    {
        if (_currentMethod != null
            && _lambdaConventionOverrides.TryGetValue(_currentMethod, out var conv)
            && param.Ordinal < conv.ArgVarIds.Length)
            return conv.ArgVarIds[param.Ordinal];
        if (param.ContainingSymbol is IMethodSymbol method
            && _methodParamVarIds.TryGetValue(method, out var paramIds)
            && param.Ordinal < paramIds.Length)
            return paramIds[param.Ordinal];
        if (_currentMethod != null && param.ContainingSymbol is IMethodSymbol paramMethod
            && _currentMethod.IsGenericMethod && !_currentMethod.IsDefinition
            && SymbolEqualityComparer.Default.Equals(paramMethod, _currentMethod.OriginalDefinition)
            && _methodParamVarIds.TryGetValue(_currentMethod, out var specParamIds)
            && param.Ordinal < specParamIds.Length)
            return specParamIds[param.Ordinal];
        throw new InvalidOperationException(
            $"Cannot resolve parameter '{param.Name}' (ordinal {param.Ordinal}) "
          + $"in method '{_currentMethod?.Name ?? "(none)"}'. "
          + "Not found in lambda overrides, method params, or variable table.");
    }

    /// <summary>Read a parameter value as an HExpr (field load).</summary>
    protected HExpr LoadParam(IParameterSymbol param)
    {
        var fieldName = GetParamVarId(param);
        var type = GetUdonType(param.Type);
        return LoadField(fieldName, type);
    }

    protected HExpr EmitEnumToUnderlying(HExpr operand, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return operand;
        var underlyingType = named.EnumUnderlyingType;
        var convertMethod = ExternResolver.GetConvertMethodName(underlyingType);
        if (convertMethod == null) return operand;
        var underlyingUdon = GetUdonType(underlyingType);
        return ExternCall(
            $"SystemConvert.__{convertMethod}__SystemObject__{underlyingUdon}",
            new List<HExpr> { operand },
            underlyingUdon);
    }


    // ── L-Value Assignment ──

    /// <summary>
    /// Assign a value to a common l-value target (declaration, local, this-field, parameter, discard).
    /// Callers with specialized targets (array elements, cross-behaviour fields) should handle those
    /// first, then delegate to this method for the common cases.
    /// </summary>
    protected void AssignToLValue(IOperation target, HExpr value)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _ctx.DeclareLocal(localRef.Local.Name, udonType);
                    _localBindings[localRef.Local] = new EmitContext.LocalBinding(localId);
                    EmitStoreField(localId, value);
                }
                break;

            case ILocalReferenceOperation existingLocal:
                if (_localBindings.TryGetValue(existingLocal.Local, out var existingBinding))
                {
                    EmitStoreField(existingBinding.Id, value);
                }
                else
                {
                    var udonType = GetUdonType(existingLocal.Type);
                    var newId = _ctx.DeclareLocal(existingLocal.Local.Name, udonType);
                    _localBindings[existingLocal.Local] = new EmitContext.LocalBinding(newId);
                    EmitStoreField(newId, value);
                }
                break;

            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                EmitStoreField(fieldRef.Field.Name, value);
                break;

            case IParameterReferenceOperation paramRef:
                EmitStoreField(GetParamVarId(paramRef.Parameter), value);
                break;

            case IDiscardOperation:
                break;

            default:
                throw new System.NotSupportedException(
                    $"Unsupported l-value target: {target.GetType().Name}");
        }
    }

    // ── Lambda / Local Function Helpers ──

    protected void RegisterLocalFunction(IMethodSymbol localFunc)
    {
        if (_methodFunctions.ContainsKey(localFunc)) return;
        var funcName = string.IsNullOrEmpty(localFunc.Name) ? "lambda" : localFunc.Name;
        var slot = _ctx.RegisterMethod(localFunc, i => $"__{i}_{funcName}");
        var idx = slot.Index;
        var irName = slot.VarPrefix;

        // Create HFunction (internal, no export)
        var func = _hirModule.AddFunction(irName);

        // Declare params as fields (HIR uses field-based parameter passing)
        var lfParamIds = new string[localFunc.Parameters.Length];
        for (int pi = 0; pi < localFunc.Parameters.Length; pi++)
        {
            var param = localFunc.Parameters[pi];
            var isDlg = param.Type is INamedTypeSymbol nt4 && nt4.DelegateInvokeMethod != null;
            var udonType = isDlg ? "SystemUInt32" : GetUdonType(param.Type);
            var paramId = $"__{idx}_{param.Name}__param";
            _ctx.DeclareVar(paramId, udonType);
            lfParamIds[pi] = paramId;
        }
        _methodParamVarIds[localFunc] = lfParamIds;
        foreach (var pid in lfParamIds) func.ParamFieldNames.Add(pid);

        if (!localFunc.ReturnsVoid)
        {
            var retType = GetUdonType(localFunc.ReturnType);
            func.ReturnType = retType;
            var retId = $"__{idx}_{funcName}__ret";
            func.ReturnSlots.Add(new ReturnSlot(retId, retType));
            _methodReturns[localFunc] = new[] { new ReturnSlot(retId, retType) };
        }

        _methodFunctions[localFunc] = func;
        _pendingLocalFunctions.Add((localFunc, func));
    }

    /// <summary>
    /// Hoist a lambda expression to an internal method.
    ///
    /// KNOWN LIMITATION: Captured locals are mapped to module-level fields via
    /// <see cref="EmitContext.LocalBindings"/>. All lambdas share the same field for a
    /// given local, so nested lambdas (lambda inside lambda) that capture the same
    /// variable will alias. This is correct for sequential execution but not for
    /// concurrent delegate storage with different capture values (e.g., loop-variable
    /// capture where the delegate outlives the loop iteration). This is a fundamental
    /// constraint of the Udon VM's flat heap — there are no per-invocation closures.
    /// </summary>
    protected IMethodSymbol HoistLambdaToMethod(IAnonymousFunctionOperation lambda)
    {
        var symbol = lambda.Symbol;
        if (_methodFunctions.ContainsKey(symbol)) return symbol;
        RegisterLocalFunction(symbol);
        return symbol;
    }

    // ── Delegate convention helpers ──

    /// <summary>Compute signature-based convention field names for a delegate type.</summary>
    internal static (string[] argNames, string retName) GetConventionFieldNames(INamedTypeSymbol delegateType)
    {
        var invoke = delegateType.DelegateInvokeMethod;
        var sigPart = BuildConventionSigPart(invoke);

        var argNames = new string[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
            argNames[i] = $"__dlgc_{sigPart}__a{i}";

        string retName = null;
        if (!invoke.ReturnsVoid)
            retName = $"__dlgc_{sigPart}__ret";

        return (argNames, retName);
    }

    /// <summary>Build the canonical convention signature key for a delegate invoke method.</summary>
    internal static string BuildConventionSigPart(IMethodSymbol invoke)
    {
        // Normalize delegate-typed params to SystemUInt32 (JUMP addresses)
        var paramParts = invoke.Parameters.Select(p =>
        {
            if (p.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
                return "SystemUInt32";
            return ExternResolver.GetUdonTypeName(p.Type);
        });

        // Include return type to avoid Func<int> vs Func<bool> collision
        var retPart = invoke.ReturnsVoid ? "Void" : ExternResolver.GetUdonTypeName(invoke.ReturnType);
        var paramStr = string.Join("_", paramParts);
        if (paramStr == "") paramStr = "Void";
        return $"{paramStr}__{retPart}";
    }

    /// <summary>Check if a lambda captures variables from outer scope.</summary>
    protected static bool HasCaptures(IAnonymousFunctionOperation lambda)
    {
        var lambdaParams = new HashSet<ISymbol>(lambda.Symbol.Parameters, SymbolEqualityComparer.Default);
        foreach (var desc in lambda.Body.DescendantsAndSelf())
        {
            if (desc is ILocalReferenceOperation localRef && !lambdaParams.Contains(localRef.Local))
                return true;
            if (desc is IParameterReferenceOperation paramRef && !lambdaParams.Contains(paramRef.Parameter))
                return true;
        }
        return false;
    }

    // ── Delegate bridge resolution ──

    /// <summary>Resolve delegate creation to bridge name, FuncRef, and target instance.</summary>
    protected (string bridgeName, HExpr funcRef, HExpr targetInstance) ResolveDelegateBridge(IDelegateCreationOperation op)
    {
        IMethodSymbol targetMethod = null;
        HExpr targetInstance = null;
        switch (op.Target)
        {
            case IAnonymousFunctionOperation lambda:
                targetMethod = HoistLambdaToMethod(lambda);
                break;
            case IMethodReferenceOperation methodRef:
                targetMethod = methodRef.Method;
                if (methodRef.Instance != null && methodRef.Instance is not IInstanceReferenceOperation)
                    targetInstance = VisitExpression(methodRef.Instance);
                break;
        }
        if (targetMethod == null)
            throw new System.NotSupportedException($"Unsupported delegate target: {op.Target.GetType().Name}");

        // For hoisted lambdas/local functions, create a pending bridge dynamically
        // since they aren't part of the TypeLayout's pre-computed bridges.
        string bridgeExportName;
        if (targetMethod.MethodKind == MethodKind.LambdaMethod || targetMethod.MethodKind == MethodKind.LocalFunction)
        {
            if (!_methodSlots.TryGetValue(targetMethod, out var targetSlot))
                throw new System.InvalidOperationException($"Lambda/local function '{targetMethod.Name}' not registered.");
            bridgeExportName = $"__dlg_{targetSlot.VarPrefix}";
            // Snapshot current type parameter map — bridge emission happens after generic method
            // emit completes and TypeParamMap is cleared, so we must capture resolved types now.
            var typeParamSnapshot = _ctx.TypeParamMap != null
                ? new Dictionary<ITypeParameterSymbol, ITypeSymbol>(_ctx.TypeParamMap, SymbolEqualityComparer.Default)
                : null;
            _ctx.PendingDelegateBridges.Add((targetMethod, bridgeExportName, typeParamSnapshot));
        }
        else
        {
            var bridge = _planner.GetDelegateBridgeLayout(targetMethod);
            bridgeExportName = bridge.BridgeExportName;
        }

        var funcRef = FuncRef(bridgeExportName);
        return (bridgeExportName, funcRef, targetInstance);
    }

    // ── Call helpers ──

    protected (string exportName, string[] paramIds, string retId) GetCalleeLayout(IMethodSymbol target)
    {
        if (_methodParamVarIds.TryGetValue(target, out var localParamIds))
        {
            var exportName = _methodSlots[target].VarPrefix;
            string retId = null;
            if (_methodReturns.TryGetValue(target, out var rets) && rets.Length == 1)
                retId = rets[0].Id;
            return (exportName, localParamIds, retId);
        }
        var ml = _planner.GetCalleeLayout(target);
        return (ml.ExportName, ml.ParamIds.ToArray(), ml.ReturnId);
    }

    /// <summary>Get return slots for a callee method.</summary>
    protected ReturnSlot[] GetCalleeReturns(IMethodSymbol target)
    {
        if (_methodReturns.TryGetValue(target, out var slots))
            return slots;
        var ml = _planner.GetCalleeLayout(target);
        return ml.Returns.ToArray();
    }


    /// <summary>
    /// Call an internal function via HirBuilder.InternalCall.
    /// Returns the result HExpr — this is an expression only, NOT emitted to the HIR.
    /// For void calls (e.g. property setters), wrap with <c>EmitExprStmt()</c> to add to the HIR.
    /// </summary>
    protected HExpr EmitCallToMethod(IMethodSymbol target, List<HExpr> args)
    {
        if (!_methodFunctions.TryGetValue(target, out var func))
            throw new InvalidOperationException($"No HFunction registered for method '{target.Name}'");
        var retType = func.ReturnType ?? "SystemVoid";
        return InternalCall(func.Name, args, retType);
    }
}
