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
    protected List<IMethodSymbol> _pendingGenericSpecs => _ctx.PendingGenericSpecs;
    protected Dictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap { get => _ctx.TypeParamMap; set => _ctx.TypeParamMap = value; }
    protected Dictionary<ILocalSymbol, EmitContext.LocalBinding> _localBindings => _ctx.LocalBindings;
    protected List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _ctx.FieldInitOps;
    protected Dictionary<string, string> _fieldChangeCallbacks => _ctx.FieldChangeCallbacks;
    protected Stack<CLeaf> _conditionalAccessStack => _ctx.ConditionalAccessStack;
    protected Stack<List<(CLeaf val, ITypeSymbol type)>> _usingDisposableStack => _ctx.UsingDisposableStack;
    protected HashSet<string> _delegateFields => _ctx.DelegateFields;
    protected List<EmitDiagnostic> _diagnostics => _ctx.Diagnostics;
    protected bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee) => _ctx.IsRecursiveEdge(caller, callee);

    // ── Dispatch (recursive descent into other handlers via UasmEmitter facade) ──
    protected void VisitOperation(IOperation op) => _ctx.VisitOperation(op);
    protected CLeaf VisitExpression(IOperation op) => _ctx.VisitExpression(op);
    protected CLeaf EmitPatternCheck(CLeaf value, ITypeSymbol valueType, IPatternOperation pattern)
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

    // ── Core IR convenience methods ──

    /// <summary>Emit: slot = expr</summary>
    protected void EmitAssign(int destSlot, CValue value) => _builder.EmitAssign(destSlot, value);

    /// <summary>Emit: fieldName = expr</summary>
    protected void EmitStoreField(string fieldName, CLeaf value) => _builder.EmitStoreField(fieldName, value);

    /// <summary>Emit: return [value]</summary>
    protected void EmitReturn(CLeaf value = null) => _builder.EmitReturn(value);

    /// <summary>Create a constant.</summary>
    protected CConst Const(object value, string type) => _builder.Const(value, type);

    /// <summary>Create a slot reference expression.</summary>
    protected CSlotRef SlotRef(int slotId) => _builder.SlotRef(slotId);

    /// <summary>Read a field's value — materialized to a scratch slot (A-normal form), returns the leaf.</summary>
    protected CSlotRef LoadField(string fieldName, string type) => _builder.LoadField(fieldName, type);

    /// <summary>Create a field address reference (for extern out/ref).</summary>
    protected CFieldAddr FieldAddr(string fieldName, string type) => _builder.FieldAddr(fieldName, type);

    /// <summary>Emit an extern call, materialized to a scratch slot (returns the leaf; null for void).</summary>
    protected CSlotRef ExternCall(string sig, List<CLeaf> args, string retType)
        => _builder.ExternCall(ResolveExtern(sig), args, retType);

    /// <summary>
    /// Integer conversion matching C# *unchecked* semantics (wrap / bit-reinterpret). Udon's
    /// SystemConvert.ToX is CHECKED and throws on overflow, so a narrowing / cross-sign integer cast is
    /// reduced to its low 32 bits (sign-extended via a 64-bit shift) before the final in-range convert.
    /// Lossless widenings (and non-integer conversions) use the plain convert extern directly. The 64-bit
    /// unsigned cases require unchecked 64-bit ops Udon does not expose and fall back to the checked convert.
    /// </summary>
    protected CLeaf EmitNarrowingConvert(CLeaf value, string fromUdonType, string toUdonType)
    {
        if (fromUdonType == toUdonType)
            return value;

        // long <-> ulong is a pure bit reinterpret in C# (the cast is unchecked), but Convert.To{U}Int64 is
        // CHECKED and throws on a high-bit-set value (e.g. (ulong)(-1L)). Round-trip the 8 bytes instead.
        if ((fromUdonType == "SystemInt64" && toUdonType == "SystemUInt64")
            || (fromUdonType == "SystemUInt64" && toUdonType == "SystemInt64"))
        {
            var bytes = ExternCall($"SystemBitConverter.__GetBytes__{fromUdonType}__SystemByteArray",
                new List<CLeaf> { value }, "SystemByteArray");
            var toMethod = toUdonType == "SystemUInt64" ? "ToUInt64" : "ToInt64";
            return ExternCall($"SystemBitConverter.__{toMethod}__SystemByteArray_SystemInt32__{toUdonType}",
                new List<CLeaf> { bytes, Const(0, "SystemInt32") }, toUdonType);
        }

        // Other ulong conversions are UNCHECKED bit ops in C# but SystemConvert.To{U}Int* are CHECKED and
        // throw (e.g. (ulong)(-1), (int)(largeUlong)). Route via int64: widen/reinterpret the source to long
        // (sign/zero-extend) then reinterpret long→ulong above; or reinterpret ulong→long then narrow.
        if (toUdonType == "SystemUInt64")
            return EmitNarrowingConvert(EmitNarrowingConvert(value, fromUdonType, "SystemInt64"),
                "SystemInt64", "SystemUInt64");
        if (fromUdonType == "SystemUInt64")
            return EmitNarrowingConvert(EmitNarrowingConvert(value, "SystemUInt64", "SystemInt64"),
                "SystemInt64", toUdonType);

        // Non-integer conversions, and lossless integer widenings, never overflow → plain convert is correct.
        if (!IsIntegerUdon(fromUdonType) || !IsIntegerUdon(toUdonType)
            || IsLosslessIntegerWiden(fromUdonType, toUdonType))
            return ExternCall(ExternResolver.BuildConvertSignature(fromUdonType, toUdonType),
                new List<CLeaf> { value }, toUdonType);

        // Reduce the source to its low 32 bits as a SIGNED int32, then wrap / reinterpret to the target width.
        var lowSigned = LowInt32Bits(value, fromUdonType);
        switch (toUdonType)
        {
            case "SystemInt32":  return lowSigned;
            case "SystemByte":   return ConvertInRange(ModWrap(lowSigned, 256), toUdonType);
            case "SystemChar":
            case "SystemUInt16": return ConvertInRange(ModWrap(lowSigned, 65536), toUdonType);
            case "SystemSByte":  return ConvertInRange(ShiftTruncate(lowSigned, 24), toUdonType);
            case "SystemInt16":  return ConvertInRange(ShiftTruncate(lowSigned, 16), toUdonType);
            case "SystemUInt32": return Int32BitsToUInt32(lowSigned);
        }
        // Unreachable for the supported integer target set; safety fallback.
        return ExternCall(ExternResolver.BuildConvertSignature(fromUdonType, toUdonType),
            new List<CLeaf> { value }, toUdonType);
    }

    /// <summary>Low 32 bits of an integer value as a SIGNED int32 (C# unchecked reinterpret). Sources wider than
    /// int32 are reduced by a 64-bit sign-extending shift; ≤32-bit sources widen losslessly to int64 first.</summary>
    CLeaf LowInt32Bits(CLeaf value, string fromUdonType)
    {
        if (fromUdonType == "SystemInt32")
            return value;
        var asLong = fromUdonType == "SystemInt64"
            ? value
            : ExternCall(ExternResolver.BuildConvertSignature(fromUdonType, "SystemInt64"),
                new List<CLeaf> { value }, "SystemInt64");
        // (x << 32) >> 32 : arithmetic right shift sign-extends bit 31 → value in [-2^31, 2^31), safe to ToInt32.
        var shl = ExternCall("SystemInt64.__op_LeftShift__SystemInt64_SystemInt32__SystemInt64",
            new List<CLeaf> { asLong, Const(32, "SystemInt32") }, "SystemInt64");
        var sar = ExternCall("SystemInt64.__op_RightShift__SystemInt64_SystemInt32__SystemInt64",
            new List<CLeaf> { shl, Const(32, "SystemInt32") }, "SystemInt64");
        return ExternCall("SystemConvert.__ToInt32__SystemInt64__SystemInt32",
            new List<CLeaf> { sar }, "SystemInt32");
    }

    /// <summary>Reinterpret an int32 bit pattern as uint32 (C# unchecked (uint)int): negatives map to +2^32.</summary>
    CLeaf Int32BitsToUInt32(CLeaf int32Val)
    {
        var asLong = ExternCall("SystemConvert.__ToInt64__SystemInt32__SystemInt64",
            new List<CLeaf> { int32Val }, "SystemInt64");
        var isNeg = ExternCall("SystemInt64.__op_LessThan__SystemInt64_SystemInt64__SystemBoolean",
            new List<CLeaf> { asLong, Const(0L, "SystemInt64") }, "SystemBoolean");
        var plus = ExternCall("SystemInt64.__op_Addition__SystemInt64_SystemInt64__SystemInt64",
            new List<CLeaf> { asLong, Const(4294967296L, "SystemInt64") }, "SystemInt64");
        var wrapped = Select(isNeg, plus, asLong, "SystemInt64");
        return ExternCall("SystemConvert.__ToUInt32__SystemInt64__SystemUInt32",
            new List<CLeaf> { wrapped }, "SystemUInt32");
    }

    static bool IsIntegerUdon(string t) => ExternResolver.IntInfo(t).rank > 0;

    /// <summary>True when every value of the source integer type is representable in the target (so the checked
    /// SystemConvert never throws and yields the same result as the C# cast).</summary>
    static bool IsLosslessIntegerWiden(string from, string to)
    {
        var (fr, fs) = ExternResolver.IntInfo(from);
        var (tr, ts) = ExternResolver.IntInfo(to);
        if (fr == 0 || tr == 0) return false;
        if (fs == ts) return tr >= fr;   // same signedness → equal/wider is lossless
        if (!fs && ts) return tr > fr;   // unsigned → signed needs a strictly wider target
        return false;                    // signed → unsigned is never lossless (negatives)
    }

    CLeaf ConvertInRange(CLeaf inRangeInt, string toUdonType)
        => ExternCall(ExternResolver.BuildConvertSignature("SystemInt32", toUdonType),
            new List<CLeaf> { inRangeInt }, toUdonType);

    // ((x % mod) + mod) % mod  →  [0, mod)  : C# unsigned narrowing wrap
    CLeaf ModWrap(CLeaf x, int mod)
    {
        var add = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { Rem(x, mod), Const(mod, "SystemInt32") }, "SystemInt32");
        return Rem(add, mod);
    }

    CLeaf Rem(CLeaf x, int mod)
        => ExternCall("SystemInt32.__op_Remainder__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { x, Const(mod, "SystemInt32") }, "SystemInt32");

    // (x << s) >> s  →  signed (32-s)-bit truncation with sign extension
    CLeaf ShiftTruncate(CLeaf x, int shift)
    {
        var left = ExternCall("SystemInt32.__op_LeftShift__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { x, Const(shift, "SystemInt32") }, "SystemInt32");
        return ExternCall("SystemInt32.__op_RightShift__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { left, Const(shift, "SystemInt32") }, "SystemInt32");
    }

    /// <summary>True for the integer Udon types whose op_Remainder extern does not exist (Int64/UInt64, and
    /// also UInt32 — Udon ships uint Division/Multiplication/Subtraction but no uint Remainder).</summary>
    protected static bool RemainderNeedsPolyfill(string udonType)
        => udonType is "SystemInt64" or "SystemUInt64" or "SystemUInt32";

    /// <summary>Remainder polyfill for types lacking an op_Remainder extern (see RemainderNeedsPolyfill): lower
    /// `a % b` to `a - (a / b) * b` using the matching signed/unsigned Division/Multiplication/Subtraction.
    /// Truncate-toward-zero (signed) / floor (unsigned) division makes this exact for every case, including
    /// unsigned dividends above int.MaxValue. Shared by the binary and compound paths.</summary>
    protected CLeaf EmitRemainderViaDivision(CLeaf left, CLeaf right, string t)
    {
        // left/right are CLeaf params — stable single-assignment leaves under ANF; the intermediate
        // ExternCall results each bind their own fresh scratch, so neither operand is mutated here.
        var quot = ExternCall($"{t}.__op_Division__{t}_{t}__{t}",
            new List<CLeaf> { left, right }, t);
        var prod = ExternCall($"{t}.__op_Multiplication__{t}_{t}__{t}",
            new List<CLeaf> { quot, right }, t);
        return ExternCall($"{t}.__op_Subtraction__{t}_{t}__{t}",
            new List<CLeaf> { left, prod }, t);
    }

    /// <summary>Emit a void extern call as a statement. <paramref name="reentrant"/> marks a
    /// delegate-dispatch arm that can re-enter the containing function (design §4.3).</summary>
    protected void EmitExternVoid(string sig, List<CLeaf> args, bool reentrant = false)
        => _builder.EmitExternVoid(ResolveExtern(sig), args, reentrant);

    /// <summary>Create an internal call expression.</summary>
    protected CSlotRef InternalCall(string funcName, List<CLeaf> args, string retType)
        => _builder.InternalCall(funcName, args, retType);

    /// <summary>Emit a cross-behaviour call. Single-return → materialized to a scratch slot (returns the
    /// leaf); void or multi-return → side-effecting statement (returns null).</summary>
    protected CSlotRef CrossCall(CLeaf instance, string eventName,
        List<(string, CLeaf)> parameters, IReadOnlyList<ReturnSlot> returns, string retType)
        => _builder.CrossCall(instance, eventName, parameters, returns, retType);

    /// <summary>Create a select (ternary) expression.</summary>
    protected CSlotRef Select(CLeaf cond, CLeaf trueVal, CLeaf falseVal, string type)
        => _builder.Select(cond, trueVal, falseVal, type);

    /// <summary>Create a function reference (for delegate/JUMP_INDIRECT).</summary>
    protected CFuncRef FuncRef(string funcName) => _builder.FuncRef(funcName);

    /// <summary>Emit a statement.</summary>
    protected void Emit(CStmt stmt) => _builder.Emit(stmt);

    /// <summary>Emit an expression as a statement (side-effecting call). Under A-normal form a value-producing
    /// call is already materialized at construction, so a leaf or null reaching here has no remaining side
    /// effect — skip it. Only an unbound producer (void call) needs emitting as a statement.</summary>
    protected void EmitExprStmt(CValue expr)
    {
        if (expr == null || expr is CLeaf) return;
        _builder.EmitExprStmt(expr);
    }

    /// <summary>Emit a void internal call as a side-effecting statement (not materialized to a slot).
    /// <paramref name="reentrant"/> marks a delegate-dispatch arm that can re-enter the containing
    /// function (design §4.3).</summary>
    protected void EmitInternalVoid(string funcName, List<CLeaf> args, bool reentrant = false)
        => _builder.EmitInternalVoid(funcName, args, reentrant);

    // ── Nullable<T> (boxed-object emulation) helpers ──

    /// <summary>HasValue: the boxed nullable object is non-null. Returns SystemBoolean.
    /// <paramref name="nullableVal"/> must be pure or pre-materialised (it is read once).</summary>
    protected CLeaf EmitNullableHasValue(CLeaf nullableVal)
    {
        var isNull = ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { nullableVal, Const(null, "SystemObject") }, "SystemBoolean");
        return ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            new List<CLeaf> { isNull }, "SystemBoolean");
    }

    /// <summary>Default value for a Udon value type (0 / false). Used for `default(T)`-style fills.</summary>
    protected CLeaf EmitValueTypeDefault(string udonType)
        => Const(EmitContext.ParseConstValue(udonType, udonType == "SystemBoolean" ? "False" : "0"), udonType);

    /// <summary>Deep value-copy of an object[]-backed aggregate (struct/tuple): a fresh array with each
    /// element copied, recursing into nested-aggregate elements. A shallow SystemObjectArray.__Clone__ would
    /// copy the nested object[] REFERENCE, so mutating the copy's nested struct would corrupt the source.</summary>
    protected CLeaf EmitDeepCloneAggregate(CLeaf src, INamedTypeSymbol aggType)
    {
        var layout = _ctx.GetAggregateLayout(aggType);
        // src is a single-assignment SystemObjectArray leaf under ANF; the loop only READS its elements
        // (writes target the fresh dstSlot), so it is stable without a snapshot.
        var dstSlot = _ctx.AllocTemp("SystemObjectArray");
        EmitAssign(dstSlot, ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CLeaf> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray"));
        for (int i = 0; i < layout.Count; i++)
        {
            var elem = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CLeaf> { src, Const(i, "SystemInt32") }, "SystemObject");
            CLeaf copy = layout.Fields[i].Type is INamedTypeSymbol nested && EmitContext.IsAggregateType(nested)
                ? EmitDeepCloneAggregate(elem, nested) // nested aggregate → recurse
                : elem;                                // boxed scalar → reference copy is fine (immutable box)
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CLeaf> { SlotRef(dstSlot), Const(i, "SystemInt32"), copy });
        }
        return SlotRef(dstSlot);
    }

    /// <summary>Allocate a fresh object[]-backed aggregate (struct/tuple) and default-initialize it as a
    /// VALUE (e.g. `new V()` used as an expression). Nested aggregate fields are recursively allocated.</summary>
    public CLeaf EmitNewAggregate(INamedTypeSymbol aggType)
    {
        var layout = _ctx.GetAggregateLayout(aggType);
        var slot = _ctx.AllocTemp("SystemObjectArray");
        EmitAssign(slot, ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CLeaf> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray"));
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
                    new List<CLeaf> { Const(nl.Count, "SystemInt32") }, "SystemObjectArray"));
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<CLeaf> { SlotRef(slot), Const(i, "SystemInt32"), SlotRef(subSlot) });
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
                    new List<CLeaf> { SlotRef(slot), Const(i, "SystemInt32"), Const(defVal, GetUdonType(fieldType)) });
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

    /// <summary>Promote a boxed small-int/char operand to int32 (Udon has no operators on those types and a
    /// boxed small-int does not coerce to int for a SystemInt32 extern). Routes through ToInt32(SystemObject)
    /// rather than the type-strict ToInt32(SystemByte/SystemChar/…): a nullable small-int's stored value is
    /// often a boxed plain int (e.g. <c>byte? x = 5</c> keeps the int literal un-narrowed), which a strict
    /// typed fetch rejects with InvalidCast. Convert.ToInt32(object) tolerates any boxed numeric. Pass-through
    /// for non-small types.</summary>
    protected CLeaf PromoteBoxedToInt32(CLeaf boxed, ITypeSymbol underlying, out ITypeSymbol effectiveType)
    {
        if (ExternResolver.IsSmallIntOrChar(GetUdonType(underlying)))
        {
            effectiveType = _compilation.GetSpecialType(SpecialType.System_Int32);
            return ExternCall("SystemConvert.__ToInt32__SystemObject__SystemInt32", new List<CLeaf> { boxed }, "SystemInt32");
        }
        effectiveType = underlying;
        return boxed;
    }

    /// <summary>Lifted binary operator on Nullable&lt;T&gt; (null propagation), from already-evaluated operand
    /// values. Arithmetic yields T? (null unless both present); relational yields bool (false if either null);
    /// equality yields bool (both-null is equal). Shared by <c>OperatorHandler</c> and compound assignment.</summary>
    protected CLeaf EmitLiftedBinaryCore(
        CValue leftVal, bool leftNullable, ITypeSymbol ltUnderlying,
        CValue rightVal, bool rightNullable, ITypeSymbol rtUnderlying,
        Microsoft.CodeAnalysis.Operations.BinaryOperatorKind kind, IMethodSymbol operatorMethod, ITypeSymbol resultType)
    {
        var resultNullable = EmitContext.IsNullableT(resultType, out var resU);

        var aSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(aSlot, leftVal);
        var bSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(bSlot, rightVal);

        CLeaf IsNullV(int slot) => ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CLeaf> { SlotRef(slot), Const(null, "SystemObject") }, "SystemBoolean");

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
            bool resPromotes = ExternResolver.IsSmallIntOrChar(GetUdonType(resUnder));
            var resEff = resPromotes ? _compilation.GetSpecialType(SpecialType.System_Int32) : resUnder;
            var raw = ExternCall(
                ExternResolver.ResolveBinaryExtern(k, operatorMethod, ResolveType(ltEff), ResolveType(rtEff), ResolveType(resEff)),
                new List<CLeaf> { aV, bV }, GetUdonType(resEff));
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
                    new List<CLeaf> { SlotRef(eqSlot) }, "SystemBoolean");
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

    /// <summary>Read a parameter value as a CLeaf (field load). A delegate-typed parameter is a
    /// SystemObjectArray bundle reference via the type-map delegate arm (design §2.1).</summary>
    protected CLeaf LoadParam(IParameterSymbol param)
    {
        var fieldName = GetParamVarId(param);
        return LoadField(fieldName, GetUdonType(param.Type));
    }

    protected CLeaf EmitEnumToUnderlying(CLeaf operand, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return operand;
        var underlyingType = named.EnumUnderlyingType;
        var convertMethod = ExternResolver.GetConvertMethodName(underlyingType);
        if (convertMethod == null) return operand;
        var underlyingUdon = GetUdonType(underlyingType);
        return ExternCall(
            $"SystemConvert.__{convertMethod}__SystemObject__{underlyingUdon}",
            new List<CLeaf> { operand },
            underlyingUdon);
    }


    // ── Aggregate Instance Load (no Clone) ──

    /// <summary>
    /// Load an aggregate instance reference WITHOUT cloning. Used for field access/write
    /// where we need the original object[], not a copy.
    /// VisitExpression() clones aggregate locals/params by default for value semantics,
    /// but field access operates on the original array.
    /// </summary>
    protected CLeaf LoadInstanceRaw(IOperation instance)
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
    protected CLeaf ReadArrayElementRaw(IArrayElementReferenceOperation ae)
    {
        var arrayVal = VisitExpression(ae.ArrayReference);
        var arrSym = ae.ArrayReference.Type as IArrayTypeSymbol;
        var arrType = GetArrayType(arrSym);
        var elemType = GetArrayElemType(arrSym);
        var idx = ae.Indices[0];
        CLeaf idxVal = idx is IUnaryOperation { Type: { Name: "Index" } } fromEnd
            ? ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", new List<CLeaf>
                { ExternCall($"{arrType}.__get_Length__SystemInt32", new List<CLeaf> { arrayVal }, "SystemInt32"),
                  VisitExpression(fromEnd.Operand) }, "SystemInt32")
            : VisitExpression(idx);
        return ExternCall($"{arrType}.__Get__SystemInt32__{elemType}", new List<CLeaf> { arrayVal, idxVal }, "SystemObject");
    }

    /// <summary>Read an aggregate-typed field as the raw stored object[] (no clone): a nested element via
    /// __Get__, or a this.field directly. Used for receiver access; value reads add a clone on top.</summary>
    protected CLeaf ReadAggregateFieldRaw(IFieldReferenceOperation fr)
    {
        if (fr.Instance != null && fr.Instance.Type is INamedTypeSymbol cont && EmitContext.IsAggregateType(cont)
            && _ctx.GetAggregateLayout(cont).TryGetIndex(fr.Field, out var idx))
            return ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CLeaf> { LoadInstanceRaw(fr.Instance), Const(idx, "SystemInt32") }, "SystemObject");
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
    protected void AssignToLValue(IOperation target, CLeaf value)
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
                else if (declExpr.Expression is ITupleOperation declTuple)
                    AssignNestedTupleElements(declTuple, value);
                break;

            // A nested deconstruction target tuple, e.g. the (b,c) in `var (a, (b,c)) = …`. `value` is the
            // object[]-emulated nested tuple; read each element and recurse (handles arbitrary nesting depth).
            case ITupleOperation nestedTuple:
                AssignNestedTupleElements(nestedTuple, value);
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

            // Field on a user STRUCT instance (`p.v`) as an l-value target — e.g. `ref p.v` copy-back or
            // `(p.v, …) = …` deconstruction. The struct is an object[]; write the field's layout slot in place
            // (the receiver array is shared, so the mutation reflects back to the caller's local).
            case IFieldReferenceOperation aggFieldRef
                when aggFieldRef.Instance != null
                && aggFieldRef.Instance.Type is INamedTypeSymbol aggFieldType
                && EmitContext.IsAggregateType(aggFieldType)
                && _ctx.GetAggregateLayout(aggFieldType).TryGetIndex(aggFieldRef.Field, out var aggElemIdx):
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<CLeaf> { LoadInstanceRaw(aggFieldRef.Instance), Const(aggElemIdx, "SystemInt32"), value });
                break;

            case IParameterReferenceOperation paramRef:
                EmitStoreField(GetParamVarId(paramRef.Parameter), value);
                break;

            case IArrayElementReferenceOperation arrayElem:
            {
                // Deconstruction into an array element: `(arr[0], arr[1]) = (...)`. The caller's two-loop split
                // already evaluated every RHS element before any store, so the swap/rotate idiom is safe here.
                var arrayVal = VisitExpression(arrayElem.ArrayReference);
                var indexVal = VisitExpression(arrayElem.Indices[0]);
                var arrSym = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSym);
                var elementType = GetArrayElemType(arrSym);
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                    new List<CLeaf> { arrayVal, indexVal, value });
                break;
            }

            case IDiscardOperation:
                break;

            default:
                throw new System.NotSupportedException(
                    $"Unsupported l-value target: {target.GetType().Name}");
        }
    }

    /// <summary>Assign a nested deconstruction target tuple from its object[]-emulated value: read each element
    /// via __Get and delegate to AssignToLValue (which recurses for deeper tuples / handles the leaf lvalues).
    /// A struct (non-tuple aggregate) leaf is deep-cloned for value semantics; a nested tuple recurses instead.</summary>
    void AssignNestedTupleElements(ITupleOperation tuple, CLeaf arrValue)
    {
        for (int i = 0; i < tuple.Elements.Length; i++)
        {
            var elemVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CLeaf> { arrValue, Const(i, "SystemInt32") }, "SystemObject");
            var toAssign = tuple.Elements[i].Type is INamedTypeSymbol et
                && EmitContext.IsAggregateType(et) && !et.IsTupleType
                ? EmitDeepCloneAggregate(elemVal, et) : elemVal;
            AssignToLValue(tuple.Elements[i], toAssign);
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

        // Declare params as fields (the Core IR passes parameters as fields). Delegate-typed params are
        // SystemObjectArray bundle references via the type-map delegate arm (design §2.1).
        var lfParamIds = new string[localFunc.Parameters.Length];
        for (int pi = 0; pi < localFunc.Parameters.Length; pi++)
        {
            var param = localFunc.Parameters[pi];
            var paramId = $"__{idx}_{param.Name}__param";
            _ctx.DeclareVar(paramId, GetUdonType(param.Type));
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

    /// <summary>Compute signature-based convention field names for a delegate type
    /// (sig key via the unified DelegateAbi.BuildSigPart — design §3.2). Pass the type-param map when
    /// resolving inside a generic-spec body so e.g. Func&lt;T&gt; keys on the substituted type.</summary>
    internal static (string[] argNames, string retName) GetConventionFieldNames(INamedTypeSymbol delegateType,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var invoke = delegateType.DelegateInvokeMethod;
        var sigPart = DelegateAbi.BuildSigPart(invoke, typeParamMap);

        var argNames = new string[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
            argNames[i] = $"__dlgc_{sigPart}__a{i}";

        string retName = null;
        if (!invoke.ReturnsVoid)
            retName = $"__dlgc_{sigPart}__ret";

        return (argNames, retName);
    }

    // ── §2.8 capture recording + capture-escape guard ──

    protected const string CaptureEscapeError =
        "Lambdas that capture local variables cannot be stored into arrays/objects or returned in v2.x Stage 1. "
      + "Use a capture-free lambda or a method group; closure environments arrive in Stage 2.";

    protected const string BuriedCapturingLambdaError =
        "A lambda that captures local variables cannot appear inside a composite expression "
      + "(ternary / coalesce / switch arm) assigned to a delegate-typed target in v2.x Stage 1 — "
      + "restructure: assign the lambda directly. Capture-free lambdas and method groups are unrestricted.";

    /// <summary>Unwrap conversions to a delegate creation whose target is a lambda; true when found.</summary>
    protected static bool TryGetLambdaCreation(IOperation value, out IAnonymousFunctionOperation lambda)
    {
        lambda = null;
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (v is IDelegateCreationOperation { Target: IAnonymousFunctionOperation l }) { lambda = l; return true; }
        return false;
    }

    /// <summary>Value is directly a capturing-lambda delegate creation (conversions unwrapped) —
    /// or, §2.8 round-3 [A], a method-group conversion of a CAPTURING LOCAL FUNCTION, which is the
    /// same closure in IMethodReferenceOperation clothing (the lambda analyzer never sees it).</summary>
    protected bool IsDirectCapturingLambda(IOperation value)
    {
        if (TryGetLambdaCreation(value, out var l)) return _ctx.CaptureAnalyzer.HasCaptures(l);
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        return v is IDelegateCreationOperation { Target: IMethodReferenceOperation mr }
            && _ctx.IsCapturingLocalFunction(mr.Method);
    }

    /// <summary>Value reads a local tainted by a capturing-lambda store (§2.8(b) flow-insensitive taint).</summary>
    protected bool IsCaptureTaintedRead(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        return v is ILocalReferenceOperation lr && _ctx.CapturingLambdaLocals.Contains(lr.Local);
    }

    /// <summary>object / object[] — storing a delegate there erases the loud delegate typing (§2.8(b)).</summary>
    protected static bool IsObjectish(ITypeSymbol t)
        => t != null && (t.SpecialType == SpecialType.System_Object
            || (t is IArrayTypeSymbol at && at.ElementType.SpecialType == SpecialType.System_Object));

    /// <summary>Resolved delegate-CAPABLE type (§2.8 round-2): a type whose value can carry a delegate
    /// bundle past the delegate-typed guards — a delegate itself, System.Object (boxing erases the
    /// delegate typing: `object IdO(object x)` laundered, VM-verified), an UNRESOLVED type parameter
    /// (generic bodies are emitted from the definition tree so Parameter.Type stays T; the type-param
    /// map is consulted first via ResolveType, keeping non-delegate instantiations precise and legal),
    /// or a tuple with any delegate-capable element (tuples erase delegate typing the same way object
    /// does — tuple return/param envelopes laundered, VM-verified). Over-rejection accepted per §8-3.</summary>
    protected bool IsDelegateCapableType(ITypeSymbol t)
    {
        if (t == null) return false;
        var r = ResolveType(t);
        if (r == null) return false;
        if (r.SpecialType == SpecialType.System_Object) return true;
        return IsNonObjectDelegateCapableType(r);
    }

    /// <summary>Delegate-capable minus bare System.Object: delegate proper, unresolved type param,
    /// or delegate-capable tuple. Bare-object VALUES cannot legally carry a bundle — every entry
    /// point is sealed (objectish store targets reject, objectish locals reject at declaration,
    /// erasing-typed ARGUMENTS are guarded at the call site) — so object-typed param/member reads
    /// are clean and ordinary object plumbing keeps compiling.</summary>
    protected bool IsNonObjectDelegateCapableType(ITypeSymbol t)
    {
        if (t == null) return false;
        var r = ResolveType(t);
        if (r == null) return false;
        if (r is ITypeParameterSymbol) return true;
        if (r is INamedTypeSymbol nt)
        {
            if (nt.DelegateInvokeMethod != null) return true;
            if (nt.IsTupleType)
            {
                foreach (var el in nt.TupleElements)
                    if (IsDelegateCapableType(el.Type)) return true;
            }
            // §2.8 round-3 [B]: a USER STRUCT with a (recursively) delegate-capable instance field
            // is an envelope — its object[] emulation carries the bundle past every delegate-typed
            // gate (whole-struct array stores / returns / erased args, VM-verified laundering).
            // Auto-prop backing fields are IFieldSymbols, so fields cover all stored members.
            // Terminates: C# forbids value-type field cycles, and array fields are not capable
            // (array-element stores of dangerous values are loud everywhere already).
            else if (EmitContext.IsUserStruct(nt))
            {
                foreach (var member in nt.GetMembers())
                    if (member is IFieldSymbol fld && !fld.IsStatic && IsDelegateCapableType(fld.Type))
                        return true;
            }
        }
        return false;
    }

    /// <summary>Resolved delegate type proper (Func/Action/custom delegate after type-param
    /// substitution). Arguments to delegate-PROPER params stay unguarded — fcd37's
    /// `Apply(x =&gt; x * kk, 5)` is the supported consumption flow; the callee's own escape
    /// guards own any misuse of the param.</summary>
    protected bool IsDelegateProperType(ITypeSymbol t)
        => t != null && ResolveType(t) is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null;

    /// <summary>Conversion-stripped read of a parameter whose RESOLVED type is a delegate proper
    /// (or a still-unresolved type param — conservative). Such reads are tainted-EQUIVALENT at
    /// escaping STORE targets (array element / object / field / property, plus local-taint
    /// propagation): the callee cannot see whether its caller passed a capturing lambda, so
    /// `void Keep(Func&lt;int&gt; x) { fs[k] = x; }` — and `Keep&lt;T&gt;(T[] a, int i, T x)` at a delegate
    /// instantiation (the emitted body keeps Parameter.Type == T; ResolveType consults the
    /// type-param map) — would launder the capture past every loud reject (VM-verified silent
    /// wrong values). Object/tuple-typed params are NOT tainted here: bundles cannot legally
    /// reach them (GuardCaptureEscapeArguments seals the call sites), which keeps ordinary
    /// object plumbing (`void Add(object[] a, object b) { a[0] = b; }`) compiling. RETURNING a
    /// param stays legal — identity callees (`Id(x) { return x; }`) are the method-group /
    /// capture-free flow, and the CALLER's invocation-result taint guards the laundered result.
    /// Over-rejection of method-group setters (`SetCb(M)` → `cb = x` rejects) is accepted per
    /// design §8-3.</summary>
    protected bool IsDelegateParamRead(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (v is not IParameterReferenceOperation pr) return false;
        var rt = ResolveType(pr.Parameter.Type);
        return IsDelegateProperType(rt) || rt is ITypeParameterSymbol;
    }

    /// <summary>§2.8 round-2 caller-side argument guard: a capturing lambda (or tainted-equivalent
    /// read) passed to an ERASING-typed parameter — object, delegate-carrying tuple, or a type
    /// param resolving to one — is a loud compile error. Type erasure makes the callee blind
    /// (`KeepObj(object x)` / `KeepT((Func&lt;int&gt;,int) t)` / `Id&lt;object&gt;(x)` all laundered,
    /// VM-verified silent wrong values), so the bundle must be stopped from ENTERING the erased
    /// param instead of tainting every object read. Delegate-PROPER params stay unguarded (fcd37);
    /// method groups and capture-free lambdas pass everywhere.</summary>
    protected void GuardCaptureEscapeArguments(IEnumerable<IArgumentOperation> args)
    {
        foreach (var arg in args)
        {
            if (arg?.Value == null || arg.Parameter == null) continue;
            var pt = arg.Parameter.Type;
            if (!IsDelegateCapableType(pt) || IsDelegateProperType(pt)) continue;
            if (ContainsCapturingLambdaOrTaintedRead(arg.Value))
                throw new System.NotSupportedException(CaptureEscapeError);
        }
    }

    /// <summary>§2.8 round-2 member-read taint, RECIPIENT flavor: conversion-stripped read of a
    /// field / auto-property / struct member that received a DIRECT capturing-lambda store anywhere
    /// in this class (pre-scanned before emission — order-independent). The store itself is legal
    /// (one live bundle per member is correct; the aliasing detector owns the 2+-lambda case), but
    /// COPYING the member out re-creates multi-activation aliasing with a single lambda, which the
    /// detector's 2+ threshold cannot see (auto-property/field round-trips, VM-verified wrong).
    /// Members seeded only with method groups / capture-free lambdas stay freely readable.</summary>
    protected bool IsCaptureReceivingMemberRead(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        switch (v)
        {
            case IFieldReferenceOperation fr: return _ctx.CaptureReceivingMembers.Contains(fr.Field);
            case IPropertyReferenceOperation pr: return _ctx.CaptureReceivingMembers.Contains(pr.Property);
            default: return false;
        }
    }

    /// <summary>§2.8 round-2 member-read taint, full STORE-position flavor: a recipient-member read
    /// (above), OR a delegate-capable member read whose instance chain roots at a PARAMETER (the
    /// callee is blind to what the caller packed into a tuple/struct envelope — `fs[k] = t.Item1` /
    /// `fs[k] = s.f` inside the callee, both VM-verified laundering) or at a capture-tainted local
    /// (aggregate copies must not re-launder). Param/tainted-rooted reads stay legal at RETURNS,
    /// mirroring bare param reads: the caller's invocation-result taint guards the laundered result.</summary>
    protected bool IsLaunderingMemberRead(IOperation value)
    {
        if (IsCaptureReceivingMemberRead(value)) return true;
        if (IsForeignDelegateMemberRead(value)) return true;
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        ITypeSymbol memberType;
        IOperation root;
        switch (v)
        {
            case IFieldReferenceOperation fr: memberType = fr.Field.Type; root = fr.Instance; break;
            case IPropertyReferenceOperation pr: memberType = pr.Property.Type; root = pr.Instance; break;
            default: return false;
        }
        // Bare-object members are excluded: bundles cannot legally reach them (objectish store
        // targets and erasing-typed arguments are sealed), so `objs[i] = param.objField` plumbing
        // stays legal. Delegate / delegate-tuple / unresolved-T members carry the taint.
        if (!IsNonObjectDelegateCapableType(memberType)) return false;
        while (true)
        {
            if (root is IFieldReferenceOperation rf) { root = rf.Instance; continue; }
            if (root is IPropertyReferenceOperation rp) { root = rp.Instance; continue; }
            if (root is IConversionOperation rc) { root = rc.Operand; continue; }
            break;
        }
        return root is IParameterReferenceOperation
            || (root is ILocalReferenceOperation rl && _ctx.CapturingLambdaLocals.Contains(rl.Local));
    }

    protected const string ForeignDelegateReadError =
        "A delegate-typed member read from another behaviour class cannot be stored into "
      + "arrays/objects/members/escaping locals or returned in v2.x Stage 1 — the emitting class "
      + "cannot verify the foreign member never holds a capturing lambda (capture tracking is "
      + "per-class). Invoke it directly (other.cb()) instead, or route the value through the "
      + "owning class.";

    /// <summary>§2.8 round-3 [D]: a delegate-capable member read whose member chain contains a
    /// member of ANOTHER behaviour class. The recipient pre-scan is per-class, so the emitting
    /// class can never see whether the foreign class seeded that member with a capturing lambda
    /// (B legally seeds its own field; A reads other.cb) — conservatism is the only sound option
    /// (§8-3). Loud at escaping stores / array initializers / returns and taints locals it is
    /// copied into; DIRECT invocation (`other.cb()`) stays legal. Same-class cross-instance reads
    /// are NOT foreign: the per-class pre-scan covers every store site of this class's members.</summary>
    protected bool IsForeignDelegateMemberRead(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        ITypeSymbol memberType;
        switch (v)
        {
            case IFieldReferenceOperation fr: memberType = fr.Field.Type; break;
            case IPropertyReferenceOperation pr: memberType = pr.Property.Type; break;
            default: return false;
        }
        if (!IsNonObjectDelegateCapableType(memberType)) return false;
        while (true)
        {
            if (v is IFieldReferenceOperation rf)
            {
                if (IsForeignClassMember(rf.Field)) return true;
                v = rf.Instance; continue;
            }
            if (v is IPropertyReferenceOperation rp)
            {
                if (IsForeignClassMember(rp.Property)) return true;
                v = rp.Instance; continue;
            }
            if (v is IConversionOperation rc) { v = rc.Operand; continue; }
            return false;
        }
    }

    /// <summary>Caller-side §2.8(b) laundering guard: a delegate-CAPABLE invocation RESULT whose
    /// arguments contain a capturing lambda / tainted read is itself tainted-equivalent — an
    /// identity callee (`Func&lt;int&gt; Id(Func&lt;int&gt; x) { return x; }`, or its type-erased flavors
    /// `object IdO(object x)` / `T Id&lt;T&gt;(T x)` at T=object, or a tuple-returning packer) otherwise
    /// launders the capture past both the callee's return guard (a param ref passes it) and this
    /// caller's store guard (the tree-walk does not descend past invocations). Results that are NOT
    /// delegate-capable stay legal: the capture is consumed by the callee (fcd37's
    /// `int Apply(Func&lt;int,int&gt; fn, int v)` — int cannot carry a bundle).</summary>
    protected bool IsTaintedDelegateInvocationResult(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (v is not IInvocationOperation inv) return false;
        if (!IsDelegateCapableType(inv.Type)) return false;
        foreach (var arg in inv.Arguments)
            if (arg.Value != null && ContainsCapturingLambdaOrTaintedRead(arg.Value)) return true;
        return false;
    }

    /// <summary>Tree-walk: does this operation contain a capturing lambda — or a read of a
    /// capture-tainted local / delegate-typed param — anywhere (e.g. buried in a ternary /
    /// coalesce / switch-expression arm)? Invocation subtrees are descended into ONLY via the
    /// delegate-typed-result laundering rule: a non-delegate-typed call consumes any capturing
    /// lambda in its arguments itself (fcd37 stays legal; the callee's own return/store sites
    /// guard any escape), while a delegate-typed result carrying taint in its arguments is the
    /// identity-callee laundering shape and stays tainted.</summary>
    protected bool ContainsCapturingLambdaOrTaintedRead(IOperation op)
    {
        switch (op)
        {
            case IAnonymousFunctionOperation l:
                return _ctx.CaptureAnalyzer.HasCaptures(l);
            // §2.8 round-3 [A]: a method-group reference to a CAPTURING local function is a
            // capturing lambda in different clothing (reached as the child of a delegate creation
            // or buried in ternary/tuple/object-initializer composites). Non-capturing method
            // refs fall through to the children walk (their instance expression may carry taint).
            case IMethodReferenceOperation mr when _ctx.IsCapturingLocalFunction(mr.Method):
                return true;
            case ILocalReferenceOperation lr:
                return _ctx.CapturingLambdaLocals.Contains(lr.Local);
            case IParameterReferenceOperation pr:
                // Delegate-proper (or unresolved-T) param reads only: object/tuple params cannot
                // legally hold a bundle (their call sites are sealed by GuardCaptureEscapeArguments),
                // so plumbing an object param onward stays legal.
                return IsDelegateProperType(pr.Parameter.Type)
                    || ResolveType(pr.Parameter.Type) is ITypeParameterSymbol;
            case IInvocationOperation inv:
                return IsTaintedDelegateInvocationResult(inv);
            // §2.8 round-2: member reads carry taint too (recipient members / param-rooted envelope
            // reads). A non-matching member read falls through to the children walk so taint inside
            // the INSTANCE expression (e.g. `TaintedCall().field`) is still found.
            case IFieldReferenceOperation or IPropertyReferenceOperation when IsLaunderingMemberRead(op):
                return true;
        }
        foreach (var child in op.Children)
            if (child != null && ContainsCapturingLambdaOrTaintedRead(child)) return true;
        return false;
    }

    /// <summary>
    /// Loud-or-correct backstop for NON-direct delegate RHS shapes: a capturing lambda (or tainted-local
    /// read) buried inside a composite delegate-typed expression — ternary, coalesce, switch-expression
    /// arm — evades both the §2.8(a) aliasing recording and the §2.8(b) taint set, so it must be a loud
    /// compile error at delegate-typed stores and value positions. The direct creation shape and simple
    /// reads are excluded: §2.8 recording/taint owns those (a bare tainted-local read stays legal into a
    /// local — F4 propagates the taint — and is rejected target-sensitively at escaping stores).
    /// Capture-free lambdas in composite shapes stay allowed (verified working on the real VM).
    /// </summary>
    protected void GuardBuriedCapturingLambda(IOperation value)
    {
        // §2.8 round-2: the gate is delegate-CAPABLE (object / tuple-with-delegate / unresolved type
        // param), not delegate-typed — tuple literals and object-typed composites erase the delegate
        // typing but carry the bundle all the same (VM-verified laundering).
        if (value == null || !IsDelegateCapableType(value.Type)) return;
        var v = value;
        while (v is IConversionOperation conv) v = conv.Operand;
        if (v is IDelegateCreationOperation) return;
        if (v is ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation
            or IPropertyReferenceOperation or IArrayElementReferenceOperation or IInvocationOperation
            or IInstanceReferenceOperation or ILiteralOperation or IDefaultValueOperation) return;
        if (ContainsCapturingLambdaOrTaintedRead(v))
            throw new System.NotSupportedException(BuriedCapturingLambdaError);
    }

    /// <summary>
    /// §2.8(a): record a capturing lambda stored LONG-LIVED — a delegate field / auto-property / struct
    /// member (self or cross) — for the post-emit aliasing detector. Delegate locals and argument-position
    /// lambdas are intentionally NOT recorded (observationally equivalent today; fcd27/28/37 stay working).
    /// </summary>
    protected void RecordLongLivedLambdaStore(IOperation target, IOperation value)
    {
        if (target?.Type is not INamedTypeSymbol tnt || tnt.DelegateInvokeMethod == null) return;
        if (target is not (IFieldReferenceOperation or IPropertyReferenceOperation)) return;
        if (TryGetLambdaCreation(value, out var lambda) && _ctx.CaptureAnalyzer.HasCaptures(lambda))
            _ctx.RecordLambdaCaptures(lambda);
    }

    /// <summary>
    /// §2.8(b) capture-escape guard for ARRAY-INITIALIZER elements (escaping stores): a capturing
    /// lambda, a tainted-local read, a laundered delegate-typed invocation result, or a
    /// delegate-typed param read escaping into the array is a loud compile error in Stage 1
    /// (capture-free lambdas and method groups are unrestricted).
    /// </summary>
    protected void GuardCaptureEscapeValue(IOperation value)
    {
        GuardBuriedCapturingLambda(value);
        if (IsForeignDelegateMemberRead(value))
            throw new System.NotSupportedException(ForeignDelegateReadError);
        if (IsDirectCapturingLambda(value) || IsCaptureTaintedRead(value)
            || IsTaintedDelegateInvocationResult(value) || IsDelegateParamRead(value)
            || IsLaunderingMemberRead(value))
            throw new System.NotSupportedException(CaptureEscapeError);
    }

    /// <summary>
    /// §2.8(b) capture-escape guard for RETURN values. Differs from the array-initializer guard in
    /// exactly one way: returning a delegate-typed PARAM — or a param-rooted member read of one —
    /// stays legal (`Id(x) { return x; }` is the supported method-group / capture-free flow; the
    /// CALLER's invocation-result taint guards a laundered result), while a capturing lambda, a
    /// tainted-local read, a tainted invocation result (`return Id(x =&gt; x + a);`), or a read of a
    /// capture-RECEIVING member (`return cb;` after `cb = () =&gt; v;` — a zero-arg laundering shape
    /// the caller-side rule cannot see, VM-verified wrong) is loud.
    /// </summary>
    protected void GuardCaptureEscapeReturn(IOperation value)
    {
        GuardBuriedCapturingLambda(value);
        if (IsDirectCapturingLambda(value) || IsCaptureTaintedRead(value)
            || IsTaintedDelegateInvocationResult(value) || IsCaptureReceivingMemberRead(value))
            throw new System.NotSupportedException(CaptureEscapeError);
        // §2.8 round-3 [D]: returning a foreign-class delegate member launders it past the caller's
        // store guards (the zero-arg invocation result is untainted by construction) — loud here.
        // Param-ROOTED member reads stay legal at returns (the caller's invocation-result taint
        // owns those), so this is a separate check, not part of IsLaunderingMemberRead's role here.
        if (IsForeignDelegateMemberRead(value))
            throw new System.NotSupportedException(ForeignDelegateReadError);
    }

    /// <summary>
    /// §2.8(b) capture-escape guard for STORE sites, plus flow-insensitive taint registration.
    /// Array-element and object/object[]-typed targets reject a direct capturing lambda and any
    /// tainted-equivalent read (tainted local, laundered delegate-typed invocation result,
    /// delegate-typed param); a LOCAL target stores legally but is tainted; field/property/
    /// struct-member targets reject tainted-equivalent reads only (a direct capturing lambda into a
    /// delegate field stays legal — the aliasing detector owns that case). Over-rejection after a
    /// method-group reassign is accepted: loud and safe beats a silent wrong value (design §8-3).
    /// </summary>
    protected void GuardCaptureEscapeStore(IOperation target, IOperation value)
    {
        // F3 backstop first: a capturing lambda buried in a non-direct RHS shape is loud regardless
        // of target kind (the recording/taint below can only see the direct shape).
        GuardBuriedCapturingLambda(value);

        bool direct = IsDirectCapturingLambda(value);
        bool foreign = IsForeignDelegateMemberRead(value);
        bool tainted = foreign || IsCaptureTaintedRead(value)
            || IsTaintedDelegateInvocationResult(value) || IsDelegateParamRead(value)
            || IsLaunderingMemberRead(value);
        if (!direct && !tainted) return;

        if (target is IArrayElementReferenceOperation || IsObjectish(target.Type))
            throw new System.NotSupportedException(foreign ? ForeignDelegateReadError : CaptureEscapeError);

        switch (target)
        {
            // Local targets store legally but become tainted. Taint propagates through local-to-local
            // copies too (F4: `var g = f;` then `fs[i] = g;` must stay loud — laundering a tainted
            // read through a copy used to drop the taint).
            case ILocalReferenceOperation localTarget:
                _ctx.CapturingLambdaLocals.Add(localTarget.Local);
                return;
            case IDeclarationExpressionOperation { Expression: ILocalReferenceOperation declLocal }:
                _ctx.CapturingLambdaLocals.Add(declLocal.Local);
                return;
            case IFieldReferenceOperation or IPropertyReferenceOperation when tainted:
                throw new System.NotSupportedException(foreign ? ForeignDelegateReadError : CaptureEscapeError);
            // §2.8 round-2 (H6): a PARAMETER store target is an unguarded escape — an out/ref param
            // write hands the capture to the caller's local with no taint mechanism (VM-verified
            // wrong values), and a by-value param assigned a capturing lambda can launder through
            // the param-ref-return legality. Reaching this arm implies direct || tainted, so reject
            // all RefKinds; capture-free lambda / method-group param defaulting stays legal.
            case IParameterReferenceOperation:
                throw new System.NotSupportedException(CaptureEscapeError);
            // §2.8 round-3 [B]: reaching here, the value is a DIRECT capturing store into a member
            // chain (s.f = () => v, s.inner.f = () => v) — the CONTAINER is now an envelope carrying
            // the bundle, so resolve the chain root: a LOCAL root is tainted (whole-struct copies /
            // returns / array stores of it must go loud — VM-verified silent aliasing otherwise);
            // a PARAM root is loud (the by-value copy launders out via the legal param-ref return,
            // VM-verified); an ARRAY-ELEMENT root is loud (§2.8 round-4 [K1]/[K4]: arr[i].f = () => v
            // mints N live bundles from ONE lambda site with no local to taint — unrepresentable in
            // the taint model, and whole-element reads cannot be made loud, VM-verified silent
            // aliasing); a chain through ANOTHER behaviour class's member is loud (per-class
            // pre-scan cannot make that class's reads loud — round-2 armor, now chain-deep);
            // a `this`-rooted chain stays legal — the pre-scan records every chain member.
            case IFieldReferenceOperation or IPropertyReferenceOperation:
            {
                var root = target;
                while (true)
                {
                    if (root is IFieldReferenceOperation rf)
                    {
                        if (IsForeignClassMember(rf.Field))
                            throw new System.NotSupportedException(CaptureEscapeError);
                        root = rf.Instance; continue;
                    }
                    if (root is IPropertyReferenceOperation rp)
                    {
                        if (IsForeignClassMember(rp.Property))
                            throw new System.NotSupportedException(CaptureEscapeError);
                        root = rp.Instance; continue;
                    }
                    if (root is IConversionOperation rc) { root = rc.Operand; continue; }
                    break;
                }
                if (root is IParameterReferenceOperation || root is IArrayElementReferenceOperation)
                    throw new System.NotSupportedException(CaptureEscapeError);
                if (root is ILocalReferenceOperation rootLocal)
                    _ctx.CapturingLambdaLocals.Add(rootLocal.Local);
                return;
            }
            default:
                return;
        }
    }

    /// <summary>Member of a CLASS other than the one being emitted (or its bases) — the per-class
    /// recipient pre-scan cannot make that class's reads loud. Struct members are not foreign:
    /// struct values cross call boundaries as params, where the param-rooted member-read taint
    /// applies in the receiving method regardless of class.</summary>
    protected bool IsForeignClassMember(ISymbol member)
    {
        var ct = member?.ContainingType;
        if (ct == null || ct.TypeKind != TypeKind.Class) return false;
        for (var t = _ctx.ClassSymbol; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, ct)) return false;
        return true;
    }

    // ── Delegate bridge resolution ──

    /// <summary>Resolve delegate creation to bridge name, FuncRef, and target instance.</summary>
    protected (string bridgeName, CLeaf funcRef, CLeaf targetInstance) ResolveDelegateBridge(IDelegateCreationOperation op)
    {
        IMethodSymbol targetMethod = null;
        CLeaf targetInstance = null;
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
            // Cross dispatch (SendCustomEvent) needs an EXPORTED entry point, so a locally
            // registered NON-exported function must not shadow the planner layout: an overridden
            // base member referenced through a base-TYPED receiver binds the BASE symbol, whose
            // local registration is the internal base-instance COPY (`__N_get_P`, emitted for
            // `base.X` calls only) — dispatching its never-exported name silently no-ops and the
            // stale return var reads 0/null (VM-verified: a virtual auto-property override read
            // through a base reference returned default). The planner normalizes the override
            // chain to the chain-ROOT layout, which every program in the class family exports
            // (override layouts are reused from the base), so reads/writes through ANY symbol in
            // the chain dispatch to the ONE exported accessor over the one backing store. Symbols
            // with no planned layout (local functions, generic specializations) keep the local
            // registration; exported local registrations are byte-identical to their layout.
            MethodLayout overrideMl = null;
            if (_methodFunctions.TryGetValue(target, out var localFunc) && localFunc.ExportName == null
                && ExternResolver.IsUdonSharpBehaviour(target.ContainingType))
                overrideMl = _planner.TryGetCalleeLayout(target);
            if (overrideMl != null)
                return (overrideMl.ExportName, overrideMl.ParamIds.ToArray(), overrideMl.ReturnId);

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
        {
            // Same non-exported-shadow rule as GetCalleeLayout: a base-instance copy's return var
            // is never written by the exported override, so cross reads must use the layout id.
            if (_methodFunctions.TryGetValue(target, out var localFunc) && localFunc.ExportName == null
                && ExternResolver.IsUdonSharpBehaviour(target.ContainingType)
                && _planner.TryGetCalleeLayout(target) is MethodLayout overrideMl)
                return overrideMl.Returns.ToArray();
            return slots;
        }
        var ml = _planner.GetCalleeLayout(target);
        return ml.Returns.ToArray();
    }


    /// <summary>
    /// Call an internal function via CoreBuilder.InternalCall.
    /// Returns the result CValue — this is an expression only, NOT emitted to the IR.
    /// For void calls (e.g. property setters), wrap with <c>EmitExprStmt()</c> to add to the IR.
    /// </summary>
    protected CLeaf EmitCallToMethod(IMethodSymbol target, List<CLeaf> args)
    {
        if (!_methodFunctions.TryGetValue(target, out var func))
            throw new InvalidOperationException($"No CFunction registered for method '{target.Name}'");
        var retType = func.ReturnType ?? "SystemVoid";

        // Recursion-cycle edge: the callee can re-enter the current method and clobber its param/local fields
        // and shared scratch slots (Udon's flat heap shares them across frames). Record the edge + the named
        // frame fields to save; the post-coalesce InsertRecursionSpills pass wraps the call with spill/reload
        // of those fields PLUS only the slots live across the call — bounded under A-normal form, where an
        // emit-time total-spill of every (now numerous) scratch slot would overflow the software stack.
        if (IsRecursiveEdge(_currentMethod, target))
        {
            _ctx.EnsureRecursionStack();
            _builder.CurrentFunction.RecursiveCalleeNames.Add(func.Name);
            AccumulateRecursionSpillFields(_builder.CurrentFunction);
        }

        return InternalCall(func.Name, args, retType);
    }

    /// <summary>True when the dispatch invocation at <paramref name="dispatchOp"/> can re-enter the
    /// containing function (design §4.3: containing function on a synthetic-edge-inclusive SCC cycle
    /// AND the dispatch is non-tail — pre-computed syntax-keyed by BuildRecursionInfo). When true,
    /// also registers the frame: ensures the recursion stack and accumulates the named frame fields,
    /// so InsertRecursionSpills wraps the flagged dispatch arms with the spill/reload.</summary>
    protected bool MarkReentrantDispatch(IOperation dispatchOp)
    {
        if (_ctx.ReentrantDispatchSites == null || dispatchOp?.Syntax == null
            || !_ctx.ReentrantDispatchSites.Contains(dispatchOp.Syntax))
            return false;
        _ctx.EnsureRecursionStack();
        AccumulateRecursionSpillFields(_builder.CurrentFunction);
        return true;
    }

    /// <summary>Accumulate the UNION of in-scope frame fields across every spill site: a later site has
    /// more locals in scope than an earlier one, and the post-pass uses a single field set for all sites.
    /// Over-spilling a not-yet-assigned local at an earlier site is inert (its garbage is saved/restored).</summary>
    void AccumulateRecursionSpillFields(CFunction cf)
    {
        foreach (var f in CollectRecursionSpillFields())
        {
            bool seen = false;
            foreach (var e in cf.RecursionSpillFields) if (e.Name == f.Name) { seen = true; break; }
            if (!seen) cf.RecursionSpillFields.Add(f);
        }
    }

    // The named heap fields that must survive a recursive re-entry of the current method: its parameters,
    // the struct receiver, and its in-scope frame locals (NOT captured locals — those are shared by reference,
    // so the flat-heap sharing is the correct closure behaviour). The SLOTS to spill are computed per call
    // site from post-coalesce liveness by InsertRecursionSpills, so they are not collected here.
    List<(string Name, string Type)> CollectRecursionSpillFields()
    {
        var fields = new List<(string, string)>();
        var seen = new HashSet<string>();
        void AddField(string id)
        {
            if (id == null || !seen.Add(id)) return;
            var t = _ctx.GetFieldType(id);
            if (t != null) fields.Add((id, t));
        }
        if (_currentMethod != null && _methodParamVarIds.TryGetValue(_currentMethod, out var pids))
            for (int i = 0; i < pids.Length; i++)
            {
                // A ref/out param aliases the caller's storage and a recursive call threads that SAME storage,
                // so its mutations must PERSIST across the call. Saving+restoring it would discard the
                // recursive levels' writes (only the outermost would reach the caller). Value params are
                // per-frame and must still be spilled. (diff-fuzz wave 3 #3)
                if (i < _currentMethod.Parameters.Length
                    && _currentMethod.Parameters[i].RefKind is RefKind.Ref or RefKind.Out)
                    continue;
                AddField(pids[i]);
            }
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

        return fields;
    }
}
