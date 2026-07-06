using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public abstract partial class HandlerBase
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
    protected IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _typeParamMap => _ctx.TypeParamMap;
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
    protected EmittedValue VisitEmittedValue(IOperation op)
        => new EmittedValue(VisitExpression(op), _ctx.Boundary.ClassifyValue(op));
    protected CLeaf EmitPatternCheck(CLeaf value, ITypeSymbol valueType, IPatternOperation pattern)
        => _ctx.EmitPatternCheck(value, valueType, pattern);

    // A `checked` context asks the runtime to trap integer overflow, but the Udon VM has no overflow
    // trap — the arithmetic silently wraps where C# would throw OverflowException. `unchecked`/default
    // wrapping IS USugar's behavior (C#-correct), so only an explicit `checked` (IsChecked==true) rejects.
    protected static void RejectChecked(bool isChecked)
    {
        if (isChecked)
            throw new NotSupportedException(
                "A 'checked' context is not supported: the Udon VM has no integer-overflow trap, so "
                + "overflow silently wraps. Remove 'checked' (wrapping is the only available behavior) "
                + "or guard the range yourself.");
    }


    // ── Type resolution ──
    protected string GetUdonType(ITypeSymbol type) => ExternResolver.GetUdonTypeName(type, _ctx.TypeParamMap);
    protected TypeClassifierContext TypeCtx => new TypeClassifierContext(_ctx.TypeParamMap);
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

    // Extern-owner resolution choke point (B55/B59/B60 + audit): an INHERITED instance member is
    // registered in Udon under the RECEIVER's own static type, not its declaring base (System.Object /
    // ValueType / Enum / Reflection.MemberInfo / UnityEngine.Component|Behaviour). Every site that builds
    // an instance member's extern owner (getter/this-getter/indexer/property-set/field-set/method-call)
    // routes through here. Array receivers are excluded — they keep their own owner logic (SystemArray for
    // .Length, element-typed array otherwise). A user-struct (object[]-emulated) receiver of an inherited
    // Object/ValueType member has no extern and ValueType semantics cannot be emulated → the designed loud
    // reject (B60), matching the type-parameter case.
    protected ITypeSymbol ResolveExternOwnerType(ITypeSymbol memberContainingType, ITypeSymbol receiverType, string memberName)
    {
        // B65: a type-parameter receiver (T : SomeBase, e.g. `where T : Behaviour`) carries an inherited
        // member's extern under its CONCRETE leaf — Udon registers `.enabled` per concrete type, never under
        // the abstract constraint base — so substitute the receiver through the ambient monomorphization map
        // before resolving the owner. A receiver STATICALLY typed as the abstract base itself (non-generic
        // `Behaviour b`) does not substitute, so it stays the designed loud reject (no such extern owner).
        receiverType = ResolveType(receiverType);
        if (receiverType is not INamedTypeSymbol recv
            || SymbolEqualityComparer.Default.Equals(memberContainingType, recv))
            return memberContainingType;
        if (EmitPolicy.IsAggregateType(recv))
            throw new NotSupportedException(
                $"'{memberName}' on user-defined struct '{recv.Name}' is not supported: Udon has no extern "
                + "for it and C#'s ValueType semantics (field-wise Equals, type-name ToString) cannot be "
                + "emulated. Compare/format the struct's fields directly instead.");
        return recv;
    }

    // Layer-2 runtime-type-test choke point (is / switch / as). ExternResolver.GetUdonTypeName is
    // non-injective: it folds many distinct CLR types onto one Udon runtime tag (every delegate/struct/
    // tuple/array-of-those + object[] → SystemObjectArray; UdonSharpBehaviour + every derived type + every
    // user interface → IUdonEventReceiver; a user enum → its underlying int; Nullable<T> → a box). A
    // runtime type test against such a type CANNOT discriminate it — it matches ANY same-tag value and
    // silently takes the wrong branch. Reject loudly (design §8-3); bare `object` and uniquely-tagged
    // SDK/native types stay distinguishable and compile.
    protected CLeaf EmitTypeCheck(CLeaf valueVal, ITypeSymbol targetType)
    {
        if (!ExternResolver.IsRuntimeDistinguishable(targetType, _ctx.TypeParamMap))
        {
            var resolvedTarget = ResolveType(targetType);
            ClassAbi.RejectRuntimeTypeTest(resolvedTarget);
            var disp = resolvedTarget.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var hint = resolvedTarget is INamedTypeSymbol dlgTarget && dlgTarget.DelegateInvokeMethod != null
                ? " (Udon represents every delegate as one runtime type, so it cannot tell delegate signatures "
                  + "apart and would match any delegate, then read the wrong argument/return channel)"
                : "";
            throw new NotSupportedException(
                $"Runtime type test against '{disp}' is not supported: Udon collapses it and several distinct "
                + "types onto one runtime type tag, so an 'is'/'switch'/'as' test cannot tell them apart and "
                + "would match the wrong value" + hint + ". Keep the value typed as its static type instead of "
                + "recovering it with a runtime type test.");
        }
        // The type token is baked through the shared choke point (B51 silent-class armor: an unresolved
        // type parameter would bake a null System.Type constant no validator catches → loud reject there).
        return ExternCall(
            "SystemType.__IsInstanceOfType__SystemObject__SystemBoolean",
            new List<CLeaf> { ConstTypeToken(targetType), valueVal },
            "SystemBoolean");
    }

    // The single place a System.Type CONSTANT (type token) is baked — `o is T`, `typeof(T)`, and the
    // GetComponent<T> type-token arg all route here. A SystemType const is a heap constant no validator
    // checks, so an UNRESOLVED type parameter would silently resolve to a null System.Type and NRE at
    // runtime (B51 silent class) — reject loudly instead. The IUdonEventReceiver collapse tag is not
    // VM-resolvable as a token; the concrete UdonBehaviour type is (GetComponent<T>'s prior remap).
    protected CLeaf ConstTypeToken(ITypeSymbol typeSymbol)
    {
        if (ResolveType(typeSymbol) is ITypeParameterSymbol unresolvedTp)
            throw new NotSupportedException(
                $"A System.Type token for unresolved type parameter '{unresolvedTp.Name}' cannot be emitted: "
                + "its type argument did not reach this emit site (a generic-instantiation map gap). The token "
                + "would bake a null System.Type constant and fault at runtime.");
        var name = GetUdonType(typeSymbol);
        if (name == "VRCUdonCommonInterfacesIUdonEventReceiver") name = "VRCUdonUdonBehaviour";
        return Const(name, "SystemType");
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
    /// delegate-dispatch arm that can re-enter the containing function (design §4.3). preSpillStmts:
    /// wave-12 r2 [V1], see CExternCall.PreSpillStmts (cross setter copy-ins inside the wrap).</summary>
    protected void EmitExternVoid(string sig, List<CLeaf> args, bool reentrant = false, int preSpillStmts = 0)
        => _builder.EmitExternVoid(ResolveExtern(sig), args, reentrant, preSpillStmts);

    /// <summary>Create an internal call expression.</summary>
    protected CSlotRef InternalCall(string funcName, List<CLeaf> args, string retType, bool tailSpared = false)
        => _builder.InternalCall(funcName, args, retType, tailSpared);

    /// <summary>Emit a cross-behaviour call. Single-return → materialized to a scratch slot (returns the
    /// leaf); void or multi-return → side-effecting statement (returns null). reentrant: wave-12 r2
    /// [V1] — this dispatch can re-enter the containing function (see TryMarkReentrantCrossDispatch).</summary>
    protected CSlotRef CrossCall(CLeaf instance, string eventName,
        List<(string, CLeaf)> parameters, IReadOnlyList<ReturnSlot> returns, string retType,
        bool reentrant = false)
        => _builder.CrossCall(instance, eventName, parameters, returns, retType, reentrant);

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

    /// <summary>Default value for a Udon value type (0 / false). Used for `default(T)`-style fills.</summary>
    protected CLeaf EmitValueTypeDefault(string udonType)
        => Const(EmitPolicy.ParseConstValue(udonType, udonType == "SystemBoolean" ? "False" : "0"), udonType);

    /// <summary>Lifted binary operator on Nullable&lt;T&gt; (null propagation), from already-evaluated operand
    /// values. Arithmetic yields T? (null unless both present); relational yields bool (false if either null);
    /// equality yields bool (both-null is equal). Shared by <c>OperatorHandler</c> and compound assignment.</summary>
    protected CLeaf EmitLiftedBinaryCore(
        CValue leftVal, bool leftNullable, ITypeSymbol ltUnderlying,
        CValue rightVal, bool rightNullable, ITypeSymbol rtUnderlying,
        Microsoft.CodeAnalysis.Operations.BinaryOperatorKind kind, IMethodSymbol operatorMethod, ITypeSymbol resultType)
        => NullableAbi.EmitLiftedBinaryCore(_builder,
            leftVal, leftNullable, ltUnderlying,
            rightVal, rightNullable, rtUnderlying,
            kind, operatorMethod, resultType, _compilation.GetSpecialType(SpecialType.System_Int32),
            _ctx.AllocTemp, EmitAssign, SlotRef, GetUdonType, ResolveType,
            (boxed, underlying) => NullableAbi.PromoteBoxedToInt32(_builder, boxed, underlying,
                _compilation.GetSpecialType(SpecialType.System_Int32), GetUdonType),
            EmitNarrowingConvert);

    // ── Extern resolution ──

    static readonly string[] UnityEngineComponentBaseTypes = new[]
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
        // Wave-12 [V3]: the owner fallback exists for Component-hierarchy receivers whose leaf type
        // lacks a direct extern (Udon registers e.g. __GetComponent on UnityEngineComponent only). A
        // System.* receiver can never be Component-derived, so substituting one of these base types
        // mechanically laundered an invalid System-typed signature into an unrelated Component extern
        // (VM-proven: boxed value-type Equals/GetHashCode/ToString adopted UnityEngineComponent.__*).
        // Non-UnityEngine owners are equally ineligible; let the invalid signature through unchanged
        // so the validator rejects it loudly instead of adopting an unrelated Component extern.
        if (!containingType.StartsWith("UnityEngine", System.StringComparison.Ordinal))
            return externSig;
        var rest = externSig.Substring(dotIdx);
        foreach (var baseType in UnityEngineComponentBaseTypes)
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
        // !IsDefinition (not IsGenericMethod): a constructed spec of ANY kind — a generic method
        // instantiation, a member of a constructed generic struct (feature G; the method itself need
        // not be generic), or both. IsGenericMethod alone misses the containing-type-generic case.
        if (_currentMethod != null && param.ContainingSymbol is IMethodSymbol paramMethod
            && !_currentMethod.IsDefinition
            && SymbolEqualityComparer.Default.Equals(paramMethod, _currentMethod.OriginalDefinition)
            && _methodParamVarIds.TryGetValue(_currentMethod, out var specParamIds)
            && param.Ordinal < specParamIds.Length)
            return specParamIds[param.Ordinal];
        // Wave-9 round-7 [Y3]: a hoisted lambda/local-function body inside a GENERIC method (or,
        // feature G, a method on a generic struct) reads the enclosing method's parameter. The
        // reference binds the generic DEFINITION's parameter symbol (the closure body is the
        // definition's operation tree) while the param heap vars are registered under the
        // monomorphized SPEC — and _currentMethod here is the closure, not the spec, so neither arm
        // above fires. A capturing closure pins its generic to a single instantiation ([X6] round 5
        // reject), so FirstGenericSpec is the exact owner. No IsGenericMethod pre-filter: the
        // dictionary lookup on OriginalDefinition is itself the correct, sufficient gate (same
        // reasoning as the EmitMethod closure-map walk-up).
        if (param.ContainingSymbol is IMethodSymbol genericOwner
            && _ctx.FirstGenericSpec.TryGetValue(genericOwner.OriginalDefinition, out var ownerSpec)
            && _methodParamVarIds.TryGetValue(ownerSpec, out var ownerSpecIds)
            && param.Ordinal < ownerSpecIds.Length)
            return ownerSpecIds[param.Ordinal];
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
            // Stage 2 §4.1: captured locals/params live in env records — raw (no-clone) loads read
            // the env cell directly so mutation hits the live storage.
            ILocalReferenceOperation lr when _ctx.TryGetEnvBinding(lr.Local, out _)
                => EnvEmit.Read(_builder, _ctx, lr.Local,
                       EmitPolicy.IsAggregateType(lr.Type) ? "SystemObjectArray" : GetUdonType(lr.Type)),
            ILocalReferenceOperation lr when _localBindings.TryGetValue(lr.Local, out var b)
                => LoadField(b.Id, EmitPolicy.IsAggregateType(lr.Type) ? "SystemObjectArray" : GetUdonType(lr.Type)),
            IParameterReferenceOperation pr when _ctx.TryGetEnvBinding(pr.Parameter, out _)
                => EnvEmit.Read(_builder, _ctx, pr.Parameter,
                       EmitPolicy.IsAggregateType(pr.Type) ? "SystemObjectArray" : GetUdonType(pr.Type)),
            IParameterReferenceOperation pr
                => LoadParam(pr.Parameter),
            // Inside a struct method/ctor, `this` is the receiver object[] param, not the Behaviour.
            IInstanceReferenceOperation when _ctx.CurrentStructReceiverParamId != null
                => LoadField(_ctx.CurrentStructReceiverParamId, "SystemObjectArray"),
            // Aggregate field as a RECEIVER (e.g. `o.inner.x`, `this.structField.x`) must NOT be cloned —
            // the access/mutation has to hit the live storage. (Value reads clone in VisitFieldReference.)
            IFieldReferenceOperation fr when EmitPolicy.IsAggregateType(fr.Type)
                => ReadAggregateFieldRaw(fr),
            // Aggregate array element as a RECEIVER (`arr[i].x = …`) likewise hits live storage, no clone.
            IArrayElementReferenceOperation ae when EmitPolicy.IsAggregateType(ae.Type)
                => ReadArrayElementRaw(ae),
            _ => VisitExpression(instance), // method return, field on this, etc. — fresh or already raw
        };
    }

    // ── Stage 2 §4.1: captured-variable storage ──

    /// <summary>Bind a freshly declared local: env-bound (captured) locals get NO flat field — the
    /// caller must route the initial value through <see cref="EnvEmit.Write"/> (returns false);
    /// ordinary locals get their flat field + LocalBindings entry as before (returns true, flat id
    /// in <paramref name="flatId"/>).</summary>
    protected bool BindLocal(ILocalSymbol local, string udonType, out string flatId)
    {
        if (_ctx.TryGetEnvBinding(local, out _))
        {
            flatId = null;
            return false;
        }
        flatId = _ctx.DeclareLocal(local.Name, udonType);
        _localBindings[local] = new EmitContext.LocalBinding(flatId);
        return true;
    }

    /// <summary>Env arm shared by every assignment write path: when the target is a captured
    /// local/param, store the value into its env cell and return true; false = caller proceeds with
    /// its flat-field path. Aggregate value semantics are the CALLER's job (pass an already-cloned
    /// value where the flat path would clone).</summary>
    protected bool TryEmitEnvStore(IOperation target, CLeaf value)
    {
        ISymbol sym = target switch
        {
            ILocalReferenceOperation lr => lr.Local,
            IParameterReferenceOperation pr => pr.Parameter,
            _ => null,
        };
        if (sym == null || !_ctx.TryGetEnvBinding(sym, out _)) return false;
        EnvEmit.Write(_builder, _ctx, sym, value);
        return true;
    }

    /// <summary>Round-7 follow-up [Q5]: the this-FIELD whose storage a ref/out argument aliases, or
    /// null when the argument's storage is not on this program's heap-named fields (locals, params,
    /// other-behaviour members, fresh values). Walks struct member chains and array-element links to
    /// the root: `ref f`, `ref s.v` (s a this-field struct), and `ref arr[0]` (arr a this-field) all
    /// resolve to the root field — the storage the callee can also reach directly through this.</summary>
    protected static IFieldSymbol TryGetThisRootedRefStorage(IOperation arg)
    {
        var op = arg;
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c:
                    op = c.Operand; continue;
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fr when !fr.Field.IsStatic:
                    return fr.Field.OriginalDefinition;
                case IFieldReferenceOperation fr2 when fr2.Instance != null
                    && fr2.Field.ContainingType?.IsValueType == true:
                    op = fr2.Instance; continue; // struct member chain → resolve its root
                case IArrayElementReferenceOperation ae:
                    op = ae.ArrayReference; continue; // element storage roots at the array reference
                default:
                    return null; // local / param / cross-behaviour member / fresh value
            }
        }
    }

    /// <summary>Round-8 [R4]: the storage ROOT a ref/out argument resolves to — a local, a
    /// parameter, or a this-field (struct member chains and array-element links walk to the root,
    /// mirroring the [Q5] walk above). Two ref/out arguments of ONE call sharing a root are two
    /// independent heap vars under the copy-in/copy-back convention: the callee never observes the
    /// alias and the last copy-back silently wins (DiffFuzz: M(ref a, ref a) with x=x+1;y=y+3
    /// ref=5 vs VM 4, local and this-field flavors). Null = cross-behaviour member / fresh value.</summary>
    protected static ISymbol TryGetRefStorageRoot(IOperation arg)
    {
        var op = arg;
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c:
                    op = c.Operand; continue;
                case IDeclarationExpressionOperation de:
                    op = de.Expression; continue; // out var x → the declared local
                case ILocalReferenceOperation lr:
                    return lr.Local;
                case IParameterReferenceOperation pr:
                    return pr.Parameter.OriginalDefinition;
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fr when !fr.Field.IsStatic:
                    return fr.Field.OriginalDefinition;
                case IFieldReferenceOperation fr2 when fr2.Instance != null
                    && fr2.Field.ContainingType?.IsValueType == true:
                    op = fr2.Instance; continue; // struct member chain → resolve its root
                case IArrayElementReferenceOperation ae:
                    op = ae.ArrayReference; continue; // element storage roots at the array reference
                default:
                    return null;
            }
        }
    }

    /// <summary>Design §3.3, Q-S2 (feature B): walks a WRITE target's receiver chain (array-element /
    /// struct-member links, mirroring TryGetRefStorageRoot's walk) down to its storage root, returning
    /// the static readonly FIELD when that root is a per-program-materialized static readonly field —
    /// each behaviour instance holds its own copy of that field, so a write through an array element
    /// or aggregate member (`A[i] = v`, `A[i].x = v`) would silently diverge per instance instead of
    /// being shared as C# expects (the readonly-ness only forbids reassigning the FIELD itself; its
    /// referenced contents are still mutable in real C#). Any other root (local/param/this-field/
    /// cross-behaviour/fresh value) returns null — no hazard.</summary>
    protected static IFieldSymbol TryGetStaticReadonlyWriteThroughRoot(IOperation target)
    {
        var op = target;
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c:
                    op = c.Operand; continue;
                case IArrayElementReferenceOperation ae:
                    op = ae.ArrayReference; continue; // element storage roots at the array reference
                case IFieldReferenceOperation { Instance: null } fr when fr.Field.IsStatic && fr.Field.IsReadOnly:
                    return fr.Field.OriginalDefinition;
                case IFieldReferenceOperation fr2 when fr2.Instance != null
                    && fr2.Field.ContainingType?.IsValueType == true:
                    op = fr2.Instance; continue; // struct member chain → resolve its root
                default:
                    return null;
            }
        }
    }

    /// <summary>Loud reject for a write-through mutation rooted at a static readonly field (§3.3, R5).
    /// A no-op when <paramref name="target"/> isn't rooted there.</summary>
    protected static void RejectStaticReadonlyWriteThrough(IOperation target)
    {
        if (TryGetStaticReadonlyWriteThroughRoot(target) is not { } root) return;
        throw new NotSupportedException(
            $"cannot mutate the contents of a static readonly field '{root.Name}'; each behaviour instance "
            + "holds its own copy, so the write would not be shared as in C#. Use an instance field.");
    }

    /// <summary>Round-8 [R1]/[R7]: true when a non-readonly struct member invocation's receiver
    /// chain is READONLY in C# and so runs on a defensive copy the emulation must reproduce.
    /// Two flavors (both DiffFuzz-proven):
    /// [R1] (corrects the round-7 [Q4] over-clone) — the chain reaches a foreach ITERATION variable
    /// through AT LEAST ONE value-typed field link. Roslyn does NOT defensive-copy a DIRECT member
    /// invocation on the loop local (ldloca on the local; the readonly-ness only forbids assignment
    /// — direct mutating call ref=1112), but member ACCESS on the readonly variable yields a value,
    /// so a chain through a value-typed FIELD link operates on a copy (nested s.inner.Bump()
    /// ref=102).
    /// [R7] — the chain contains a READONLY field link anywhere (`readonly P9T rs; rs.Bump();`
    /// mutated live storage: VM 20 vs CLR ref=0; nested ro.inner.Bump() VM 10 vs ref=0). Unlike the
    /// loop local, a readonly FIELD defensive-copies even on direct invocation (ldfld is a value).
    /// A chain that passes through an ARRAY ELEMENT stops in both flavors (arrays are
    /// reference-typed: the CLR mutates through them even from a readonly variable).</summary>
    protected bool ReceiverNeedsDefensiveCopy(IOperation instance)
    {
        var op = instance;
        bool sawValueFieldLink = false;
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c:
                    op = c.Operand; continue;
                case ILocalReferenceOperation lr:
                    return sawValueFieldLink && _ctx.ForeachIterationLocals.Contains(lr.Local);
                case IFieldReferenceOperation fr when fr.Field.IsReadOnly:
                    return true; // [R7] readonly field link → the access chain is a value
                case IFieldReferenceOperation fr when fr.Instance != null
                    && fr.Field.ContainingType?.IsValueType == true:
                    sawValueFieldLink = true;
                    op = fr.Instance; continue;
                default:
                    return false; // array element / param / call result / this — live or fresh storage
            }
        }
    }

    /// <summary>Read an aggregate array element as the raw stored object[] (no clone), for receiver access.</summary>
    protected CLeaf ReadArrayElementRaw(IArrayElementReferenceOperation ae)
    {
        // Wave-14 ndimaccess lens: an N-dim aggregate-array element used as a RECEIVER (`arr[i,j].X += 1`,
        // `arr[i,j].Item1`) reaches this method (EmitPolicy.IsAggregateType(ae.Type) at the
        // LoadInstanceRaw dispatch site), but this method was written before feature N and always used
        // Indices[0] alone against the BUNDLE array directly — every OTHER array-index site
        // (ArrayHandler.VisitArrayElementReference, HandlerBase.PrepareArrayElementSet,
        // AssignmentHandlerBase's capture/write-back arms, InvocationHandler.Extern's ref/out prepare)
        // already special-cases Indices.Length>1 via PrepareNdimAccess; this receiver-access path was the
        // one gap. Pre-fix, a single stray index read bundle[idx] directly (the bundle's OWN 1+r slots —
        // flat backing at [0], boxed dim lengths at [1..r] — not the logical element), corrupting the
        // struct-field mutation (VM-proven: `arr[idx,1].X += 10; sum += arr[i,j].X + arr[i,j].Y;` faulted
        // with a heap type mismatch reading a dimension-length int as if it were the struct's object[]).
        if (ae.Indices.Length > 1)
        {
            var ndimType = (IArrayTypeSymbol)ae.ArrayReference.Type;
            var plan = PrepareNdimAccess(ae.ArrayReference, ae.Indices, ndimType);
            return EmitNdimReadFromPlan(ae, plan, GetUdonType(ndimType.ElementType));
        }
        var arrayVal = VisitExpression(ae.ArrayReference);
        var arrSym = ae.ArrayReference.Type as IArrayTypeSymbol;
        var arrType = GetArrayType(arrSym);
        var elemType = GetArrayElemType(arrSym);
        var idxVal = ResolveArrayIndex(arrayVal, arrType, ae.Indices[0]);
        return ExternCall(ExternResolver.BuildArrayGetSignature(arrType, elemType), new List<CLeaf> { arrayVal, idxVal }, "SystemObject");
    }

    /// <summary>Lower a single array-element index operand to its resolved SystemInt32 position,
    /// Index-aware: `arr[^k]` (a from-end IUnaryOperation of type System.Index) becomes
    /// `arr.Length - k`, matching C#'s <c>new Index(k, fromEnd: true)</c> resolved against the
    /// array's length at access time; any other operand is a plain int index. This is the SINGLE
    /// lowering shared by every array-index site — read (ArrayHandler), receiver reads
    /// (ReadArrayElementRaw), and both write paths (PrepareArrayElementSet, CaptureLValue's array
    /// arm, TryPrepareRefOutArg's array-element ref/out leg) — so read and write can't drift on
    /// which Index shapes are supported (B40: the write paths used to call VisitExpression
    /// directly, which cannot lower `^k` and threw an unrelated "Unsupported unary operator: Hat").</summary>
    protected CLeaf ResolveArrayIndex(CLeaf arrayVal, string arrayType, IOperation indexOp)
        => indexOp is IUnaryOperation { Type: { Name: "Index" } } fromEnd
            ? EmitIndexFromEnd(arrayVal, arrayType, fromEnd.Operand)
            : VisitExpression(indexOp);

    /// <summary>`arr[^k]` → `arr.Length - k`. <paramref name="arrayVal"/> must already be a
    /// single-assignment scratch leaf (read once here); <paramref name="operand"/> is the `k` in `^k`.</summary>
    protected CLeaf EmitIndexFromEnd(CLeaf arrayVal, string arrayType, IOperation operand)
    {
        var lenVal = ExternCall($"{arrayType}.__get_Length__SystemInt32", new List<CLeaf> { arrayVal }, "SystemInt32");
        var nVal = VisitExpression(operand);
        return ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", new List<CLeaf> { lenVal, nVal }, "SystemInt32");
    }

    /// <summary>Read an aggregate-typed field as the raw stored object[] (no clone): a nested element via
    /// __Get__, or a this.field directly. Used for receiver access; value reads add a clone on top.</summary>
    protected CLeaf ReadAggregateFieldRaw(IFieldReferenceOperation fr)
    {
        // B80: the container may be a v1 CLASS too (a struct field on a class — `w.P.Ref = x`). Reading the
        // struct-field slot RAW yields the LIVE nested object[] stored in the class bundle (no clone), so a
        // chained write lands in the class's storage, not a discarded copy. Gated on IsObjectArrayEmulated
        // (Category-A: object[] slot resolution); the caller only asks for a raw receiver, never a value read.
        if (fr.Instance != null && fr.Instance.Type is INamedTypeSymbol cont && EmitPolicy.IsObjectArrayEmulated(cont)
            && _ctx.GetAggregateLayout(cont).TryGetIndex(fr.Field, out var idx))
            return AggregateAbi.ReadSlot(_builder, LoadInstanceRaw(fr.Instance), idx, "SystemObject");
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
    protected void AssignToLValue(IOperation target, CLeaf value,
        Dictionary<IOperation, System.Action<CLeaf>> preparedStores = null)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    // Stage 2 §4.1: captured declaration target → env cell, no flat field.
                    if (_ctx.TryGetEnvBinding(localRef.Local, out _))
                    {
                        EnvEmit.Write(_builder, _ctx, localRef.Local, value);
                        break;
                    }
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _ctx.DeclareLocal(localRef.Local.Name, udonType);
                    _localBindings[localRef.Local] = new EmitContext.LocalBinding(localId);
                    EmitStoreField(localId, value);
                }
                else if (declExpr.Expression is ITupleOperation declTuple)
                    AssignNestedTupleElements(declTuple, value, preparedStores);
                break;

            // A nested deconstruction target tuple, e.g. the (b,c) in `var (a, (b,c)) = …`. `value` is the
            // object[]-emulated nested tuple; read each element and recurse (handles arbitrary nesting depth).
            case ITupleOperation nestedTuple:
                AssignNestedTupleElements(nestedTuple, value, preparedStores);
                break;

            case ILocalReferenceOperation existingLocal:
                // Stage 2 §4.1: captured local target → env cell.
                if (TryEmitEnvStore(existingLocal, value))
                {
                    break;
                }
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

            // Wave-9 round-7 [Y2]/[Y4]-[Y10]: field lvalues with receiver legs (struct member chains,
            // struct-array-element receivers, cross-behaviour/extern variable receivers) route through
            // the shared TryPrepareFieldSet path. Deconstruction callers supply pre-RHS legs via
            // preparedStores (C# evaluates every target's component expressions BEFORE the RHS —
            // store-time legs landed writes in the WRONG CELL when a leg read state the RHS mutated);
            // other callers (`ref p.v` copy-back, nested-tuple legs) prepare at the store point,
            // byte-identical to the old inline aggregate arm. Cross-behaviour / extern field targets
            // were a loud "Unsupported l-value target" before this arm.
            case IFieldReferenceOperation prepFieldRef
                when preparedStores != null && preparedStores.TryGetValue(prepFieldRef, out var preparedFieldStore):
                preparedFieldStore(value);
                break;

            case IFieldReferenceOperation lateFieldRef when TryPrepareFieldSet(lateFieldRef) is { } lateFieldStore:
                lateFieldStore(value);
                break;

            // Behaviour this-field (no legs; TryPrepareFieldSet returns null for it).
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                EmitStoreField(fieldRef.Field.Name, value);
                break;

            case IParameterReferenceOperation paramRef:
                EmitStoreField(GetParamVarId(paramRef.Parameter), value);
                break;

            case IArrayElementReferenceOperation arrayElem:
                // Deconstruction into an array element: `(arr[0], arr[1]) = (...)`. The caller's two-loop split
                // already evaluated every RHS element before any store, so the swap/rotate VALUES are safe here.
                // Wave-9 round-6 [X6]: the array/index LEGS must come from the pre-RHS preparation when the
                // deconstruction caller supplied one — C# evaluates every target's component expressions
                // left-to-right BEFORE the RHS, so a store-time `VisitExpression(Indices[0])` that reads state
                // the RHS mutated lands the write in the WRONG CELL (VM-proven ref=806 vs 86).
                if (preparedStores != null && preparedStores.TryGetValue(arrayElem, out var arrStore))
                    arrStore(value);
                else
                    PrepareArrayElementSet(arrayElem)(value);
                break;

            case IDiscardOperation:
                break;

            // Wave-9 round-5 [X13]: deconstruction into a property/indexer lvalue (`(p.X, this[i]) = …`).
            // Pre-fix every property target fell through to the default reject below on legal C#. The
            // value is already evaluated (two-loop split), so the factory is trivially side-effect-free.
            // Wave-9 round-6 [X2]-[X5]: the receiver/index LEGS likewise come from the pre-RHS preparation
            // when supplied (same wrong-cell family as the array arm above).
            case IPropertyReferenceOperation propLValue:
                if (preparedStores != null && preparedStores.TryGetValue(propLValue, out var propStore))
                    propStore(value);
                else
                    EmitPropertySet(propLValue, () => value);
                break;

            default:
                throw new System.NotSupportedException(
                    $"Unsupported l-value target: {target.GetType().Name}");
        }
    }

    /// <summary>Assign a nested deconstruction target tuple from its object[]-emulated value: read each element
    /// via __Get and delegate to AssignToLValue (which recurses for deeper tuples / handles the leaf lvalues).
    /// A struct (non-tuple aggregate) leaf is deep-cloned for value semantics; a nested tuple recurses instead.</summary>
    void AssignNestedTupleElements(ITupleOperation tuple, CLeaf arrValue,
        Dictionary<IOperation, System.Action<CLeaf>> preparedStores = null)
    {
        for (int i = 0; i < tuple.Elements.Length; i++)
        {
            var elemVal = AggregateAbi.ReadSlot(_builder, arrValue, i, "SystemObject");
            var toAssign = tuple.Elements[i].Type is INamedTypeSymbol et
                && EmitPolicy.IsAggregateType(et) && !et.IsTupleType
                ? AggregateAbi.DeepClone(_builder, elemVal, et, _ctx.GetAggregateLayout) : elemVal;
            AssignToLValue(tuple.Elements[i], toAssign, preparedStores);
        }
    }

    /// <summary>Wave-9 round-6 [X2]-[X6]: pre-evaluate the receiver/index LEGS of every property/indexer
    /// and array-element target of a deconstruction, left-to-right in lexical order (nested target tuples
    /// included), BEFORE the caller evaluates the RHS — the C# order is "each target's component
    /// expressions left-to-right, then the RHS, then the stores left-to-right". Returns a deferred store
    /// per prepared target (keyed by the target operation, consumed by AssignToLValue), or null when no
    /// target carries legs (plain locals/fields/discards — byte-identical to the pre-round-6 emission).</summary>
    protected Dictionary<IOperation, System.Action<CLeaf>> PrepareDeconstructionTargets(ITupleOperation targetTuple)
    {
        Dictionary<IOperation, System.Action<CLeaf>> prepared = null;
        void Walk(IOperation element)
        {
            switch (element)
            {
                case IDeclarationExpressionOperation declExpr:
                    Walk(declExpr.Expression);
                    break;
                case ITupleOperation nested:
                    foreach (var e in nested.Elements) Walk(e);
                    break;
                case IPropertyReferenceOperation propTarget:
                    prepared ??= new Dictionary<IOperation, System.Action<CLeaf>>();
                    prepared[propTarget] = PreparePropertySet(propTarget);
                    break;
                case IArrayElementReferenceOperation arrayElem:
                    prepared ??= new Dictionary<IOperation, System.Action<CLeaf>>();
                    prepared[arrayElem] = PrepareArrayElementSet(arrayElem);
                    break;
                // Wave-9 round-7 [Y2]/[Y4]/[Y6]/[Y8]/[Y10]: FIELD targets with receiver legs
                // (struct-array-element receivers `arr[i].v`, member chains, cross-behaviour
                // variable receivers) — the round-6 pass covered property/indexer/array-element
                // leaves only, so field-target legs kept store-time evaluation (wrong cell when a
                // leg read state the RHS mutates; VM-proven ref=702 vs 72). Behaviour this-fields
                // return null (no legs) and keep the plain store.
                case IFieldReferenceOperation fieldTarget:
                    if (TryPrepareFieldSet(fieldTarget) is { } fieldStore)
                    {
                        prepared ??= new Dictionary<IOperation, System.Action<CLeaf>>();
                        prepared[fieldTarget] = fieldStore;
                    }
                    break;
            }
        }
        foreach (var e in targetTuple.Elements) Walk(e);
        return prepared;
    }

    /// <summary>Pure (no-emission) twin of TryPrepareFieldSet's arm dispatch: true exactly when
    /// TryPrepareFieldSet would return a store (aggregate member slot, cross-behaviour field,
    /// extern value-type / reference-type field), false for behaviour this-fields and static
    /// fields. Lets callers decide evaluation ORDER before any legs are emitted.</summary>
    protected bool IsPreparableFieldSetTarget(IFieldReferenceOperation fieldRef)
    {
        if (fieldRef.Instance == null) return false;                       // static — no receiver legs
        if (fieldRef.Instance is not IInstanceReferenceOperation) return true; // variable receiver — always a prepared arm
        return fieldRef.Field.ContainingType.IsValueType                   // struct `this.v` (emulated receiver)
            || (AggregateAbi.TryGetMemberTarget(fieldRef, out var inst, out var name)
                && inst.Type is INamedTypeSymbol agg && EmitPolicy.IsObjectArrayEmulated(agg)
                && _ctx.GetAggregateLayout(agg).TryGetIndex(name, out _));
    }

    /// <summary>Wave-9 round-7 [Y2]/[Y4]-[Y10]: the single field SET path, shared by simple
    /// assignment and deconstruction lvalues (the field twin of PreparePropertySet /
    /// PrepareArrayElementSet). Evaluates the target's receiver legs NOW (C# order: the lvalue's
    /// component expressions run BEFORE the RHS) and returns the deferred store: aggregate member
    /// slot → cross-behaviour SetProgramVariable → extern value-type field → extern reference-type
    /// field. Returns null for behaviour this-fields and static fields (no legs) — callers keep
    /// their direct-store path. Keep the arm dispatch in lockstep with IsPreparableFieldSetTarget
    /// above.</summary>
    protected System.Action<CLeaf> TryPrepareFieldSet(IFieldReferenceOperation fieldRef)
    {
        // Aggregate (struct/tuple) OR v1-class member → layout slot write on the backing object[].
        if (AggregateAbi.TryGetMemberTarget(fieldRef, out var aggInstance, out var aggMemberName)
            && aggInstance.Type is INamedTypeSymbol aggContaining && EmitPolicy.IsObjectArrayEmulated(aggContaining)
            && _ctx.GetAggregateLayout(aggContaining).TryGetIndex(aggMemberName, out var fieldIndex))
        {
            RejectStaticReadonlyWriteThrough(aggInstance); // §3.3, R5
            var arrExpr = LoadInstanceRaw(aggInstance);
            return value => AggregateAbi.WriteSlot(_builder, arrExpr, fieldIndex, value);
        }

        // Cross-behaviour field → one SetProgramVariable (a delegate field ships the bundle
        // REFERENCE — design §2.3, incl. a tuple-return delegate's SystemObjectArray bundle).
        if (fieldRef is { Instance: not null and not IInstanceReferenceOperation }
            && ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            var crossInstanceVal = VisitExpression(fieldRef.Instance);
            return value => EmitCrossBehaviourFieldSet(fieldRef.Field, crossInstanceVal, value);
        }

        // Extern value-type field (e.g. a Vector3 component) → extern field setter.
        if (fieldRef.Instance != null && fieldRef.Field.ContainingType.IsValueType)
        {
            var vtContainingType = GetUdonType(fieldRef.Field.ContainingType);
            var vtInstanceVal = fieldRef.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(vtContainingType), vtContainingType)
                : VisitExpression(fieldRef.Instance);
            var vtSig = ExternResolver.BuildFieldSetSignature(
                vtContainingType, fieldRef.Field.Name, GetUdonType(fieldRef.Field.Type));
            return value => EmitExternVoid(vtSig, new List<CLeaf> { vtInstanceVal, value });
        }

        // Extern reference-type field through a variable receiver → extern field setter.
        if (fieldRef is { Instance: not null and not IInstanceReferenceOperation }
            && !fieldRef.Field.ContainingType.IsValueType
            && !ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            var refInstanceVal = VisitExpression(fieldRef.Instance);
            var refSig = ExternResolver.BuildFieldSetSignature(
                GetUdonType(fieldRef.Field.ContainingType), fieldRef.Field.Name,
                GetUdonType(fieldRef.Field.Type), isValueType: false);
            return value => EmitExternVoid(refSig, new List<CLeaf> { refInstanceVal, value });
        }

        return null; // behaviour this-field / static field — no legs
    }

    /// <summary>Evaluate an array-element lvalue's array/index legs NOW and return the deferred
    /// element store (wave-9 round-6 [X6]; the legs/value split twin of PreparePropertySet).</summary>
    protected System.Action<CLeaf> PrepareArrayElementSet(IArrayElementReferenceOperation arrayElem)
    {
        RejectStaticReadonlyWriteThrough(arrayElem.ArrayReference); // §3.3, R5
        if (arrayElem.Indices.Length > 1) return PrepareNdimElementSet(arrayElem);
        var arrayVal = VisitExpression(arrayElem.ArrayReference);
        var arrSym = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
        var indexVal = ResolveArrayIndex(arrayVal, GetArrayType(arrSym), arrayElem.Indices[0]);
        return value => EmitArrayElementSet(arrSym, arrayVal, indexVal, value);
    }

    /// <summary>Emit an array element Set extern from already-evaluated array/index/value leaves.
    /// Shared by PrepareArrayElementSet (single write) and AssignmentHandlerBase.EmitWriteBack's
    /// read-modify-write array arm (which reuses CaptureLValue's cached array/index leaves instead
    /// of re-evaluating them).</summary>
    protected void EmitArrayElementSet(IArrayTypeSymbol arrSymbol, CLeaf arrayVal, CLeaf indexVal, CLeaf value)
    {
        var arrayType = GetArrayType(arrSymbol);
        var elementType = GetArrayElemType(arrSymbol);
        EmitExternVoid(ExternResolver.BuildArraySetSignature(arrayType, elementType),
            new List<CLeaf> { arrayVal, indexVal, value });
    }

    /// <summary>Emit a cross-behaviour field Set via SetProgramVariable from an already-evaluated
    /// instance leaf. Shared by TryPrepareFieldSet (single write) and AssignmentHandlerBase.EmitWriteBack's
    /// read-modify-write field arm (which reuses CaptureLValue's cached instance leaf instead of
    /// re-evaluating it).</summary>
    protected void EmitCrossBehaviourFieldSet(IFieldSymbol field, CLeaf instanceVal, CLeaf value)
    {
        RejectProgramLocalCrossBehaviourFieldWrite(field);
        var nameConst = Const(field.Name, "SystemString");
        EmitExternVoid(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
            new List<CLeaf> { instanceVal, nameConst, value });
    }

    /// <summary>Wave-9 round-5 [X2]/[X13]: the single property/indexer SET path, shared by simple
    /// assignment and deconstruction lvalues. Evaluation follows the C# order — receiver, then index
    /// arguments, then the value (valueFactory) — which is the [X2] fix: the old simple-assignment
    /// arm evaluated the RHS before the receiver and index args, so `this[i] = Step()`-style sites
    /// whose index/receiver expressions share state with the RHS diverged from the CLR.
    /// Wave-9 round-6 [X2]-[X5]: split into PreparePropertySet (receiver/index legs, evaluated NOW)
    /// + a deferred store, so deconstruction can evaluate every target's legs BEFORE the RHS.
    /// Returns the stored value (the assignment-expression result).</summary>
    protected CLeaf EmitPropertySet(IPropertyReferenceOperation propRef, System.Func<CLeaf> valueFactory)
    {
        var store = PreparePropertySet(propRef);
        var val = valueFactory();
        store(val);
        return val;
    }

    /// <summary>Evaluate a property/indexer SET target's receiver and index-argument legs NOW (in the
    /// C# receiver → index args order) and return the deferred store that emits the actual SET with a
    /// later-evaluated value. The single-assignment path (EmitPropertySet) runs legs → value → store,
    /// byte-identical to the pre-split emission; the deconstruction path runs ALL targets' legs, then
    /// the RHS, then the stores (wave-9 round-6 [X2]-[X5] — store-time leg evaluation inverted the C#
    /// order and landed writes in the wrong cell when the legs read state the RHS mutates).</summary>
    protected System.Action<CLeaf> PreparePropertySet(IPropertyReferenceOperation propRef)
    {
        // Aggregate (struct/tuple) OR v1-class auto-property → layout slot write on the backing object[].
        if (propRef.Instance is { Type: INamedTypeSymbol aggContaining } aggInst
            && EmitPolicy.IsObjectArrayEmulated(aggContaining)
            && _ctx.GetAggregateLayout(aggContaining).TryGetIndex(propRef.Property.Name, out var aggSlotIndex))
        {
            var arrExpr = LoadInstanceRaw(aggInst);
            return aggVal => AggregateAbi.WriteSlot(_builder, arrExpr, aggSlotIndex, aggVal);
        }

        // Computed (non-auto) struct property setter: p.Both = v → call the user setter with the receiver
        // object[] as synthetic param0 (mutates this-fields through the shared backing array).
        if (propRef.Property is { IsIndexer: false, SetMethod: { } aggSetterRaw }
            && propRef.Instance?.Type is INamedTypeSymbol aggSetType && EmitPolicy.IsObjectArrayEmulated(aggSetType))
        {
            var aggSetter = ResolveStructMember(aggSetterRaw);
            var aggRecv = LoadInstanceRaw(propRef.Instance);
            return aggSetVal => EmitExprStmt(
                EmitCallToMethod(aggSetter, new List<CLeaf> { aggRecv, aggSetVal }));
        }

        // User-defined indexer on a user STRUCT instance (`s[i] = v`) → call the setter with the struct
        // receiver object[] as param0, the index args, then the value. Mirrors the GET routing in
        // VisitIndexerGet; without it this falls to a bogus SystemObjectArray.__set_Item extern. (diff-fuzz wave 4)
        if (propRef.Property is { IsIndexer: true, SetMethod: { } aggIdxSetterRaw }
            && propRef.Instance?.Type is INamedTypeSymbol aggIdxSetType && EmitPolicy.IsObjectArrayEmulated(aggIdxSetType))
        {
            var aggIdxSetter = ResolveStructMember(aggIdxSetterRaw);
            var setterArgs = new List<CLeaf> { LoadInstanceRaw(propRef.Instance) };
            setterArgs.AddRange(EvaluateIndexerArgs(propRef)); // wave-9 r4: named index args bind by ordinal
            return aggIdxVal =>
            {
                setterArgs.Add(aggIdxVal);
                EmitExprStmt(EmitCallToMethod(aggIdxSetter, setterArgs));
            };
        }

        // Virtual dispatch through `this` (round 7): a write inside an inherited base body binds
        // the BASE accessor — resolve to the chain-leaf override for the this-path setter lookups
        // below; `base.P` (and every non-this receiver) keeps the static binding.
        var dispatchProp = ResolveDispatchProperty(propRef);
        // B74 fold: route the extern owner through the shared funnel (an inherited property registers under
        // the receiver's own static type, not its declaring base) — replaces the old Behaviour/MonoBehaviour-
        // only string fixup below. A null instance (static setter) leaves the owner unchanged.
        var propOwnerReceiver = propRef.Instance is IInstanceReferenceOperation ? _classSymbol : propRef.Instance?.Type;
        var propContainingUdon = GetUdonType(ResolveExternOwnerType(propRef.Property.ContainingType, propOwnerReceiver, propRef.Property.Name));

        // User-defined indexer on this/base → internal setter call (index args followed by the value).
        if (dispatchProp.IsIndexer && propRef.Instance is IInstanceReferenceOperation
            && dispatchProp.SetMethod != null && _methodFunctions.ContainsKey(dispatchProp.SetMethod))
        {
            // Wave-9 round-4: index args slotted by parameter ordinal (named/reordered index args
            // bind by name; the base[...] flavor rides this same arm via the base-instance copy).
            var thisIdxArgs = EvaluateIndexerArgs(propRef);
            return thisIdxVal =>
            {
                thisIdxArgs.Add(thisIdxVal);
                EmitExprStmt(EmitCallToMethod(dispatchProp.SetMethod, thisIdxArgs));
            };
        }

        // Static property setter (no instance) — e.g. Time.timeScale = 1.0f
        if (propRef.Instance == null)
        {
            var staticValType = GetUdonType(propRef.Property.Type);
            return staticVal => EmitExternVoid(
                ExternResolver.BuildPropertySetSignature(propContainingUdon, propRef.Property.Name, staticValType),
                new List<CLeaf> { staticVal });
        }

        var instanceVal = propRef.Instance is IInstanceReferenceOperation
            ? LoadField(_ctx.DeclareThisOnce(propContainingUdon), propContainingUdon)
            : VisitExpression(propRef.Instance);
        var containingType = propContainingUdon;
        var valueType = GetUdonType(propRef.Property.Type);
        if (propRef.Property.IsIndexer)
        {
            // Wave-9 round-2 [W6]: user indexer WRITE through a VARIABLE receiver → cross-program
            // setter dispatch (index args + the value as the setter's LAST parameter). Pre-fix this
            // fell to the extern arm below and emitted a nonexistent IUdonEventReceiver.__set_Item.
            if (IsVariableReceiverBehaviourIndexer(propRef) && propRef.Property.SetMethod is { } recvIdxSetter)
            {
                var orderedIdx = EvaluateIndexerArgs(propRef);
                return recvIdxVal =>
                {
                    orderedIdx.Add(recvIdxVal);
                    EmitCrossIndexerCall(recvIdxSetter, instanceVal, orderedIdx,
                        TryMarkReentrantCrossDispatch(propRef, recvIdxSetter)); // void: self-emitting
                };
            }
            // Wave-9 round-4 [X4]/[X9]: user indexer WRITE through an INTERFACE-typed receiver →
            // dispatch the setter through its interface bridge (index args + the value as the
            // setter's LAST parameter). Pre-fix this fell to the extern arm below and emitted a
            // nonexistent IUdonEventReceiver.__set_Item (loud validator crash on legal C#).
            if (TryGetInterfaceAccessorLayout(propRef, propRef.Property.SetMethod, out var ifaceIdxSetMl))
            {
                var ifaceOrderedIdx = EvaluateIndexerArgs(propRef);
                return ifaceIdxVal =>
                {
                    ifaceOrderedIdx.Add(ifaceIdxVal);
                    EmitInterfaceAccessorCall(propRef.Property.SetMethod, ifaceIdxSetMl, instanceVal,
                        ifaceOrderedIdx,
                        TryMarkReentrantCrossDispatch(propRef, propRef.Property.SetMethod)); // void: self-emitting
                };
            }
            var indexArgs = new List<CLeaf> { instanceVal };
            var indexTypes = new List<string>();
            foreach (var arg in propRef.Arguments)
            {
                indexArgs.Add(VisitExpression(arg.Value));
                indexTypes.Add(GetUdonType(arg.Value.Type));
            }
            var indexParamStr = string.Join("_", indexTypes);
            return externIdxVal =>
            {
                indexArgs.Add(externIdxVal);
                // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                EmitExternVoid($"{containingType}.__set_{propRef.Property.MetadataName}__{indexParamStr}_{valueType}__SystemVoid", indexArgs);
            };
        }

        return srcVal =>
        {
        switch (propRef.Instance)
        {
            case IInstanceReferenceOperation
                when dispatchProp.SetMethod != null && _methodFunctions.TryGetValue(dispatchProp.SetMethod, out _):
                // User-defined property setter on this → internal call
                EmitExprStmt(EmitCallToMethod(dispatchProp.SetMethod, new List<CLeaf> { srcVal }));
                break;
            case IInstanceReferenceOperation
                when dispatchProp.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                     && ExternResolver.IsUdonSharpBehaviour(dispatchProp.ContainingType)
                     && dispatchProp.ContainingType.Name != "UdonSharpBehaviour":
                // Auto-property set on this → direct variable assignment (user-defined classes only)
                EmitStoreField(dispatchProp.Name, srcVal);
                break;
            default:
            {
                // Interface property set → dispatch the setter through its interface bridge (SetProgramVariable
                // the value, SendCustomEvent the setter), like an interface method call. Without this the
                // fall-through emits a non-existent __set_Value extern on IUdonEventReceiver.
                if (propRef.Property.SetMethod is { } ifaceSetter
                    && propRef.Property.ContainingType.TypeKind == TypeKind.Interface
                    && propRef.Property.ContainingType.SpecialType == SpecialType.None
                    && propRef.Instance is not IInstanceReferenceOperation
                    && _planner.GetLayout(propRef.Property.ContainingType).Methods.TryGetValue(ifaceSetter, out var ifaceSetterMl))
                {
                    GuardInterfaceHasBehaviourImplementor(propRef.Property.ContainingType, propRef.Property.Name);
                    // Wave-12 r2 [V1]: a reentrant setter dispatch pulls its value copy-in inside the
                    // spill window (preSpillStmts: 1 — the SetProgramVariable emitted just above).
                    bool ifaceSetReentrant = TryMarkReentrantCrossDispatch(propRef, ifaceSetter);
                    var paramNameConst = Const(ifaceSetterMl.ParamIds[0], "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, srcVal });
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
                        new List<CLeaf> { instanceVal, Const(LayoutPlanner.InterfaceDispatchName(ifaceSetter, ifaceSetterMl), "SystemString") },
                        ifaceSetReentrant, ifaceSetReentrant ? 1 : 0);
                }
                else if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                {
                    // Delegate-typed cross-behaviour property SET (Stage 1.75 design 2026-07-04 §3):
                    // the bundle is an opaque object[] reference — it transports through the EXISTING
                    // cross property machinery below with no special casing (P6 cross-boundary
                    // machinery already proven for any reference-typed value; P4 VM-verified both the
                    // auto SPV-direct and non-auto SCE-accessor transports preserve the bundle).

                    // Wave-12 [V2]: a NON-public auto-property's backing symbol is declared but its
                    // accessors are never exported — write the symbol directly (needs no entry point).
                    var isAutoSet = propRef.Property.SetMethod == null
                        || IsNonPublicAutoCrossProperty(propRef.Property.SetMethod, propRef.Property);
                    if (isAutoSet)
                    {
                        // Auto-property or read-only: direct SetProgramVariable("PropertyName")
                        var nameConst = Const(propRef.Property.Name, "SystemString");
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, nameConst, srcVal });
                    }
                    else
                    {
                        // Non-auto property setter: call via SendCustomEvent
                        RejectNonPublicCrossAccessor(propRef.Property.SetMethod, propRef.Property); // wave-12 [V2]
                        // Wave-12 r2 [V1]: reentrant setter — value copy-in inside the spill window.
                        bool setReentrant = TryMarkReentrantCrossDispatch(propRef, propRef.Property.SetMethod);
                        var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);

                        // SetProgramVariable for the value parameter
                        var paramNameConst = Const(setParamIds[0], "SystemString");
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, srcVal });

                        // SendCustomEvent to invoke setter
                        var eventConst = Const(exportName, "SystemString");
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<CLeaf> { instanceVal, eventConst },
                            setReentrant, setReentrant ? 1 : 0);
                    }
                }
                else
                {
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<CLeaf> { instanceVal, srcVal });
                }

                break;
            }
        }
        };
    }

    // ── Lambda / Local Function Helpers ──

    protected void RegisterLocalFunction(IMethodSymbol localFunc)
    {
        if (_methodFunctions.ContainsKey(localFunc)) return;
        EmitPolicy.RejectInParameters(localFunc); // round-7 follow-up [Q3]
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
        // Stage 2 §1.3/§6: a CAPTURING hoisted closure carries a hidden trailing __envp param — the
        // binding-scope env arrives here (bridge conv-consume §5.1 / direct call §5.6 / TCO rebind).
        // Appended to lfParamIds → _methodParamVarIds (so CollectRecursionSpillFields' param loop
        // spills it unchanged) and func.ParamFieldNames (so EmitCallInternal's positional copy-in
        // binds the trailing env arg into it). NOT in the delegate sig / conv-arg count (§1.3). A
        // capture-free closure and every named method get NO __envp — the capture-free byte invariant.
        if (_ctx.CaptureScope != null && _ctx.CaptureScope.IsCapturingClosure(localFunc))
        {
            var envpId = $"__{idx}_{funcName}__envp";
            _ctx.DeclareVar(envpId, EnvEmit.EnvType);
            var withEnvp = new string[lfParamIds.Length + 1];
            System.Array.Copy(lfParamIds, withEnvp, lfParamIds.Length);
            withEnvp[lfParamIds.Length] = envpId;
            lfParamIds = withEnvp;
            _ctx.RegisterEnvpField(localFunc.OriginalDefinition, envpId);
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
    /// Hoist a lambda expression to an internal method. A CAPTURING lambda's captured variables are
    /// resolved PER-ACTIVATION through the Stage-2 closure environment records (design §3/§4): the
    /// capture analysis (<see cref="CaptureScopeAnalysis"/>) assigns each captured symbol an env-record
    /// slot and all access routes through <see cref="EnvEmit"/> __Get/__Set on the owning scope's env —
    /// never the flat <see cref="EmitContext.LocalBindings"/> slot. This holds for closures hoisted from
    /// user-STRUCT member bodies too (roadmap B45): CaptureScopeAnalysis.Build walks struct members
    /// transitively, so a struct-method closure joins the same env chain rather than aliasing a shared
    /// module field.
    ///
    /// (Pre-Stage-2 this method's captures aliased a single module-level LocalBindings field, correct
    /// only for sequential non-escaping single-activation use — retired. VM-proven multi-activation
    /// clobber for the struct-hosted case: roadmap B45 M0 shapes (c)/(d).)
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
    internal static (string[] argNames, string retName, string envName) GetConventionFieldNames(INamedTypeSymbol delegateType,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
        => GetConventionFieldNames(delegateType.DelegateInvokeMethod, typeParamMap);

    /// <summary>Overload taking the Invoke (or Invoke-shaped) method directly — the delegate-type
    /// overload above just re-derives this from delegateType.DelegateInvokeMethod, so a caller that
    /// already holds the method (or, per Stage 1.75 §2.3, a WRAPPER dispatching an inner bundle whose
    /// native protocol is a PLAIN method's own signature, never itself a delegate's Invoke method) skips
    /// the round-trip. BuildSigPart only reads Parameters/ReturnsVoid/ReturnType, so any IMethodSymbol
    /// is a valid "invoke" here, not only a genuine DelegateInvokeMethod.</summary>
    internal static (string[] argNames, string retName, string envName) GetConventionFieldNames(IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var sigPart = DelegateAbi.BuildSigPart(invoke, typeParamMap);

        var argNames = new string[invoke.Parameters.Length];
        for (int i = 0; i < invoke.Parameters.Length; i++)
            argNames[i] = DelegateAbi.ConvArgName(sigPart, i);

        string retName = null;
        if (!invoke.ReturnsVoid)
            retName = DelegateAbi.ConvRetName(sigPart);

        // Stage 2 §1.3: the signature-keyed env conv global. Env is NOT part of BuildSigPart (the
        // cross-program byte contract), only this convention name. Declared on-first-use at the
        // dispatch site / capturing bridge — NEVER unconditionally (capture-free byte invariant).
        var envName = DelegateAbi.ConvEnvName(sigPart);

        return (argNames, retName, envName);
    }

    /// <summary>Multicast design (2026-07-03 §1.4): register the sig this `+=`/`-=` site needs a
    /// combine/remove helper (and fan-out bridge) for. Keyed on sig content (first registration wins) —
    /// a second `+=` site sharing the signature is a no-op here, same class of dedup as
    /// EmitPendingDelegateBridges' `emitted.Add`. Snapshots the type-param map for the same reason
    /// ResolveDelegateBridge does (§7 A-M1): synthetic emission runs after body emission, when the
    /// ambient map may already be cleared.</summary>
    protected void RegisterMulticastSig(string sigPart, IMethodSymbol invoke)
    {
        if (_ctx.PendingMulticastSigs.ContainsKey(sigPart)) return;
        // Carry the immutable ambient map by reference — the drain resolves the sig later (ambient map null by then).
        _ctx.PendingMulticastSigs[sigPart] = (invoke, _ctx.TypeParamMap);
    }

    // B67: the synthesized value→name helper's name for a user enum (one per enum, drained in UasmEmitter).
    // B77: the name MUST be injective. The former ToDisplayString().Replace('.','_') was lossy — ns Foo.Bar
    // type Baz and ns Foo type Bar_Baz both mapped to __enumstr_Foo_Bar_Baz, so two enums overwrote one
    // another in _funcByName and emitted a corrupt shared helper. Derive it from the enum's containing-type
    // (nesting) then namespace chain, each segment carrying a T/N kind marker and its underscores doubled, so
    // distinct enums never collide (a lone '_' is always a separator; '__' is always an escaped literal
    // underscore) — while staying a pure function of the symbol, so the mint and drain sites still agree.
    internal static string EnumToStringHelperName(INamedTypeSymbol enumType)
    {
        var chain = new List<string>();
        for (INamedTypeSymbol t = enumType; t != null; t = t.ContainingType)
            chain.Add("T" + t.Name.Replace("_", "__"));
        for (var ns = enumType.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            chain.Add("N" + ns.Name.Replace("_", "__"));
        chain.Reverse();
        return "__enumstr_" + string.Join("_", chain);
    }

    /// <summary>B67: if <paramref name="type"/> is a user enum, convert its (already underlying-int)
    /// <paramref name="value"/> to the C#-correct NAME string via the synthesized per-enum helper and return
    /// that string leaf; otherwise return null so the caller emits the value as-is. A [Flags] enum rejects
    /// (Udon cannot synthesize the comma-separated decomposition — that is gold-plating).</summary>
    protected CLeaf TryEmitEnumToString(CLeaf value, ITypeSymbol type)
    {
        var resolved = ResolveType(type);
        if (!ExternResolver.IsUserEnum(resolved) || resolved is not INamedTypeSymbol e)
            return null;
        if (e.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute"))
            throw new NotSupportedException(
                $"'{e.Name}.ToString()' is not supported: '{e.Name}' is a [Flags] enum and Udon cannot "
                + "synthesize the comma-separated flag decomposition. Format the individual flag bits manually "
                + "(e.g. compare against each flag and build the string yourself).");
        _ctx.PendingEnumToString.Add(e);
        // `value` is already the enum's underlying-typed leaf (GetUdonType(enum) == underlying), which the
        // helper's parameter type matches — pass it straight through.
        return InternalCall(EnumToStringHelperName(e), new List<CLeaf> { value }, "SystemString");
    }

    /// <summary>Variance design (2026-07-04 §2.3, B-2): register the (outer sig-S, inner sig-T) pair a
    /// wrapper-with-payload bridge is needed for, returning its name. Same dedup/snapshot discipline as
    /// <see cref="RegisterMulticastSig"/> (first registration wins; a second site needing the same
    /// (outer,inner) wrapper is a no-op here) — keyed by the wrapper's own name since that's already the
    /// unique key for this pair.</summary>
    protected string RegisterWrapperSig(IMethodSymbol outerInvoke, IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        var wrapperName = DelegateAbi.WrapperName(
            DelegateAbi.BuildSigPart(outerInvoke, typeParamMap), DelegateAbi.BuildSigPart(innerInvoke, typeParamMap));
        if (_ctx.PendingWrapperSigs.ContainsKey(wrapperName)) return wrapperName;
        // Carry the immutable map by reference (callers pass the ambient _ctx.TypeParamMap).
        _ctx.PendingWrapperSigs[wrapperName] = (outerInvoke, innerInvoke, typeParamMap);
        return wrapperName;
    }

    // ── Override-chain resolution (shared core) ──
    //
    // The "walk _classSymbol's BaseType chain; for each same-named member, walk its
    // Overridden{Method,Property} chain looking for a match on the target's OriginalDefinition" search
    // was independently copy-pasted four times (this file's method/property flavors, plus
    // UasmEmitter.ResolveLeafOverrideDef/LeafPropertyTarget for the recursion-graph's emission-faithful
    // mirror of this same dispatch) — the two walker methods below are the single shared core; each of
    // the four call sites differs only in what it does with the raw match (generic re-Construct here,
    // OriginalDefinition-normalization in UasmEmitter) and its own guards/fallback.

    /// <summary>Search <paramref name="classSymbol"/>'s BaseType chain for a same-named method whose
    /// OverriddenMethod chain reaches <paramref name="def"/> (an OriginalDefinition). Returns the found
    /// member AS DECLARED — callers that need it OriginalDefinition-normalized do that themselves — or
    /// null if no override chain reaches it.</summary>
    internal static IMethodSymbol FindOverrideMethodInChain(INamedTypeSymbol classSymbol, IMethodSymbol def, string name)
    {
        for (var t = classSymbol; t != null; t = t.BaseType)
            foreach (var m in t.GetMembers(name).OfType<IMethodSymbol>())
                for (IMethodSymbol o = m; o != null; o = o.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, def))
                        return m;
        return null;
    }

    /// <summary>Property twin of <see cref="FindOverrideMethodInChain"/> — walks OverriddenProperty
    /// instead of OverriddenMethod.</summary>
    internal static IPropertySymbol FindOverridePropertyInChain(INamedTypeSymbol classSymbol, IPropertySymbol def, string name)
    {
        for (var t = classSymbol; t != null; t = t.BaseType)
            foreach (var p in t.GetMembers(name).OfType<IPropertySymbol>())
                for (var o = p; o != null; o = o.OverriddenProperty)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, def))
                        return p;
        return null;
    }

    /// <summary>Most-derived override of <paramref name="baseMethod"/> reachable from the compiled type
    /// (_classSymbol), or baseMethod itself if none — mirrors C# virtual dispatch for a `this` call whose
    /// static target is a base declaration. Round-8 [R8]: GetMembers returns the UNCONSTRUCTED member,
    /// so a generic virtual called through this lost its type arguments and monomorphized the open
    /// definition — the SDK assembler then ICEd with TypeResolverException 'T' (even same-class).
    /// Re-construct the resolved member with the original call's type arguments. (Moved from
    /// InvocationHandler in round 9 — StatementHandler's TCO gate shares it.)</summary>
    protected IMethodSymbol ResolveMostDerivedOverride(IMethodSymbol baseMethod)
    {
        var def = baseMethod.OriginalDefinition;
        var m = FindOverrideMethodInChain(_classSymbol, def, baseMethod.Name);
        if (m == null) return baseMethod;
        return baseMethod.IsGenericMethod && m.IsGenericMethod
            ? m.OriginalDefinition.Construct(baseMethod.TypeArguments.ToArray())
            : m;
    }

    // ── Generic Monomorphization ──

    /// <summary>Resolve type parameters in a generic method's type arguments through the current
    /// type-param map (e.g. Min&lt;T&gt; → Min&lt;int&gt; inside a specialization's emission). Shared by
    /// the invocation path and the delegate-creation path (wave-9 round-2 [W7]).
    ///
    /// Feature G: a call INSIDE a generic struct's own method body to another member of the SAME
    /// struct (self-recursion, or a same-struct helper call) binds its target at the OPEN containing
    /// type (Box&lt;T&gt;.Helper(), never Box&lt;int&gt;.Helper()) — the body is always processed from the
    /// shared/unconstructed operation tree regardless of which instantiation is emitting (same
    /// invariant as a generic method's own type args). So this also re-closes the CONTAINING type
    /// through the ambient map when it carries an open type parameter, re-locating the member on the
    /// closed containing type before (if the method is itself ALSO generic) re-applying the method's
    /// own type-arg substitution on top — the two dimensions are independent and compose.</summary>
    protected IMethodSymbol SubstituteMethodTypeArgs(IMethodSymbol target)
    {
        if (_typeParamMap == null) return target;

        bool ContainsOpenParam(IEnumerable<ITypeSymbol> args) =>
            args.Any(ta => ta is ITypeParameterSymbol tp && _typeParamMap.ContainsKey(tp));

        // Wave-14 r3: a local function/lambda's ContainingType is the enclosing generic struct/method's
        // type (e.g. Helper's ContainingType is Box<T> when Helper is declared inside Box<T>.Compute), so
        // it also looks "containingNeedsSub" — but a local function/lambda is lexically scoped, never a
        // MEMBER of that type, so INamedTypeSymbol.GetMembers(name) below can never find it (empty
        // sequence, LINQ .First() throws "Sequence contains no matching element"). The shared/unconstructed
        // operation tree already carries the correct symbol identity for these (RegisterLocalFunction keys
        // on it directly) — skip relocation for them entirely.
        bool containingNeedsSub = target.ContainingType.IsGenericType
            && target.MethodKind is not (MethodKind.LocalFunction or MethodKind.LambdaMethod)
            && ContainsOpenParam(target.ContainingType.TypeArguments);
        bool methodNeedsSub = target.IsGenericMethod && ContainsOpenParam(target.TypeArguments);
        if (!containingNeedsSub && !methodNeedsSub) return target;

        var memberDef = target.OriginalDefinition;
        var relocated = memberDef;
        if (containingNeedsSub)
        {
            var newContainingArgs = target.ContainingType.TypeArguments
                .Select(ta => ta is ITypeParameterSymbol tp && _typeParamMap.TryGetValue(tp, out var sub) ? sub : ta)
                .ToArray();
            var closedContaining = target.ContainingType.OriginalDefinition.Construct(newContainingArgs);
            relocated = closedContaining.GetMembers(memberDef.Name).OfType<IMethodSymbol>()
                .First(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, memberDef));
        }

        if (methodNeedsSub)
        {
            var newMethodArgs = target.TypeArguments
                .Select(ta => ta is ITypeParameterSymbol tp2 && _typeParamMap.TryGetValue(tp2, out var sub2) ? sub2 : ta)
                .ToArray();
            // Wave-14 r3: Construct() on relocated.OriginalDefinition RESETS the containing type back to
            // fully open (Box<T>, not Box<int>) when containingNeedsSub already closed it above — a
            // generic METHOD on a generic STRUCT (Box<T>.RepeatGen<U>) then loses its T substitution on
            // this second dimension (VM-proven: emitted UASM referenced the unresolved type parameter
            // 'U' — actually T resurfacing as the containing type — "Type referenced by 'U' could not be
            // resolved", SelfRecursiveGenericMethod). `relocated` is already method-dimension-open with
            // the CORRECT (possibly-closed) containing type from the branch above (or straight from
            // `target.OriginalDefinition` when containingNeedsSub was false) — construct directly on it.
            relocated = relocated.Construct(newMethodArgs);
        }

        return relocated;
    }

    /// <summary>Register a monomorphized generic specialization: CFunction + ordinal param vars +
    /// return slot, queued on PendingGenericSpecs for the post-body emission drain. Idempotent per
    /// constructed symbol. (Moved from InvocationHandler when [W7] gave the delegate-creation path a
    /// second caller — one registration knowledge source.)</summary>
    // Wave-9 round-5 [X6] / round-8 [Y2] gate — first-wins record of a generic definition's instantiation
    // (drives the closure-compose that carries the enclosing generic's T into a nested closure/LF, and the
    // multi-instantiation pin). Struct-hosted generic methods route through EmitStructInstanceCall, which
    // registers the spec itself but NOT through RegisterGenericSpecialization — so this must run there too
    // (B56), else a nested LF referencing the method's T finds no owner and CoreVerify ICEs on raw 'T'.
    protected void RegisterFirstGenericSpec(IMethodSymbol constructed)
    {
        var genericDef = constructed.OriginalDefinition;
        if (_ctx.FirstGenericSpec.TryGetValue(genericDef, out var firstSpec))
            EmitContext.ThrowIfClosureAliasesInstantiation(_compilation, firstSpec, constructed);
        else
            _ctx.FirstGenericSpec[genericDef] = constructed;
    }

    protected void RegisterGenericSpecialization(IMethodSymbol constructed)
    {
        if (_methodFunctions.ContainsKey(constructed)) return;
        EmitPolicy.RejectInParameters(constructed); // round-7 follow-up [Q3]

        // Wave-9 round-5 [X6]: a SECOND distinct instantiation of a generic definition whose body
        // contains a CAPTURING lambda/local function is loud. The hoisted closure is keyed by
        // IMethodSymbol and shared across specs, so its capture cells are seeded by whichever spec
        // emitted last — the first spec's dispatch then reads the other instantiation's captured
        // values (VM-proven r1=8 vs 3). Round-8 [Y2] widening: a closure whose signature or body
        // REFERENCES the generic's type parameters pins the instantiation the same way (the shared
        // function was emitted with the first spec's map). Pin-free closures and single
        // instantiations stay legal.
        RegisterFirstGenericSpec(constructed);

        var slot = _ctx.RegisterMethod(constructed, i => i.ToString());
        var idx = slot.Index;

        var typeArgPart = string.Join("_", constructed.TypeArguments.Select(ExternResolver.GetUdonTypeName));
        var name = $"__{idx}_{SanitizeId(constructed.Name)}_{typeArgPart}";
        var func = _module.AddFunction(name);
        _methodFunctions[constructed] = func;

        // Feature G residual gap (wave-14): a member of a CONSTRUCTED generic struct carries the same
        // synthetic receiver object[] as param0 that the Phase-1 struct-method registration gives every
        // non-static struct instance member (UasmEmitter's structMethods loop, "param0 = receiver
        // object[]") — EmitMethod's CurrentStructReceiverParamId reads func.ParamFieldNames[0]
        // unconditionally for one. This on-demand path predates feature G (plain generic METHODS have
        // no receiver concept — a class instance's `this` is a declared field, not param0; a foreign
        // static has no receiver at all) and never grew this convention, so a struct member reached
        // ONLY via internal self-reference (never pre-collected) got no receiver slot and EmitMethod's
        // ParamFieldNames[0] read threw IndexOutOfRange.
        if (!constructed.IsStatic
            && constructed.ContainingType is INamedTypeSymbol structRecvCt && EmitPolicy.IsObjectArrayEmulated(structRecvCt)
            && constructed.MethodKind is not (MethodKind.LambdaMethod or MethodKind.LocalFunction))
        {
            var receiverId = $"__{idx}_this__param";
            _ctx.DeclareVar(receiverId, "SystemObjectArray");
            func.ParamFieldNames.Add(receiverId);
        }

        var gsParamIds = new string[constructed.Parameters.Length];
        for (int pi = 0; pi < constructed.Parameters.Length; pi++)
        {
            var param = constructed.Parameters[pi];
            var paramId = $"__{idx}_{param.Name}__param";
            _ctx.DeclareVar(paramId, GetUdonType(param.Type));
            gsParamIds[pi] = paramId;
        }
        // Stage 2 §1.3: __envp twin of RegisterLocalFunction, for a capturing GENERIC local function
        // specialization (keyed by OriginalDefinition = the generic def, matching EnvEmit.Leaf's
        // lookup). Non-T-dependent capturing generics share one physical node; T-dependent ones are
        // pinned to a single instantiation by the ClosurePin gate above, so one envp field per def.
        if (_ctx.CaptureScope != null && _ctx.CaptureScope.IsCapturingClosure(constructed))
        {
            var envpId = $"__{idx}_{SanitizeId(constructed.Name)}__envp";
            _ctx.DeclareVar(envpId, EnvEmit.EnvType);
            var withEnvp = new string[gsParamIds.Length + 1];
            System.Array.Copy(gsParamIds, withEnvp, gsParamIds.Length);
            withEnvp[gsParamIds.Length] = envpId;
            gsParamIds = withEnvp;
            // Keyed by the CONSTRUCTED spec, not OriginalDefinition: two specs of one capturing
            // generic (Lf<int> + Lf<long>) each own an __envp field — a definition key is
            // last-spec-wins and wires spec 1's body to spec 2's field (silent wrong env / fault).
            // Same keying discipline as _methodParamVarIds[constructed] below.
            _ctx.RegisterEnvpField(constructed, envpId);
        }
        _methodParamVarIds[constructed] = gsParamIds;
        foreach (var pid in gsParamIds) func.ParamFieldNames.Add(pid);

        if (!constructed.ReturnsVoid)
        {
            var retType = GetUdonType(constructed.ReturnType);
            var retId = $"__{idx}_{SanitizeId(constructed.Name)}__ret";
            _ctx.DeclareVar(retId, retType);
            func.ReturnType = retType;
            func.ReturnSlots.Add(new ReturnSlot(retId, retType));
            _methodReturns[constructed] = new[] { new ReturnSlot(retId, retType) };
        }

        _pendingGenericSpecs.Add(constructed);
    }

    /// <summary>Feature G residual gap (wave-14): a struct-member reference (computed property/indexer
    /// accessor, ctor, operator/conversion method) discovered while emitting a GENERIC STRUCT'S OWN
    /// method body binds to the OPEN containing type (Box&lt;T&gt;.Member, never Box&lt;int&gt;.Member) —
    /// the operation tree is built from the shared/unconstructed syntax regardless of which spec is
    /// emitting, the exact invariant SubstituteMethodTypeArgs closes for plain method calls
    /// (VisitInvocation). Every OTHER struct-member call site instead depended solely on
    /// CollectStructMethodsInOperation's pre-pass, which deliberately SKIPS this same open-form
    /// self-reference (IsCollectibleStructMember's feature-G comment — collecting it registers a dead
    /// second CFunction that corrupted definition-keyed recursion bookkeeping). So a member reached
    /// ONLY via internal self/sibling reference — e.g. a computed property read by a sibling method, an
    /// indexer used from within another instance method, a ctor called from a same-struct helper, or an
    /// operator/conversion invoked from another operator's body — never got a CFunction and fell through
    /// to a bogus SystemObjectArray extern (VM-proven: DiffFuzz wave-14 8/10 UsugarRejected). Substitute
    /// through the live type-param map, then register on demand exactly like a plain self-recursive
    /// call — both operations are idempotent, so this is a no-op for non-generic structs and for members
    /// already reached by an external concretely-typed call site.</summary>
    protected IMethodSymbol ResolveStructMember(IMethodSymbol member)
    {
        var resolved = SubstituteMethodTypeArgs(member);
        if (!_methodFunctions.ContainsKey(resolved))
            RegisterGenericSpecialization(resolved);
        return resolved;
    }

    // [X6] gate moved to EmitContext.GenericBodyClosurePin (round-8 [Y2]/[Y10] — shared with the
    // UasmEmitter base-instance-copy registration and widened to type-param-referencing closures).

    // ── Delegate bridge resolution ──

    /// <summary>Resolve delegate creation to bridge name, FuncRef, and target instance.</summary>
    /// <summary>Stage 2 §3.7/§4.1: the env leaf for a delegate/direct-call target — the binding-scope
    /// env of a CAPTURING closure (resolved statically from the current frame, §4.1), or a null const
    /// for a capture-free closure / named method (byte-invariant). Emit-time armor: a capturing
    /// closure must have a BindingScope lexically enclosing this creation site.</summary>
    protected CLeaf ClosureEnvLeaf(IMethodSymbol targetMethod)
    {
        if (targetMethod == null || _ctx.CaptureScope == null
            || !_ctx.CaptureScope.IsCapturingClosure(targetMethod.OriginalDefinition))
            return Const(null, "SystemObject");
        if (!_ctx.CaptureScope.ClosureScopes.TryGetValue(targetMethod.OriginalDefinition, out var closureScope)
            || closureScope.BindingScope == null)
            throw new System.InvalidOperationException(
                $"Capturing closure '{targetMethod.Name}' has no binding scope enclosing its creation site.");
        return EnvEmit.Leaf(_builder, _ctx, closureScope.BindingScope);
    }

    protected void RejectUnsafeCrossProgramDelegateWrite(IFieldReferenceOperation target, ValueInfo value)
        => _ctx.Boundary.RequireCanStoreCrossProgramDelegate(target, value);

    protected void RejectUnsafeCrossProgramEventHandler(IEventSymbol evt, ValueInfo value)
        => _ctx.Boundary.RequireCanStorePublicEventHandler(evt, value);

    protected void RejectProgramLocalCrossBehaviourFieldWrite(IFieldSymbol field)
        => _ctx.Boundary.RequireCanWriteCrossBehaviourField(field);

    protected void RejectProgramLocalCrossBehaviourFieldRead(IFieldSymbol field)
        => _ctx.Boundary.RequireCanReadCrossBehaviourField(field);

    protected void RejectProgramLocalCrossBehaviourArgument(ITypeSymbol argType)
        => _ctx.Boundary.RequireCanPassCrossBehaviourArgument(argType);

    protected void RejectProgramLocalErasure(IConversionOperation conversion,
        ITypeSymbol sourceType, ITypeSymbol destinationType)
        => _ctx.Boundary.RequireCanEraseProgramLocalPayload(conversion, sourceType, destinationType);

    protected (string bridgeName, CLeaf funcRef, CLeaf targetInstance, CLeaf envLeaf) ResolveDelegateBridge(IDelegateCreationOperation op)
    {
        IMethodSymbol targetMethod = null;
        CLeaf targetInstance = null;
        bool baseReceiver = false;
        switch (op.Target)
        {
            case IAnonymousFunctionOperation lambda:
                targetMethod = HoistLambdaToMethod(lambda);
                break;
            case IMethodReferenceOperation methodRef:
                targetMethod = methodRef.Method;
                baseReceiver = methodRef.Instance is IInstanceReferenceOperation
                    { Syntax: Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax };
                if (methodRef.Instance != null && methodRef.Instance is not IInstanceReferenceOperation)
                    targetInstance = VisitExpression(methodRef.Instance);
                break;
        }
        if (targetMethod == null)
            throw new System.NotSupportedException($"Unsupported delegate target: {op.Target.GetType().Name}");

        // B54: a struct INSTANCE method bound as a delegate. C# copies the struct receiver BY VALUE at
        // bind time; USugar represents a struct as a shared object[], so the bound delegate would alias
        // the live receiver and observe (or leak) later mutations — a silent value divergence, not a
        // clean feature gap. Reject loudly (design §8-3: loud over silent-wrong) instead of hitting the
        // frozen-planner ICE. A static struct method (no receiver) is unaffected and stays legal.
        if (op.Target is IMethodReferenceOperation && !targetMethod.IsStatic
            && targetMethod.ContainingType is INamedTypeSymbol structCt && EmitPolicy.IsUserStruct(structCt))
            throw new System.NotSupportedException(
                $"A delegate cannot be created from struct instance method '{structCt.Name}.{targetMethod.Name}': "
                + "C# captures the struct receiver by value at bind time, but USugar represents a struct as a "
                + "shared object[], so the delegate would alias the live receiver and observe its later mutations "
                + "(a silent value divergence). Wrap the call in a behaviour method and bind that instead.");

        if (op.Target is IMethodReferenceOperation)
            ClassAbi.RejectDelegateBindingToInstanceMethod(targetMethod);

        // Wave-12 r4 [W3]: a method group bound to an INTERFACE member (`cb = iface.Get`) previously
        // ICEd in GetDelegateBridgeLayout ('No delegate bridge'). It cannot compile correctly today:
        // DelegateAbi.Method is SendCustomEvent'd on the RUNTIME receiver, so it must name a __dlgc_-convention
        // bridge export derivable from the interface member alone, and implementers only export
        // bridges named by their own implementing methods — a canonical per-interface-member bridge
        // family in every implementer is a feature-scale ABI addition, not a fix (§8-3: loud over
        // silent/ICE; same rationale as the variable-receiver generic method-group reject below).
        // The lambda wrapping IS supported (VM-proven Match): it captures the receiver and dispatches
        // through the interface-call convention.
        if (targetMethod.ContainingType?.TypeKind == TypeKind.Interface)
            throw new System.NotSupportedException(
                $"A delegate cannot be created from interface member "
                + $"'{targetMethod.ContainingType.Name}.{targetMethod.Name}': the receiver's concrete "
                + "program is not known at compile time, so no bridge entry point exists for the "
                + "delegate dispatch. Wrap the call in a lambda instead ('() => receiver."
                + $"{targetMethod.Name}(...)'), or bind the implementing class's method directly.");

        // Stage 2 §3.7: DelegateAbi.Env for a capturing closure target (null for named methods / base.M
        // / capture-free lambdas). Resolved here in the creation site's frame.
        var envLeaf = ClosureEnvLeaf(targetMethod);

        // Wave-9 [W3]: `base.M` binds the BASE implementation NON-virtually (C# ldftn). When the
        // compiled class (or an intermediate) overrides M, the locally registered function for the
        // base symbol is the never-exported base-instance COPY (the same body `base.M()` jumps to),
        // so bridge THAT via a pending bridge — the planner bridge would normalize to the chain-root
        // export, i.e. the most-derived override (VM-proven 6 where C# gives 103). When nothing
        // overrides M, the base symbol's registration IS the exported inherited function and the
        // planner path below stays correct (and byte-identical).
        string bridgeExportName;
        if (baseReceiver && _methodFunctions.TryGetValue(targetMethod, out var baseCopy)
            && baseCopy.ExportName == null)
        {
            bridgeExportName = DelegateAbi.BridgeName(baseCopy.Name);
            _ctx.PendingDelegateBridges.Add((targetMethod, bridgeExportName, _ctx.TypeParamMap));
        }
        // For hoisted lambdas/local functions, create a pending bridge dynamically
        // since they aren't part of the TypeLayout's pre-computed bridges.
        else if (targetMethod.MethodKind == MethodKind.LambdaMethod || targetMethod.MethodKind == MethodKind.LocalFunction)
        {
            // Stage 2 M4 (fcd54): a GENERIC local function referenced as a method group
            // (`Func<int,int> d = Lf<int>`) arrives as a constructed spec whose MethodKind stays
            // LocalFunction, so it enters this arm rather than the generic-method arm below — but no
            // invocation registered it (the [Y8] invoke path monomorphizes generic LFs on the call
            // site; a method-group-only reference never hits it). Register the fully-resolved
            // specialization on demand, the same RegisterGenericSpecialization the invoke/generic-method
            // paths use (it also wires the __envp field for a capturing LF, keyed by OriginalDefinition).
            if (targetMethod.IsGenericMethod && !_methodFunctions.ContainsKey(targetMethod))
            {
                var constructedLf = SubstituteMethodTypeArgs(targetMethod);
                if (!constructedLf.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
                {
                    targetMethod = constructedLf;
                    if (!_methodFunctions.ContainsKey(targetMethod))
                        RegisterGenericSpecialization(targetMethod);
                }
            }
            if (!_methodSlots.TryGetValue(targetMethod, out var targetSlot))
                throw new System.InvalidOperationException($"Lambda/local function '{targetMethod.Name}' not registered.");
            bridgeExportName = DelegateAbi.BridgeName(targetSlot.VarPrefix);
            // Carry the current type-param map by reference — it is immutable and per-EmitMethod fresh, so
            // it stays valid for the drain (which runs after generic-method emit clears the ambient map).
            _ctx.PendingDelegateBridges.Add((targetMethod, bridgeExportName, _ctx.TypeParamMap));
        }
        else if (targetMethod.IsGenericMethod)
        {
            // Wave-9 round-2 [W7]: a method group of a GENERIC method. The planner never plans bridges
            // for generic definitions (no per-spec layout), so the planner path below ICEd with
            // 'No delegate bridge'. Legal C# whose monomorphization machinery already exists: register
            // the constructed specialization (the same per-call-site registration invocations use) and
            // bridge it via PendingDelegateBridges, exactly like a local function. Same-class targets
            // with fully resolved type args only — a variable receiver would need the RECEIVER's
            // program to export this specialization's bridge (it cannot know the instantiation), and
            // an inherited/foreign generic target has no local body registration — loud per §8-3.
            var constructed = SubstituteMethodTypeArgs(targetMethod);
            bool unresolved = constructed.TypeArguments.Any(ta => ta is ITypeParameterSymbol);
            // Wave-9 round-9 [Y7]: an INHERITED user-base generic method is part of the class
            // family — round-8 [Y11] made its closed specializations emit in THIS program (the
            // call flavor already worked), so the method-group flavor bridges the same on-demand
            // spec. Foreign/SDK declarers stay loud (their bodies never live in this program).
            var declType = constructed.OriginalDefinition.ContainingType;
            bool declaredOnFamily = SymbolEqualityComparer.Default.Equals(declType, _classSymbol);
            if (!declaredOnFamily && declType != null && declType.DeclaringSyntaxReferences.Length > 0
                && declType.Name != "UdonSharpBehaviour"
                && !USugarCompilerHelper.IsFrameworkNamespace(declType.ContainingNamespace))
                for (var bt = _classSymbol.BaseType; bt != null; bt = bt.BaseType)
                    if (SymbolEqualityComparer.Default.Equals(bt, declType)) { declaredOnFamily = true; break; }
            // B58: a foreign generic STATIC (on a plain helper class or a struct) has its spec body
            // inlined into THIS program exactly like a foreign generic static CALL, so — like the
            // non-generic foreign-static delegate arm below — it can bridge through the same registration.
            // It has no receiver (static), so the same-family gate does not apply. B75: it must be a
            // SOURCE-defined, non-framework static (mirroring IsForeignStatic's exclusions) — a framework
            // generic static method group (Array.Empty<int>, no syntax) has no body in this program, so it
            // must fall to the loud reject below, not silently register an EMPTY bridge.
            bool foreignStatic = constructed.IsStatic && declType != null
                && declType.DeclaringSyntaxReferences.Length > 0
                && !USugarCompilerHelper.IsFrameworkNamespace(declType.ContainingNamespace)
                && !ExternResolver.IsUdonSharpBehaviour(declType);
            if (unresolved || targetInstance != null || baseReceiver || (!declaredOnFamily && !foreignStatic))
                throw new System.NotSupportedException(
                    $"A delegate can only be created from generic method '{targetMethod.Name}' when it is "
                    + "declared on the compiled class, an inherited user base class, or a helper class/struct "
                    + "as a static, and every type argument resolves to a concrete type at the creation site "
                    + "(the specialization's bridge must live in this program).");
            RegisterGenericSpecialization(constructed);
            bridgeExportName = DelegateAbi.BridgeName(_methodFunctions[constructed].Name);
            _ctx.PendingDelegateBridges.Add((constructed, bridgeExportName, _ctx.TypeParamMap));
            // B52: advance targetMethod to the registered specialization (mirroring the local-function
            // arm) so the variance/adapter block below enqueues the ADAPTER against the spec that is
            // actually emitted — otherwise the adapter names the raw generic definition, EmitPending-
            // SigAdapterBridges cannot find it in _methodFunctions, and the sig-adapter FuncRef dangles.
            targetMethod = constructed;
        }
        // wave-13 staticro lens (2026-07-04): a static method on a plain (non-UdonSharpBehaviour)
        // helper class is never pre-planned by LayoutPlanner (Phase 1 only discovers
        // UdonSharpBehaviour classes) — GetDelegateBridgeLayout's Plan() call would throw on the
        // frozen planner. A plain (non-delegate) call to the same method already works via
        // CollectForeignStaticCallsInOperation's per-program inlining into _methodFunctions; route
        // the delegate-bridge naming through that same registration instead, exactly like the
        // lambda/local-function/generic-method arms above.
        else if (targetMethod.IsStatic && _methodFunctions.TryGetValue(targetMethod, out var foreignFunc)
            && !ExternResolver.IsUdonSharpBehaviour(targetMethod.ContainingType))
        {
            bridgeExportName = DelegateAbi.BridgeName(foreignFunc.Name);
            _ctx.PendingDelegateBridges.Add((targetMethod, bridgeExportName, _ctx.TypeParamMap));
        }
        // R-M2 (design §2): a method-group binding of a THIS-CLASS private / private-protected method. The
        // planner no longer plans a speculative bridge for it (LayoutPlanner.IsExcludedFromSpeculativeBridge),
        // so GetDelegateBridgeLayout below would throw — register the bridge on demand via
        // PendingDelegateBridges, exactly like the lambda/local-function/generic/foreign-static arms. The
        // binding itself is what makes the bridge needed, so this arm fires for the actual binding (the
        // reentrancy narrowing is preserved: an UNBOUND private method never reaches here and gets no bridge).
        //
        // Both receiver forms are covered because the target is a member of THIS compiled class:
        //   - this-bound (targetInstance == null): same-program dispatch.
        //   - same-class variable receiver (`other.Priv`, other : this class): a legal C# binding (private is
        //     type-scoped, not instance-scoped) that dispatches CROSS-program to another instance of THIS
        //     class. Registering the pending bridge force-exports `__dlg_Priv`, and since `other` is the same
        //     class it is compiled from the same source and exports the same name — the dispatch resolves.
        // A cross-CLASS private binding is not expressible in C# (CS0122), so the target is always this class.
        else if (!baseReceiver
                 && _methodFunctions.TryGetValue(targetMethod, out var privFunc)
                 && LayoutPlanner.IsExcludedFromSpeculativeBridge(targetMethod)
                 && SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, _classSymbol))
        {
            bridgeExportName = DelegateAbi.BridgeName(privFunc.Name);
            _ctx.PendingDelegateBridges.Add((targetMethod, bridgeExportName, _ctx.TypeParamMap));
        }
        else
        {
            var bridge = _planner.GetDelegateBridgeLayout(targetMethod);
            bridgeExportName = bridge.BridgeExportName;
        }

        // Variance hinge (design 2026-07-04 §2.2/§2.3): a method-group binding whose delegate-declared
        // sig-S differs from targetMethod's OWN sig is pure C# reference variance (a lambda's sig is
        // inferred from the delegate type, so op.Target is never IMethodReferenceOperation for one —
        // this arm is unreachable for lambdas by construction, matching §2.2's note). Same-program
        // target (targetInstance == null: this-bound, base.M, a hoisted lambda/local-function, or a
        // same-family generic spec) mints a sig adapter (B-1, direct InternalCall, no extra hop).
        // Third-party target (targetInstance != null) cannot host an adapter (the foreign program can't
        // know at ITS OWN compile time which adapter shapes another class will need) — mint the INNER
        // exact-sig third-party bundle here (identical shape to any third-party bundle: addr=0u, env=
        // null) and hand back a sig-S WRAPPER (B-2) as the outer creation's bridge, with the inner
        // bundle riding DelegateAbi.Env — the wrapper's unified dispatch handles the cross hop generically.
        if (op.Target is IMethodReferenceOperation
            && op.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod is { } delegateInvoke)
        {
            var sigS = DelegateAbi.BuildSigPart(delegateInvoke, _ctx.TypeParamMap);
            if (sigS != DelegateAbi.BuildSigPart(targetMethod, _ctx.TypeParamMap))
            {
                if (targetInstance == null)
                {
                    var targetKey = bridgeExportName.StartsWith("__dlg_")
                        ? bridgeExportName.Substring("__dlg_".Length) : bridgeExportName;
                    var adapterName = DelegateAbi.SigAdapterName(targetKey, sigS);
                    _ctx.PendingSigAdapterBridges.Add((targetMethod, delegateInvoke, adapterName, _ctx.TypeParamMap));
                    return (adapterName, FuncRef(adapterName), targetInstance, envLeaf);
                }

                var innerBundle = DelegateAbi.EmitBundleMint(_builder, () => targetInstance,
                    Const(bridgeExportName, "SystemString"), Const(0u, "SystemUInt32"), Const(null, "SystemObject"));

                // The wrapper's INNER dispatch must speak the inner bundle's OWN protocol — here, the
                // third-party target's OWN signature (targetMethod, sig-T), never sig-S: DelegateAbi.Method names
                // targetMethod's OWN plain bridge (bridgeExportName, planned unconditionally on the
                // FOREIGN class per its speculative-bridge policy), which reads/writes sig-T's conv
                // vars — staging under sig-S would silently drop values across the dispatch.
                var wrapperName = RegisterWrapperSig(delegateInvoke, targetMethod, _ctx.TypeParamMap);
                return (wrapperName, FuncRef(wrapperName), null, innerBundle);
            }
        }

        var funcRef = FuncRef(bridgeExportName);
        return (bridgeExportName, funcRef, targetInstance, envLeaf);
    }

    /// <summary>Virtual dispatch through `this` for PROPERTY/INDEXER accessors (round 7): a property
    /// reference inside an INHERITED base method body statically binds the BASE declaration, so the
    /// this-path accessor lookups must resolve to the most-derived override visible from the compiled
    /// class — the chain-leaf accessor over the chain-leaf storage — exactly like
    /// <see cref="ResolveMostDerivedOverride"/> for MethodKind.Ordinary calls (shares its
    /// <see cref="FindOverridePropertyInChain"/> walker). Without this the lookup hits the
    /// base-instance COPY, which runs the base accessor body (manual props/indexers, pre-existing v2.x)
    /// or reads the base declaration's per-declaration `__basebk` storage (auto-props, post-917d99c).
    /// `base.P` keeps the static binding (the single non-virtual property access in C#), as does every
    /// non-this receiver (cross dispatch is receiver-correct via the planner chain-root layout).</summary>
    protected IPropertySymbol ResolveDispatchProperty(IPropertyReferenceOperation op)
    {
        var prop = op.Property;
        if (!(prop.IsVirtual || prop.IsOverride || prop.IsAbstract)) return prop;
        if (op.Instance is not IInstanceReferenceOperation iref) return prop;
        if (iref.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax) return prop;
        var def = prop.OriginalDefinition;
        return FindOverridePropertyInChain(_classSymbol, def, prop.Name) ?? prop;
    }

    // ── Call helpers ──

    /// <summary>Wave-12 [V2]: a NON-auto property accessor dispatched through a variable receiver
    /// needs an exported entry point (SetProgramVariable + SendCustomEvent), but EmitMethods only
    /// exports PUBLIC accessors — the dispatch of a non-public one targets an event name matching no
    /// .export, a silent no-op on device (VM-proven: the setter body never ran, the cross write was
    /// lost and the getter read a stale return var). Loud per design §8-3, mirroring the
    /// EmitCrossIndexerCall gate and the [J2] non-this method reject. Auto-properties are exempt:
    /// they route through SetProgramVariable/GetProgramVariable on the backing symbol, which needs no
    /// entry point.</summary>
    protected static void RejectNonPublicCrossAccessor(IMethodSymbol accessor, IPropertySymbol prop)
    {
        if (accessor.DeclaredAccessibility != Accessibility.Public)
            throw new System.NotSupportedException(
                $"Property '{prop.Name}' of '{prop.ContainingType.Name}' is accessed through a "
                + "variable receiver, which dispatches cross-program (SetProgramVariable + "
                + "SendCustomEvent) and so needs a public "
                + (accessor.MethodKind == MethodKind.PropertySet ? "setter" : "getter")
                + ". Make the accessor public, or access the property through 'this'.");
    }

    /// <summary>Wave-12 [V2]: TRUE auto-property detection — a compiler-generated backing field is
    /// associated with the property. The cross-arm `DeclaringSyntaxReferences.IsEmpty` checks were
    /// always FALSE for source `{ get; set; }` accessors (same trap UasmEmitter's field-declaration
    /// pass documents), so the SetProgramVariable/GetProgramVariable direct arms were dead and every
    /// cross property access dispatched accessor functions — a silent no-op for NON-public autos,
    /// whose accessors are never exported yet whose backing symbol IS declared on the receiver's
    /// heap. Non-public autos now take the direct-symbol arm (needs no entry point); public
    /// accessors keep the dispatch path byte-for-byte.</summary>
    protected static bool IsNonPublicAutoCrossProperty(IMethodSymbol accessor, IPropertySymbol prop)
        => accessor != null
           && accessor.DeclaredAccessibility != Accessibility.Public
           && prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
               .Any(f => f.IsImplicitlyDeclared && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));

    /// <summary>Wave-9 round-2 [W6]: user-defined indexer accessed through a VARIABLE receiver (an
    /// own-typed copy of this, a base-typed reference, or another behaviour). Only the literal
    /// `this[i]` form had a dispatch path (round-7 [P1]); every variable receiver fell through to
    /// extern resolution against the receiver's Udon-mapped type and emitted a nonexistent
    /// `IUdonEventReceiver.__get_Item` (assembler/validator crash on legal C#). Dispatch the accessor
    /// cross-program like a non-auto property: SetProgramVariable each index (and the value, for the
    /// setter — its LAST parameter) + SendCustomEvent the chain-ROOT export (GetCalleeLayout
    /// normalization), which runs the receiver program's most-derived override. A non-public accessor
    /// has no exported entry point — loud per design §8-3 (mirrors the [J2] non-this method reject).</summary>
    protected CLeaf EmitCrossIndexerCall(IMethodSymbol accessor, CLeaf instanceVal, List<CLeaf> orderedArgs,
        bool reentrant = false)
    {
        if (accessor.DeclaredAccessibility != Accessibility.Public)
            throw new System.NotSupportedException(
                $"Indexer of '{accessor.ContainingType.Name}' is accessed through a variable receiver, "
                + "which dispatches cross-program (SetProgramVariable + SendCustomEvent) and so needs a "
                + "public accessor. Make the accessor public, or access the indexer through 'this'.");
        var (exportName, paramIds, _) = GetCalleeLayout(accessor);
        var pairs = new List<(string, CLeaf)>();
        for (int i = 0; i < orderedArgs.Count && i < paramIds.Length; i++)
            pairs.Add((paramIds[i], orderedArgs[i]));
        var returns = accessor.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : GetCalleeReturns(accessor);
        var retType = accessor.ReturnsVoid ? "SystemVoid" : GetUdonType(accessor.ReturnType);
        return CrossCall(instanceVal, exportName, pairs, returns, retType, reentrant);
    }

    /// <summary>[W6] gate shared by the read/write/compound indexer sites: a user-behaviour indexer
    /// reference through a non-this receiver (the struct/extern receivers keep their own arms).</summary>
    protected static bool IsVariableReceiverBehaviourIndexer(IPropertyReferenceOperation op)
        => op.Property.IsIndexer
           && op.Instance != null && op.Instance is not IInstanceReferenceOperation
           && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
           && op.Property.ContainingType.Name != "UdonSharpBehaviour";

    /// <summary>[W6] index arguments evaluated in source order, slotted by parameter ordinal
    /// (named/reordered index args bind by name, mirroring the [W1] convention).</summary>
    protected List<CLeaf> EvaluateIndexerArgs(IPropertyReferenceOperation op)
    {
        var ordered = new CLeaf[op.Arguments.Length];
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            var p = op.Arguments[i].Parameter;
            var ordinal = p != null && p.Ordinal >= 0 && p.Ordinal < ordered.Length ? p.Ordinal : i;
            ordered[ordinal] = VisitExpression(op.Arguments[i].Value);
        }
        return new List<CLeaf>(ordered);
    }

    /// <summary>True when the type is NOT a behaviour after resolving type parameters — an interface
    /// call/accessor on such a receiver must use externs, not SendCustomEvent dispatch (e.g.
    /// IComparable&lt;T&gt;.CompareTo with T=int). Interface-typed receivers stay undetermined (false).</summary>
    protected bool IsResolvedConcreteNonBehaviour(ITypeSymbol type)
    {
        switch (type)
        {
            case null:
            // Type parameter: resolve via TypeParamMap
            case ITypeParameterSymbol when _typeParamMap == null:
                return false;
            case ITypeParameterSymbol tp:
            {
                if (!_typeParamMap.TryGetValue(tp, out var concrete)) return false;
                return !ExternResolver.IsUdonSharpBehaviour(concrete);
            }
        }

        // Concrete type: if not a UdonSharpBehaviour, interface calls should use extern
        if (type.TypeKind == TypeKind.Interface) return false; // can't determine yet
        return !ExternResolver.IsUdonSharpBehaviour(type);
    }

    /// <summary>Wave-14 r3: interface dispatch (method or property/indexer accessor) is a cross-behaviour
    /// SendCustomEvent bridge — there is no struct-vtable equivalent. If some user STRUCT in the
    /// compilation implements `ifaceType`, a receiver flowing through it MAY be that struct, and the
    /// generated dispatch can never resolve for a struct receiver (SendCustomEvent to a bridge name that
    /// is never exported by any program — VM-proven: infinite self re-entry / stack overflow, not merely a
    /// silent no-op). Reject loudly at the call site rather than emit it. An interface with no struct
    /// implementor is unaffected — including one whose only implementor is a UdonSharpBehaviour not
    /// present in THIS narrow compile (the ordinary, working cross-behaviour dispatch feature; not
    /// rejectable just because no class implementor happens to be visible here). Call this from every
    /// gate that currently reads `!IsResolvedConcreteNonBehaviour(...)` to route to interface dispatch.
    /// </summary>
    protected void GuardInterfaceHasBehaviourImplementor(INamedTypeSymbol ifaceType, string memberName)
    {
        if (ifaceType != null && _planner.InterfaceHasStructImplementor(ifaceType))
            throw new System.NotSupportedException(
                $"Interface member '{ifaceType.Name}.{memberName}' is invoked through an interface-typed "
              + $"receiver, but a struct in this compilation implements '{ifaceType.Name}'. USugar's "
              + "interface dispatch is a cross-behaviour SendCustomEvent bridge with no struct-vtable "
              + "equivalent; calling a struct-implemented interface through an interface-typed reference "
              + "is not supported.");
    }

    /// <summary>Wave-9 round-4 [X4]/[X5]/[X9]: gate + layout lookup for a USER-INTERFACE property or
    /// indexer accessor reached through an interface-typed receiver. The [W6] cross-indexer gate
    /// tests IsUdonSharpBehaviour(Property.ContainingType) — the INTERFACE for these sites — so
    /// indexer read/write/compound and the property compound/inc-dec WRITE-BACK fell through to
    /// extern resolution and emitted nonexistent IUdonEventReceiver.__get_Item/__set_Item/__set_P
    /// externs (UasmValidationException on legal C#). Mirrors the gates of the existing interface
    /// property get/set arms: user interface (SpecialType None), variable receiver, not a resolved
    /// concrete non-behaviour, and the accessor present in the planned interface layout.</summary>
    protected bool TryGetInterfaceAccessorLayout(IPropertyReferenceOperation op, IMethodSymbol accessor,
        out MethodLayout ml)
    {
        ml = null;
        var matched = accessor != null
            && op.Property.ContainingType is INamedTypeSymbol ifaceType
            && ifaceType.TypeKind == TypeKind.Interface
            && ifaceType.SpecialType == SpecialType.None
            && op.Instance != null && op.Instance is not IInstanceReferenceOperation
            && !IsResolvedConcreteNonBehaviour(op.Instance.Type)
            && _planner.GetLayout(ifaceType).Methods.TryGetValue(accessor, out ml);
        if (matched)
            GuardInterfaceHasBehaviourImplementor((INamedTypeSymbol)op.Property.ContainingType, accessor.Name);
        return matched;
    }

    /// <summary>Dispatch an interface property/indexer accessor through its canonical interface
    /// bridge (the `__iface_*` name every implementing class exports), exactly like an interface
    /// METHOD call: SetProgramVariable each ordinal-ordered arg (indexes, then the value for a
    /// setter), SendCustomEvent the bridge, GetProgramVariable the return. Tuple-returning accessors
    /// dispatch the bare export (no bridge), mirroring EmitInterfaceCall. Void accessors self-emit
    /// and return null — never wrap in EmitExprStmt.</summary>
    protected CLeaf EmitInterfaceAccessorCall(IMethodSymbol accessor, MethodLayout ml, CLeaf instanceVal,
        List<CLeaf> orderedArgs, bool reentrant = false)
    {
        var pairs = new List<(string, CLeaf)>();
        for (int i = 0; i < orderedArgs.Count && i < ml.ParamIds.Count; i++)
            pairs.Add((ml.ParamIds[i], orderedArgs[i]));
        var rets = ml.Returns.ToArray();
        if (rets.Length > 1)
            return CrossCall(instanceVal, ml.ExportName, pairs, rets, "SystemVoid", reentrant);
        var dispatchName = LayoutPlanner.InterfaceDispatchName(accessor, ml);
        var retType = accessor.ReturnsVoid ? "SystemVoid" : GetUdonType(accessor.ReturnType);
        return CrossCall(instanceVal, dispatchName, pairs,
            accessor.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : rets, retType, reentrant);
    }

    // ── Delegate value comparison (design §2.5; shared by OperatorHandler `==`/`!=` and the
    // wave-9 round-4 [X1] `.Equals` method arm in InvocationHandler) ──

    /// <summary>Wave-9 round-3 [W4]: cross-program call arguments. Evaluated in TEXTUAL order
    /// (C# evaluation semantics — the values become ANF leaves immediately) and paired with the
    /// param id at the argument's PARAMETER ordinal; pairs returned in ORDINAL order so the
    /// SetProgramVariable stores are canonical (a named/reordered call emits byte-identically to
    /// its declaration-order twin). IInvocationOperation.Arguments is call-site-ordered for
    /// named args — pairing by textual index bound names positionally on every cross-dispatch
    /// path (VM-proven ref=54 vs usugar=45). Positional calls are unchanged (textual == ordinal).</summary>
    protected List<(string, CLeaf)> CrossCallArgPairs(
        System.Collections.Immutable.ImmutableArray<IArgumentOperation> args, string[] paramIds)
    {
        var byOrdinal = new CLeaf[paramIds.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Value.Type is { } argTy)
                RejectProgramLocalCrossBehaviourArgument(argTy);
            var p = args[i].Parameter;
            var ordinal = p != null && p.Ordinal >= 0 && p.Ordinal < byOrdinal.Length ? p.Ordinal : i;
            byOrdinal[ordinal] = VisitExpression(args[i].Value);
        }
        var pairs = new List<(string, CLeaf)>();
        for (int o = 0; o < byOrdinal.Length; o++)
            if (byOrdinal[o] != null)
                pairs.Add((paramIds[o], byOrdinal[o]));
        return pairs;
    }

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
    protected CLeaf EmitCallToMethod(IMethodSymbol target, List<CLeaf> args, SyntaxNode callSite = null)
    {
        if (!_methodFunctions.TryGetValue(target, out var func))
            throw new InvalidOperationException($"No CFunction registered for method '{target.Name}'");
        var retType = func.ReturnType ?? "SystemVoid";

        // Recursion-cycle edge: the callee can re-enter the current method and clobber its param/local fields
        // and shared scratch slots (Udon's flat heap shares them across frames). Record the edge + the named
        // frame fields to save; the post-coalesce InsertRecursionSpills pass wraps the call with spill/reload
        // of those fields PLUS only the slots live across the call — bounded under A-normal form, where an
        // emit-time total-spill of every (now numerous) scratch slot would overflow the software stack.
        // Wave-9 round-9 [Y3]: spilling is per-SITE — a tail-position site (pre-computed syntax-keyed by
        // BuildRecursionInfo, exactly like the dispatch arm's ReentrantDispatchSites) reads nothing of its
        // frame after the call, so it is flagged TailSpared instead of wrapped; ONE non-tail site used to
        // make EVERY site of the callee spill and deep mixed tail/non-tail recursion overflowed the
        // 8192-entry __recurStack (compile-clean VmFault on legal C#).
        if (IsRecursiveEdge(_currentMethod, target))
        {
            bool tailSpared = callSite != null && _ctx.Recursion.TailSparedDirectCallSites != null
                && _ctx.Recursion.TailSparedDirectCallSites.Contains(callSite);
            if (tailSpared)
                return InternalCall(func.Name, args, retType, tailSpared: true);
            _ctx.EnsureRecursionStack();
            _builder.CurrentFunction.RecursiveCalleeNames.Add(func.Name);
            AccumulateRecursionSpillFields(_builder.CurrentFunction);
        }

        return InternalCall(func.Name, args, retType);
    }

    /// <summary>Loud-fail armor for the struct-member-REACHABILITY side of walk-scope drift — the
    /// analogue of <see cref="ClosureEnvLeaf"/> on the delegate-capture side. A user-struct member
    /// only reaches generic extern-signature construction (<c>BuildExternCallSignature</c> for a
    /// call, <c>BuildPropertyGetSignature</c> for an accessor) when it has NO registered CFunction —
    /// i.e. a Phase-1 collector (CollectStructMethodsInOperation / CollectForeignStaticCallsInOperation)
    /// or an on-demand ResolveStructMember arm did not cover this member/reach shape. Historically that
    /// silently minted a bogus <c>SystemObjectArray.__&lt;Name&gt;__…</c> extern that only UasmValidator
    /// or the VM caught, with a message that never named the root cause (this exact shape recurred as
    /// roadmap B41/B46/B47). Fail HERE, where the bogus extern would be born, with a diagnosis instead.
    /// Sound: <see cref="EmitPolicy.IsUserStruct"/> is false for every SDK/native/BCL type, so this can
    /// never fire on a legitimate extern call. The source location and operation kind are appended
    /// automatically by UasmEmitter.TagLocation (the statement/expression dispatch wraps every handler).</summary>
    protected void GuardUserStructMemberReachedExtern(ITypeSymbol containingType, string memberName)
    {
        // CA-M1: the same armor covers a v1 class member (object[]-emulated) — a class instance member
        // that reached the extern path was not routed to its CFunction (collector-scope drift), which
        // would otherwise mint a bogus SystemObjectArray.__<Name>__ extern.
        if (containingType is INamedTypeSymbol ct && EmitPolicy.IsObjectArrayEmulated(ct))
            throw new InvalidOperationException(
                $"user struct/class member '{ct.Name}.{memberName}' reached emission without a registered "
                + "CFunction — a Phase-1 collector or on-demand registration arm does not cover this "
                + "member/reach shape (collector-scope drift; see roadmap B46/B47 family).");
    }

    /// <summary>True when the dispatch invocation at <paramref name="dispatchOp"/> can re-enter the
    /// containing function (design §4.3: containing function on a synthetic-edge-inclusive SCC cycle
    /// AND the dispatch is non-tail — pre-computed syntax-keyed by BuildRecursionInfo). When true,
    /// also registers the frame: ensures the recursion stack and accumulates the named frame fields,
    /// so InsertRecursionSpills wraps the flagged dispatch arms with the spill/reload.</summary>
    protected bool MarkReentrantDispatch(IOperation dispatchOp)
    {
        if (_ctx.Recursion.ReentrantDispatchSites == null || dispatchOp?.Syntax == null
            || !_ctx.Recursion.ReentrantDispatchSites.Contains(dispatchOp.Syntax))
            return false;
        _ctx.EnsureRecursionStack();
        AccumulateRecursionSpillFields(_builder.CurrentFunction);
        return true;
    }

    /// <summary>Wave-12 r2 [V1]: the LOCAL method a variable-receiver / interface cross dispatch
    /// lands on when the receiver holds `this` at runtime — the class family's most-derived
    /// override of the target (the chain-root export the dispatch names runs the receiver
    /// program's own override), or the interface member's local implementation. Null when the
    /// dispatch can never land on this program (foreign class, unimplemented interface, static).
    /// Mirrors UasmEmitter.CrossDispatchLocalTarget (the analysis side that adds the graph edge).</summary>
    protected IMethodSymbol CrossDispatchLocalCallee(IMethodSymbol target)
    {
        if (target == null || target.IsStatic) return null;
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            var impl = (_classSymbol.FindImplementationForInterfaceMember(target)
                        ?? _classSymbol.FindImplementationForInterfaceMember(target.OriginalDefinition))
                       as IMethodSymbol;
            return impl == null ? null : ResolveMostDerivedOverride(impl);
        }
        for (var t = _classSymbol; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target.ContainingType))
                return ResolveMostDerivedOverride(target);
        return null;
    }

    /// <summary>Wave-12 r2 [V1]: true when the cross dispatch at <paramref name="site"/> (a method
    /// invocation or property/indexer accessor access through a variable / interface-typed receiver)
    /// can re-enter the containing function: its local landing method is a recursion-cycle edge from
    /// the current method (BuildRecursionInfo's cross arms) and the site is not tail-spared. When
    /// true, also registers the frame (recursion stack + named spill fields) so InsertRecursionSpills
    /// wraps the flagged SendCustomEvent — with the param copy-ins inside the window
    /// (CExternCall.PreSpillStmts), because a same-program reentrant callee shares the caller's param
    /// heap vars and a copy-in preceding the save would be captured post-clobber.</summary>
    protected bool TryMarkReentrantCrossDispatch(IOperation site, IMethodSymbol staticCallee)
    {
        if (_currentMethod == null) return false;
        var local = CrossDispatchLocalCallee(staticCallee);
        if (local == null || !_ctx.IsRecursiveEdge(_currentMethod, local)) return false;
        if (site?.Syntax != null && _ctx.Recursion.TailSparedDirectCallSites != null
            && _ctx.Recursion.TailSparedDirectCallSites.Contains(site.Syntax))
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
    // the struct receiver, and its in-scope frame locals. Captured locals are shared by reference (the
    // flat-heap sharing IS the closure behaviour) — EXCEPT the wave-9 round-4 [X2]/[X3] case below: a
    // read-only capture cell whose declaring function shares the hoisted node's SCC must be spilled,
    // because a dispatch can re-enter that function and re-seed the one flat slot (fresh-environment
    // semantics). The SLOTS to spill are computed per call site from post-coalesce liveness by
    // InsertRecursionSpills, so they are not collected here.
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
                if (i < _currentMethod.Parameters.Length)
                {
                    var param = _currentMethod.Parameters[i];
                    if (param.RefKind is RefKind.Ref or RefKind.Out)
                        continue;
                    // Stage 2 §6.2 (E5 corollary): a captured param is consumed into the env record at
                    // MethodEntry (§3.1) and every later read routes through env (§4.1), so its param
                    // field is dead across the recursive call — the env carrier (the closure's env-ref
                    // local / bundle) is what the existing spill preserves. Spilling the dead field is
                    // the wastefully-conservative over-spill the entry criteria forbid. Definition-keyed
                    // via TryGetEnvBinding (constructed specs re-key through OriginalDefinition, §2 rule 2).
                    if (_ctx.TryGetEnvBinding(param, out _))
                        continue;
                }
                AddField(pids[i]);
            }
        AddField(_ctx.CurrentStructReceiverParamId);
        // Only the CURRENT method's own locals are frame-local and need spilling. LocalBindings is a
        // persistent class-wide map (survives scope pop for capture resolution), so before wave-9
        // round-8 [Y9] a non-hoisted method spilled every local of every PREVIOUSLY EMITTED method
        // too — per-frame __recurStack cost scaled with the FUNCTION COUNT of a mutual-recursion
        // cycle (a 7-member ring with 6 locals each exhausted the 512-entry stack at ~21 frames,
        // compile-clean VmFault; the CLR completes). Each frame saving its OWN fields at its own
        // recursive-edge sites is the complete discipline — another method's locals are saved by
        // that method's frames. For a hoisted function (local function or lambda) the same filter
        // additionally keeps captured enclosing locals shared by reference (C# closure semantics —
        // pre-existing behaviour); generic specs/base copies emit under the CONSTRUCTED symbol while
        // their locals belong to the DEFINITION, so match through OriginalDefinition too.
        foreach (var kv in _localBindings)
        {
            if (kv.Key.ContainingSymbol is not IMethodSymbol localOwner
                || _currentMethod == null
                || (!SymbolEqualityComparer.Default.Equals(localOwner, _currentMethod)
                    && (_currentMethod.OriginalDefinition == null
                        || !SymbolEqualityComparer.Default.Equals(localOwner, _currentMethod.OriginalDefinition))))
                continue;
            AddField(kv.Value.Id);
        }

        return fields;
    }
}
