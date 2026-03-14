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
            if (_ctx.Vars.GetDeclaredType(hint) == udonType)
                return hint;
        }
        return _ctx.Vars.DeclareTemp(udonType);
    }

    // ── Shared helpers (used by multiple handlers) ──

    protected string GetParamVarId(IParameterSymbol param)
    {
        if (_ctx.CurrentMethod != null
            && _ctx.LambdaConventionOverrides.TryGetValue(_ctx.CurrentMethod, out var conv)
            && param.Ordinal < conv.ArgVarIds.Length)
            return conv.ArgVarIds[param.Ordinal];
        if (param.ContainingSymbol is IMethodSymbol method
            && _ctx.MethodParamVarIds.TryGetValue(method, out var paramIds)
            && param.Ordinal < paramIds.Length)
        {
            var paramId = paramIds[param.Ordinal];
            if (paramId != null)
                return paramId;
        }
        if (_ctx.CurrentMethod != null && param.ContainingSymbol is IMethodSymbol paramMethod
            && _ctx.CurrentMethod.IsGenericMethod && !_ctx.CurrentMethod.IsDefinition
            && SymbolEqualityComparer.Default.Equals(paramMethod, _ctx.CurrentMethod.OriginalDefinition)
            && _ctx.MethodParamVarIds.TryGetValue(_ctx.CurrentMethod, out var specParamIds)
            && param.Ordinal < specParamIds.Length)
        {
            var paramId = specParamIds[param.Ordinal];
            if (paramId != null)
                return paramId;
        }
        if (TryGetParamTupleVarIds(param, out _))
            throw new System.NotSupportedException(
                $"Tuple parameter '{param.Name}' cannot be used as a scalar expression. Deconstruct it or access one of its elements.");
        return _ctx.Vars.Lookup(param.Name)
            ?? throw new System.InvalidOperationException(
                $"Cannot resolve parameter '{param.Name}' (ordinal {param.Ordinal}) "
              + $"in method '{_ctx.CurrentMethod?.Name ?? "(none)"}'. "
              + "Not found in lambda overrides, method params, or variable table.");
    }

    protected bool TryGetMethodTupleParamVarIds(IMethodSymbol method, int ordinal, out string[] tupleIds)
        => _ctx.MethodTupleParamVarIds.TryGetValue((method, ordinal), out tupleIds);

    protected bool TryGetParamTupleVarIds(IParameterSymbol param, out string[] tupleIds)
    {
        if (param.ContainingSymbol is IMethodSymbol method
            && TryGetMethodTupleParamVarIds(method, param.Ordinal, out tupleIds))
            return true;

        if (_ctx.CurrentMethod != null && param.ContainingSymbol is IMethodSymbol paramMethod
            && _ctx.CurrentMethod.IsGenericMethod && !_ctx.CurrentMethod.IsDefinition
            && SymbolEqualityComparer.Default.Equals(paramMethod, _ctx.CurrentMethod.OriginalDefinition)
            && TryGetMethodTupleParamVarIds(_ctx.CurrentMethod, param.Ordinal, out tupleIds))
            return true;

        tupleIds = null;
        return false;
    }

    protected bool TryGetTupleTargetVarIds(IOperation target, out string[] tupleIds)
    {
        switch (target)
        {
            case ILocalReferenceOperation localRef when _ctx.TupleLocalVarIds.TryGetValue(localRef.Local, out tupleIds):
                return true;
            case IParameterReferenceOperation paramRef when TryGetParamTupleVarIds(paramRef.Parameter, out tupleIds):
                return true;
            default:
                tupleIds = null;
                return false;
        }
    }

    protected bool TryResolveTupleValue(IOperation op, out string[] tupleVarIds)
    {
        while (op is IConversionOperation conv && conv.Type?.IsTupleType == true)
            op = conv.Operand;

        switch (op)
        {
            case ITupleOperation tuple:
                tupleVarIds = new string[tuple.Elements.Length];
                for (int i = 0; i < tuple.Elements.Length; i++)
                    tupleVarIds[i] = VisitExpression(tuple.Elements[i]);
                return true;

            case ILocalReferenceOperation localRef when _ctx.TupleLocalVarIds.TryGetValue(localRef.Local, out var localIds):
                tupleVarIds = localIds;
                return true;

            case IParameterReferenceOperation paramRef when TryGetParamTupleVarIds(paramRef.Parameter, out var paramIds):
                tupleVarIds = paramIds;
                return true;

            default:
                _ctx.LastTupleCallRetVars = null;
                VisitExpression(op);
                if (_ctx.LastTupleCallRetVars != null)
                {
                    tupleVarIds = _ctx.LastTupleCallRetVars;
                    return true;
                }
                tupleVarIds = null;
                return false;
        }
    }

    protected void CopyTupleValueToSlots(IOperation value, IReadOnlyList<string> dstSlots, string context)
    {
        if (!TryResolveTupleValue(value, out var srcSlots))
            throw new System.NotSupportedException($"Unsupported tuple value for {context}: {value.GetType().Name}");

        if (srcSlots.Length != dstSlots.Count)
            throw new System.InvalidOperationException(
                $"Tuple arity mismatch for {context}: destination expects {dstSlots.Count} values but source produced {srcSlots.Length}.");

        for (int i = 0; i < dstSlots.Count; i++)
            if (srcSlots[i] != dstSlots[i])
                _ctx.Module.AddCopy(srcSlots[i], dstSlots[i]);
    }

    protected bool TryResolveTupleElementReference(IFieldReferenceOperation fieldRef, out string valueId)
    {
        valueId = null;
        var tupleType = fieldRef.Instance?.Type as INamedTypeSymbol ?? fieldRef.Field.ContainingType as INamedTypeSymbol;
        if (fieldRef.Instance == null
            || tupleType == null
            || !tupleType.IsTupleType
            || !TryResolveTupleValue(fieldRef.Instance, out var tupleIds))
            return false;

        var ordinal = GetTupleElementIndex(fieldRef.Field, tupleType);
        if (ordinal < 0 || ordinal >= tupleIds.Length)
            return false;

        valueId = tupleIds[ordinal];
        return true;
    }

    static int GetTupleElementIndex(IFieldSymbol field, INamedTypeSymbol tupleType)
    {
        for (int i = 0; i < tupleType.TupleElements.Length; i++)
        {
            var tupleField = tupleType.TupleElements[i];
            if (SymbolEqualityComparer.Default.Equals(tupleField, field)
                || SymbolEqualityComparer.Default.Equals(tupleField.CorrespondingTupleField, field)
                || SymbolEqualityComparer.Default.Equals(field.CorrespondingTupleField, tupleField))
                return i;
            if (tupleField.Name == field.Name)
                return i;
        }

        if (field.Name.StartsWith("Item", StringComparison.Ordinal)
            && int.TryParse(field.Name.Substring("Item".Length), out var itemIndex))
            return itemIndex - 1;

        return -1;
    }

    protected string EmitEnumToUnderlying(string operandId, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return operandId;
        var underlyingType = named.EnumUnderlyingType;
        var convertMethod = ExternResolver.GetConvertMethodName(underlyingType);
        if (convertMethod == null) return operandId;
        var underlyingUdon = GetUdonType(underlyingType);
        var convertedId = _ctx.Vars.DeclareTemp(underlyingUdon);
        _ctx.Module.AddPush(operandId);
        _ctx.Module.AddPush(convertedId);
        AddExternChecked($"SystemConvert.__{convertMethod}__SystemObject__{underlyingUdon}");
        return convertedId;
    }

    protected string GetOrCreateEnumArray(INamedTypeSymbol enumType)
    {
        if (_ctx.EnumArrayVars.TryGetValue(enumType, out var existing))
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
        _ctx.Vars.DeclareEnumArray(arrayId, enumArr);
        _ctx.EnumArrayVars[enumType] = arrayId;
        return arrayId;
    }

    // ── LValue Capture ──
    // Evaluates and caches sub-expressions of an l-value (array ref, index, instance)
    // to avoid re-evaluating side-effecting expressions during write-back.

    protected struct LValueCapture
    {
        public string ValueId;       // The evaluated l-value variable ID
        public string ArrayId;       // Cached array reference (for array elements)
        public string IndexId;       // Cached index (for array elements)
        public string InstanceId;    // Cached instance (for cross-behaviour fields/properties)
    }

    protected LValueCapture CaptureLValue(IOperation target)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation arrayElem:
            {
                var arrayId = VisitExpression(arrayElem.ArrayReference);
                var indexId = VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elemAccessorType = GetArrayElemType(arrSymbol);

                var valId = _ctx.Vars.DeclareTemp(GetUdonType(arrayElem.Type));
                _ctx.Module.AddPush(arrayId);
                _ctx.Module.AddPush(indexId);
                _ctx.Module.AddPush(valId);
                AddExternChecked($"{arrayType}.__Get__SystemInt32__{elemAccessorType}");
                return new LValueCapture { ValueId = valId, ArrayId = arrayId, IndexId = indexId };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceId = VisitExpression(fieldRef.Instance);
                var fldType = GetUdonType(fieldRef.Field.Type);
                var valId = _ctx.Vars.DeclareTemp(fldType);
                var nameConst = _ctx.Vars.DeclareConst("SystemString", fieldRef.Field.Name);
                _ctx.Module.AddPush(instanceId);
                _ctx.Module.AddPush(nameConst);
                _ctx.Module.AddPush(valId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
                return new LValueCapture { ValueId = valId, InstanceId = instanceId };
            }
            default:
                return new LValueCapture { ValueId = VisitExpression(target) };
        }
    }

    // ── EmitWriteBack ──
    // Write back a computed value to non-trivial l-value targets (array elements, properties).

    protected void EmitWriteBack(IOperation target, string valueId, LValueCapture lv = default)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation arrayElem:
            {
                var arrayId = lv.ArrayId ?? VisitExpression(arrayElem.ArrayReference);
                var indexId = lv.IndexId ?? VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                _ctx.Module.AddPush(arrayId);
                _ctx.Module.AddPush(indexId);
                _ctx.Module.AddPush(valueId);
                AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceId = lv.InstanceId ?? VisitExpression(fieldRef.Instance);
                var nameConst = _ctx.Vars.DeclareConst("SystemString", fieldRef.Field.Name);
                _ctx.Module.AddPush(instanceId);
                _ctx.Module.AddPush(nameConst);
                _ctx.Module.AddPush(valueId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                break;
            }
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { GetMethod: { IsImplicitlyDeclared: true } } } propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType):
                return;
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { SetMethod: not null } } propRef when _ctx.MethodLabels.TryGetValue(propRef.Property.SetMethod, out var setterLabel):
                _ctx.Module.AddCopy(valueId, GetParamVarId(propRef.Property.SetMethod.Parameters[0]));
                EmitCallByLabel(propRef.Property.SetMethod, setterLabel);
                return;
            case IPropertyReferenceOperation propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation:
            {
                var instanceId = VisitExpression(propRef.Instance);
                var isAutoSet = propRef.Property.SetMethod?.IsImplicitlyDeclared == true;
                if (isAutoSet || propRef.Property.SetMethod == null)
                {
                    var nameConst = _ctx.Vars.DeclareConst("SystemString", propRef.Property.Name);
                    _ctx.Module.AddPush(instanceId);
                    _ctx.Module.AddPush(nameConst);
                    _ctx.Module.AddPush(valueId);
                    AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                }
                else
                {
                    var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                    var paramNameConst = _ctx.Vars.DeclareConst("SystemString", setParamIds[0]);
                    _ctx.Module.AddPush(instanceId);
                    _ctx.Module.AddPush(paramNameConst);
                    _ctx.Module.AddPush(valueId);
                    AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                    var eventConst = _ctx.Vars.DeclareConst("SystemString", exportName);
                    _ctx.Module.AddPush(instanceId);
                    _ctx.Module.AddPush(eventConst);
                    AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");
                }
                return;
            }
            case IPropertyReferenceOperation propRef:
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                if (containingType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
                    containingType = propRef.Instance is IInstanceReferenceOperation
                        ? GetUdonType(_ctx.ClassSymbol)
                        : GetUdonType(propRef.Instance.Type);

                string wbInstanceId;
                if (propRef.Instance is IInstanceReferenceOperation)
                    wbInstanceId = _ctx.Vars.DeclareThisOnce(containingType);
                else if (propRef.Instance != null)
                    wbInstanceId = VisitExpression(propRef.Instance);
                else
                {
                    var valueType = GetUdonType(propRef.Property.Type);
                    _ctx.Module.AddPush(valueId);
                    AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType));
                    return;
                }

                var propValueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    _ctx.Module.AddPush(wbInstanceId);
                    var indexTypes = new System.Collections.Generic.List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        _ctx.Module.AddPush(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    _ctx.Module.AddPush(valueId);
                    var indexParamStr = string.Join("_", indexTypes);
                    AddExternChecked($"{containingType}.__set_Item__{indexParamStr}_{propValueType}__SystemVoid");
                }
                else
                {
                    _ctx.Module.AddPush(wbInstanceId);
                    _ctx.Module.AddPush(valueId);
                    AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, propValueType));
                }
                break;
            }
        }
    }

    // ── AssignToTarget (shared: deconstruction, ref/out copy-back) ──

    protected void AssignToTarget(IOperation target, string valueId)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _ctx.Vars.DeclareLocal(localRef.Local.Name, udonType);
                    _ctx.LocalVarIds[localRef.Local] = localId;
                    _ctx.Module.AddCopy(valueId, localId);
                }
                break;

            case ILocalReferenceOperation existingLocal:
                var existingId = _ctx.Vars.Lookup(existingLocal.Local.Name)
                    ?? _ctx.LocalVarIds.GetValueOrDefault(existingLocal.Local);
                if (existingId == null)
                {
                    var udonType = GetUdonType(existingLocal.Type);
                    existingId = _ctx.Vars.DeclareLocal(existingLocal.Local.Name, udonType);
                    _ctx.LocalVarIds[existingLocal.Local] = existingId;
                }
                _ctx.Module.AddCopy(valueId, existingId);
                break;

            case IFieldReferenceOperation fieldRef when fieldRef.Instance is IInstanceReferenceOperation:
                _ctx.Module.AddCopy(valueId, fieldRef.Field.Name);
                break;

            case IArrayElementReferenceOperation arrayElem:
                var arrayId = VisitExpression(arrayElem.ArrayReference);
                var indexId = VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                _ctx.Module.AddPush(arrayId);
                _ctx.Module.AddPush(indexId);
                _ctx.Module.AddPush(valueId);
                AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
                break;

            case IFieldReferenceOperation fieldRef
                when fieldRef.Instance != null
                && fieldRef.Instance is not IInstanceReferenceOperation
                && ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
                var instanceId = VisitExpression(fieldRef.Instance);
                var nameConst = _ctx.Vars.DeclareConst("SystemString", fieldRef.Field.Name);
                _ctx.Module.AddPush(instanceId);
                _ctx.Module.AddPush(nameConst);
                _ctx.Module.AddPush(valueId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                break;

            case IDiscardOperation:
                break;

            default:
                throw new System.NotSupportedException(
                    $"Unsupported deconstruction target element: {target.GetType().Name}");
        }
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
                _ctx.Module.AddPush(instanceId);
                _ctx.Module.AddPush(valueId);
                AddExternChecked(sig);
                break;
            }
            case IPropertyReferenceOperation propRef:
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                var valueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    _ctx.Module.AddPush(instanceId);
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        _ctx.Module.AddPush(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    _ctx.Module.AddPush(valueId);
                    var indexParamStr = string.Join("_", indexTypes);
                    AddExternChecked($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid");
                }
                else
                {
                    _ctx.Module.AddPush(instanceId);
                    _ctx.Module.AddPush(valueId);
                    AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType));
                }
                break;
            }
            case IFieldReferenceOperation fieldRef2:
                // Non-struct field assignment (class fields via SetProgramVariable or direct)
                _ctx.Module.AddCopy(valueId, fieldRef2.Field.Name);
                break;
        }
    }

    // ── Lambda / Local Function Helpers ──

    protected void RegisterLocalFunction(IMethodSymbol localFunc)
    {
        if (_ctx.MethodLabels.ContainsKey(localFunc)) return;
        _ctx.RegisterMethod(localFunc);
        _ctx.PendingLocalFunctions.Add((localFunc, _ctx.MethodLabels[localFunc]));
    }

    protected IMethodSymbol HoistLambdaToMethod(IAnonymousFunctionOperation lambda)
    {
        var symbol = lambda.Symbol;
        if (_ctx.MethodLabels.ContainsKey(symbol)) return symbol;
        RegisterLocalFunction(symbol);
        return symbol;
    }

    // ── Call helpers (used by assignment, invocation, property handlers) ──

    protected (string exportName, string[] paramIds, string retId) GetCalleeLayout(IMethodSymbol target)
    {
        // For methods in the current class, use stored layout
        if (_ctx.MethodParamVarIds.TryGetValue(target, out var localParamIds))
        {
            var exportName = _ctx.MethodVarPrefix[target];
            _ctx.MethodRetVars.TryGetValue(target, out var retId);
            return (exportName, localParamIds, retId);
        }

        // For foreign methods, delegate to LayoutPlanner
        var ml = _ctx.Planner.GetCalleeLayout(target);
        return (ml.ExportName, ml.ParamIds.ToArray(), ml.ReturnId);
    }

    protected string EmitCallByLabel(IMethodSymbol target, int targetLabel)
    {
        // Use body label to skip sentinel push on internal calls (matches UdonSharp's MethodLabel)
        int jumpTarget = _ctx.MethodBodyLabels.TryGetValue(target, out var bodyLabel) ? bodyLabel : targetLabel;

        // Stack-based return address: push return addr onto VM stack, callee's RET pops it
        var returnLabel = _ctx.Module.DefineLabel("__call_return");
        _ctx.Module.AddPushLabel(returnLabel);
        _ctx.Module.AddJump(jumpTarget);
        _ctx.Module.MarkLabel(returnLabel);

        // Tuple return: COW-copy canonical ret vars to fresh temps
        if (_ctx.MethodTupleRetVars.TryGetValue(target, out var tupleRetVars))
        {
            var cowTemps = new string[tupleRetVars.Length];
            for (int i = 0; i < tupleRetVars.Length; i++)
            {
                var elemType = _ctx.Vars.GetDeclaredType(tupleRetVars[i]);
                cowTemps[i] = _ctx.Vars.DeclareTemp(elemType);
                _ctx.Module.AddCopy(tupleRetVars[i], cowTemps[i]);
            }
            // Store COW temps for caller to read (canonical vars stay immutable)
            _ctx.LastTupleCallRetVars = cowTemps;
            return null;
        }

        // COW: copy return value to prevent overwrite by subsequent calls
        // If TargetHint is set, copy directly to that target (avoids extra temp)
        if (_ctx.MethodRetVars.TryGetValue(target, out var retVarId))
        {
            if (_ctx.MethodRetTypes.TryGetValue(target, out var retType))
            {
                var cowDst = ConsumeTargetHintOrTemp(retType);
                _ctx.Module.AddCopy(retVarId, cowDst);
                return cowDst;
            }
            return retVarId;
        }
        return null;
    }

}
