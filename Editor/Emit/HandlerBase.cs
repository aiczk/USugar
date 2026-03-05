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

    // ── Property shims (same names as UasmEmitter fields for zero-change method migration) ──
    protected Compilation _compilation => _ctx.Compilation;
    protected INamedTypeSymbol _classSymbol => _ctx.ClassSymbol;
    protected UasmModule _module => _ctx.Module;
    protected VariableTable _vars => _ctx.Vars;
    protected LayoutPlanner _planner => _ctx.Planner;
    protected Dictionary<IMethodSymbol, int> _methodLabels => _ctx.MethodLabels;
    protected Dictionary<IMethodSymbol, int> _methodIndices => _ctx.MethodIndices;
    protected Dictionary<IMethodSymbol, string> _methodVarPrefix => _ctx.MethodVarPrefix;
    protected Dictionary<IMethodSymbol, string> _methodRetVars => _ctx.MethodRetVars;
    protected Dictionary<IMethodSymbol, string> _methodRetTypes => _ctx.MethodRetTypes;
    protected Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.MethodParamVarIds;
    protected IMethodSymbol _currentMethod { get => _ctx.CurrentMethod; set => _ctx.CurrentMethod = value; }
    protected int _nextMethodIndex { get => _ctx.NextMethodIndex; set => _ctx.NextMethodIndex = value; }
    protected List<(IMethodSymbol symbol, int label)> _pendingLocalFunctions => _ctx.PendingLocalFunctions;
    protected Dictionary<ILocalSymbol, IMethodSymbol> _delegateVarMap => _ctx.DelegateVarMap;
    protected List<IMethodSymbol> _pendingGenericSpecs => _ctx.PendingGenericSpecs;
    protected Dictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap { get => _ctx.TypeParamMap; set => _ctx.TypeParamMap = value; }
    protected Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> _delegateParamConventions => _ctx.DelegateParamConventions;
    protected Dictionary<IMethodSymbol, DelegateConvention> _lambdaConventionOverrides => _ctx.LambdaConventionOverrides;
    protected Dictionary<IMethodSymbol, int> _methodBodyLabels => _ctx.MethodBodyLabels;
    protected Dictionary<ILocalSymbol, string> _localVarIds => _ctx.LocalVarIds;
    protected List<(string fieldId, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.FieldInitOps;
    protected Dictionary<string, string> _fieldChangeCallbacks => _ctx.FieldChangeCallbacks;
    protected Dictionary<ITypeSymbol, string> _enumArrayVars => _ctx.EnumArrayVars;
    protected Stack<string> _conditionalAccessTargets => _ctx.ConditionalAccessTargets;
    protected Stack<int> _breakLabels => _ctx.BreakLabels;
    protected Stack<int> _continueLabels => _ctx.ContinueLabels;
    protected Dictionary<ILabelSymbol, int> _gotoLabels => _ctx.GotoLabels;
    protected Stack<List<(string varId, ITypeSymbol type)>> _usingDisposableStack => _ctx.UsingDisposableStack;
    protected List<EmitDiagnostic> _diagnostics => _ctx.Diagnostics;

    // ── Dispatch (recursive descent into other handlers via UasmEmitter facade) ──
    protected void VisitOperation(IOperation op) => _ctx.VisitOperation(op);
    protected string VisitExpression(IOperation op) => _ctx.VisitExpression(op);
    protected string EmitPatternCheck(string valueId, ITypeSymbol valueType, IPatternOperation pattern)
        => _ctx.EmitPatternCheck(valueId, valueType, pattern);

    // ── Utility methods (used across multiple handlers) ──
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
    protected void AddExternChecked(string externSig, IOperation sourceOp = null)
    {
        _ctx.AddExternChecked(externSig);
    }
    protected static string SanitizeId(string name) => name.Replace('.', '_');
    protected static string ToInvariantString(object value)
        => value is IFormattable fmt ? fmt.ToString(null, CultureInfo.InvariantCulture)
         : value?.ToString() ?? "null";

    // TargetHint optimization: when the caller already knows the destination variable
    // (e.g., `x = SomeExtern()`), we pass that variable ID as TargetHint so the extern
    // writes directly into it, skipping a PUSH+COPY. The hint is only consumed if the
    // type matches — a type mismatch means the destination can't hold the result directly.
    //
    // Callers must clear TargetHint before evaluating sub-expressions to prevent an inner
    // expression from accidentally consuming the outer hint. The pattern is:
    //   savedHint = _ctx.TargetHint; _ctx.TargetHint = null;
    //   ... evaluate operands ...
    //   _ctx.TargetHint = savedHint; ConsumeTargetHintOrTemp(type);
    protected string ConsumeTargetHintOrTemp(string udonType)
    {
        var hint = _ctx.TargetHint;
        if (hint != null)
        {
            _ctx.TargetHint = null;
            if (_vars.GetDeclaredType(hint) == udonType)
                return hint;
        }
        return _vars.DeclareTemp(udonType);
    }

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
        return _vars.Lookup(param.Name)
            ?? throw new System.InvalidOperationException(
                $"Cannot resolve parameter '{param.Name}' (ordinal {param.Ordinal}) "
              + $"in method '{_currentMethod?.Name ?? "(none)"}'. "
              + "Not found in lambda overrides, method params, or variable table.");
    }

    protected string EmitEnumToUnderlying(string operandId, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return operandId;
        var underlyingType = named.EnumUnderlyingType;
        var convertMethod = ExternResolver.GetConvertMethodName(underlyingType);
        if (convertMethod == null) return operandId;
        var underlyingUdon = GetUdonType(underlyingType);
        var convertedId = _vars.DeclareTemp(underlyingUdon);
        _module.AddPush(operandId);
        _module.AddPush(convertedId);
        AddExternChecked($"SystemConvert.__{convertMethod}__SystemObject__{underlyingUdon}");
        return convertedId;
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
            var val = System.Convert.ToInt64(m.ConstantValue);
            if (val < 0)
                throw new System.NotSupportedException(
                    $"Cannot cast integer to enum {enumType.Name}: negative value {val} is not supported");
            if (val > maxVal) maxVal = val;
        }

        if (maxVal > 2048)
            throw new System.NotSupportedException(
                $"Cannot cast integer to enum {enumType.Name}: max value {maxVal} exceeds 2048 limit");

        int msb = 0;
        long tmp = maxVal;
        while (tmp > 0) { tmp >>= 1; msb++; }
        int arraySize = System.Math.Max(1 << msb, 1);

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
            enumArr[i] = System.Convert.ChangeType(i, clrType);

        var enumFullName = enumType.ToDisplayString().Replace('.', '_');
        var arrayId = $"__enumArr_{enumFullName}";
        _vars.DeclareEnumArray(arrayId, enumArr);
        _enumArrayVars[enumType] = arrayId;
        return arrayId;
    }

    // ── EmitMemberSet (shared by AssignmentHandler and InvocationHandler.Members) ──

    protected void EmitMemberSet(string instanceId, IOperation target, string valueId)
    {
        switch (target)
        {
            case IFieldReferenceOperation fieldRef when fieldRef.Field.ContainingType.IsValueType:
            {
                var containingType = GetUdonType(fieldRef.Field.ContainingType);
                var valueType = GetUdonType(fieldRef.Field.Type);
                var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef.Field.Name, valueType);
                _module.AddPush(instanceId);
                _module.AddPush(valueId);
                AddExternChecked(sig);
                break;
            }
            case IPropertyReferenceOperation propRef:
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                var valueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    _module.AddPush(instanceId);
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        _module.AddPush(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    _module.AddPush(valueId);
                    var indexParamStr = string.Join("_", indexTypes);
                    AddExternChecked($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid");
                }
                else
                {
                    _module.AddPush(instanceId);
                    _module.AddPush(valueId);
                    AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType));
                }
                break;
            }
            case IFieldReferenceOperation fieldRef2:
                // Non-struct field assignment (class fields via SetProgramVariable or direct)
                _module.AddCopy(valueId, fieldRef2.Field.Name);
                break;
        }
    }

    // ── Lambda / Local Function Helpers ──

    protected void RegisterLocalFunction(IMethodSymbol localFunc)
    {
        if (_methodLabels.ContainsKey(localFunc)) return;
        _ctx.RegisterMethod(localFunc);
        _pendingLocalFunctions.Add((localFunc, _methodLabels[localFunc]));
    }

    protected IMethodSymbol HoistLambdaToMethod(IAnonymousFunctionOperation lambda)
    {
        var symbol = lambda.Symbol;
        if (_methodLabels.ContainsKey(symbol)) return symbol;
        RegisterLocalFunction(symbol);
        return symbol;
    }

    // ── Call helpers (used by assignment, invocation, property handlers) ──

    protected (string exportName, string[] paramIds, string retId) GetCalleeLayout(IMethodSymbol target)
    {
        // For methods in the current class, use stored layout
        if (_methodParamVarIds.TryGetValue(target, out var localParamIds))
        {
            var exportName = _methodVarPrefix[target];
            _methodRetVars.TryGetValue(target, out var retId);
            return (exportName, localParamIds, retId);
        }

        // For foreign methods, delegate to LayoutPlanner
        var ml = _planner.GetCalleeLayout(target);
        return (ml.ExportName, ml.ParamIds.ToArray(), ml.ReturnId);
    }

    protected string EmitCallByLabel(IMethodSymbol target, int targetLabel)
    {
        // Use body label to skip sentinel push on internal calls (matches UdonSharp's MethodLabel)
        int jumpTarget = _methodBodyLabels.TryGetValue(target, out var bodyLabel) ? bodyLabel : targetLabel;

        // Stack-based return address: push return addr onto VM stack, callee's RET pops it
        var returnLabel = _module.DefineLabel("__call_return");
        _module.AddPushLabel(returnLabel);
        _module.AddJump(jumpTarget);
        _module.MarkLabel(returnLabel);

        // COW: copy return value to prevent overwrite by subsequent calls
        // If TargetHint is set, copy directly to that target (avoids extra temp)
        if (_methodRetVars.TryGetValue(target, out var retVarId))
        {
            if (_methodRetTypes.TryGetValue(target, out var retType))
            {
                var cowDst = ConsumeTargetHintOrTemp(retType);
                _module.AddCopy(retVarId, cowDst);
                return cowDst;
            }
            return retVarId;
        }
        return null;
    }

}
