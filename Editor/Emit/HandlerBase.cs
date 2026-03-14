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
    protected Dictionary<IMethodSymbol, int> _methodIndices => _ctx.MethodIndices;
    protected Dictionary<IMethodSymbol, string> _methodVarPrefix => _ctx.MethodVarPrefix;
    protected Dictionary<IMethodSymbol, string> _methodRetVars => _ctx.MethodRetVars;
    protected Dictionary<IMethodSymbol, string> _methodRetTypes => _ctx.MethodRetTypes;
    protected Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.MethodParamVarIds;
    protected IMethodSymbol _currentMethod { get => _ctx.CurrentMethod; set => _ctx.CurrentMethod = value; }
    protected int _nextMethodIndex { get => _ctx.NextMethodIndex; set => _ctx.NextMethodIndex = value; }
    protected List<(IMethodSymbol symbol, HFunction func)> _pendingLocalFunctions => _ctx.PendingLocalFunctions;
    protected Dictionary<ILocalSymbol, IMethodSymbol> _delegateVarMap => _ctx.DelegateVarMap;
    protected List<IMethodSymbol> _pendingGenericSpecs => _ctx.PendingGenericSpecs;
    protected Dictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap { get => _ctx.TypeParamMap; set => _ctx.TypeParamMap = value; }
    protected Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> _delegateParamConventions => _ctx.DelegateParamConventions;
    protected Dictionary<IMethodSymbol, DelegateConvention> _lambdaConventionOverrides => _ctx.LambdaConventionOverrides;
    protected Dictionary<ILocalSymbol, string> _localVarIds => _ctx.LocalVarIds;
    protected List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.FieldInitOps;
    protected Dictionary<string, string> _fieldChangeCallbacks => _ctx.FieldChangeCallbacks;
    protected Dictionary<ITypeSymbol, string> _enumArrayVars => _ctx.EnumArrayVars;
    protected Stack<HExpr> _conditionalAccessTargets => _ctx.ConditionalAccessTargets;
    protected Stack<List<(HExpr val, ITypeSymbol type)>> _usingDisposableStack => _ctx.UsingDisposableStack;
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
    protected HExternCall ExternCall(string sig, List<HExpr> args, string retType, bool isPure = false)
        => _builder.ExternCall(ResolveExtern(sig), args, retType, isPure);

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
        if (ExternResolver.IsExternValid == null || ExternResolver.IsExternValid(externSig))
            return externSig;
        var dotIdx = externSig.IndexOf(".__");
        if (dotIdx < 0) return externSig;
        var containingType = externSig.Substring(0, dotIdx);
        var rest = externSig.Substring(dotIdx);
        foreach (var baseType in FallbackBaseTypes)
        {
            if (baseType == containingType) continue;
            var alt = baseType + rest;
            if (ExternResolver.IsExternValid(alt))
                return alt;
        }
        return externSig;
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

    protected string GetOrCreateEnumArray(INamedTypeSymbol enumType)
    {
        if (_enumArrayVars.TryGetValue(enumType, out var existing))
            return existing;

        var members = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue && f.IsConst)
            .ToList();

        long maxVal = 0;
        foreach (var m in members)
        {
            var val = Convert.ToInt64(m.ConstantValue);
            if (val < 0)
                throw new NotSupportedException(
                    $"Cannot cast integer to enum {enumType.Name}: negative value {val} is not supported");
            if (val > maxVal) maxVal = val;
        }

        if (maxVal > 2048)
            throw new NotSupportedException(
                $"Cannot cast integer to enum {enumType.Name}: max value {maxVal} exceeds 2048 limit");

        int msb = 0;
        long tmp = maxVal;
        while (tmp > 0) { tmp >>= 1; msb++; }
        int arraySize = Math.Max(1 << msb, 1);

        var underlyingType = enumType.EnumUnderlyingType;
        var clrType = underlyingType?.SpecialType switch
        {
            SpecialType.System_Byte => typeof(byte),
            SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Int16 => typeof(short),
            SpecialType.System_UInt16 => typeof(ushort),
            SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint),
            SpecialType.System_Int64 => typeof(long),
            SpecialType.System_UInt64 => typeof(ulong),
            _ => typeof(int),
        };

        var enumArr = new object[arraySize];
        for (int i = 0; i < arraySize; i++)
            enumArr[i] = Convert.ChangeType(i, clrType);

        var enumFullName = enumType.ToDisplayString().Replace('.', '_');
        var arrayId = $"__enumArr_{enumFullName}";
        _ctx.DeclareEnumArray(arrayId, enumArr);
        _enumArrayVars[enumType] = arrayId;
        return arrayId;
    }

    // ── Lambda / Local Function Helpers ──

    protected void RegisterLocalFunction(IMethodSymbol localFunc)
    {
        if (_methodFunctions.ContainsKey(localFunc)) return;
        var idx = _nextMethodIndex++;
        _methodIndices[localFunc] = idx;
        var funcName = string.IsNullOrEmpty(localFunc.Name) ? "lambda" : localFunc.Name;
        var irName = $"__{idx}_{funcName}";
        _methodVarPrefix[localFunc] = irName;

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
            func.ReturnFieldName = retId;
            _methodRetVars[localFunc] = retId;
            _methodRetTypes[localFunc] = retType;
        }

        _methodFunctions[localFunc] = func;
        _pendingLocalFunctions.Add((localFunc, func));
    }

    protected IMethodSymbol HoistLambdaToMethod(IAnonymousFunctionOperation lambda)
    {
        var symbol = lambda.Symbol;
        if (_methodFunctions.ContainsKey(symbol)) return symbol;
        RegisterLocalFunction(symbol);
        return symbol;
    }

    // ── Call helpers ──

    protected (string exportName, string[] paramIds, string retId) GetCalleeLayout(IMethodSymbol target)
    {
        if (_methodParamVarIds.TryGetValue(target, out var localParamIds))
        {
            var exportName = _methodVarPrefix[target];
            _methodRetVars.TryGetValue(target, out var retId);
            return (exportName, localParamIds, retId);
        }
        var ml = _planner.GetCalleeLayout(target);
        return (ml.ExportName, ml.ParamIds.ToArray(), ml.ReturnId);
    }

    /// <summary>
    /// Call an internal function via HirBuilder.InternalCall.
    /// Returns the result HExpr (or null for void functions).
    /// </summary>
    protected HExpr EmitCallToMethod(IMethodSymbol target, List<HExpr> args)
    {
        if (!_methodFunctions.TryGetValue(target, out var func))
            throw new InvalidOperationException($"No HFunction registered for method '{target.Name}'");
        var retType = func.ReturnType ?? "SystemVoid";
        return InternalCall(func.Name, args, retType);
    }
}
