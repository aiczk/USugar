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
    protected CModule _module => _ctx.Module;
    protected CoreBuilder _builder => _ctx.Builder;
    protected LayoutPlanner _planner => _ctx.Planner;
    protected Dictionary<IMethodSymbol, CFunction> _methodFunctions => _ctx.MethodFunctions;
    protected Dictionary<IMethodSymbol, EmitContext.MethodSlot> _methodSlots => _ctx.MethodSlots;
    protected Dictionary<IMethodSymbol, ReturnSlot[]> _methodReturns => _ctx.MethodReturns;
    protected Dictionary<IMethodSymbol, string[]> _methodParamVarIds => _ctx.MethodParamVarIds;
    protected IMethodSymbol _currentMethod { get => _ctx.CurrentMethod; set => _ctx.CurrentMethod = value; }
    protected List<(IMethodSymbol symbol, CFunction func)> _pendingLocalFunctions => _ctx.PendingLocalFunctions;
    protected Dictionary<ILocalSymbol, IMethodSymbol> _delegateVarMap => _ctx.DelegateVarMap;
    protected List<IMethodSymbol> _pendingGenericSpecs => _ctx.PendingGenericSpecs;
    protected Dictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap { get => _ctx.TypeParamMap; set => _ctx.TypeParamMap = value; }
    protected Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> _delegateParamConventions => _ctx.DelegateParamConventions;
    protected Dictionary<IMethodSymbol, DelegateConvention> _lambdaConventionOverrides => _ctx.LambdaConventionOverrides;
    protected Dictionary<ILocalSymbol, EmitContext.LocalBinding> _localBindings => _ctx.LocalBindings;
    protected List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.FieldInitOps;
    protected Dictionary<string, string> _fieldChangeCallbacks => _ctx.FieldChangeCallbacks;
    protected Stack<(CValue Target, string DelegateFieldName)> _conditionalAccessStack => _ctx.ConditionalAccessStack;
    protected Stack<List<(CValue val, ITypeSymbol type)>> _usingDisposableStack => _ctx.UsingDisposableStack;
    protected HashSet<string> _delegateFields => _ctx.DelegateFields;
    protected List<EmitDiagnostic> _diagnostics => _ctx.Diagnostics;
    protected bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee) => _ctx.IsRecursiveEdge(caller, callee);
    protected static bool IsHoistedFunction(IMethodSymbol m) => EmitContext.IsHoistedFunction(m);
    protected void MarkRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee) => _ctx.MarkRecursiveEdge(caller, callee);

    // ── Dispatch (recursive descent into other handlers via UasmEmitter facade) ──
    protected void VisitOperation(IOperation op) => _ctx.VisitOperation(op);
    protected CValue VisitExpression(IOperation op) => _ctx.VisitExpression(op);
    protected CValue EmitPatternCheck(CValue value, ITypeSymbol valueType, IPatternOperation pattern)
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
    protected void EmitAssign(int destSlot, CValue value) => _builder.EmitAssign(destSlot, value);

    /// <summary>Emit: fieldName = expr</summary>
    protected void EmitStoreField(string fieldName, CValue value) => _builder.EmitStoreField(fieldName, value);

    /// <summary>Emit: return [value]</summary>
    protected void EmitReturn(CValue value = null) => _builder.EmitReturn(value);

    /// <summary>Create a constant.</summary>
    protected CConst Const(object value, string type) => _builder.Const(value, type);

    /// <summary>Create a slot reference expression.</summary>
    protected CSlotRef SlotRef(int slotId) => _builder.SlotRef(slotId);

    /// <summary>Create a field load expression.</summary>
    protected CFieldRef LoadField(string fieldName, string type) => _builder.LoadField(fieldName, type);

    /// <summary>Create a field address reference (for extern out/ref).</summary>
    protected CFieldRef FieldAddr(string fieldName, string type) => _builder.FieldAddr(fieldName, type);

    /// <summary>Create an extern call expression.</summary>
    protected CExternCall ExternCall(string sig, List<CValue> args, string retType)
        => _builder.ExternCall(ResolveExtern(sig), args, retType);

    /// <summary>
    /// Integer narrowing conversion matching C# *unchecked* semantics (wrap). Udon's
    /// SystemConvert.ToX is CHECKED and throws on overflow, so an int narrowed to a small integer
    /// is masked (and sign-extended for signed targets) before the final convert, which is then
    /// always in range. Any other conversion falls back to the plain convert extern.
    /// </summary>
    protected CValue EmitNarrowingConvert(CValue value, string fromUdonType, string toUdonType)
    {
        // Udon has no bitwise-AND extern, so wrap unsigned targets with modulo and signed targets
        // with a shift-left / arithmetic-shift-right truncation. After wrapping, the value is in
        // range, so the final SystemConvert.ToX cannot overflow.
        if (fromUdonType == "SystemInt32")
        {
            switch (toUdonType)
            {
                case "SystemByte":   return ConvertInRange(ModWrap(value, 256), toUdonType);
                case "SystemChar":
                case "SystemUInt16": return ConvertInRange(ModWrap(value, 65536), toUdonType);
                case "SystemSByte":  return ConvertInRange(ShiftTruncate(value, 24), toUdonType);
                case "SystemInt16":  return ConvertInRange(ShiftTruncate(value, 16), toUdonType);
            }
        }
        return ExternCall(ExternResolver.BuildConvertSignature(fromUdonType, toUdonType),
            new List<CValue> { value }, toUdonType);
    }

    CValue ConvertInRange(CValue inRangeInt, string toUdonType)
        => ExternCall(ExternResolver.BuildConvertSignature("SystemInt32", toUdonType),
            new List<CValue> { inRangeInt }, toUdonType);

    // ((x % mod) + mod) % mod  →  [0, mod)  : C# unsigned narrowing wrap
    CValue ModWrap(CValue x, int mod)
    {
        var add = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { Rem(x, mod), Const(mod, "SystemInt32") }, "SystemInt32");
        return Rem(add, mod);
    }

    CValue Rem(CValue x, int mod)
        => ExternCall("SystemInt32.__op_Remainder__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { x, Const(mod, "SystemInt32") }, "SystemInt32");

    // (x << s) >> s  →  signed (32-s)-bit truncation with sign extension
    CValue ShiftTruncate(CValue x, int shift)
    {
        var left = ExternCall("SystemInt32.__op_LeftShift__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { x, Const(shift, "SystemInt32") }, "SystemInt32");
        return ExternCall("SystemInt32.__op_RightShift__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { left, Const(shift, "SystemInt32") }, "SystemInt32");
    }

    /// <summary>Emit a void extern call as a statement.</summary>
    protected void EmitExternVoid(string sig, List<CValue> args)
        => _builder.EmitExternVoid(ResolveExtern(sig), args);

    /// <summary>Create an internal call expression.</summary>
    protected CInternalCall InternalCall(string funcName, List<CValue> args, string retType)
        => _builder.InternalCall(funcName, args, retType);

    /// <summary>Create a select (ternary) expression.</summary>
    protected CSelect Select(CValue cond, CValue trueVal, CValue falseVal, string type)
        => _builder.Select(cond, trueVal, falseVal, type);

    /// <summary>Create a function reference (for delegate/JUMP_INDIRECT).</summary>
    protected CFuncRef FuncRef(string funcName) => _builder.FuncRef(funcName);

    /// <summary>Emit a statement.</summary>
    protected void Emit(CStmt stmt) => _builder.Emit(stmt);

    /// <summary>Emit an expression as a statement (side-effecting calls).</summary>
    protected void EmitExprStmt(CValue expr) => _builder.EmitExprStmt(expr);

    // ── Nullable<T> (boxed-object emulation) helpers ──

    /// <summary>HasValue: the boxed nullable object is non-null. Returns SystemBoolean.
    /// <paramref name="nullableVal"/> must be pure or pre-materialised (it is read once).</summary>
    protected CValue EmitNullableHasValue(CValue nullableVal)
    {
        var isNull = ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CValue> { nullableVal, Const(null, "SystemObject") }, "SystemBoolean");
        return ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            new List<CValue> { isNull }, "SystemBoolean");
    }

    /// <summary>Default value for a Udon value type (0 / false). Used for `default(T)`-style fills.</summary>
    protected CValue EmitValueTypeDefault(string udonType)
        => Const(EmitContext.ParseConstValue(udonType, udonType == "SystemBoolean" ? "False" : "0"), udonType);

    /// <summary>Deep value-copy of an object[]-backed aggregate (struct/tuple): a fresh array with each
    /// element copied, recursing into nested-aggregate elements. A shallow SystemObjectArray.__Clone__ would
    /// copy the nested object[] REFERENCE, so mutating the copy's nested struct would corrupt the source.</summary>
    protected CValue EmitDeepCloneAggregate(CValue src, INamedTypeSymbol aggType)
    {
        var layout = _ctx.GetAggregateLayout(aggType);
        var srcSlot = _ctx.AllocTemp("SystemObjectArray"); EmitAssign(srcSlot, src);
        var dstSlot = _ctx.AllocTemp("SystemObjectArray");
        EmitAssign(dstSlot, ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CValue> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray"));
        for (int i = 0; i < layout.Count; i++)
        {
            var elem = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CValue> { SlotRef(srcSlot), Const(i, "SystemInt32") }, "SystemObject");
            CValue copy = layout.Fields[i].Type is INamedTypeSymbol nested && EmitContext.IsAggregateType(nested)
                ? EmitDeepCloneAggregate(elem, nested) // nested aggregate → recurse
                : elem;                                // boxed scalar → reference copy is fine (immutable box)
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CValue> { SlotRef(dstSlot), Const(i, "SystemInt32"), copy });
        }
        return SlotRef(dstSlot);
    }

    /// <summary>Allocate a fresh object[]-backed aggregate (struct/tuple) and default-initialize it as a
    /// VALUE (e.g. `new V()` used as an expression). Nested aggregate fields are recursively allocated.</summary>
    protected CValue EmitNewAggregate(INamedTypeSymbol aggType)
    {
        var layout = _ctx.GetAggregateLayout(aggType);
        var slot = _ctx.AllocTemp("SystemObjectArray");
        EmitAssign(slot, ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CValue> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray"));
        EmitDefaultInitAggregate(SlotRef(slot), layout);
        return SlotRef(slot);
    }

    /// <summary>Set each value-type element of an object[]-emulated aggregate to its type default; a nested
    /// aggregate field is recursively allocated + default-initialized rather than left null.</summary>
    protected void EmitDefaultInitAggregate(CValue arrayVal, AggregateLayout layout)
    {
        var slot = _ctx.AllocTemp("SystemObjectArray");
        EmitAssign(slot, arrayVal);
        for (int i = 0; i < layout.Count; i++)
        {
            var fieldType = layout.Fields[i].Type;
            if (fieldType is INamedTypeSymbol nested && EmitContext.IsAggregateType(nested))
            {
                var nl = _ctx.GetAggregateLayout(nested);
                var subSlot = _ctx.AllocTemp("SystemObjectArray");
                EmitAssign(subSlot, ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
                    new List<CValue> { Const(nl.Count, "SystemInt32") }, "SystemObjectArray"));
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<CValue> { SlotRef(slot), Const(i, "SystemInt32"), SlotRef(subSlot) });
                EmitDefaultInitAggregate(SlotRef(subSlot), nl);
                continue;
            }
            object defVal = fieldType.SpecialType switch
            {
                SpecialType.System_Boolean => (object)false,
                SpecialType.System_Int32 => (object)0,
                SpecialType.System_Single => (object)0f,
                SpecialType.System_Double => (object)0d,
                SpecialType.System_Int64 => (object)0L,
                SpecialType.System_Byte => (object)(byte)0,
                SpecialType.System_UInt32 => (object)0u,
                SpecialType.System_UInt64 => (object)0UL,
                SpecialType.System_Int16 => (object)(short)0,
                SpecialType.System_UInt16 => (object)(ushort)0,
                SpecialType.System_Char => (object)'\0',
                SpecialType.System_SByte => (object)(sbyte)0,
                _ => null, // reference types default to null
            };
            if (defVal != null)
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<CValue> { SlotRef(slot), Const(i, "SystemInt32"), Const(defVal, GetUdonType(fieldType)) });
        }
    }

    /// <summary>Unwrap a field or auto-property member access into (instance, member name) for
    /// aggregate (struct/tuple) object[] element resolution.</summary>
    protected static bool TryGetAggregateMemberTarget(IOperation target, out IOperation instance, out string memberName)
    {
        switch (target)
        {
            case IFieldReferenceOperation { Instance: not null } fr:
                instance = fr.Instance; memberName = fr.Field.Name; return true;
            case IPropertyReferenceOperation { Instance: not null } pr:
                instance = pr.Instance; memberName = pr.Property.Name; return true;
            default:
                instance = null; memberName = null; return false;
        }
    }

    static bool SmallIntOrChar(string udonType)
        => udonType is "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16" or "SystemChar";

    /// <summary>Promote a boxed small-int/char operand to int32 (Udon has no operators on those types and a
    /// boxed small-int does not coerce to int for a SystemInt32 extern). Routes through ToInt32(SystemObject)
    /// rather than the type-strict ToInt32(SystemByte/SystemChar/…): a nullable small-int's stored value is
    /// often a boxed plain int (e.g. <c>byte? x = 5</c> keeps the int literal un-narrowed), which a strict
    /// typed fetch rejects with InvalidCast. Convert.ToInt32(object) tolerates any boxed numeric. Pass-through
    /// for non-small types.</summary>
    CValue PromoteBoxedToInt32(CValue boxed, ITypeSymbol underlying, out ITypeSymbol effectiveType)
    {
        if (SmallIntOrChar(GetUdonType(underlying)))
        {
            effectiveType = _compilation.GetSpecialType(SpecialType.System_Int32);
            return ExternCall("SystemConvert.__ToInt32__SystemObject__SystemInt32", new List<CValue> { boxed }, "SystemInt32");
        }
        effectiveType = underlying;
        return boxed;
    }

    /// <summary>Lifted binary operator on Nullable&lt;T&gt; (null propagation), from already-evaluated operand
    /// values. Arithmetic yields T? (null unless both present); relational yields bool (false if either null);
    /// equality yields bool (both-null is equal). Shared by <c>OperatorHandler</c> and compound assignment.</summary>
    protected CValue EmitLiftedBinaryCore(
        CValue leftVal, bool leftNullable, ITypeSymbol ltUnderlying,
        CValue rightVal, bool rightNullable, ITypeSymbol rtUnderlying,
        Microsoft.CodeAnalysis.Operations.BinaryOperatorKind kind, IMethodSymbol operatorMethod, ITypeSymbol resultType)
    {
        var resultNullable = EmitContext.IsNullableT(resultType, out var resU);
        var valueResultType = GetUdonType(resultNullable ? resU : resultType);

        var aSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(aSlot, leftVal);
        var bSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(bSlot, rightVal);

        CValue IsNullV(int slot) => ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CValue> { SlotRef(slot), Const(null, "SystemObject") }, "SystemBoolean");

        void IfBothPresent(Action<CoreBuilder> body)
        {
            Action<CoreBuilder> inner = rightNullable
                ? _ => _builder.EmitIf(EmitNullableHasValue(SlotRef(bSlot)), body) : body;
            if (leftNullable) _builder.EmitIf(EmitNullableHasValue(SlotRef(aSlot)), inner);
            else inner(_builder);
        }

        // Small-int/char underlying: Udon has no byte/short/char operators, so promote the (boxed) operands
        // to int32 — the boxed small-int does not implicitly coerce to int for the SystemInt32 extern — then
        // narrow an arithmetic result back. (int/float/etc. underlyings pass through unchanged.)
        CValue ValueOp(Microsoft.CodeAnalysis.Operations.BinaryOperatorKind k)
        {
            var resUnder = resultNullable ? resU : resultType;
            var aV = PromoteBoxedToInt32(SlotRef(aSlot), ltUnderlying, out var ltEff);
            var bV = PromoteBoxedToInt32(SlotRef(bSlot), rtUnderlying, out var rtEff);
            bool resPromotes = SmallIntOrChar(GetUdonType(resUnder));
            var resEff = resPromotes ? _compilation.GetSpecialType(SpecialType.System_Int32) : resUnder;
            var raw = ExternCall(
                ExternResolver.ResolveBinaryExtern(k, operatorMethod, ResolveType(ltEff), ResolveType(rtEff), ResolveType(resEff)),
                new List<CValue> { aV, bV }, GetUdonType(resEff));
            return resPromotes && GetUdonType(resUnder) != "SystemInt32"
                ? EmitNarrowingConvert(raw, "SystemInt32", GetUdonType(resUnder)) : raw;
        }

        if (resultNullable) // arithmetic → T? : null unless both present
        {
            var rSlot = _ctx.AllocTemp("SystemObject");
            EmitAssign(rSlot, Const(null, "SystemObject"));
            IfBothPresent(_ => EmitAssign(rSlot, ValueOp(kind)));
            return SlotRef(rSlot);
        }
        if (kind is Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Equals
            or Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.NotEquals)
        {
            var eqSlot = _ctx.AllocTemp("SystemBoolean");
            EmitAssign(eqSlot, Const(false, "SystemBoolean"));
            if (leftNullable && rightNullable) // both null → equal
                _builder.EmitIf(IsNullV(aSlot), _ => _builder.EmitIf(IsNullV(bSlot),
                    __ => EmitAssign(eqSlot, Const(true, "SystemBoolean"))));
            IfBothPresent(_ => EmitAssign(eqSlot, ValueOp(Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Equals)));
            if (kind == Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.NotEquals)
                return ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CValue> { SlotRef(eqSlot) }, "SystemBoolean");
            return SlotRef(eqSlot);
        }
        var relSlot = _ctx.AllocTemp("SystemBoolean"); // relational → bool : false unless both present
        EmitAssign(relSlot, Const(false, "SystemBoolean"));
        IfBothPresent(_ => EmitAssign(relSlot, ValueOp(kind)));
        return SlotRef(relSlot);
    }

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

    /// <summary>Read a parameter value as an CValue (field load).</summary>
    protected CValue LoadParam(IParameterSymbol param)
    {
        var fieldName = GetParamVarId(param);
        var type = GetUdonType(param.Type);
        return LoadField(fieldName, type);
    }

    protected CValue EmitEnumToUnderlying(CValue operand, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return operand;
        var underlyingType = named.EnumUnderlyingType;
        var convertMethod = ExternResolver.GetConvertMethodName(underlyingType);
        if (convertMethod == null) return operand;
        var underlyingUdon = GetUdonType(underlyingType);
        return ExternCall(
            $"SystemConvert.__{convertMethod}__SystemObject__{underlyingUdon}",
            new List<CValue> { operand },
            underlyingUdon);
    }


    // ── Aggregate Instance Load (no Clone) ──

    /// <summary>
    /// Load an aggregate instance reference WITHOUT cloning. Used for field access/write
    /// where we need the original object[], not a copy.
    /// VisitExpression() clones aggregate locals/params by default for value semantics,
    /// but field access operates on the original array.
    /// </summary>
    protected CValue LoadInstanceRaw(IOperation instance)
    {
        return instance switch
        {
            ILocalReferenceOperation lr when _localBindings.TryGetValue(lr.Local, out var b)
                => LoadField(b.Id, EmitContext.IsAggregateType(lr.Type) ? "SystemObjectArray" : GetUdonType(lr.Type)),
            IParameterReferenceOperation pr
                => LoadParam(pr.Parameter),
            // Inside a struct method/ctor, `this` is the receiver object[] param, not the Behaviour.
            IInstanceReferenceOperation when _ctx.CurrentStructReceiverParamId != null
                => LoadField(_ctx.CurrentStructReceiverParamId, "SystemObjectArray"),
            // Aggregate field as a RECEIVER (e.g. `o.inner.x`, `this.structField.x`) must NOT be cloned —
            // the access/mutation has to hit the live storage. (Value reads clone in VisitFieldReference.)
            IFieldReferenceOperation fr when EmitContext.IsAggregateType(fr.Type)
                => ReadAggregateFieldRaw(fr),
            // Aggregate array element as a RECEIVER (`arr[i].x = …`) likewise hits live storage, no clone.
            IArrayElementReferenceOperation ae when EmitContext.IsAggregateType(ae.Type)
                => ReadArrayElementRaw(ae),
            _ => VisitExpression(instance), // method return, field on this, etc. — fresh or already raw
        };
    }

    /// <summary>Read an aggregate array element as the raw stored object[] (no clone), for receiver access.</summary>
    protected CValue ReadArrayElementRaw(IArrayElementReferenceOperation ae)
    {
        var arrayVal = VisitExpression(ae.ArrayReference);
        var arrSym = ae.ArrayReference.Type as IArrayTypeSymbol;
        var arrType = GetArrayType(arrSym);
        var elemType = GetArrayElemType(arrSym);
        var idx = ae.Indices[0];
        CValue idxVal = idx is IUnaryOperation { Type.Name: "Index" } fromEnd
            ? ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", new List<CValue>
                { ExternCall($"{arrType}.__get_Length__SystemInt32", new List<CValue> { arrayVal }, "SystemInt32"),
                  VisitExpression(fromEnd.Operand) }, "SystemInt32")
            : VisitExpression(idx);
        return ExternCall($"{arrType}.__Get__SystemInt32__{elemType}", new List<CValue> { arrayVal, idxVal }, "SystemObject");
    }

    /// <summary>Read an aggregate-typed field as the raw stored object[] (no clone): a nested element via
    /// __Get__, or a this.field directly. Used for receiver access; value reads add a clone on top.</summary>
    protected CValue ReadAggregateFieldRaw(IFieldReferenceOperation fr)
    {
        if (fr.Instance != null && fr.Instance.Type is INamedTypeSymbol cont && EmitContext.IsAggregateType(cont)
            && _ctx.GetAggregateLayout(cont).TryGetIndex(fr.Field, out var idx))
            return ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CValue> { LoadInstanceRaw(fr.Instance), Const(idx, "SystemInt32") }, "SystemObject");
        if (fr.Instance is IInstanceReferenceOperation)
            return LoadField(fr.Field.Name, "SystemObjectArray");
        return VisitExpression(fr); // cross-behaviour aggregate field etc. — rare
    }

    // ── L-Value Assignment ──

    /// <summary>
    /// Assign a value to a common l-value target (declaration, local, this-field, parameter, discard).
    /// Callers with specialized targets (array elements, cross-behaviour fields) should handle those
    /// first, then delegate to this method for the common cases.
    /// </summary>
    protected void AssignToLValue(IOperation target, CValue value)
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

        // Create CFunction (internal, no export)
        var func = _module.AddFunction(irName);

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

    // ── Delegate bridge resolution ──

    /// <summary>Resolve delegate creation to bridge name, FuncRef, and target instance.</summary>
    protected (string bridgeName, CValue funcRef, CValue targetInstance) ResolveDelegateBridge(IDelegateCreationOperation op)
    {
        IMethodSymbol targetMethod = null;
        CValue targetInstance = null;
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
    /// Call an internal function via CoreBuilder.InternalCall.
    /// Returns the result CValue — this is an expression only, NOT emitted to the HIR.
    /// For void calls (e.g. property setters), wrap with <c>EmitExprStmt()</c> to add to the HIR.
    /// </summary>
    protected CValue EmitCallToMethod(IMethodSymbol target, List<CValue> args)
    {
        if (!_methodFunctions.TryGetValue(target, out var func))
            throw new InvalidOperationException($"No CFunction registered for method '{target.Name}'");
        var retType = func.ReturnType ?? "SystemVoid";

        // Recursion-cycle edge: the callee can re-enter the current method and clobber its param/local
        // slots (Udon's flat heap shares them across frames). Spill the caller's live values onto the
        // software stack, materialise the call so it is sequenced between spill and reload, then reload.
        if (IsRecursiveEdge(_currentMethod, target))
        {
            _ctx.EnsureRecursionStack();
            var spill = CollectRecursionSpillVars();
            EmitRecursionSpill(spill);
            CValue result;
            if (retType == "SystemVoid")
            {
                EmitExprStmt(InternalCall(func.Name, args, retType));
                result = null;
            }
            else
            {
                var t = _ctx.AllocTemp(retType);
                EmitAssign(t, InternalCall(func.Name, args, retType));
                result = SlotRef(t);
            }
            EmitRecursionReload(spill);
            return result;
        }

        return InternalCall(func.Name, args, retType);
    }

    // One frame value to spill across a recursive call — either a named heap field (param / receiver /
    // local) or an IR slot (any scratch/frame temp: array temps, foreach loop-control, and crucially the
    // result temp of an EARLIER recursive call in the same expression).
    readonly struct SpillEntry
    {
        public readonly int Slot;       // >= 0 for a slot entry
        public readonly string FieldId; // non-null for a field entry
        public readonly string Type;
        SpillEntry(int slot, string fieldId, string type) { Slot = slot; FieldId = fieldId; Type = type; }
        public static SpillEntry Field(string id, string type) => new SpillEntry(-1, id, type);
        public static SpillEntry SlotOf(int slot, string type) => new SpillEntry(slot, null, type);
        public bool IsSlot => FieldId == null;
    }

    // Every frame value that must survive a recursive re-entry of the current method: its parameters, the
    // struct receiver, all in-scope locals (named heap fields), AND every IR slot allocated so far in the
    // current function (scratch/frame temps). The slot snapshot is the key fix — Udon's flat heap shares a
    // function's scratch variables across all its activations, so an unnamed temp holding a value across a
    // sibling recursive call (e.g. the first `Fib(n-1)` result while `Fib(n-2)` runs, or a foreach's index)
    // is clobbered unless spilled. Captured BEFORE this call's own result temp is allocated, so that temp is
    // excluded. Over-approximation (spilling a dead temp) is inert.
    List<SpillEntry> CollectRecursionSpillVars()
    {
        var entries = new List<SpillEntry>();
        var seen = new HashSet<string>();
        void AddField(string id)
        {
            if (id == null || !seen.Add(id)) return;
            var t = _ctx.GetFieldType(id);
            if (t != null) entries.Add(SpillEntry.Field(id, t));
        }
        if (_currentMethod != null && _methodParamVarIds.TryGetValue(_currentMethod, out var pids))
            foreach (var pid in pids) AddField(pid);
        AddField(_ctx.CurrentStructReceiverParamId);
        // For a hoisted function (local function or lambda), only its OWN locals are frame-local and need
        // spilling. Locals captured from an enclosing scope are shared by reference (C# closure semantics) —
        // the same flat-heap sharing the recursion otherwise corrupts is here the CORRECT behaviour, so they
        // must NOT be saved/restored.
        bool isHoisted = _currentMethod != null
            && _currentMethod.MethodKind is MethodKind.LocalFunction or MethodKind.LambdaMethod or MethodKind.AnonymousFunction;
        foreach (var kv in _localBindings)
        {
            if (isHoisted && !SymbolEqualityComparer.Default.Equals(kv.Key.ContainingSymbol, _currentMethod))
                continue;
            AddField(kv.Value.Id);
        }

        var slots = _builder.CurrentFunction.Slots;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Class != SlotClass.Pinned) // Pinned = special infrastructure, not frame-local computation
                entries.Add(SpillEntry.SlotOf(slots[i].Id, slots[i].Type));
        return entries;
    }

    void EmitRecursionSpill(List<SpillEntry> entries)
    {
        foreach (var e in entries)
        {
            // __recurStack[__recurSp] = v   (boxed into the object[] element)
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid", new List<CValue>
            {
                LoadField(EmitContext.RecurStackId, "SystemObjectArray"),
                LoadField(EmitContext.RecurSpId, "SystemInt32"),
                e.IsSlot ? (CValue)SlotRef(e.Slot) : LoadField(e.FieldId, e.Type),
            });
            EmitRecurSpDelta(1);
        }
    }

    void EmitRecursionReload(List<SpillEntry> entries)
    {
        // Pop in reverse (LIFO).
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            EmitRecurSpDelta(-1);
            // v = __recurStack[__recurSp]   (boxed value; Udon unboxes transparently on typed use)
            var get = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject", new List<CValue>
            {
                LoadField(EmitContext.RecurStackId, "SystemObjectArray"),
                LoadField(EmitContext.RecurSpId, "SystemInt32"),
            }, "SystemObject");
            if (entries[i].IsSlot) EmitAssign(entries[i].Slot, get);
            else EmitStoreField(entries[i].FieldId, get);
        }
    }

    void EmitRecurSpDelta(int delta)
    {
        var sig = delta >= 0
            ? "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32"
            : "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32";
        EmitStoreField(EmitContext.RecurSpId, ExternCall(sig, new List<CValue>
        {
            LoadField(EmitContext.RecurSpId, "SystemInt32"),
            Const(System.Math.Abs(delta), "SystemInt32"),
        }, "SystemInt32"));
    }
}
