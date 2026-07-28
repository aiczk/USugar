using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

internal sealed class LoweringServices
{
    internal readonly LoweringState _state;

    public LoweringServices(LoweringState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    // Explicit handler-facing dependencies. The underscored shims below remain private to the
    // lowering implementation while it is split into narrower services; handlers must not couple
    // themselves to that implementation detail.
    internal LoweringState State => _state;
    internal Compilation Compilation => _compilation;
    internal INamedTypeSymbol ClassSymbol => _classSymbol;
    internal CoreBuilder Builder => _builder;
    internal FrozenLayoutPlan Planner => _planner;
    internal IReadOnlyDictionary<IMethodSymbol, FlatFunction> MethodFunctions => _methodFunctions;
    internal IReadOnlyDictionary<IMethodSymbol, ReturnSlot[]> MethodReturns => _methodReturns;
    internal IReadOnlyDictionary<IMethodSymbol, string[]> MethodParamVarIds => _methodParamVarIds;
    internal IMethodSymbol CurrentMethod
    {
        get => _currentMethod;
        set => _currentMethod = value;
    }
    internal IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap => _typeParamMap;
    internal Dictionary<ILocalSymbol, LocalBinding> LocalBindings => _localBindings;
    internal Stack<CLeaf> ConditionalAccessStack => _conditionalAccessStack;
    internal Stack<List<(CLeaf val, ITypeSymbol type)>> UsingDisposableStack => _usingDisposableStack;
    internal List<EmitDiagnostic> Diagnostics => _diagnostics;

    // Internal projections used across the lowering concern files.
    internal Compilation _compilation => _state.Compilation;
    internal INamedTypeSymbol _classSymbol => _state.ClassSymbol;
    internal FlatModule _module => _state.Module;
    internal CoreBuilder _builder => _state.Builder;
    internal FrozenLayoutPlan _planner => _state.Planner;
    internal IReadOnlyDictionary<IMethodSymbol, FlatFunction> _methodFunctions => _state.Methods.Functions;
    internal IReadOnlyDictionary<IMethodSymbol, MethodSlot> _methodSlots => _state.Methods.Slots;
    internal IReadOnlyDictionary<IMethodSymbol, ReturnSlot[]> _methodReturns => _state.Methods.Returns;
    internal IReadOnlyDictionary<IMethodSymbol, string[]> _methodParamVarIds => _state.Methods.ParamVarIds;
    internal IMethodSymbol _currentMethod { get => _state.Methods.CurrentMethod; set => _state.Methods.CurrentMethod = value; }
    internal IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
        _typeParamMap => _state.TypeParamMap;
    internal Dictionary<ILocalSymbol, LocalBinding> _localBindings => _state.Storage.LocalBindings;
    internal List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> _fieldInitOps => _state.FieldInitOps;
    internal Dictionary<string, string> _fieldChangeCallbacks => _state.FieldChangeCallbacks;
    internal Stack<CLeaf> _conditionalAccessStack => _state.ConditionalAccessStack;
    internal Stack<List<(CLeaf val, ITypeSymbol type)>> _usingDisposableStack => _state.UsingDisposableStack;
    internal List<EmitDiagnostic> _diagnostics => _state.Diagnostics;
    internal bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => _state.Recursion.IsRecursiveEdge(caller, callee);

    // Recursive descent has one concrete owner.
    internal void VisitOperation(IOperation op) => _state.Operations.VisitOperation(op);
    internal CLeaf VisitExpression(IOperation op) => _state.Operations.VisitExpression(op);
    internal LoweredValue VisitLoweredExpression(IOperation op)
        => _state.Operations.VisitLoweredExpression(op);
    internal CLeaf EmitPatternCheck(CLeaf value, ITypeSymbol valueType, IPatternOperation pattern)
        => _state.Operations.EmitPatternCheck(value, valueType, pattern);

    CallSiteBindingScope RequireBindingScope(IOperation operation, string what)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        return _state.CurrentBindingScope
            ?? throw new InvalidOperationException(
                what + " is being lowered outside a bound semantic scope.");
    }

    internal BoundCallSite RequireBoundCallSite(
        IOperation operation,
        CallableSiteKind kind)
    {
        var scope = RequireBindingScope(operation, $"Callable site '{operation?.Syntax}'");
        return _state.Program.RequireCallSite(
            operation.Syntax, kind, scope);
    }

    internal IMethodSymbol RequireBoundCallable(
        IOperation operation,
        CallableSiteKind kind)
        => RequireRegisteredCallable(
            RequireBoundCallSite(
                operation, kind).Target);

    internal IMethodSymbol RequireBoundTarget(
        IOperation operation,
        CallableSiteKind kind)
        => RequireBoundCallSite(
            operation, kind).Target;

    internal IMethodSymbol RequireBoundDeconstruction(IOperation operation)
    {
        var scope = RequireBindingScope(operation, $"Deconstruction '{operation?.Syntax}'");
        return _state.Program.RequireDeconstruction(
            operation, scope);
    }

    internal ClosedConversionPlan RequireBoundConversion(
        IConversionOperation operation)
    {
        var scope = RequireBindingScope(operation, $"Conversion '{operation?.Syntax}'");
        return _state.Program.RequireConversion(
            operation, scope);
    }

    internal RuntimeShape SourceShape(ITypeSymbol type)
    {
        if (type == null)
            return default;
        return _state.Types.SourceShape(
            type, _state.TypeParamMap);
    }

    internal bool IsUserClass(ITypeSymbol type)
        => SourceShape(type).Bundle == RuntimeBundleKind.Class;

    internal bool IsAggregateValue(ITypeSymbol type)
        => SourceShape(type).Bundle == RuntimeBundleKind.Aggregate;

    internal bool IsObjectArrayEmulated(ITypeSymbol type)
    {
        var bundle = SourceShape(type).Bundle;
        return bundle is
            RuntimeBundleKind.Aggregate
            or RuntimeBundleKind.Class;
    }

    internal bool ContainsUserClassPayload(ITypeSymbol type)
        => SourceShape(type).ContainsUserClassPayload;

    internal bool IsUserStruct(ITypeSymbol type)
        => IsAggregateValue(type)
           && ResolveType(type) is INamedTypeSymbol named
           && named.TypeKind == TypeKind.Struct
           && !named.IsTupleType;

    internal BoundExtern RequireBoundAbi(
        IOperation operation,
        BoundAbiRole role)
    {
        var scope = RequireBindingScope(operation, $"ABI role '{role}' for '{operation?.Syntax}'");
        return _state.BoundAbi.RequireOperation(
            operation, scope, role);
    }

    internal BoundExtern RequireParamsInvocation(
        IOperation operation,
        out bool expand)
    {
        var scope = RequireBindingScope(operation, $"Params ABI for '{operation?.Syntax}'");
        return _state.BoundAbi.RequireParamsInvocation(
            operation, scope, out expand);
    }

    /// <summary>
    /// Udon array constructors consume an Int32 length even though C# accepts every integral array
    /// dimension type. Normalize at the allocation choke so UInt32/Int64/etc. never reach the wrapper
    /// in a differently typed strongbox.
    /// </summary>
    internal CLeaf EmitArrayDimension(IOperation dimension)
    {
        var value = VisitExpression(dimension);
        var sourceType = value.Type.Name;
        if (sourceType == StorageTypes.Int32.Name) return value;
        if (ExternResolver.IntInfo(sourceType).rank == 0)
            throw new NotSupportedException(
                $"Array dimension type '{sourceType}' has no integral Udon representation.");
        return ExternCall(
            UdonAbi.Convert(sourceType, StorageTypes.Int32.Name),
            new List<CLeaf> { value },
            StorageTypes.Int32);
    }

    // A `checked` context asks the runtime to trap integer overflow, but the Udon VM has no overflow
    // trap — the arithmetic silently wraps where C# would throw OverflowException. `unchecked`/default
    // wrapping IS USugar's behavior (C#-correct), so only an explicit `checked` (IsChecked==true) rejects.
    internal static void RejectChecked(bool isChecked)
    {
        if (isChecked)
            throw new NotSupportedException(
                "A 'checked' context is not supported: the Udon VM has no integer-overflow trap, so "
                + "overflow silently wraps. Remove 'checked' (wrapping is the only available behavior) "
                + "or guard the range yourself.");
    }


    // ── Type resolution ──
    internal StorageType GetStorageType(ITypeSymbol type)
        => _state.ResolveStorageType(type);
    internal string GetStorageTypeName(ITypeSymbol type) => GetStorageType(type).Name;
    internal ITypeSymbol ResolveType(ITypeSymbol type)
        => _state.ResolveSourceType(type);
    /// <summary>
    /// Resolve through the active specialization before asking whether a source value has aggregate
    /// semantics. Generic members expose their declaration type (for example T) even while lowering
    /// Box&lt;Inner&gt;, so raw-symbol shape checks are not authoritative.
    /// </summary>
    internal INamedTypeSymbol ResolveAggregateValueType(ITypeSymbol type)
    {
        var resolved = ResolveType(type) as INamedTypeSymbol;
        return resolved != null && IsAggregateValue(resolved)
            ? resolved
            : null;
    }
    internal bool IsFoldedEnum(ITypeSymbol type)
        => _state.Types.IsFoldedEnum(ResolveType(type));
    internal string GetArrayType(IArrayTypeSymbol arrType) => GetStorageTypeName(arrType);
    internal string GetArrayElemType(IArrayTypeSymbol arrType)
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
    internal ITypeSymbol ResolveExternOwnerType(ITypeSymbol memberContainingType, ITypeSymbol receiverType, string memberName)
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
        if (IsAggregateValue(recv))
            throw new NotSupportedException(
                $"'{memberName}' on user-defined struct '{recv.Name}' is not supported: Udon has no extern "
                + "for it and C#'s ValueType semantics (field-wise Equals, type-name ToString) cannot be "
                + "emulated. Compare/format the struct's fields directly instead.");
        return recv;
    }

    // Layer-2 runtime-type-test choke point (is / switch / as). Session lowering is non-injective:
    // it folds many distinct CLR types onto one Udon runtime tag (every delegate/struct/
    // tuple/array-of-those + object[] → SystemObjectArray; UdonSharpBehaviour + every derived type + every
    // user interface → IUdonEventReceiver; a user enum → its underlying int; Nullable<T> → a box). A
    // runtime type test against such a type CANNOT discriminate it — it matches ANY same-tag value and
    // silently takes the wrong branch. Reject loudly (design §8-3); bare `object` and uniquely-tagged
    // SDK/native types stay distinguishable and compile.
    internal CLeaf EmitTypeCheck(CLeaf valueVal, ITypeSymbol targetType)
    {
        // CA-v2b-1 (charter #2): a runtime type test against a v1 user-class FAMILY is answered by
        // hop-zero ReferenceEquals of the value's bundle[0] against the compile-time-enumerated typeobjs
        // of every MINTED class that is-or-derives-from the target (closed-world). A laundered value's
        // slot 0 (delegate KindTag / env Kind / struct first field / tuple / Foo[] element) is never a
        // family typeobj, so this stays sound for the laundered five without a per-node guard (charter #7).
        var resolvedTarget = ResolveType(targetType);
        var targetShape = _state.Types.SourceShape(
            targetType, _state.TypeParamMap);
        if (targetShape.IsBundle)
        {
            ClassAbiPolicy.AssertClosed(
                resolvedTarget, "runtime type test");
            if (targetShape.Bundle
                    == RuntimeBundleKind.Delegate
                && _state.Program.Types
                    .DelegateRuntimeTestNeedsVariantAdapter(
                        targetType,
                        _state.TypeParamMap))
                throw new NotSupportedException(
                    $"Runtime type test against delegate "
                    + $"'{resolvedTarget.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + "cannot safely recover a variant delegate from object: "
                    + "the matching runtime value needs a signature adapter "
                    + "that an erased cast cannot bind statically.");
            var acceptedTypeIds = new List<CLeaf>();
            if (targetShape.Bundle == RuntimeBundleKind.Class
                && resolvedTarget is INamedTypeSymbol targetClass)
                acceptedTypeIds.AddRange(
                    _state.ClassTypes
                        .TypeObjVarsAssignableTo(targetClass)
                        .Select(v => (CLeaf)LoadField(
                            v, StorageTypes.String)));
            else
                acceptedTypeIds.Add(Const(
                    BundleAbi.RuntimeTypeId(resolvedTarget),
                    StorageTypes.String));
            if (acceptedTypeIds.Count == 0)
                return Const(false, StorageTypes.Boolean);
            // Charter #7 soundness: read bundle[0] ONLY when the value is actually a SystemObjectArray
            // (a class bundle — also structs/tuples/delegates/env/Foo[], whose [0] is never a family
            // typeobj, so the compare is false for them). An `object`-typed value holding a scalar or a
            // typed array (int[]) is NOT an object[] → the read would fault, so the read and the whole
            // ReferenceEquals chain live INSIDE the guard. IsInstanceOfType(null,·) is false → null too.
            var isBundle = BundleProbe.IsTagged(
                _builder, valueVal, BundleAbi.Prefix);
            var guarded = _state.Builder.AllocScratch(StorageTypes.Boolean);
            EmitAssign(guarded, Const(false, StorageTypes.Boolean));
            _builder.EmitIf(isBundle, _ =>
            {
                var typeSlot = AggregateAbi.ReadSlot(_builder, valueVal, BundleAbi.Type, StorageTypes.String);
                CLeaf test = null;
                foreach (var typeId in acceptedTypeIds)
                {
                    var eq = ExternCall(UdonAbi.StringEquality,
                        new List<CLeaf> { typeSlot, typeId },
                        StorageTypes.Boolean);
                    test = test == null ? eq
                        : ExternCall(UdonAbi.BooleanLogicalOr,
                            new List<CLeaf> { test, eq }, StorageTypes.Boolean);
                }
                EmitAssign(guarded, test);
            }, null);
            return SlotRef(guarded);
        }
        ClassAbiPolicy.ValidateRuntimeTypeTest(
            resolvedTarget, _state.TypeParamMap,
            _state.Types);
        // The type token is baked through the shared choke point (B51 silent-class armor: an unresolved
        // type parameter would bake a null System.Type constant no validator catches → loud reject there).
        return ExternCall(
            UdonAbiKey.Method("SystemType", "IsInstanceOfType", new[] { "SystemObject" }, "SystemBoolean"),
            new List<CLeaf> { ConstTypeToken(targetType), valueVal },
            StorageTypes.Boolean);
    }

    // The single place a System.Type CONSTANT (type token) is baked — `o is T`, `typeof(T)`, and the
    // GetComponent<T> type-token arg all route here. A SystemType const is a heap constant no validator
    // checks, so an UNRESOLVED type parameter would silently resolve to a null System.Type and NRE at
    // runtime (B51 silent class) — reject loudly instead. The IUdonEventReceiver collapse tag is not
    // VM-resolvable as a token; the concrete UdonBehaviour type is (GetComponent<T>'s prior remap).
    internal CLeaf ConstTypeToken(ITypeSymbol typeSymbol)
        => Const(TypeTokenName(typeSymbol), StorageTypes.Type);

    /// <summary>The Udon type name a <see cref="ConstTypeToken"/> bakes. Split out so a caller that
    /// must reason about the token's runtime identity — a generic extern whose dispatch keys on it —
    /// asks the same producer instead of recomputing the collapse un-fold.</summary>
    internal string TypeTokenName(ITypeSymbol typeSymbol)
        => TypeTokenName(typeSymbol, _typeParamMap);

    internal string TypeTokenName(
        ITypeSymbol typeSymbol,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap)
    {
        if (_state.Types.Resolve(
                typeSymbol, typeParameterMap)
            is ITypeParameterSymbol unresolvedTp)
            throw new NotSupportedException(
                $"A System.Type token for unresolved type parameter '{unresolvedTp.Name}' cannot be emitted: "
                + "its type argument did not reach this emit site (a generic-instantiation map gap). The token "
                + "would bake a null System.Type constant and fault at runtime.");
        var name = _state.Types.GetStorageType(
            typeSymbol, typeParameterMap).Name;
        return name == "VRCUdonCommonInterfacesIUdonEventReceiver" ? "VRCUdonUdonBehaviour" : name;
    }

    // ── Core IR convenience methods ──

    /// <summary>Emit: slot = expr</summary>
    internal void EmitAssign(int destSlot, CValue value) => _builder.EmitAssign(destSlot, value);

    /// <summary>Emit: fieldName = expr</summary>
    internal void EmitStoreField(string fieldName, CLeaf value) => _builder.EmitStoreField(fieldName, value);

    /// <summary>Emit: return [value]</summary>
    internal void EmitReturn(CLeaf value = null) => _builder.EmitReturn(value);

    /// <summary>Create a constant.</summary>
    internal CConst Const(object value, StorageType type) => _builder.Const(value, type);

    /// <summary>Create a slot reference expression.</summary>
    internal CSlotRef SlotRef(int slotId) => _builder.SlotRef(slotId);

    /// <summary>Read a field's value — materialized to a scratch slot (A-normal form), returns the leaf.</summary>
    internal CSlotRef LoadField(string fieldName, StorageType type) => _builder.LoadField(fieldName, type);

    /// <summary>Create a field address reference (for extern out/ref).</summary>
    internal CFieldAddr FieldAddr(string fieldName, StorageType type) => _builder.FieldAddr(fieldName, type);

    /// <summary>Create an explicit codegen-free destination-typed view of a materialized value.</summary>
    internal CLeaf RepresentationCast(CLeaf source, StorageType type, RepresentationCastKind kind)
        => _builder.RepresentationCast(source, type, kind);

    /// <summary>
    /// Materialize an expression value in the exact storage type declared by
    /// an extern parameter. Signature selection and argument adaptation are
    /// separate concerns: generic erasure may change the expression storage,
    /// but it must never change the ABI prototype.
    /// </summary>
    internal CLeaf AdaptExternArgument(CLeaf value, ITypeSymbol declaredType)
    {
        var expected = GetStorageType(declaredType);
        if (value.Type == expected) return value;
        var slot = _builder.AllocScratch(expected);
        EmitAssign(slot, value);
        return SlotRef(slot);
    }

    /// <summary>Emit an extern call, materialized to a scratch slot (returns the leaf; null for void).</summary>
    internal CSlotRef ExternCall(UdonAbiKey sig, List<CLeaf> args, StorageType retType)
        => _builder.ExternCall(sig, args, retType);

    internal CSlotRef ExternCall(BoundExtern bound, List<CLeaf> args, StorageType retType)
        => _builder.ExternCall(bound, args, retType);

    /// <summary>
    /// Integer conversion matching C# *unchecked* semantics (wrap / bit-reinterpret). Udon's
    /// SystemConvert.ToX is CHECKED and throws on overflow, so a narrowing / cross-sign integer cast is
    /// reduced to its low 32 bits (sign-extended via a 64-bit shift) before the final in-range convert.
    /// Lossless widenings (and non-integer conversions) use the plain convert extern directly. The 64-bit
    /// unsigned cases require unchecked 64-bit ops Udon does not expose and fall back to the checked convert.
    /// </summary>
    internal CLeaf EmitNarrowingConvert(CLeaf value, string fromUdonType, string toUdonType)
    {
        if (fromUdonType == toUdonType)
            return value;

        // long <-> ulong is a pure bit reinterpret in C# (the cast is unchecked), but Convert.To{U}Int64 is
        // CHECKED and throws on a high-bit-set value (e.g. (ulong)(-1L)). Round-trip the 8 bytes instead.
        if ((fromUdonType == "SystemInt64" && toUdonType == "SystemUInt64")
            || (fromUdonType == "SystemUInt64" && toUdonType == "SystemInt64"))
        {
            var bytes = ExternCall(UdonAbiKey.Method("SystemBitConverter", "GetBytes",
                    new[] { fromUdonType }, "SystemByteArray"),
                new List<CLeaf> { value }, StorageTypes.ByteArray);
            var toMethod = toUdonType == "SystemUInt64" ? "ToUInt64" : "ToInt64";
            return ExternCall(UdonAbiKey.Method("SystemBitConverter", toMethod,
                    new[] { "SystemByteArray", "SystemInt32" }, toUdonType),
                new List<CLeaf> { bytes, Const(0, StorageTypes.Int32) }, new StorageType(toUdonType));
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
            return ExternCall(UdonAbi.Convert(fromUdonType, toUdonType),
                new List<CLeaf> { value }, new StorageType(toUdonType));

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
        // Unreachable for the supported integer target set; defensive default.
        return ExternCall(UdonAbi.Convert(fromUdonType, toUdonType),
            new List<CLeaf> { value }, new StorageType(toUdonType));
    }

    /// <summary>CW18: tolerant re-tag of a small-underlying nullable's boxed value. The boxed-object ABI
    /// admits a plain-int tag inside a small-int/char (or small-underlying user-enum) nullable box — the
    /// lifted-operator/pattern consumers tolerate it via <see cref="NullableAbi.PromoteBoxedToInt32"/>, but
    /// the value accessors (.Value / GetValueOrDefault / ??) copied the raw box into a strict
    /// underlying-typed slot, and the next strict-typed extern read HeapTypeMismatch-faulted the VM.
    /// Promote with the same ToInt32(SystemObject) tolerance, then narrow back to the underlying tag (a
    /// null box keeps the B18 deviation: Convert.ToInt32(null) is 0 → default). Non-small underlyings
    /// return the box untouched.</summary>
    internal CLeaf RetagSmallNullablePresent(CLeaf boxedValue, ITypeSymbol underlying)
    {
        var uType = GetStorageTypeName(underlying);
        if (!ExternResolver.IsSmallIntOrChar(uType))
            return boxedValue;
        var promoted = NullableAbi.PromoteBoxedToInt32(_builder, boxedValue, underlying,
            _compilation.GetSpecialType(SpecialType.System_Int32), GetStorageTypeName).Value;
        return EmitNarrowingConvert(promoted, "SystemInt32", uType);
    }

    /// <summary>Shared scrutinee-vs-constant equality lowering (the constant-pattern arm and the CW19
    /// nullable switch single-value clause): enum operands compare on the underlying type; a null
    /// constant compares with SystemObject equality; a small-int/char (or small-underlying enum) pair
    /// promotes BOTH sides to int32 — the scrutinee box may carry a boxed plain int rather than the
    /// strict small-int tag, and ToInt32(SystemObject) tolerates any boxed numeric — then compares
    /// with the int32 extern, like the binary path.</summary>
    internal CLeaf EmitConstantEquality(CLeaf valueVal, ITypeSymbol valueType, CLeaf constVal, bool constIsNull)
    {
        var convertedValueVal = EmitEnumToUnderlying(valueVal, valueType);
        constVal = EmitEnumToUnderlying(constVal, valueType);
        var underlyingSym = valueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum
            ? named.EnumUnderlyingType : valueType;
        var eqType = GetStorageTypeName(underlyingSym);
        if (constIsNull)
            eqType = "SystemObject"; // null comparisons use SystemObject equality
        else if (ExternResolver.IsSmallIntOrChar(eqType))
        {
            convertedValueVal = NullableAbi.PromoteBoxedToInt32(_builder, convertedValueVal, underlyingSym,
                _compilation.GetSpecialType(SpecialType.System_Int32), GetStorageTypeName).Value;
            constVal = NullableAbi.PromoteBoxedToInt32(_builder, constVal, underlyingSym,
                _compilation.GetSpecialType(SpecialType.System_Int32), GetStorageTypeName).Value;
            eqType = "SystemInt32";
        }
        return ExternCall(
            UdonAbiKey.Method(
                eqType, "op_Equality", new[] { eqType, eqType }, "SystemBoolean"),
            new List<CLeaf> { convertedValueVal, constVal },
            StorageTypes.Boolean);
    }

    /// <summary>Low 32 bits of an integer value as a SIGNED int32 (C# unchecked reinterpret). Sources wider than
    /// int32 are reduced by a 64-bit sign-extending shift; ≤32-bit sources widen losslessly to int64 first.</summary>
    CLeaf LowInt32Bits(CLeaf value, string fromUdonType)
    {
        if (fromUdonType == "SystemInt32")
            return value;
        var asLong = fromUdonType == "SystemInt64"
            ? value
            : ExternCall(UdonAbi.Convert(fromUdonType, "SystemInt64"),
                new List<CLeaf> { value }, StorageTypes.Int64);
        // (x << 32) >> 32 : arithmetic right shift sign-extends bit 31 → value in [-2^31, 2^31), safe to ToInt32.
        var shl = ExternCall(UdonAbiKey.Method("SystemInt64", "op_LeftShift", new[] { "SystemInt64", "SystemInt32" }, "SystemInt64"),
            new List<CLeaf> { asLong, Const(32, StorageTypes.Int32) }, StorageTypes.Int64);
        var sar = ExternCall(UdonAbiKey.Method("SystemInt64", "op_RightShift", new[] { "SystemInt64", "SystemInt32" }, "SystemInt64"),
            new List<CLeaf> { shl, Const(32, StorageTypes.Int32) }, StorageTypes.Int64);
        return ExternCall(UdonAbiKey.Method("SystemConvert", "ToInt32", new[] { "SystemInt64" }, "SystemInt32"),
            new List<CLeaf> { sar }, StorageTypes.Int32);
    }

    /// <summary>Reinterpret an int32 bit pattern as uint32 (C# unchecked (uint)int): negatives map to +2^32.</summary>
    CLeaf Int32BitsToUInt32(CLeaf int32Val)
    {
        var asLong = ExternCall(UdonAbiKey.Method("SystemConvert", "ToInt64", new[] { "SystemInt32" }, "SystemInt64"),
            new List<CLeaf> { int32Val }, StorageTypes.Int64);
        var isNeg = ExternCall(UdonAbiKey.Method("SystemInt64", "op_LessThan", new[] { "SystemInt64", "SystemInt64" }, "SystemBoolean"),
            new List<CLeaf> { asLong, Const(0L, StorageTypes.Int64) }, StorageTypes.Boolean);
        var plus = ExternCall(UdonAbiKey.Method("SystemInt64", "op_Addition", new[] { "SystemInt64", "SystemInt64" }, "SystemInt64"),
            new List<CLeaf> { asLong, Const(4294967296L, StorageTypes.Int64) }, StorageTypes.Int64);
        var wrapped = Select(isNeg, plus, asLong, StorageTypes.Int64);
        return ExternCall(UdonAbiKey.Method("SystemConvert", "ToUInt32", new[] { "SystemInt64" }, "SystemUInt32"),
            new List<CLeaf> { wrapped }, StorageTypes.UInt32);
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
        => ExternCall(UdonAbi.Convert("SystemInt32", toUdonType),
            new List<CLeaf> { inRangeInt }, new StorageType(toUdonType));

    // ((x % mod) + mod) % mod  →  [0, mod)  : C# unsigned narrowing wrap
    CLeaf ModWrap(CLeaf x, int mod)
    {
        var add = ExternCall(UdonAbi.Int32Add,
            new List<CLeaf> { Rem(x, mod), Const(mod, StorageTypes.Int32) }, StorageTypes.Int32);
        return Rem(add, mod);
    }

    CLeaf Rem(CLeaf x, int mod)
        => ExternCall(UdonAbiKey.Method("SystemInt32", "op_Remainder", new[] { "SystemInt32", "SystemInt32" }, "SystemInt32"),
            new List<CLeaf> { x, Const(mod, StorageTypes.Int32) }, StorageTypes.Int32);

    // (x << s) >> s  →  signed (32-s)-bit truncation with sign extension
    CLeaf ShiftTruncate(CLeaf x, int shift)
    {
        var left = ExternCall(UdonAbiKey.Method("SystemInt32", "op_LeftShift", new[] { "SystemInt32", "SystemInt32" }, "SystemInt32"),
            new List<CLeaf> { x, Const(shift, StorageTypes.Int32) }, StorageTypes.Int32);
        return ExternCall(UdonAbiKey.Method("SystemInt32", "op_RightShift", new[] { "SystemInt32", "SystemInt32" }, "SystemInt32"),
            new List<CLeaf> { left, Const(shift, StorageTypes.Int32) }, StorageTypes.Int32);
    }

    /// <summary>True for the integer Udon types whose op_Remainder extern does not exist (Int64/UInt64, and
    /// also UInt32 — Udon ships uint Division/Multiplication/Subtraction but no uint Remainder).</summary>
    internal static bool RemainderNeedsPolyfill(string udonType)
        => udonType is "SystemInt64" or "SystemUInt64" or "SystemUInt32";

    /// <summary>Remainder polyfill for types lacking an op_Remainder extern (see RemainderNeedsPolyfill): lower
    /// `a % b` to `a - (a / b) * b` using the matching signed/unsigned Division/Multiplication/Subtraction.
    /// Truncate-toward-zero (signed) / floor (unsigned) division makes this exact for every case, including
    /// unsigned dividends above int.MaxValue. Shared by the binary and compound paths.</summary>
    internal CLeaf EmitRemainderViaDivision(
        IOperation operation,
        CLeaf left,
        CLeaf right,
        string t)
    {
        // left/right are CLeaf params — stable single-assignment leaves under ANF; the intermediate
        // ExternCall results each bind their own fresh scratch, so neither operand is mutated here.
        var quot = ExternCall(
            RequireBoundAbi(
                operation, BoundAbiRole.RemainderDivision),
            new List<CLeaf> { left, right }, new StorageType(t));
        var prod = ExternCall(
            RequireBoundAbi(
                operation, BoundAbiRole.RemainderMultiplication),
            new List<CLeaf> { quot, right }, new StorageType(t));
        return ExternCall(
            RequireBoundAbi(
                operation, BoundAbiRole.RemainderSubtraction),
            new List<CLeaf> { left, prod }, new StorageType(t));
    }

    /// <summary>Emit a void extern call as a statement. <paramref name="reentrant"/> marks a
    /// delegate-dispatch arm that can re-enter the containing function (design §4.3). preSpillStmts:
    /// wave-12 r2 [V1], see CExternCall.PreSpillStmts (cross setter copy-ins inside the wrap).</summary>
    internal void EmitExternVoid(UdonAbiKey sig, List<CLeaf> args, bool reentrant = false, int preSpillStmts = 0)
        => _builder.EmitExternVoid(sig, args, reentrant, preSpillStmts);

    internal void EmitExternVoid(BoundExtern bound, List<CLeaf> args, bool reentrant = false, int preSpillStmts = 0)
        => _builder.EmitExternVoid(bound, args, reentrant, preSpillStmts);

    /// <summary>Create an internal call expression.</summary>
    internal CSlotRef InternalCall(string funcName, List<CLeaf> args, StorageType retType, bool tailSpared = false)
        => _builder.InternalCall(funcName, args, retType, tailSpared);

    /// <summary>Emit a cross-behaviour call. Single-return → materialized to a scratch slot (returns the
    /// leaf); void or multi-return → side-effecting statement (returns null). reentrant: wave-12 r2
    /// [V1] — this dispatch can re-enter the containing function (see TryMarkReentrantCrossDispatch).</summary>
    internal CSlotRef CrossCall(CLeaf instance, string eventName,
        IReadOnlyList<CrossCallParameter> parameters, IReadOnlyList<ReturnSlot> returns, StorageType retType,
        bool reentrant = false)
        => CrossCall(instance, Const(eventName, StorageTypes.String),
            parameters, returns, retType, reentrant);

    internal CSlotRef CrossCall(CLeaf instance, CLeaf eventName,
        IReadOnlyList<CrossCallParameter> parameters, IReadOnlyList<ReturnSlot> returns, StorageType retType,
        bool reentrant = false)
        => _builder.CrossCall(instance,
            new CrossCallTransportPlan(eventName, parameters, returns, retType), reentrant);

    internal CSlotRef LoadProgramVariable(CLeaf instance, string variableName, StorageType type)
        => _builder.LoadProgramVariable(instance, Const(variableName, StorageTypes.String), type);

    internal CSlotRef LoadProgramVariable(CLeaf instance, CLeaf variableName, StorageType type)
        => _builder.LoadProgramVariable(instance, variableName, type);

    internal void StoreProgramVariable(CLeaf instance, string variableName,
        StorageType variableType, CLeaf value)
        => _builder.EmitProgramVariableStore(
            instance, Const(variableName, StorageTypes.String), variableType, value);

    internal void StoreProgramVariable(CLeaf instance, CLeaf variableName,
        StorageType variableType, CLeaf value)
        => _builder.EmitProgramVariableStore(instance, variableName, variableType, value);

    /// <summary>Create a select (ternary) expression.</summary>
    internal CSlotRef Select(CLeaf cond, CLeaf trueVal, CLeaf falseVal, StorageType type)
        => _builder.Select(cond, trueVal, falseVal, type);

    /// <summary>Create a function reference (for delegate/JUMP_INDIRECT).</summary>
    internal CFuncRef FuncRef(string funcName) => _builder.FuncRef(funcName);

    /// <summary>Emit an expression as a statement (side-effecting call). Under A-normal form a value-producing
    /// call is already materialized at construction, so a leaf or null reaching here has no remaining side
    /// effect — skip it. Only an unbound producer (void call) needs emitting as a statement.</summary>
    internal void EmitExprStmt(CValue expr)
    {
        if (expr == null || expr is CLeaf) return;
        _builder.EmitExprStmt(expr);
    }

    /// <summary>Emit a void internal call as a side-effecting statement (not materialized to a slot).
    /// <paramref name="reentrant"/> marks a delegate-dispatch arm that can re-enter the containing
    /// function (design §4.3).</summary>
    internal void EmitInternalVoid(string funcName, List<CLeaf> args, bool reentrant = false)
        => _builder.EmitInternalVoid(funcName, args, reentrant);

    // ── Nullable<T> (boxed-object emulation) helpers ──

    /// <summary>Default value for a Udon value type (0 / false). Used for `default(T)`-style fills.</summary>
    internal CLeaf EmitValueTypeDefault(string udonType)
        => Const(EmitPolicy.ParseConstValue(udonType, udonType == "SystemBoolean" ? "False" : "0"), new StorageType(udonType));

    /// <summary>Lifted binary operator on Nullable&lt;T&gt; (null propagation), from already-evaluated operand
    /// values. Arithmetic yields T? (null unless both present); relational yields bool (false if either null);
    /// equality yields bool (both-null is equal). Shared by <c>OperatorHandler</c> and compound assignment.</summary>
    internal CLeaf EmitLiftedBinaryCore(
        IOperation operation,
        CValue leftVal, bool leftNullable, ITypeSymbol ltUnderlying,
        CValue rightVal, bool rightNullable, ITypeSymbol rtUnderlying,
        Microsoft.CodeAnalysis.Operations.BinaryOperatorKind kind,
        ITypeSymbol resultType)
        => NullableAbi.EmitLiftedBinaryCore(_builder,
            leftVal, leftNullable, ltUnderlying,
            rightVal, rightNullable, rtUnderlying,
            kind,
            RequireBoundAbi(operation, BoundAbiRole.Operator),
            resultType, _compilation.GetSpecialType(SpecialType.System_Int32),
            GetStorageTypeName,
            (boxed, underlying) => NullableAbi.PromoteBoxedToInt32(_builder, boxed, underlying,
                _compilation.GetSpecialType(SpecialType.System_Int32), GetStorageTypeName),
            EmitNarrowingConvert);

    internal static IOperation UnwrapConversions(IOperation op)
    {
        while (op is IConversionOperation conv) op = conv.Operand;
        return op;
    }

    /// <summary>Unwrap a string-concat operand to its C#-semantic type: strip only VALUE-PRESERVING
    /// conversions (identity / boxing / reference — including the compiler-inserted boxing to object
    /// for string.Concat(object,object)), stopping at any value conversion. A user's inline cast IS
    /// the operand's concat type (WjR3 A11): the full UnwrapConversions strip landed
    /// `s += (Suit)(k % 4)` on the int and Concat'd the raw number, and landed `"" + (int)e` on the
    /// enum and name-stringified where C# prints the number.</summary>
    internal static IOperation UnwrapConcatOperand(IOperation op)
    {
        while (op is IConversionOperation conv)
        {
            var c = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetConversion(conv);
            if (!c.IsIdentity && !c.IsBoxing && !c.IsReference) break;
            op = conv.Operand;
        }
        return op;
    }

    internal static string SanitizeId(string name) => NameAllocator.Sanitize(name);
    internal static string ToInvariantString(object value)
        => value is IFormattable fmt ? fmt.ToString(null, CultureInfo.InvariantCulture)
         : value?.ToString() ?? "null";

    // ── Shared helpers (used by multiple handlers) ──

    internal string GetParamVarId(IParameterSymbol param)
    {
        // SS2B: a closure's own parameter lives in its per-spec record, not the definition-keyed map.
        if (_state.Methods.CurrentClosureSpec is { } pcs
            && param.ContainingSymbol is IMethodSymbol pcm
            && SymbolEqualityComparer.Default.Equals(pcm.OriginalDefinition, pcs.Definition.OriginalDefinition)
            && param.Ordinal < pcs.ParamVarIds.Length)
            return pcs.ParamVarIds[param.Ordinal];
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
        // The former [Y3] arm (closure reads an enclosing generic's param via the old owner fallback,
        // first-wins) was adjudicated DEAD 2026-07-10: a closure reading an enclosing param is a
        // capture by definition and resolves through its env record before LoadParam, so the arm was
        // unreachable across the full tracked + real-VM corpus (throw-instrumented run). Deleted —
        // any future shape that would have needed it now fails loud here instead of silently reading
        // the FIRST spec's param var.
        throw new InvalidOperationException(
            $"Cannot resolve parameter '{param.Name}' (ordinal {param.Ordinal}) "
          + $"in method '{_currentMethod?.Name ?? "(none)"}'. "
          + "Not found in lambda overrides, method params, or variable table.");
    }

    /// <summary>Read a parameter value as a CLeaf (field load). A delegate-typed parameter is a
    /// SystemObjectArray bundle reference via the type-map delegate arm (design §2.1).</summary>
    internal CLeaf LoadParam(IParameterSymbol param)
    {
        var fieldName = GetParamVarId(param);
        return LoadField(fieldName, GetStorageType(param.Type));
    }

    internal CLeaf EmitEnumToUnderlying(CLeaf operand, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return operand;
        var underlyingType = named.EnumUnderlyingType;
        var convertMethod = ExternResolver.GetConvertMethodName(underlyingType);
        if (convertMethod == null) return operand;
        var underlyingUdon = GetStorageTypeName(underlyingType);
        return ExternCall(
            UdonAbiKey.Method("SystemConvert", convertMethod,
                new[] { "SystemObject" }, underlyingUdon),
            new List<CLeaf> { operand },
            new StorageType(underlyingUdon));
    }


    // ── Aggregate Instance Load (no Clone) ──

    /// <summary>
    /// Load an aggregate instance reference WITHOUT cloning. Used for field access/write
    /// where we need the original object[], not a copy.
    /// VisitExpression() clones aggregate locals/params by default for value semantics,
    /// but field access operates on the original array.
    /// </summary>
    internal CLeaf LoadInstanceRaw(IOperation instance)
    {
        return instance switch
        {
            // Stage 2 §4.1: captured locals/params live in env records — raw (no-clone) loads read
            // the env cell directly so mutation hits the live storage.
            ILocalReferenceOperation lr when _state.TryGetEnvBinding(lr.Local, out _)
                => EnvEmit.Read(_builder, _state, lr.Local,
                       new StorageType(IsAggregateValue(lr.Type) ? "SystemObjectArray" : GetStorageTypeName(lr.Type))),
            ILocalReferenceOperation lr when _localBindings.TryGetValue(lr.Local, out var b)
                => LoadField(b.Id, new StorageType(IsAggregateValue(lr.Type) ? "SystemObjectArray" : GetStorageTypeName(lr.Type))),
            IParameterReferenceOperation pr when _state.TryGetEnvBinding(pr.Parameter, out _)
                => EnvEmit.Read(_builder, _state, pr.Parameter,
                       new StorageType(IsAggregateValue(pr.Type) ? "SystemObjectArray" : GetStorageTypeName(pr.Type))),
            IParameterReferenceOperation pr
                => LoadParam(pr.Parameter),
            // Inside a struct method/ctor, `this` is the receiver object[] param, not the Behaviour.
            IInstanceReferenceOperation when _state.Methods.CurrentStructReceiverParamId != null
                => LoadField(_state.Methods.CurrentStructReceiverParamId, StorageTypes.ObjectArray),
            // Aggregate field as a RECEIVER (e.g. `o.inner.x`, `this.structField.x`) must NOT be cloned —
            // the access/mutation has to hit the live storage. (Value reads clone in VisitFieldReference.)
            IFieldReferenceOperation fr when IsAggregateValue(fr.Type)
                => ReadAggregateFieldRaw(fr),
            // Aggregate array element as a RECEIVER (`arr[i].x = …`) likewise hits live storage, no clone.
            IArrayElementReferenceOperation ae when IsAggregateValue(ae.Type)
                => ReadArrayElementRaw(ae),
            _ => VisitExpression(instance), // method return, field on this, etc. — fresh or already raw
        };
    }

    // ── Stage 2 §4.1: captured-variable storage ──

    /// <summary>Bind a freshly declared local: env-bound (captured) locals get NO flat field — the
    /// caller must route the initial value through <see cref="EnvEmit.Write"/> (returns false);
    /// ordinary locals get their flat field + LocalBindings entry as before (returns true, flat id
    /// in <paramref name="flatId"/>).</summary>
    internal bool BindLocal(ILocalSymbol local, string udonType, out string flatId)
    {
        if (_state.TryGetEnvBinding(local, out _))
        {
            flatId = null;
            return false;
        }
        flatId = _state.Storage.DeclareLocal(local.Name, new StorageType(udonType));
        _localBindings[local] = new LocalBinding(flatId);
        return true;
    }

    /// <summary>Env arm shared by every assignment write path: when the target is a captured
    /// local/param, store the value into its env cell and return true; false = caller proceeds with
    /// its flat-field path. Aggregate value semantics are the CALLER's job (pass an already-cloned
    /// value where the flat path would clone).</summary>
    internal bool TryEmitEnvStore(IOperation target, CLeaf value)
    {
        ISymbol sym = target switch
        {
            ILocalReferenceOperation lr => lr.Local,
            IParameterReferenceOperation pr => pr.Parameter,
            _ => null,
        };
        if (sym == null || !_state.TryGetEnvBinding(sym, out _)) return false;
        EnvEmit.Write(_builder, _state, sym, value);
        return true;
    }

    /// <summary>Round-7 follow-up [Q5]: the this-FIELD whose storage a ref/out argument aliases, or
    /// null when the argument's storage is not on this program's heap-named fields (locals, params,
    /// other-behaviour members, fresh values). Walks struct member chains and array-element links to
    /// the root: `ref f`, `ref s.v` (s a this-field struct), and `ref arr[0]` (arr a this-field) all
    /// resolve to the root field — the storage the callee can also reach directly through this.</summary>
    internal static IFieldSymbol TryGetThisRootedRefStorage(IOperation arg)
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
    internal static ISymbol TryGetRefStorageRoot(IOperation arg)
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
    internal bool ReceiverNeedsDefensiveCopy(IOperation instance)
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
                    return sawValueFieldLink && _state.ForeachIterationLocals.Contains(lr.Local);
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
    internal CLeaf ReadArrayElementRaw(IArrayElementReferenceOperation ae)
    {
        var arrayVal = VisitExpression(ae.ArrayReference);
        var arrSym = ae.ArrayReference.Type as IArrayTypeSymbol;
        var arrType = GetArrayType(arrSym);
        var elemType = GetArrayElemType(arrSym);
        var idxVal = ResolveArrayIndex(arrayVal, arrType, ae.Indices[0]);
        return ExternCall(UdonAbi.ArrayGet(arrType, elemType), new List<CLeaf> { arrayVal, idxVal }, StorageTypes.Object);
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
    internal CLeaf ResolveArrayIndex(CLeaf arrayVal, string arrayType, IOperation indexOp)
        => indexOp is IUnaryOperation { Type: { Name: "Index" } } fromEnd
            ? EmitIndexFromEnd(arrayVal, arrayType, fromEnd.Operand)
            : VisitExpression(indexOp);

    /// <summary>`arr[^k]` → `arr.Length - k`. <paramref name="arrayVal"/> must already be a
    /// single-assignment scratch leaf (read once here); <paramref name="operand"/> is the `k` in `^k`.</summary>
    internal CLeaf EmitIndexFromEnd(CLeaf arrayVal, string arrayType, IOperation operand)
    {
        var lenVal = ExternCall(UdonAbi.ArrayLength(arrayType),
            new List<CLeaf> { arrayVal }, StorageTypes.Int32);
        var nVal = VisitExpression(operand);
        return ExternCall(UdonAbi.Int32Subtract, new List<CLeaf> { lenVal, nVal }, StorageTypes.Int32);
    }

    /// <summary>Read an aggregate-typed field as the raw stored object[] (no clone): a nested element via
    /// __Get__, or a this.field directly. Used for receiver access; value reads add a clone on top.</summary>
    internal CLeaf ReadAggregateFieldRaw(IFieldReferenceOperation fr)
    {
        // B80: the container may be a v1 CLASS too (a struct field on a class — `w.P.Ref = x`). Reading the
        // struct-field slot RAW yields the LIVE nested object[] stored in the class bundle (no clone), so a
        // chained write lands in the class's storage, not a discarded copy. Gated on IsObjectArrayEmulated
        // (Category-A: object[] slot resolution); the caller only asks for a raw receiver, never a value read.
        if (fr.Instance != null && fr.Instance.Type is INamedTypeSymbol cont && IsObjectArrayEmulated(cont)
            && _state.Aggregates.GetLayout(cont).TryGetIndex(fr.Field, out var idx))
            return AggregateAbi.ReadSlot(_builder, LoadInstanceRaw(fr.Instance), idx, StorageTypes.Object);
        if (fr.Instance is IInstanceReferenceOperation)
            return LoadField(_state.SourceStorageName(fr.Field), StorageTypes.ObjectArray);
        return VisitExpression(fr); // cross-behaviour aggregate field etc. — rare
    }

    // ── L-Value Assignment ──

    /// <summary>
    /// Assign a value to a common l-value target (declaration, local, this-field, parameter, discard).
    /// Callers with specialized targets (array elements, cross-behaviour fields) should handle those
    /// first, then delegate to this method for the common cases.
    /// </summary>
    internal void AssignToLValue(IOperation target, CLeaf value,
        Dictionary<IOperation, LValuePlan> preparedStores = null)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    // Stage 2 §4.1: captured declaration target → env cell, no flat field.
                    if (_state.TryGetEnvBinding(localRef.Local, out _))
                    {
                        EnvEmit.Write(_builder, _state, localRef.Local, value);
                        break;
                    }
                    var udonType = GetStorageTypeName(localRef.Type);
                    var localId = _state.Storage.DeclareLocal(localRef.Local.Name, new StorageType(udonType));
                    _localBindings[localRef.Local] = new LocalBinding(localId);
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
                    var udonType = GetStorageTypeName(existingLocal.Type);
                    var newId = _state.Storage.DeclareLocal(existingLocal.Local.Name, new StorageType(udonType));
                    _localBindings[existingLocal.Local] = new LocalBinding(newId);
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
                preparedFieldStore.Write(value);
                break;

            case IFieldReferenceOperation lateFieldRef when TryPrepareFieldSet(lateFieldRef) is { } lateFieldStore:
                lateFieldStore(value);
                break;

            // Behaviour this-field (no legs; TryPrepareFieldSet returns null for it).
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                EmitStoreField(_state.SourceStorageName(fieldRef.Field), value);
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
                    arrStore.Write(value);
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
                    propStore.Write(value);
                else
                    TryPrepareWriteLValue(propLValue).Value.Write(value);
                break;

            default:
                throw new System.NotSupportedException(
                    $"Unsupported l-value target: {target.GetType().Name}");
        }
    }

    /// <summary>Assign a nested deconstruction target tuple from its object[]-emulated value: read each element
    /// via __Get and delegate to AssignToLValue (which recurses for deeper tuples / handles the leaf lvalues).
    /// CW29: element reads route through the single CloneIfAggregate rule — the old `!IsTupleType` carve-out
    /// left a tuple-typed LEAF local aliasing the source bundle whenever the incoming value was not fresh.</summary>
    void AssignNestedTupleElements(ITupleOperation tuple, CLeaf arrValue,
        Dictionary<IOperation, LValuePlan> preparedStores = null)
    {
        var layout = _state.Aggregates.GetLayout(
            (INamedTypeSymbol)ResolveType(tuple.Type));
        for (int i = 0; i < tuple.Elements.Length; i++)
        {
            var elemVal = AggregateAbi.ReadSlot(
                _builder, arrValue, layout.Fields[i].Index, StorageTypes.Object);
            var toAssign = AggregateAbi.CloneIfAggregate(_builder, elemVal,
                ResolveType(tuple.Elements[i].Type),
                _state.Aggregates.GetLayout, IsAggregateValue);
            AssignToLValue(tuple.Elements[i], toAssign, preparedStores);
        }
    }

    /// <summary>Wave-9 round-6 [X2]-[X6]: pre-evaluate the receiver/index LEGS of every property/indexer
    /// and array-element target of a deconstruction, left-to-right in lexical order (nested target tuples
    /// included), BEFORE the caller evaluates the RHS — the C# order is "each target's component
    /// expressions left-to-right, then the RHS, then the stores left-to-right". Returns a deferred store
    /// per prepared target (keyed by the target operation, consumed by AssignToLValue), or null when no
    /// target carries legs (plain locals/fields/discards — byte-identical to the pre-round-6 emission).</summary>
    internal Dictionary<IOperation, LValuePlan> PrepareDeconstructionTargets(ITupleOperation targetTuple)
    {
        Dictionary<IOperation, LValuePlan> prepared = null;
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
                    prepared ??= new Dictionary<IOperation, LValuePlan>();
                    prepared[propTarget] = TryPrepareWriteLValue(propTarget).Value;
                    break;
                case IArrayElementReferenceOperation arrayElem:
                    prepared ??= new Dictionary<IOperation, LValuePlan>();
                    prepared[arrayElem] = TryPrepareWriteLValue(arrayElem).Value;
                    break;
                // Wave-9 round-7 [Y2]/[Y4]/[Y6]/[Y8]/[Y10]: FIELD targets with receiver legs
                // (struct-array-element receivers `arr[i].v`, member chains, cross-behaviour
                // variable receivers) — the round-6 pass covered property/indexer/array-element
                // leaves only, so field-target legs kept store-time evaluation (wrong cell when a
                // leg read state the RHS mutates; VM-proven ref=702 vs 72). Behaviour this-fields
                // return null (no legs) and keep the plain store.
                case IFieldReferenceOperation fieldTarget:
                    if (TryPrepareWriteLValue(fieldTarget) is { } fieldPlan)
                    {
                        prepared ??= new Dictionary<IOperation, LValuePlan>();
                        prepared[fieldTarget] = fieldPlan;
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
    public struct LValuePlan
    {
        System.Action<CLeaf> _write;
        public CLeaf Value;
        public CLeaf ArrayVal;
        public CLeaf IndexVal;
        public CLeaf InstanceVal;
        public List<CLeaf> IndexArgs;
        public LValuePlan(System.Action<CLeaf> write)
        {
            this = default;
            _write = write ?? throw new System.ArgumentNullException(nameof(write));
        }
        public void SetWriter(System.Action<CLeaf> write)
            => _write = write ?? throw new System.ArgumentNullException(nameof(write));
        public void Write(CLeaf value) => _write(value);
    }

    internal LValuePlan? TryPrepareWriteLValue(IOperation target)
    {
        System.Action<CLeaf> write = target switch
        {
            IFieldReferenceOperation field => TryPrepareFieldSet(field),
            IPropertyReferenceOperation property => PreparePropertySet(property),
            IArrayElementReferenceOperation element => PrepareArrayElementSet(element),
            _ => null,
        };
        return write == null ? null : new LValuePlan(write);
    }

    enum FieldSetKind { AggregateSlot, CrossBehaviour, ExternValueType, ExternReferenceType }

    readonly struct FieldSetPlan
    {
        public readonly FieldSetKind Kind;
        public readonly IOperation Instance;
        public readonly int AggregateIndex;
        public FieldSetPlan(FieldSetKind kind, IOperation instance, int aggregateIndex = -1)
        { Kind = kind; Instance = instance; AggregateIndex = aggregateIndex; }
    }

    FieldSetPlan? DescribeFieldSet(IFieldReferenceOperation fieldRef)
    {
        if (fieldRef.Instance == null) return null;
        if (AggregateAbi.TryGetMemberTarget(fieldRef, out var instance, out var member)
            && ResolveType(instance.Type) is INamedTypeSymbol aggregateType
            && IsObjectArrayEmulated(aggregateType)
            && _state.Aggregates.GetLayout(aggregateType).TryGetIndex(member, out var fieldIndex))
            return new FieldSetPlan(FieldSetKind.AggregateSlot, instance, fieldIndex);
        if (fieldRef.Instance is IInstanceReferenceOperation)
            return fieldRef.Field.ContainingType.IsValueType
                ? new FieldSetPlan(FieldSetKind.ExternValueType, fieldRef.Instance)
                : null;
        if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
            return new FieldSetPlan(FieldSetKind.CrossBehaviour, fieldRef.Instance);
        return new FieldSetPlan(fieldRef.Field.ContainingType.IsValueType
            ? FieldSetKind.ExternValueType : FieldSetKind.ExternReferenceType, fieldRef.Instance);
    }

    /// <summary>Wave-9 round-7 [Y2]/[Y4]-[Y10]: the single field SET path, shared by simple
    /// assignment and deconstruction lvalues (the field twin of PreparePropertySet /
    /// PrepareArrayElementSet). Evaluates the target's receiver legs NOW (C# order: the lvalue's
    /// component expressions run BEFORE the RHS) and returns the deferred store: aggregate member
    /// slot → cross-behaviour SetProgramVariable → extern value-type field → extern reference-type
    /// field. Returns null for behaviour this-fields and static fields (no legs) — callers keep
    /// their direct-store path. DescribeFieldSet is the single dispatch table for these cases.</summary>
    internal System.Action<CLeaf> TryPrepareFieldSet(IFieldReferenceOperation fieldRef)
    {
        var plan = DescribeFieldSet(fieldRef);
        if (!plan.HasValue) return null;
        // Aggregate (struct/tuple) OR v1-class member → layout slot write on the backing object[].
        if (plan.Value.Kind == FieldSetKind.AggregateSlot)
        {
            var arrExpr = LoadInstanceRaw(plan.Value.Instance);
            return value => AggregateAbi.WriteSlot(_builder, arrExpr, plan.Value.AggregateIndex, value);
        }

        // Cross-behaviour field → one SetProgramVariable (a delegate field ships the bundle
        // REFERENCE — design §2.3, incl. a tuple-return delegate's SystemObjectArray bundle).
        if (plan.Value.Kind == FieldSetKind.CrossBehaviour)
        {
            var crossInstanceVal = VisitExpression(plan.Value.Instance);
            return value => EmitCrossBehaviourFieldSet(fieldRef.Field, crossInstanceVal, value);
        }

        // Extern value-type field (e.g. a Vector3 component) → extern field setter.
        if (plan.Value.Kind == FieldSetKind.ExternValueType)
        {
            var vtContainingType = GetStorageTypeName(fieldRef.Field.ContainingType);
            var vtInstanceVal = plan.Value.Instance is IInstanceReferenceOperation
                ? LoadField(_state.Storage.DeclareThisOnce(new StorageType(vtContainingType)), new StorageType(vtContainingType))
                : VisitExpression(plan.Value.Instance);
            var vtSig = RequireBoundAbi(
                fieldRef, BoundAbiRole.FieldSetValue);
            return value => EmitExternVoid(vtSig, new List<CLeaf> { vtInstanceVal, value });
        }

        // Extern reference-type field through a variable receiver → extern field setter.
        if (plan.Value.Kind == FieldSetKind.ExternReferenceType)
        {
            var refInstanceVal = VisitExpression(plan.Value.Instance);
            var refSig = RequireBoundAbi(
                fieldRef, BoundAbiRole.FieldSetReference);
            return value => EmitExternVoid(refSig, new List<CLeaf> { refInstanceVal, value });
        }

        return null; // behaviour this-field / static field — no legs
    }

    /// <summary>Evaluate an array-element lvalue's array/index legs NOW and return the deferred
    /// element store (wave-9 round-6 [X6]; the legs/value split twin of PreparePropertySet).</summary>
    internal System.Action<CLeaf> PrepareArrayElementSet(IArrayElementReferenceOperation arrayElem)
    {
        var arrayVal = VisitExpression(arrayElem.ArrayReference);
        var arrSym = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
        var indexVal = ResolveArrayIndex(arrayVal, GetArrayType(arrSym), arrayElem.Indices[0]);
        return value => EmitArrayElementSet(arrSym, arrayVal, indexVal, value);
    }

    /// <summary>Emit an array element Set extern from already-evaluated array/index/value leaves.
    /// Shared by PrepareArrayElementSet (single write) and the read-modify-write lvalue plan's
    /// array arm (which reuses the plan's cached array/index leaves instead
    /// of re-evaluating them).</summary>
    internal void EmitArrayElementSet(IArrayTypeSymbol arrSymbol, CLeaf arrayVal, CLeaf indexVal, CLeaf value)
    {
        var arrayType = GetArrayType(arrSymbol);
        var elementType = GetArrayElemType(arrSymbol);
        EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
            new List<CLeaf> { arrayVal, indexVal, value });
    }

    /// <summary>Emit a cross-behaviour field Set via SetProgramVariable from an already-evaluated
    /// instance leaf. Shared by TryPrepareFieldSet (single write) and the read-modify-write lvalue plan's
    /// field arm (which reuses the plan's cached instance leaf instead of
    /// re-evaluating it).</summary>
    internal void EmitCrossBehaviourFieldSet(IFieldSymbol field, CLeaf instanceVal, CLeaf value)
    {
        RejectProgramLocalCrossBehaviourFieldWrite(field);
        StoreProgramVariable(instanceVal, field.Name, GetStorageType(field.Type), value);
    }

    /// <summary>CW27: the one aggregate object-initializer entry — every mint site routes through
    /// this wrapper so AggregateAbi.EmitObjectInitializer gets the layout recursion and the
    /// computed/indexer setter-call capability (and can therefore be loud about anything else).</summary>
    internal void EmitAggregateObjectInitializer(CLeaf instance, AggregateLayout layout,
        IObjectOrCollectionInitializerOperation initializer)
        => AggregateAbi.EmitObjectInitializer(_builder, instance, layout, initializer, VisitExpression,
            _state.Aggregates.GetLayout, IsObjectArrayEmulated,
            EmitInitializerSetterAssignment);

    /// <summary>CW27: computed-property / indexer member in an aggregate object initializer — call
    /// the user setter with the fresh instance as synthetic param0 (index args by ordinal, then the
    /// value: the C# order), the same lowering PreparePropertySet gives plain assignment.</summary>
    void EmitInitializerSetterAssignment(CLeaf instance, IPropertyReferenceOperation propRef, IOperation valueOp)
    {
        var setter = RequireBoundCallable(
            propRef, CallableSiteKind.PropertySet);
        var args = new List<CLeaf> { instance };
        if (propRef.Property.IsIndexer) args.AddRange(EvaluateIndexerArgs(propRef));
        args.Add(VisitExpression(valueOp));
        EmitExprStmt(EmitCallToMethod(setter, args));
    }

    /// <summary>Wave-9 round-5 [X2]/[X13]: the single property/indexer SET path, shared by simple
    /// assignment and deconstruction lvalues. Evaluation follows the C# order — receiver, then index
    /// arguments, then the value (valueFactory) — which is the [X2] fix: the old simple-assignment
    /// arm evaluated the RHS before the receiver and index args, so `this[i] = Step()`-style sites
    /// whose index/receiver expressions share state with the RHS diverged from the CLR.
    /// Wave-9 round-6 [X2]-[X5]: split into PreparePropertySet (receiver/index legs, evaluated NOW)
    /// + a deferred store, so deconstruction can evaluate every target's legs BEFORE the RHS.
    /// Returns the stored value (the assignment-expression result).</summary>
    /// <summary>Evaluate a property/indexer SET target's receiver and index-argument legs NOW (in the
    /// C# receiver → index args order) and return the deferred store that emits the actual SET with a
    /// later-evaluated value. The single-assignment path runs legs → value → store,
    /// byte-identical to the pre-split emission; the deconstruction path runs ALL targets' legs, then
    /// the RHS, then the stores (wave-9 round-6 [X2]-[X5] — store-time leg evaluation inverted the C#
    /// order and landed writes in the wrong cell when the legs read state the RHS mutates).</summary>
    internal System.Action<CLeaf> PreparePropertySet(IPropertyReferenceOperation propRef)
    {
        var boundSetter = RequireBoundCallSite(
            propRef,
            CallableSiteKind.PropertySet);
        var setter = boundSetter.Callable.Site.Target;
        // CW1 lift: a runtime-polymorphic property/indexer WRITE on a v1-class receiver dispatches the
        // setter through the typeobj machinery — legs staged NOW (C# order: receiver, index args), the
        // value staged inside the deferred store (it arrives later, and the chain consumes it once per
        // arm). The static arms below bind the receiver's STATIC setter; `base.P` keeps them.
        if (IsAccessorDispatchSite(
                boundSetter, out var vsRecvTy))
        {
            var (vsRecv, vsIdx) = StageAccessorDispatchLegs(propRef);
            return vsVal =>
            {
                var vSlot = _state.Builder.AllocScratch(GetStorageType(propRef.Property.Type));
                EmitAssign(vSlot, vsVal);
                EmitAccessorDispatch(
                    propRef,
                    vsRecvTy,
                    setter,
                    vsRecv,
                    vsIdx,
                    SlotRef(vSlot),
                    boundSetter);
            };
        }

        // Aggregate (struct/tuple) OR v1-class auto-property → layout slot write on the backing object[].
        if (propRef.Instance is { Type: INamedTypeSymbol aggContaining } aggInst
            && IsObjectArrayEmulated(aggContaining)
            && _state.Aggregates.GetLayout(aggContaining).TryGetIndex(propRef.Property, out var aggSlotIndex))
        {
            var arrExpr = LoadInstanceRaw(aggInst);
            return aggVal => AggregateAbi.WriteSlot(_builder, arrExpr, aggSlotIndex, aggVal);
        }

        // Computed (non-auto) struct property setter: p.Both = v → call the user setter with the receiver
        // object[] as synthetic param0 (mutates this-fields through the shared backing array).
        if (propRef.Property is { IsIndexer: false, SetMethod: { } aggSetterRaw }
            && propRef.Instance?.Type is INamedTypeSymbol aggSetType && IsObjectArrayEmulated(aggSetType))
        {
            var aggSetter = RequireRegisteredCallable(
                boundSetter.Callable.Site.Target);
            var aggRecv = LoadInstanceRaw(propRef.Instance);
            return aggSetVal => EmitExprStmt(
                EmitCallToMethod(aggSetter, new List<CLeaf> { aggRecv, aggSetVal }));
        }

        // User-defined indexer on a user STRUCT instance (`s[i] = v`) → call the setter with the struct
        // receiver object[] as param0, the index args, then the value. Mirrors the GET routing in
        // VisitIndexerGet; without it this falls to a bogus SystemObjectArray.__set_Item extern. (diff-fuzz wave 4)
        if (propRef.Property is { IsIndexer: true, SetMethod: { } aggIdxSetterRaw }
            && propRef.Instance?.Type is INamedTypeSymbol aggIdxSetType && IsObjectArrayEmulated(aggIdxSetType))
        {
            var aggIdxSetter = RequireRegisteredCallable(
                boundSetter.Callable.Site.Target);
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
        var dispatchSetter = boundSetter.Target;
        var dispatchProperty =
            dispatchSetter.AssociatedSymbol as IPropertySymbol
            ?? propRef.Property;
        // B74 fold: route the extern owner through the shared funnel (an inherited property registers under
        // the receiver's own static type, not its declaring base) — replaces the old Behaviour/MonoBehaviour-
        // only string fixup below. A null instance (static setter) leaves the owner unchanged.
        var propOwnerReceiver = propRef.Instance is IInstanceReferenceOperation ? _classSymbol : propRef.Instance?.Type;
        var propContainingUdon = GetStorageTypeName(ResolveExternOwnerType(propRef.Property.ContainingType, propOwnerReceiver, propRef.Property.Name));

        // User-defined indexer on this/base → internal setter call (index args followed by the value).
        if (propRef.Property.IsIndexer
            && propRef.Instance is IInstanceReferenceOperation
            && _methodFunctions.ContainsKey(dispatchSetter))
        {
            // Wave-9 round-4: index args slotted by parameter ordinal (named/reordered index args
            // bind by name; the base[...] flavor rides this same arm via the base-instance copy).
            var thisIdxArgs = EvaluateIndexerArgs(propRef);
            return thisIdxVal =>
            {
                thisIdxArgs.Add(thisIdxVal);
                EmitExprStmt(EmitCallToMethod(
                    dispatchSetter, thisIdxArgs));
            };
        }

        // Static property setter (no instance) — e.g. Time.timeScale = 1.0f
        if (propRef.Instance == null)
        {
            if (propRef.Property.SetMethod?.DeclaringSyntaxReferences.Length > 0
                && !USugarCompilerHelper.IsFrameworkNamespace(propRef.Property.ContainingNamespace)
                && !UasmEmitter.IsComputedProperty(propRef.Property))
                throw ClassAbiPolicy.UnsupportedStaticStorage(
                    propRef.Property);
            if (propRef.Property.SetMethod is { } sourceSetter
                && sourceSetter.DeclaringSyntaxReferences.Length > 0
                && !USugarCompilerHelper.IsFrameworkNamespace(sourceSetter.ContainingNamespace)
                && UasmEmitter.IsComputedProperty(propRef.Property))
            {
                var resolvedSetter = RequireRegisteredCallable(
                    boundSetter.Callable.Site.Target);
                return staticVal => EmitExprStmt(
                    EmitCallToMethod(resolvedSetter, new List<CLeaf> { staticVal }));
            }
            var staticValType = GetStorageTypeName(propRef.Property.Type);
            var staticSetter = RequireBoundAbi(
                propRef, BoundAbiRole.PropertySet);
            return staticVal => EmitExternVoid(
                staticSetter,
                new List<CLeaf> { staticVal });
        }

        var instanceVal = propRef.Instance is IInstanceReferenceOperation
            ? LoadField(_state.Storage.DeclareThisOnce(new StorageType(propContainingUdon)), new StorageType(propContainingUdon))
            : VisitExpression(propRef.Instance);
        var containingType = propContainingUdon;
        var valueType = GetStorageTypeName(propRef.Property.Type);
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
                indexTypes.Add(GetStorageTypeName(arg.Value.Type));
            }
            return externIdxVal =>
            {
                indexArgs.Add(externIdxVal);
                // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                EmitExternVoid(
                    RequireBoundAbi(
                        propRef, BoundAbiRole.IndexerSet),
                    indexArgs);
            };
        }

        return srcVal =>
        {
        switch (propRef.Instance)
        {
            case IInstanceReferenceOperation
                when _methodFunctions.TryGetValue(
                    dispatchSetter, out _):
                // User-defined property setter on this → internal call
                EmitExprStmt(EmitCallToMethod(
                    dispatchSetter,
                    new List<CLeaf> { srcVal }));
                break;
            case IInstanceReferenceOperation
                when dispatchSetter.DeclaringSyntaxReferences.IsEmpty
                     && ExternResolver.IsUdonSharpBehaviour(
                         dispatchProperty.ContainingType)
                     && dispatchProperty.ContainingType.Name
                     != "UdonSharpBehaviour":
                // Auto-property set on this → direct variable assignment (user-defined classes only)
                EmitStoreField(dispatchProperty.Name, srcVal);
                break;
            default:
            {
                // Interface property set → dispatch the setter through its interface bridge (SetProgramVariable
                // the value, SendCustomEvent the setter), like an interface method call. Without this the
                // fall-through emits a non-existent __set_Value extern on IUdonEventReceiver.
                if (propRef.Property.SetMethod is { } ifaceSetter
                    && ExternResolver.IsUserInterface(propRef.Property.ContainingType)
                    && propRef.Instance is not IInstanceReferenceOperation
                    && _planner.GetLayout(propRef.Property.ContainingType).Methods.TryGetValue(ifaceSetter, out var ifaceSetterMl))
                {
                    GuardInterfaceDispatchRepresentation(propRef.Property.ContainingType, propRef.Property.Name);
                    RejectProgramLocalCrossBehaviourPropertyWrite(propRef.Property); // CW22
                    // A typed cross-call keeps the value copy-in inside the reentrant spill window.
                    bool ifaceSetReentrant = TryMarkReentrantCrossDispatch(propRef, ifaceSetter);
                    CrossCall(instanceVal,
                        LayoutPlanBuilder.InterfaceDispatchName(ifaceSetter, ifaceSetterMl),
                        CrossCallParameters(ifaceSetter, ifaceSetterMl.ParamIds, new[] { srcVal }),
                        System.Array.Empty<ReturnSlot>(), StorageTypes.Void, ifaceSetReentrant);
                }
                else if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                {
                    RejectProgramLocalCrossBehaviourPropertyWrite(propRef.Property); // CW22 (field-write twin)
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
                        StoreProgramVariable(instanceVal, propRef.Property.Name,
                            GetStorageType(propRef.Property.Type), srcVal);
                    }
                    else
                    {
                        // Non-auto property setter: call via SendCustomEvent
                        // Wave-12 r2 [V1]: reentrant setter — value copy-in inside the spill window.
                        bool setReentrant = TryMarkReentrantCrossDispatch(propRef, propRef.Property.SetMethod);
                        var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                        CrossCall(instanceVal, exportName,
                            CrossCallParameters(propRef.Property.SetMethod, setParamIds, new[] { srcVal }),
                            System.Array.Empty<ReturnSlot>(), StorageTypes.Void, setReentrant);
                    }
                }
                else
                {
                    EmitExternVoid(
                        RequireBoundAbi(
                            propRef, BoundAbiRole.PropertySet),
                        new List<CLeaf> { instanceVal, srcVal });
                }

                break;
            }
        }
        };
    }

    /// <summary>Materialize one closure callable during BoundProgram planning.</summary>
    internal void RegisterClosureForPlanning(IMethodSymbol localFunc)
    {
        if (_state.Program != null)
            throw new InvalidOperationException(
                "Closure registration is a planning-phase operation.");
        var identity = _state.ResolveClosureIdentity(localFunc);
        var keyArgs = identity.KeyArgs;
        if (_state.Methods.TryGetClosureSpec(localFunc, keyArgs, out _)) return;
        var funcName = string.IsNullOrEmpty(localFunc.Name) ? "lambda" : localFunc.Name;
        var parameters = localFunc.Parameters.Select(parameter => new CallableParameterPlan(
            index => NameAllocator.ParamId(parameter.Name, index), GetStorageType(parameter.Type))).ToArray();
        var returns = localFunc.ReturnsVoid ? Array.Empty<CallableReturnPlan>() : new[]
        {
            new CallableReturnPlan(index => NameAllocator.RetId(funcName, index),
                new StorageType(GetStorageTypeName(localFunc.ReturnType)))
        };
        var capturing = _state.Captures != null
            && _state.Captures.IsCapturingClosure(localFunc);
        new CallableRegistrar(_state).Register(
            new CallableLayoutPlan(localFunc, index => NameAllocator.FormatId(funcName, index),
                slotPrefix: index => NameAllocator.FormatId(funcName, index),
                parameters: parameters, returns: returns,
                closureKeyArgs: keyArgs, closureOwnerSpecs: identity.OwnerSpecs,
                closureContainingTypeSpec:
                    identity.ContainingTypeSpec,
                environmentId: capturing
                    ? index => NameAllocator.FormatId(funcName + "__envp", index)
                    : null),
            deferredBody: true);
    }

    // ── Delegate convention helpers ──

    /// <summary>Compute signature-based convention field names for a delegate type
    /// (sig key via the unified DelegateAbi.BuildSigPart — design §3.2). Pass the type-param map when
    /// resolving inside a generic-spec body so e.g. Func&lt;T&gt; keys on the substituted type.</summary>
    internal static (string[] argNames, string retName, string envName) GetConventionFieldNames(
        INamedTypeSymbol delegateType, IUdonTypeSystem types,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
        => GetConventionFieldNames(
            delegateType.DelegateInvokeMethod, types, typeParamMap);

    /// <summary>Overload taking the Invoke (or Invoke-shaped) method directly — the delegate-type
    /// overload above just re-derives this from delegateType.DelegateInvokeMethod, so a caller that
    /// already holds the method (or, per Stage 1.75 §2.3, a WRAPPER dispatching an inner bundle whose
    /// native protocol is a PLAIN method's own signature, never itself a delegate's Invoke method) skips
    /// the round-trip. BuildSigPart only reads Parameters/ReturnsVoid/ReturnType, so any IMethodSymbol
    /// is a valid "invoke" here, not only a genuine DelegateInvokeMethod.</summary>
    internal static (string[] argNames, string retName, string envName) GetConventionFieldNames(
        IMethodSymbol invoke, IUdonTypeSystem types,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap = null)
    {
        var sigPart = DelegateAbi.BuildSigPart(
            invoke, types, typeParamMap);

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

    /// <summary>Register the signature and exact multicast operation used by this site. Operations
    /// sharing a signature merge into one plan, so one-sided use does not emit the opposite helper.
    /// A duplicate site is otherwise a no-op here, same class of dedup as
    /// DelegateBridgeEmitter's emitted-name set. Snapshots the type-param map for the same reason
    /// ResolveDelegateBridge does (§7 A-M1): synthetic emission runs after body emission, when the
    /// ambient map may already be cleared.</summary>
    internal void PlanMulticastSig(
        string sigPart,
        IMethodSymbol invoke,
        MulticastOperations operation)
    {
        _state.SyntheticDemandPlanner.RegisterMulticast(
            sigPart, invoke, _state.TypeParamMap, operation);
    }

    internal void RequireMulticastSig(
        string sigPart,
        IMethodSymbol invoke,
        MulticastOperations operation)
    {
        if (invoke == null) throw new ArgumentNullException(nameof(invoke));
        _state.Program.SyntheticDemands.RequireMulticast(
            sigPart, operation);
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
    internal CLeaf TryEmitEnumToString(CLeaf value, ITypeSymbol type)
    {
        if (RequireEnumToStringDemand(type) is not { } e)
            return null;
        return InternalCall(EnumToStringHelperName(e), new List<CLeaf> { value }, StorageTypes.String);
    }

    internal INamedTypeSymbol PlanEnumToStringDemand(
        ITypeSymbol type,
        bool rejectFlags = true)
    {
        var enumType = ClassifyEnumToStringDemand(type, rejectFlags);
        if (enumType != null)
            _state.SyntheticDemandPlanner.RegisterEnumToString(enumType);
        return enumType;
    }

    internal INamedTypeSymbol RequireEnumToStringDemand(
        ITypeSymbol type,
        bool rejectFlags = true)
    {
        var enumType = ClassifyEnumToStringDemand(type, rejectFlags);
        return enumType == null
            ? null
            : _state.Program.SyntheticDemands.RequireEnumToString(enumType);
    }

    internal INamedTypeSymbol PlanClassToStringDemand(ITypeSymbol type)
    {
        var resolved = ResolveType(type) as INamedTypeSymbol;
        if (resolved == null || resolved.IsRecord
            || !IsUserClass(resolved))
            return null;
        _state.SyntheticDemandPlanner.RegisterClassToString(resolved);
        return resolved;
    }

    internal void PlanBundleStringDemands(ITypeSymbol type)
    {
        var resolved = ResolveType(type);
        if (resolved?.SpecialType == SpecialType.System_Object
            || resolved is INamedTypeSymbol
                { TypeKind: TypeKind.Interface })
            _state.SyntheticDemandPlanner
                .RegisterDynamicBundleString();
        PlanBundleStringDemands(
            resolved,
            new HashSet<ITypeSymbol>(
                SymbolEqualityComparer.Default));
    }

    void PlanBundleStringDemands(
        ITypeSymbol type,
        HashSet<ITypeSymbol> visited)
    {
        if (type == null || !visited.Add(type)) return;
        if (type is INamedTypeSymbol enumType
            && enumType.TypeKind == TypeKind.Enum)
        {
            PlanEnumToStringDemand(
                enumType, rejectFlags: false);
            return;
        }

        var shape = SourceShape(type);
        if (!shape.IsBundle) return;
        if (shape.Bundle == RuntimeBundleKind.Class
            && type is INamedTypeSymbol classType
            && !classType.IsRecord)
        {
            PlanClassToStringDemand(classType);
            return;
        }
        if (shape.Bundle != RuntimeBundleKind.Aggregate
            && type is not INamedTypeSymbol { IsRecord: true })
            return;
        if (type is not INamedTypeSymbol aggregate) return;

        foreach (var field in _state.Aggregates
                     .GetLayout(aggregate).Fields)
            PlanBundleStringDemands(
                ResolveType(field.Type), visited);
    }

    INamedTypeSymbol ClassifyEnumToStringDemand(
        ITypeSymbol type,
        bool rejectFlags)
    {
        var resolved = ResolveType(type);
        if (!IsFoldedEnum(resolved) || resolved is not INamedTypeSymbol e)
            return null;
        if (e.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute"))
        {
            if (!rejectFlags) return null;
            throw new NotSupportedException(
                $"'{e.Name}.ToString()' is not supported: '{e.Name}' is a [Flags] enum and Udon cannot "
                + "synthesize the comma-separated flag decomposition. Format the individual flag bits manually "
                + "(e.g. compare against each flag and build the string yourself).");
        }
        return e;
    }

    /// <summary>Variance design (2026-07-04 §2.3, B-2): register the (outer sig-S, inner sig-T) pair a
    /// wrapper-with-payload bridge is needed for, returning its name. Same dedup/snapshot discipline as
    /// <see cref="RegisterMulticastSig"/> (first registration wins; a second site needing the same
    /// (outer,inner) wrapper is a no-op here) — keyed by the wrapper's own name since that's already the
    /// unique key for this pair.</summary>
    internal string PlanWrapperSig(IMethodSymbol outerInvoke, IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        var wrapperName = DelegateAbi.WrapperName(
            DelegateAbi.BuildSigPart(
                outerInvoke, _state.Types, typeParamMap),
            DelegateAbi.BuildSigPart(
                innerInvoke, _state.Types, typeParamMap));
        _state.SyntheticDemandPlanner.RegisterWrapper(
            new DelegateBindingPlan(DelegateBindingKind.Wrapper, innerInvoke, wrapperName),
            outerInvoke, innerInvoke, typeParamMap);
        return wrapperName;
    }

    DelegateBindingPlan PlanDelegateDemand(IMethodSymbol method, string bridgeName,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        var kind = method.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
            ? DelegateBindingKind.Closure : DelegateBindingKind.Direct;
        var binding = new DelegateBindingPlan(kind, method, bridgeName);
        _state.SyntheticDemandPlanner.RegisterDelegateBridge(binding, typeParamMap);
        return binding;
    }

    /// <summary>Planning-only method-group override selection. Source call sites use the frozen
    /// dispatch target stored in BoundProgram.</summary>
    IMethodSymbol ResolveMostDerivedOverrideForPlanning(
        IMethodSymbol baseMethod)
    {
        if (_state.Program != null)
            throw new InvalidOperationException(
                "Override resolution is a binding-phase operation; emission must read BoundProgram.");
        var def = baseMethod.OriginalDefinition;
        var m = VirtualDispatch.FindOverrideMethodInChain(
            _classSymbol, def, baseMethod.Name);
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
    internal IMethodSymbol CloseMethodForPlanning(IMethodSymbol target)
    {
        if (_state.Program != null)
            throw new InvalidOperationException(
                "Method closure is a binding-phase operation; emission must read BoundProgram.");
        return TypeEnvironment.CloseMethod(
            _compilation, target, _typeParamMap);
    }

    internal (
        IMethodSymbol Method,
        string Owner,
        string[] ParameterOverride)
        DescribeExternMethodAbi(
            IMethodSymbol method,
            ITypeSymbol instanceType = null,
            string[] parameterOverride = null)
    {
        ITypeSymbol containingType = method.ContainingType;

        if (containingType.TypeKind == TypeKind.Interface
            && instanceType is ITypeParameterSymbol typeParameter
            && _typeParamMap != null
            && _typeParamMap.TryGetValue(
                typeParameter, out var concreteType))
            containingType = concreteType;

        if (instanceType is ITypeParameterSymbol valueParameter
            && _typeParamMap != null
            && _typeParamMap.TryGetValue(
                valueParameter, out var concreteValue)
            && method.ContainingType.SpecialType is
                SpecialType.System_Object
                or SpecialType.System_ValueType
                or SpecialType.System_Enum)
        {
            if (concreteValue is INamedTypeSymbol aggregate
                && IsAggregateValue(aggregate))
                throw new NotSupportedException(
                    $"'{method.Name}' on type parameter "
                    + $"'{valueParameter.Name}' instantiated with user-defined "
                    + $"struct '{concreteValue.Name}' is not supported: Udon "
                    + "has no extern for C# ValueType semantics.");
            containingType = concreteValue;
        }
        else if (instanceType != null
                 && instanceType is not ITypeParameterSymbol
                 && method.ContainingType.SpecialType is
                     SpecialType.System_Object
                     or SpecialType.System_ValueType
                     or SpecialType.System_Enum)
        {
            containingType = ResolveExternOwnerType(
                method.ContainingType, instanceType, method.Name);
        }

        GuardUserStructMemberReachedExtern(
            containingType, method.Name);
        var owner = GetStorageTypeName(containingType);

        if (method.IsGenericMethod && owner == "SystemArray")
            parameterOverride = method.OriginalDefinition.Parameters
                .Select(parameter =>
                {
                    var type = parameter.Type;
                    string name;
                    if (type is ITypeParameterSymbol)
                        name = "SystemObject";
                    else if (type is IArrayTypeSymbol
                             { ElementType: ITypeParameterSymbol })
                        name = "SystemArray";
                    else
                        name = GetStorageTypeName(type);
                    return parameter.RefKind == RefKind.None
                        ? name
                        : name + "Ref";
                }).ToArray();

        return (method, owner, parameterOverride);
    }

    /// <summary>Materialize one closed generic callable during BoundProgram planning.</summary>
    internal void RegisterSpecializationForPlanning(IMethodSymbol constructed)
    {
        if (_state.Program != null)
            throw new InvalidOperationException(
                "Callable specialization registration is a planning-phase operation.");
        bool closureKind = constructed.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction;
        var closureIdentity = closureKind ? _state.ResolveClosureIdentity(constructed) : default;
        var closureKeyArgs = closureKind
            ? closureIdentity.KeyArgs : System.Collections.Immutable.ImmutableArray<ITypeSymbol>.Empty;
        if (closureKind
                ? _state.Methods.TryGetClosureSpec(
                    constructed, closureKeyArgs, out _)
                : _state.Methods.Callables.ContainsKey(constructed))
            return;

        if (!closureKind)
        {
            RegisterNamedSpecialization(constructed);
            return;
        }

        var typeArgPart = string.Join("_", constructed.TypeArguments.Select(
            type => _state.Types.GetUdonTypeName(type)));
        var parameters = constructed.Parameters.Select(parameter => new CallableParameterPlan(
            index => NameAllocator.ParamId(parameter.Name, index), GetStorageType(parameter.Type))).ToArray();
        var returns = constructed.ReturnsVoid ? Array.Empty<CallableReturnPlan>() : new[]
        {
            new CallableReturnPlan(index => NameAllocator.RetId(SanitizeId(constructed.Name), index),
                new StorageType(GetStorageTypeName(constructed.ReturnType)))
        };
        var capturing = _state.Captures != null
            && _state.Captures.IsCapturingClosure(constructed);
        new CallableRegistrar(_state).Register(
            new CallableLayoutPlan(constructed,
                index => NameAllocator.FormatId(
                    SanitizeId(constructed.Name) + "_" + typeArgPart, index),
                parameters: parameters, returns: returns,
                closureKeyArgs: closureKeyArgs,
                closureOwnerSpecs: closureIdentity.OwnerSpecs.Add(constructed),
                environmentId: capturing
                    ? index => NameAllocator.FormatId(
                        SanitizeId(constructed.Name) + "__envp", index)
                    : null),
            deferredBody: true);
    }

    void RegisterNamedSpecialization(IMethodSymbol method)
    {
        var typeArgPart = string.Join("_", method.TypeArguments.Select(
            type => _state.Types.GetUdonTypeName(type)));
        var receiver = !method.IsStatic
            && method.ContainingType is INamedTypeSymbol receiverType
            && IsObjectArrayEmulated(receiverType)
            ? MethodContext.ReceiverAbi.ObjectArray : MethodContext.ReceiverAbi.None;
        var parameters = method.Parameters.Select(parameter => new CallableParameterPlan(
            index => NameAllocator.ParamId(parameter.Name, index), GetStorageType(parameter.Type))).ToArray();
        var returns = method.ReturnsVoid ? Array.Empty<CallableReturnPlan>() : new[]
        {
            new CallableReturnPlan(index => NameAllocator.RetId(SanitizeId(method.Name), index),
                new StorageType(GetStorageTypeName(method.ReturnType)))
        };
        new CallableRegistrar(_state).Register(new CallableLayoutPlan(method,
            index => NameAllocator.FormatId(
                SanitizeId(method.Name) + "_" + typeArgPart, index),
            receiver: receiver,
            receiverId: receiver == MethodContext.ReceiverAbi.ObjectArray
                ? index => NameAllocator.ParamId("this", index) : null,
            parameters: parameters,
            returns: returns),
            deferredBody: true);
    }

    /// <summary>Feature G residual gap (wave-14): a struct-member reference (computed property/indexer
    /// accessor, ctor, operator/conversion method) discovered while emitting a GENERIC STRUCT'S OWN
    /// method body binds to the OPEN containing type (Box&lt;T&gt;.Member, never Box&lt;int&gt;.Member) —
    /// the operation tree is built from the shared/unconstructed syntax regardless of which spec is
    /// emitting, the exact invariant BoundProgram closes for plain method calls
    /// (VisitInvocation). Every OTHER struct-member call site instead depended solely on
    /// CollectStructMethodsInOperation's pre-pass, which deliberately SKIPS this same open-form
    /// self-reference (IsCollectibleStructMember's feature-G comment — collecting it registers a dead
    /// second FlatFunction that corrupted definition-keyed recursion bookkeeping). So a member reached
    /// ONLY via internal self/sibling reference — e.g. a computed property read by a sibling method, an
    /// indexer used from within another instance method, a ctor called from a same-struct helper, or an
    /// operator/conversion invoked from another operator's body — never got a FlatFunction and fell through
    /// to a bogus SystemObjectArray extern (VM-proven: DiffFuzz wave-14 8/10 UsugarRejected). Substitute
    /// through the live type-param map, then register on demand exactly like a plain self-recursive
    /// call — both operations are idempotent, so this is a no-op for non-generic structs and for members
    /// already reached by an external concretely-typed call site.</summary>
    internal IMethodSymbol RequireRegisteredCallable(IMethodSymbol method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        var closureKind = method.MethodKind is MethodKind.LambdaMethod
            or MethodKind.LocalFunction;
        if (_state.Program != null)
        {
            if (closureKind)
            {
                var identity =
                    _state.ResolveClosureIdentity(method);
                if (_state.Program.TryGetClosure(
                        method, identity.KeyArgs, out _))
                    return method;
            }
            else if (_state.Program.ContainsCallable(method))
            {
                return method;
            }
            throw new InvalidOperationException(
                $"Callable '{method.ToDisplayString()}' "
                + "was absent from the bound program.");
        }
        if (closureKind)
        {
            var identity = _state.ResolveClosureIdentity(method);
            if (_state.Methods.TryGetClosureSpec(
                    method, identity.KeyArgs, out _))
                return method;
        }
        else if (_state.Methods.Callables.ContainsKey(method))
        {
            return method;
        }

        throw new InvalidOperationException(
            $"Callable '{method.ToDisplayString()}' was absent from the bound program.");
    }

    // The [X6]/[Y2] instantiation-pin gates were retired by the per-spec closure separation
    // (2026-07-10) — closures duplicate per spec, so no second-instantiation reject exists.

    // ── Delegate bridge resolution ──

    /// <summary>Resolve delegate creation to bridge name, FuncRef, and target instance.</summary>
    /// <summary>Stage 2 §3.7/§4.1: the env leaf for a delegate/direct-call target — the binding-scope
    /// env of a CAPTURING closure (resolved statically from the current frame, §4.1), or a null const
    /// for a capture-free closure / named method (byte-invariant). Emit-time armor: a capturing
    /// closure must have a BindingScope lexically enclosing this creation site.</summary>
    internal CLeaf ClosureEnvLeaf(IMethodSymbol targetMethod)
    {
        if (targetMethod == null || _state.Captures == null
            || !_state.Captures.IsCapturingClosure(targetMethod.OriginalDefinition))
            return Const(null, StorageTypes.Object);
        if (!_state.Captures.ClosureScopes.TryGetValue(targetMethod.OriginalDefinition, out var closureScope)
            || closureScope.BindingScope == null)
            throw new System.InvalidOperationException(
                $"Capturing closure '{targetMethod.Name}' has no binding scope enclosing its creation site.");
        return EnvEmit.Leaf(_builder, _state, closureScope.BindingScope);
    }

    internal void RejectUnsafeCrossProgramDelegateWrite(IOperation target, ValueInfo value)
        => _state.Boundary.RequireCanStoreCrossProgramDelegate(target, value);

    internal void RejectUnsafeCrossProgramEventHandler(IEventSymbol evt, ValueInfo value)
        => _state.Boundary.RequireCanStorePublicEventHandler(evt, value);

    internal void RejectProgramLocalCrossBehaviourFieldWrite(IFieldSymbol field)
        => _state.Boundary.RequireCanWriteCrossBehaviourField(field);

    internal void RejectProgramLocalCrossBehaviourFieldRead(IFieldSymbol field)
        => _state.Boundary.RequireCanReadCrossBehaviourField(field);

    internal void RejectProgramLocalCrossBehaviourArgument(ITypeSymbol argType)
        => _state.Boundary.RequireCanPassCrossBehaviourArgument(argType);

    internal void RejectProgramLocalCrossBehaviourPropertyWrite(IPropertySymbol prop)
        => _state.Boundary.RequireCanWriteCrossBehaviourProperty(prop);

    internal void RejectProgramLocalCrossBehaviourPropertyRead(IPropertySymbol prop)
        => _state.Boundary.RequireCanReadCrossBehaviourProperty(prop);

    internal void RejectProgramLocalCrossBehaviourAccessor(IMethodSymbol accessor)
        => _state.Boundary.RequireCanDispatchCrossBehaviourAccessor(accessor);

    internal void RejectUnsafeCrossProgramDelegateArgument(IArgumentOperation arg)
        => _state.Boundary.RequireCanPassCrossProgramDelegateArgument(arg);

    internal void RejectProgramLocalErasure(IConversionOperation conversion,
        ITypeSymbol sourceType, ITypeSymbol destinationType)
        => _state.Boundary.RequireCanEraseProgramLocalPayload(
            conversion, sourceType, destinationType);

    internal MaterializedDelegateBinding ResolveDelegateBridge(IDelegateCreationOperation op)
    {
        var scope = _state.CurrentBindingScope
                    ?? throw new InvalidOperationException(
                        $"Delegate site '{op.Syntax}' has no binding scope.");
        var site = new BoundDelegateSiteKey(op, scope);
        var plan = _state.Program.SyntheticDemands.RequireDelegateBinding(site);
        _state.RecordEmittedDelegateSite(site);
        return MaterializeDelegateBinding(op, plan);
    }

    internal DelegateBindingPlan PlanDelegateBridge(
        IDelegateCreationOperation op,
        CallSiteBindingScope scope)
    {
        var binding = PlanDelegateBridgeCore(op).Plan;
        _state.SyntheticDemandPlanner.PlanDelegateBinding(
            new BoundDelegateSiteKey(op, scope),
            binding);
        return binding;
    }

    MaterializedDelegateBinding MaterializeDelegateBinding(
        IDelegateCreationOperation operation,
        DelegateBindingPlan plan)
    {
        CLeaf targetInstance = null;
        if (operation.Target is IMethodReferenceOperation methodReference
            && methodReference.Instance != null
            && methodReference.Instance is not IInstanceReferenceOperation)
            targetInstance = VisitExpression(methodReference.Instance);

        if (plan.Kind == DelegateBindingKind.Receiver)
        {
            var receiver = targetInstance
                ?? (_state.Methods.CurrentStructReceiverParamId is { } receiverId
                    ? LoadField(
                        receiverId,
                        new StorageType(AggregateAbi.ArrayType))
                    : throw new NotSupportedException(
                        $"Method group '{plan.TargetMethod.Name}' has no receiver in this context."));
            if (plan.TargetMethod.ContainingType is INamedTypeSymbol receiverType
                && IsUserStruct(receiverType))
                receiver = AggregateAbi.DeepClone(
                    _builder, receiver, receiverType,
                    _state.Aggregates.GetLayout);
            return new MaterializedDelegateBinding(
                plan, FuncRef(plan.BridgeName), null, receiver);
        }

        if (plan.Kind == DelegateBindingKind.CrossProgram)
        {
            if (targetInstance == null)
                throw new NotSupportedException(
                    $"Cross-program method group '{plan.TargetMethod.Name}' has no receiver.");
            return new MaterializedDelegateBinding(
                plan,
                Const(0u, StorageTypes.UInt32),
                targetInstance,
                Const(null, StorageTypes.Object));
        }

        if (plan.Kind == DelegateBindingKind.Wrapper)
        {
            if (targetInstance == null
                || string.IsNullOrEmpty(plan.InnerBridgeName))
                throw new InvalidOperationException(
                    $"Wrapper binding '{plan.BridgeName}' is incomplete.");
            var innerBundle = DelegateAbi.EmitBundleMint(
                _builder,
                Const(
                    BundleAbi.KindTag(RuntimeBundleKind.Delegate)
                    + "internal:"
                    + DelegateAbi.BuildSigPart(
                        plan.TargetMethod, _state.Types,
                        _state.TypeParamMap),
                    StorageTypes.String),
                () => targetInstance,
                Const(plan.InnerBridgeName, StorageTypes.String),
                Const(0u, StorageTypes.UInt32),
                Const(null, StorageTypes.Object));
            return new MaterializedDelegateBinding(
                plan, FuncRef(plan.BridgeName), null, innerBundle);
        }

        return new MaterializedDelegateBinding(
            plan,
            FuncRef(plan.BridgeName),
            targetInstance,
            ClosureEnvLeaf(plan.TargetMethod));
    }

    MaterializedDelegateBinding PlanDelegateBridgeCore(
        IDelegateCreationOperation op)
    {
        IMethodSymbol targetMethod = null;
        CLeaf targetInstance = null;
        bool baseReceiver = false;
        switch (op.Target)
        {
            case IAnonymousFunctionOperation lambda:
                targetMethod = RequireRegisteredCallable(lambda.Symbol);
                break;
            case IMethodReferenceOperation methodRef:
                targetMethod = methodRef.Method;
                baseReceiver = methodRef.Instance is IInstanceReferenceOperation
                    { Syntax: Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax };
                if (methodRef.Instance != null && methodRef.Instance is not IInstanceReferenceOperation)
                    targetInstance = Const(null, StorageTypes.Object);
                break;
        }
        if (targetMethod == null)
            throw new System.NotSupportedException($"Unsupported delegate target: {op.Target.GetType().Name}");
        // MG auto-wrap (design 2026-07-11 v2, replacing the B54 struct reject and the v1 class MG
        // reject): a class/struct INSTANCE method group binds via a RECEIVER-BRIDGE — the receiver
        // object[] rides DelegateAbi.Env (the slot a closure env uses), and the bridge re-dispatches
        // env as the member's param0 (CA-M1 receiver ABI). A STRUCT receiver is DeepCloned at mint:
        // C# copies the struct receiver by value at bind time (nested structs deep, arrays/references
        // shared) — B54's aliasing failure mode is gone by construction. Mint shape: Target=this /
        // Addr=real funcaddr (selfFast JUMP_INDIRECT). Portable tagged class receivers may cross a
        // program boundary with the bundle; collapsed aggregate struct receivers remain rejected.
        if (op.Target is IMethodReferenceOperation && !targetMethod.IsStatic
            && targetMethod.MethodKind is not (MethodKind.LambdaMethod or MethodKind.LocalFunction)
            && targetMethod.ContainingType is INamedTypeSymbol recvCt0
            && IsObjectArrayEmulated(recvCt0))
        {
            var member = RequireRegisteredCallable(
                CloseMethodForPlanning(targetMethod));
            var memberPlan = _state.Methods.Callables[member];
            var recvLeaf = Const(null, StorageTypes.ObjectArray);
            var recvBridgeName =
                DelegateAbi.BridgeName(memberPlan.Name) + "_rcv";
            var binding = new DelegateBindingPlan(DelegateBindingKind.Receiver, member, recvBridgeName);
            _state.SyntheticDemandPlanner.RegisterReceiverBridge(binding);
            return new MaterializedDelegateBinding(
                binding,
                FuncRef(recvBridgeName), null, recvLeaf);
        }

        // A local user-class interface carries its object[] receiver in DelegateAbi.Env and dispatches
        // through the local closed-world receiver bridge.
        if (targetMethod.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } localIface
            && _planner.InterfaceIsLocalUserClassOnly(localIface))
        {
            if (targetInstance == null)
                throw new System.NotSupportedException(
                    $"Interface method group '{localIface.Name}.{targetMethod.Name}' has no receiver.");
            var interfaceLayout = _planner.GetLayout(localIface).Methods[targetMethod];
            var localBridge = DelegateAbi.BridgeName(
                LayoutPlanBuilder.InterfaceDispatchName(targetMethod, interfaceLayout));
            var binding = new DelegateBindingPlan(DelegateBindingKind.Receiver, targetMethod, localBridge);
            _state.SyntheticDemandPlanner.RegisterReceiverBridge(binding);
            return new MaterializedDelegateBinding(
                binding,
                FuncRef(localBridge), null, targetInstance);
        }

        if (targetMethod.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
        {
            if (targetInstance == null)
                throw new System.NotSupportedException(
                    $"Interface method group '{iface.Name}.{targetMethod.Name}' has no receiver.");
            var interfaceLayout = _planner.GetLayout(iface).Methods[targetMethod];
            var bridgeName = DelegateAbi.BridgeName(
                LayoutPlanBuilder.InterfaceDispatchName(targetMethod, interfaceLayout));
            return new MaterializedDelegateBinding(
                new DelegateBindingPlan(DelegateBindingKind.CrossProgram, targetMethod, bridgeName),
                Const(0u, StorageTypes.UInt32), targetInstance, Const(null, StorageTypes.Object));
        }

        // Stage 2 §3.7: DelegateAbi.Env for a capturing closure target (null for named methods / base.M
        // / capture-free lambdas). Resolved here in the creation site's frame.
        var envLeaf = Const(null, StorageTypes.Object);

        // Wave-9 [W3]: `base.M` binds the BASE implementation NON-virtually (C# ldftn). When the
        // compiled class (or an intermediate) overrides M, the locally registered function for the
        // base symbol is the never-exported base-instance COPY (the same body `base.M()` jumps to),
        // so bridge THAT via a pending bridge — the planner bridge would normalize to the chain-root
        // export, i.e. the most-derived override (VM-proven 6 where C# gives 103). When nothing
        // overrides M, the base symbol's registration IS the exported inherited function and the
        // planner path below stays correct (and byte-identical).
        string bridgeExportName;
        if (baseReceiver
            && _state.Methods.Callables.TryGetValue(
                targetMethod, out var baseCopy)
            && baseCopy.ExportName == null)
        {
            bridgeExportName = DelegateAbi.BridgeName(baseCopy.Name);
            PlanDelegateDemand(
                targetMethod, bridgeExportName,
                _state.TypeParamMap);
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
            if (targetMethod.IsGenericMethod)
            {
                var constructedLf = CloseMethodForPlanning(targetMethod);
                if (!constructedLf.TypeArguments.Any(ta => ta is ITypeParameterSymbol))
                {
                    targetMethod = constructedLf;
                    RequireRegisteredCallable(targetMethod);
                }
            }
            // SS2B: a non-generic hoisted closure resolves through the per-spec registry (the ambient
            // enclosing-spec args identify WHICH copy this creation site binds); generic LFs keep their
            // constructed-symbol registration above.
            MethodContext.ClosureSpec bridgeClosure = null;
            MethodSlot targetSlot;
            if (_state.Methods.TryGetClosureSpec(targetMethod, _state.ComposeClosureKeyArgs(targetMethod), out bridgeClosure))
                targetSlot = bridgeClosure.Slot;
            else if (!_methodSlots.TryGetValue(targetMethod, out targetSlot))
                throw new System.InvalidOperationException($"Lambda/local function '{targetMethod.Name}' not registered.");
            bridgeExportName = DelegateAbi.BridgeName(targetSlot.VarPrefix);
            if (bridgeClosure != null)
                _state.SyntheticDemandPlanner.RegisterClosureBridge(
                    bridgeExportName, bridgeClosure.Name);
            // Carry the current type-param map by reference — it is immutable and per-EmitMethod fresh, so
            // it stays valid for the drain (which runs after generic-method emit clears the ambient map).
            PlanDelegateDemand(
                targetMethod, bridgeExportName,
                _state.TypeParamMap);
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
            var constructed = CloseMethodForPlanning(targetMethod);
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
            RequireRegisteredCallable(constructed);
            bridgeExportName = DelegateAbi.BridgeName(
                _state.Methods.Callables[constructed].Name);
            PlanDelegateDemand(
                constructed, bridgeExportName,
                _state.TypeParamMap);
            // B52: advance targetMethod to the registered specialization (mirroring the local-function
            // arm) so the variance/adapter block below enqueues the ADAPTER against the spec that is
            // actually emitted — otherwise the adapter names the raw generic definition, EmitPending-
            // SigAdapterBridges cannot find it in _methodFunctions, and the sig-adapter FuncRef dangles.
            targetMethod = constructed;
        }
        // wave-13 staticro lens (2026-07-04): a static method on a plain (non-UdonSharpBehaviour)
        // helper class is never pre-planned by LayoutPlanBuilder (Phase 1 only discovers
        // UdonSharpBehaviour classes) — GetDelegateBridgeLayout's Plan() call would throw on the
        // frozen planner. A plain (non-delegate) call to the same method already works via
        // CollectForeignStaticCallsInOperation's per-program inlining into _methodFunctions; route
        // the delegate-bridge naming through that same registration instead, exactly like the
        // lambda/local-function/generic-method arms above.
        else if (targetMethod.IsStatic
            && _state.Methods.Callables.TryGetValue(
                targetMethod, out var foreignFunc)
            && !ExternResolver.IsUdonSharpBehaviour(targetMethod.ContainingType))
        {
            bridgeExportName = DelegateAbi.BridgeName(foreignFunc.Name);
            PlanDelegateDemand(
                targetMethod, bridgeExportName,
                _state.TypeParamMap);
        }
        // R-M2 (design §2): a method-group binding of a THIS-CLASS private / private-internal method. The
        // planner no longer plans a speculative bridge for it (LayoutPlanBuilder.IsExcludedFromSpeculativeBridge),
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
                 && _state.Methods.Callables.TryGetValue(
                     targetMethod, out var privFunc)
                 && LayoutPlanBuilder.IsExcludedFromSpeculativeBridge(targetMethod)
                 && SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, _classSymbol))
        {
            bridgeExportName = DelegateAbi.BridgeName(privFunc.Name);
            PlanDelegateDemand(
                targetMethod, bridgeExportName,
                _state.TypeParamMap);
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
            var sigS = DelegateAbi.BuildSigPart(
                delegateInvoke, _state.Types,
                _state.TypeParamMap);
            if (sigS != DelegateAbi.BuildSigPart(
                    targetMethod, _state.Types,
                    _state.TypeParamMap))
            {
                if (targetInstance == null)
                {
                    var targetKey = DelegateAbi.BridgeTargetKey(bridgeExportName);
                    var adapterName = DelegateAbi.SigAdapterName(targetKey, sigS);
                    // SS2B: a closure target's func was registered under the plain bridge name above;
                    // the adapter drain resolves by name, so alias it under the adapter name too.
                    _state.SyntheticDemandPlanner.RegisterClosureBridgeAlias(
                        bridgeExportName, adapterName);
                    // [X1] leaf mapping, adapter flavor (C3 stage 2): a this-receiver VIRTUAL method
                    // group statically binds the BASE declaration, but the adapter's InternalCall must
                    // run the most-derived override visible from the compiled class — exactly like the
                    // plain chain-root bridge's body ([W1] ResolveMostDerivedOverride). Registering the
                    // base def either DANGLED the adapter FuncRef (base def never in _methodFunctions —
                    // loud CFuncRef reject) or, with a base-instance copy registered by a base.M call,
                    // silently InternalCalled the BASE body instead of the leaf. `base.M` keeps its
                    // static binding (non-virtual by C# ldftn semantics — the copy IS the target).
                    var adapterTarget = !baseReceiver && targetMethod.MethodKind == MethodKind.Ordinary
                        && (targetMethod.IsVirtual || targetMethod.IsOverride || targetMethod.IsAbstract)
                        ? ResolveMostDerivedOverrideForPlanning(
                            targetMethod)
                        : targetMethod;
                    var adapterBinding = new DelegateBindingPlan(
                        DelegateBindingKind.SignatureAdapter, adapterTarget, adapterName);
                    _state.SyntheticDemandPlanner.RegisterSigAdapter(
                        adapterBinding, delegateInvoke,
                        _state.TypeParamMap);
                    return new MaterializedDelegateBinding(
                        adapterBinding,
                        FuncRef(adapterName), targetInstance, envLeaf);
                }

                var innerBundle = Const(null, StorageTypes.ObjectArray);

                // The wrapper's INNER dispatch must speak the inner bundle's OWN protocol — here, the
                // third-party target's OWN signature (targetMethod, sig-T), never sig-S: DelegateAbi.Method names
                // targetMethod's OWN plain bridge (bridgeExportName, planned unconditionally on the
                // FOREIGN class per its speculative-bridge policy), which reads/writes sig-T's conv
                // vars — staging under sig-S would silently drop values across the dispatch.
                var wrapperName = PlanWrapperSig(
                    delegateInvoke, targetMethod,
                    _state.TypeParamMap);
                return new MaterializedDelegateBinding(
                    new DelegateBindingPlan(
                        DelegateBindingKind.Wrapper,
                        targetMethod,
                        wrapperName,
                        bridgeExportName),
                    FuncRef(wrapperName), null, innerBundle);
            }
        }

        var funcRef = FuncRef(bridgeExportName);
        var bindingKind = targetMethod.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
            ? DelegateBindingKind.Closure : DelegateBindingKind.Direct;
        return new MaterializedDelegateBinding(
            new DelegateBindingPlan(bindingKind, targetMethod, bridgeExportName),
            funcRef, targetInstance, envLeaf);
    }

    // ── CW1 lift: runtime-polymorphic PROPERTY/INDEXER accessor dispatch on v1-class receivers ──
    // The accessor twin of InvocationHandler's v2b-2 method arm: the SAME VirtualDispatch machinery
    // (IsDispatchSite gate shared with the recursion enumerator, ResolveTargets closed-world set,
    // closed-typeobj invariant) lowers a virtual/override/abstract accessor reference to an
    // inline typeobj-ReferenceEquals chain / devirtualized direct access / empty-set null lowering.
    // Lives in LoweringServices because four emission surfaces share it: the property/indexer READ arms
    // (InvocationHandler.Members), the SET path (PreparePropertySet), the compound read/write-back
    // (LValueLowerer.CaptureLValue/EmitWriteBack), and the property-subpattern read
    // (OperatorHandler's pattern lowering).

    /// <summary>Phase-A armor: virtual dispatch answers through runtime type identity (typeobj) or the
    /// minted-set enumeration, and neither is spec-sound when the receiver's static type still carries a
    /// type parameter or when the family was minted through an OPEN construction site — every closed spec
    /// there shares one typeobj, and cross-context mints are invisible to exact-symbol assignability.
    /// Same choke-point polarity as EmitTypeCheck's family reject: loud over a silent
    /// base-impl call / cross-spec dispatch.</summary>
    internal void AssertClosedVirtualDispatch(INamedTypeSymbol recvTy, IReadOnlyList<VDispatchTarget> targets, IMethodSymbol target)
    {
        ClassAbiPolicy.AssertClosed(recvTy, $"virtual call '{target.Name}' receiver");
        foreach (var dispatchTarget in targets)
            ClassAbiPolicy.AssertClosed(dispatchTarget.Concrete, $"virtual call '{target.Name}' target");
    }

    /// <summary>Consume the accessor dispatch gate and closed receiver type
    /// materialized for this exact source specialization.</summary>
    internal bool IsAccessorDispatchSite(
        BoundCallSite site,
        out INamedTypeSymbol recvTy)
    {
        recvTy = site?.ReceiverType;
        return site?.UsesRuntimeDispatch == true
               && recvTy != null;
    }

    /// <summary>Stage the dispatch legs ONCE in the C# order — receiver, then ordinal-slotted index
    /// args — into scratch slots: the typeobj chain consumes each leg once per arm, so raw leaves must
    /// be materialized (mirrors EmitVirtualChain's staging; indexer parameters can never be ref/out,
    /// so no copy-back protocol exists here).</summary>
    internal (CLeaf Recv, List<CLeaf> IndexArgs) StageAccessorDispatchLegs(IPropertyReferenceOperation op)
    {
        var recvSlot = _state.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
        EmitAssign(recvSlot, LoadInstanceRaw(op.Instance));
        var staged = new List<CLeaf>();
        if (op.Property.IsIndexer)
        {
            var ordered = EvaluateIndexerArgs(op);
            for (int i = 0; i < ordered.Count; i++)
            {
                var argType = i < op.Property.Parameters.Length
                    ? GetStorageTypeName(op.Property.Parameters[i].Type) : "SystemObject";
                var s = _state.Builder.AllocScratch(new StorageType(argType));
                EmitAssign(s, ordered[i]);
                staged.Add(SlotRef(s));
            }
        }
        return (SlotRef(recvSlot), staged);
    }

    /// <summary>CW1 lift: lower a runtime-polymorphic accessor access through the SAME closed-world
    /// machinery as the v2b-2 method arm — ResolveTargets, closed invariant, then ≥2 targets →
    /// inline typeobj-ReferenceEquals chain; singleton/sealed → devirtualized direct access; empty →
    /// LogError + default for a read / LogError + skip for a write (closed-world: no minted implementor
    /// means the receiver must be null; CLR NREs — §2.6 polarity, legs already evaluated for
    /// side-effect parity). <paramref name="setValue"/> null ⇒ GET (returns the value leaf); non-null ⇒
    /// SET (returns null). Legs arrive STAGED (<see cref="StageAccessorDispatchLegs"/>).</summary>
    internal CLeaf EmitAccessorDispatch(IPropertyReferenceOperation operation, INamedTypeSymbol recvTy,
        IMethodSymbol accessor, CLeaf recv, List<CLeaf> indexArgs, CLeaf setValue,
        BoundCallSite boundSite = null)
    {
        var prop = operation.Property;
        var interfaceDispatch = recvTy.TypeKind == TypeKind.Interface;
        boundSite ??= RequireBoundCallSite(
            operation,
            setValue == null ? CallableSiteKind.PropertyGet : CallableSiteKind.PropertySet);
        var targets = boundSite.RequireDispatch().RuntimeTargets;
        if (!interfaceDispatch) AssertClosedVirtualDispatch(recvTy, targets, accessor);
        bool isSet = setValue != null;
        string memberKind = prop.IsIndexer ? "indexer" : "property";

        if (targets.Count == 0)
        {
            EmitExternVoid(UdonAbi.DebugLogError,
                new List<CLeaf> { Const(
                    $"USugar: NullReferenceException — virtual {memberKind} '{prop.ContainingType.Name}.{prop.Name}' has no minted implementor, so the receiver must be null ({_classSymbol.Name}). "
                    + (isSet ? "Skipping the write." : "Returning default."),
                    StorageTypes.String) });
            return isSet ? null : SlotRef(_state.Builder.AllocScratch(GetStorageType(prop.Type)));
        }

        if (recvTy.IsSealed || targets.Count == 1)
            return EmitAccessorImplAccess(targets[0], prop, recv, indexArgs, setValue);

        var typeObjSlot = _state.Builder.AllocScratch(StorageTypes.String);
        EmitAssign(typeObjSlot, AggregateAbi.ReadSlot(_builder, recv, BundleAbi.Type, StorageTypes.String));
        int destSlot = isSet ? -1 : _state.Builder.AllocScratch(GetStorageType(prop.Type));

        // Phase-A armor: a null receiver or a laundered non-bundle value matches no arm — LogError +
        // default(read)/skip(write), never silent (mirrors EmitVirtualChain's matched flag).
        var matched = _state.Builder.AllocScratch(StorageTypes.Boolean);
        EmitAssign(matched, Const(false, StorageTypes.Boolean));

        foreach (var t in targets)
        {
            var eq = ExternCall(UdonAbi.StringEquality,
                new List<CLeaf> { SlotRef(typeObjSlot), LoadField(t.TypeObjVar, StorageTypes.String) }, StorageTypes.Boolean);
            _builder.EmitIf(eq, _ =>
            {
                EmitAssign(matched, Const(true, StorageTypes.Boolean));
                var val = EmitAccessorImplAccess(t, prop, recv, indexArgs, setValue);
                if (!isSet) EmitAssign(destSlot, val);
            }, null);
        }

        var noMatch = ExternCall(UdonAbi.BooleanNot,
            new List<CLeaf> { SlotRef(matched) }, StorageTypes.Boolean);
        _builder.EmitIf(noMatch, _ =>
            EmitExternVoid(UdonAbi.DebugLogError,
                new List<CLeaf> { Const(
                    $"USugar: NullReferenceException — virtual {memberKind} '{prop.ContainingType.Name}.{prop.Name}' accessed on a null or non-class receiver ({_classSymbol.Name}). "
                    + (isSet ? "Skipping the write." : "Returning default."),
                    StorageTypes.String) }), null);

        return isSet ? null : SlotRef(destSlot);
    }

    /// <summary>One dispatch arm's impl access. A COMPUTED accessor impl is a direct FlatFunction call
    /// (receiver + index args [+ value] — the same convention as the static arms); an AUTO accessor
    /// impl has no body, so it lowers to a layout-slot read/write against the CONCRETE target's layout
    /// (an auto OVERRIDE's backing slot exists only in the concrete layout; a base auto slot keeps its
    /// chain-walk index there). A struct-typed getter result deep-clones (C# getters return by value);
    /// a class-typed result stays a reference (IsAggregateValue is false for classes).</summary>
    CLeaf EmitAccessorImplAccess(VDispatchTarget t, IPropertySymbol prop, CLeaf recv, List<CLeaf> indexArgs, CLeaf setValue)
    {
        if (t.Impl.AssociatedSymbol is IPropertySymbol implProp && !UasmEmitter.IsComputedProperty(implProp))
        {
            var layout = _state.Aggregates.GetLayout(t.Concrete);
            if (!layout.TryGetIndex(implProp, out var slotIdx))
                throw new InvalidOperationException(
                    $"Auto-property '{implProp.ContainingType.Name}.{implProp.Name}' has no layout slot on '{t.Concrete.Name}'.");
            if (setValue != null)
            {
                AggregateAbi.WriteSlot(_builder, recv, slotIdx, setValue);
                return null;
            }
            var slotVal = AggregateAbi.ReadSlot(_builder, recv, slotIdx, StorageTypes.Object);
            return prop.Type is INamedTypeSymbol slotAgg && IsAggregateValue(slotAgg)
                ? AggregateAbi.DeepClone(_builder, slotVal, slotAgg, _state.Aggregates.GetLayout) : slotVal;
        }
        var args = new List<CLeaf> { recv };
        args.AddRange(indexArgs);
        if (setValue != null)
        {
            args.Add(setValue);
            EmitExprStmt(EmitCallToMethod(
                RequireRegisteredCallable(t.Impl), args));
            return null;
        }
        var ret = EmitCallToMethod(
            RequireRegisteredCallable(t.Impl), args);
        return prop.Type is INamedTypeSymbol retAgg && IsAggregateValue(retAgg)
            ? AggregateAbi.DeepClone(_builder, ret, retAgg, _state.Aggregates.GetLayout) : ret;
    }

    // ── M4b: ToString dispatch on v1-class receivers (the object.ToString slot) ──

    /// <summary>One string.Concat(object,object) operand, already evaluated from its UNWRAPPED node:
    /// dispatches a v1-class value through the object.ToString slot (M4b), rejects a type that cannot
    /// stringify honestly (v1-class-free ndim per CW15 — the class case is handled above, not rejected),
    /// and synthesizes a user enum's name string (B67); everything else passes through. Shared by the
    /// binary concat arms (OperatorHandler) and the compound `s += x` arm (CompoundAssignmentHandler)
    /// so the three conversions cannot drift per surface — and so an ndim operand rejects even when it
    /// sits beside a class operand (the pre-share class arm returned before the reject ran).</summary>
    internal CLeaf ConvertConcatOperand(CLeaf value, IOperation unwrapped)
    {
        var type = ResolveType(unwrapped.Type);
        if (type != null && SourceShape(type).IsBundle)
            return EmitKnownBundleToString(
                value, type, nullIsError: false);
        var enumString =
            TryEmitEnumToString(value, type);
        if (enumString != null) return enumString;
        if (type?.SpecialType == SpecialType.System_Object
            || type is INamedTypeSymbol
                { TypeKind: TypeKind.Interface })
            return EmitDynamicBundleStringOperand(value);
        return value;
    }

    internal CLeaf EmitKnownBundleToString(
        CLeaf value, ITypeSymbol type, bool nullIsError)
    {
        type = ResolveType(type);
        var shape = SourceShape(type);
        if (!shape.IsBundle)
            throw new InvalidOperationException(
                $"'{type}' is not a compiler-owned bundle.");

        if (type is INamedTypeSymbol record
            && record.IsRecord)
            return EmitAggregateString(value, record);
        if (shape.Bundle == RuntimeBundleKind.Class
            && type is INamedTypeSymbol classType)
            return EmitClassToStringDispatch(
                classType, value, nullIsError,
                useOverrides: true);
        if (shape.Bundle == RuntimeBundleKind.Aggregate
            && type is INamedTypeSymbol aggregate)
            return EmitAggregateString(value, aggregate);

        var result =
            _builder.AllocScratch(StorageTypes.String);
        _builder.EmitIf(
            NullableAbi.IsNull(_builder, value),
            _ =>
            {
                if (nullIsError)
                    EmitExternVoid(
                        UdonAbi.DebugLogError,
                        new List<CLeaf>
                        {
                            Const(
                                $"USugar: ToString() on null "
                                + "'"
                                + type.ToDisplayString(
                                    SymbolDisplayFormat.MinimallyQualifiedFormat)
                                + "'. "
                                + "Returning \"\".",
                                StorageTypes.String)
                        });
                EmitAssign(
                    result,
                    Const("", StorageTypes.String));
            },
            _ => EmitAssign(
                result,
                Const(
                    ClassAbi.RuntimeTypeName(type),
                    StorageTypes.String)));
        return SlotRef(result);
    }

    CLeaf EmitAggregateString(
        CLeaf value, INamedTypeSymbol type)
    {
        var layout = _state.Aggregates.GetLayout(type);
        if (!type.IsTupleType
            && !type.IsAnonymousType
            && !type.IsRecord)
            return Const(
                ClassAbi.RuntimeTypeName(type),
                StorageTypes.String);

        string opening;
        string closing;
        if (type.IsTupleType)
        {
            opening = "(";
            closing = ")";
        }
        else if (type.IsAnonymousType)
        {
            opening = "{ ";
            closing = " }";
        }
        else
        {
            opening = type.Name + " { ";
            closing = " }";
        }

        CLeaf result = Const(opening, StorageTypes.String);
        for (var i = 0; i < layout.Fields.Count; i++)
        {
            var field = layout.Fields[i];
            if (i != 0)
                result = ConcatString(
                    result,
                    Const(", ", StorageTypes.String));
            if (!type.IsTupleType)
                result = ConcatString(
                    result,
                    Const(
                        field.Name + " = ",
                        StorageTypes.String));
            var fieldValue = AggregateAbi.ReadSlot(
                _builder, value, field.Index,
                StorageTypes.Object);
            result = ConcatString(
                result,
                EmitFieldString(fieldValue, field.Type));
        }
        return ConcatString(
            result, Const(closing, StorageTypes.String));
    }

    internal CLeaf EmitBundleValueEquality(
        CLeaf left, CLeaf right,
        INamedTypeSymbol type)
    {
        var shape = SourceShape(type);
        var leftSlot =
            _builder.AllocScratch(StorageTypes.ObjectArray);
        var rightSlot =
            _builder.AllocScratch(StorageTypes.ObjectArray);
        EmitAssign(leftSlot, left);
        EmitAssign(rightSlot, right);

        if (shape.Bundle != RuntimeBundleKind.Class)
            return EmitBundleFieldsEqual(
                SlotRef(leftSlot), SlotRef(rightSlot), type);

        var referenceEqual = ExternCall(
            UdonAbi.ObjectEquality,
            new List<CLeaf>
            {
                SlotRef(leftSlot), SlotRef(rightSlot)
            },
            StorageTypes.Boolean);
        var leftPresent = ExternCall(
            UdonAbi.ObjectInequality,
            new List<CLeaf>
            {
                SlotRef(leftSlot),
                Const(null, StorageTypes.Object)
            },
            StorageTypes.Boolean);
        var rightPresent = ExternCall(
            UdonAbi.ObjectInequality,
            new List<CLeaf>
            {
                SlotRef(rightSlot),
                Const(null, StorageTypes.Object)
            },
            StorageTypes.Boolean);
        var bothPresent = ExternCall(
            UdonAbi.BooleanLogicalAnd,
            new List<CLeaf>
            {
                leftPresent, rightPresent
            },
            StorageTypes.Boolean);
        var result =
            _builder.AllocScratch(StorageTypes.Boolean);
        _builder.EmitIf(
            referenceEqual,
            _ => EmitAssign(
                result,
                Const(true, StorageTypes.Boolean)),
            _ => _builder.EmitIf(
                bothPresent,
                __ => EmitAssign(
                    result,
                    EmitBundleFieldsEqual(
                        SlotRef(leftSlot),
                        SlotRef(rightSlot), type)),
                __ => EmitAssign(
                    result,
                    Const(false, StorageTypes.Boolean))));
        return SlotRef(result);
    }

    CLeaf EmitBundleFieldsEqual(
        CLeaf left, CLeaf right,
        INamedTypeSymbol type)
    {
        var layout = _state.Aggregates.GetLayout(type);
        CLeaf result = ExternCall(
            UdonAbi.StringEquality,
            new List<CLeaf>
            {
                AggregateAbi.ReadSlot(
                    _builder, left, BundleAbi.Type,
                    StorageTypes.String),
                AggregateAbi.ReadSlot(
                    _builder, right, BundleAbi.Type,
                    StorageTypes.String)
            },
            StorageTypes.Boolean);
        foreach (var field in layout.Fields)
        {
            var leftValue = AggregateAbi.ReadSlot(
                _builder, left, field.Index,
                StorageTypes.Object);
            var rightValue = AggregateAbi.ReadSlot(
                _builder, right, field.Index,
                StorageTypes.Object);
            var fieldType = ResolveType(field.Type);
            var fieldShape = SourceShape(fieldType);
            CLeaf equal;
            if (fieldType is INamedTypeSymbol nested
                && (fieldShape.Bundle
                        == RuntimeBundleKind.Aggregate
                    || nested.IsRecord))
                equal = EmitBundleValueEquality(
                    leftValue, rightValue, nested);
            else if (fieldType
                     is INamedTypeSymbol
                         { DelegateInvokeMethod: not null })
                equal = DelegateAbi.CompareDelegates(
                    _builder, leftValue, rightValue,
                    isNotEquals: false);
            else
                equal = ExternCall(
                    UdonAbi.ObjectEquals,
                    new List<CLeaf>
                    {
                        leftValue, rightValue
                    },
                    StorageTypes.Boolean);
            result = ExternCall(
                UdonAbi.BooleanLogicalAnd,
                new List<CLeaf> { result, equal },
                StorageTypes.Boolean);
        }
        return result;
    }

    internal CLeaf EmitBundleValueHash(
        CLeaf value, INamedTypeSymbol type)
    {
        var layout = _state.Aggregates.GetLayout(type);
        CLeaf hash = ExternCall(
            UdonAbi.ObjectGetHashCode,
            new List<CLeaf>
            {
                AggregateAbi.ReadSlot(
                    _builder, value, BundleAbi.Type,
                    StorageTypes.String)
            },
            StorageTypes.Int32);
        foreach (var field in layout.Fields)
        {
            var fieldValue = AggregateAbi.ReadSlot(
                _builder, value, field.Index,
                StorageTypes.Object);
            CLeaf fieldHash;
            if (SourceShape(ResolveType(field.Type))
                    .IsBundle)
            {
                // A constant contribution keeps the equality/hash contract
                // for structural bundle fields without falling back to the
                // object[] reference hash.
                fieldHash = Const(0, StorageTypes.Int32);
            }
            else
            {
                var hashSlot =
                    _builder.AllocScratch(StorageTypes.Int32);
                _builder.EmitIf(
                    NullableAbi.IsNull(
                        _builder, fieldValue),
                    _ => EmitAssign(
                        hashSlot,
                        Const(0, StorageTypes.Int32)),
                    _ => EmitAssign(
                        hashSlot,
                        ExternCall(
                            UdonAbi.ObjectGetHashCode,
                            new List<CLeaf>
                                { fieldValue },
                            StorageTypes.Int32)));
                fieldHash = SlotRef(hashSlot);
            }
            hash = ExternCall(
                UdonAbi.Int32Add,
                new List<CLeaf>
                {
                    ExternCall(
                        UdonAbi.Int32Multiply,
                        new List<CLeaf>
                        {
                            hash,
                            Const(
                                -1521134295,
                                StorageTypes.Int32)
                        },
                        StorageTypes.Int32),
                    fieldHash
                },
                StorageTypes.Int32);
        }
        return hash;
    }

    CLeaf EmitFieldString(CLeaf value, ITypeSymbol type)
    {
        type = ResolveType(type);
        if (type != null && SourceShape(type).IsBundle)
            return EmitKnownBundleToString(
                value, type, nullIsError: false);
        var enumString = TryEmitEnumToString(value, type);
        if (enumString != null) return enumString;
        return ExternCall(
            UdonAbi.StringConcatObjects,
            new List<CLeaf>
            {
                Const("", StorageTypes.String),
                value
            },
            StorageTypes.String);
    }

    CLeaf ConcatString(CLeaf left, CLeaf right)
        => ExternCall(
            UdonAbi.StringConcatObjects,
            new List<CLeaf> { left, right },
            StorageTypes.String);

    internal CLeaf EmitDynamicObjectEquals(
        CLeaf left, CLeaf right)
    {
        var leftSlot =
            _builder.AllocScratch(StorageTypes.Object);
        var rightSlot =
            _builder.AllocScratch(StorageTypes.Object);
        EmitAssign(leftSlot, left);
        EmitAssign(rightSlot, right);
        var leftValue = SlotRef(leftSlot);
        var rightValue = SlotRef(rightSlot);
        var leftBundle = EmitIsCompilerBundle(leftValue);
        var rightBundle = EmitIsCompilerBundle(rightValue);
        var result =
            _builder.AllocScratch(StorageTypes.Boolean);
        EmitAssign(
            result,
            Const(false, StorageTypes.Boolean));

        _builder.EmitIf(leftBundle, _ =>
        {
            _builder.EmitIf(rightBundle, __ =>
                EmitKnownBundleEquality(
                    leftValue, rightValue, result), null);
        }, _ =>
        {
            _builder.EmitIf(rightBundle, null, __ =>
                EmitAssign(
                    result,
                    ExternCall(
                        UdonAbi.ObjectEquals,
                        new List<CLeaf>
                        {
                            leftValue, rightValue
                        },
                        StorageTypes.Boolean)));
        });
        return SlotRef(result);
    }

    CLeaf EmitIsCompilerBundle(CLeaf value)
        => BundleProbe.IsTagged(
            _builder, value, BundleAbi.Prefix);

    void EmitKnownBundleEquality(
        CLeaf left,
        CLeaf right,
        int destination)
    {
        var leftType = AggregateAbi.ReadSlot(
            _builder, left, BundleAbi.Type,
            StorageTypes.String);
        var rightType = AggregateAbi.ReadSlot(
            _builder, right, BundleAbi.Type,
            StorageTypes.String);
        var recognizedLeft =
            _builder.AllocScratch(StorageTypes.Boolean);
        var recognizedRight =
            _builder.AllocScratch(StorageTypes.Boolean);
        EmitAssign(
            recognizedLeft,
            Const(false, StorageTypes.Boolean));
        EmitAssign(
            recognizedRight,
            Const(false, StorageTypes.Boolean));
        var seen = new HashSet<string>(
            StringComparer.Ordinal);

        void EmitCandidate(
            string runtimeTypeId,
            Func<CLeaf> equality)
        {
            if (!seen.Add(runtimeTypeId))
                return;
            var leftMatches = ExternCall(
                UdonAbi.StringEquality,
                new List<CLeaf>
                {
                    leftType,
                    Const(runtimeTypeId,
                        StorageTypes.String)
                },
                StorageTypes.Boolean);
            var rightMatches = ExternCall(
                UdonAbi.StringEquality,
                new List<CLeaf>
                {
                    rightType,
                    Const(runtimeTypeId,
                        StorageTypes.String)
                },
                StorageTypes.Boolean);
            _builder.EmitIf(leftMatches, _ =>
                EmitAssign(
                    recognizedLeft,
                    Const(true, StorageTypes.Boolean)), null);
            _builder.EmitIf(rightMatches, _ =>
                EmitAssign(
                    recognizedRight,
                    Const(true, StorageTypes.Boolean)), null);
            var both = ExternCall(
                UdonAbi.BooleanLogicalAnd,
                new List<CLeaf>
                    { leftMatches, rightMatches },
                StorageTypes.Boolean);
            _builder.EmitIf(both, _ =>
                EmitAssign(destination, equality()), null);
        }

        foreach (var candidate
                 in _state.Program.Types.KnownBundleTypes)
        {
            var resolved = ResolveType(candidate);
            if (resolved == null) continue;
            var shape = SourceShape(resolved);
            var runtimeTypeId =
                BundleAbi.RuntimeTypeId(resolved);
            if (shape.Bundle
                    == RuntimeBundleKind.Delegate
                && resolved is INamedTypeSymbol)
                EmitCandidate(
                    runtimeTypeId,
                    () => DelegateAbi.CompareDelegates(
                        _builder, left, right,
                        isNotEquals: false));
            else if (resolved
                         is INamedTypeSymbol aggregate
                     && (shape.Bundle
                             == RuntimeBundleKind.Aggregate
                         || aggregate.IsRecord))
                EmitCandidate(
                    runtimeTypeId,
                    () => EmitBundleValueEquality(
                        left, right, aggregate));
            else
                EmitCandidate(
                    runtimeTypeId,
                    () => ExternCall(
                        UdonAbi.ObjectEquality,
                        new List<CLeaf> { left, right },
                        StorageTypes.Boolean));
        }
        var bothRecognized = ExternCall(
            UdonAbi.BooleanLogicalAnd,
            new List<CLeaf>
            {
                SlotRef(recognizedLeft),
                SlotRef(recognizedRight)
            },
            StorageTypes.Boolean);
        var unknown = ExternCall(
            UdonAbi.BooleanNot,
            new List<CLeaf> { bothRecognized },
            StorageTypes.Boolean);
        _builder.EmitIf(unknown, _ =>
            EmitExternVoid(
                UdonAbi.DebugLogError,
                new List<CLeaf>
                {
                    Const(
                        "USugar: object.Equals received a foreign compiler bundle.",
                        StorageTypes.String)
                }), null);
    }

    CLeaf EmitDynamicBundleStringOperand(CLeaf value)
    {
        var result = _builder.AllocScratch(StorageTypes.Object);
        EmitAssign(result, value);
        var isBundle = BundleProbe.IsTagged(
            _builder, value, BundleAbi.Prefix);
        _builder.EmitIf(isBundle, _ =>
        {
            var typeId = AggregateAbi.ReadSlot(
                _builder, value, BundleAbi.Type,
                StorageTypes.String);
            var seen = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var candidate
                     in _state.Program.Types
                         .KnownBundleTypes)
            {
                var resolved = ResolveType(candidate);
                if (resolved == null) continue;
                var id = BundleAbi.RuntimeTypeId(resolved);
                if (!seen.Add(id)) continue;
                var matches = ExternCall(
                    UdonAbi.StringEquality,
                    new List<CLeaf>
                    {
                        typeId,
                        Const(id, StorageTypes.String)
                    },
                    StorageTypes.Boolean);
                _builder.EmitIf(matches, __ =>
                {
                    EmitAssign(
                        result,
                        EmitKnownBundleToString(
                            value, resolved,
                            nullIsError: false));
                }, null);
            }
        }, null);
        return SlotRef(result);
    }

    /// <summary>M4b: stringify a v1-class receiver through the object.ToString dispatch slot — the third
    /// lowering built on the v2b-2 machinery (ResolveTargets closed-world set, closed-typeobj invariant
    /// armor, typeobj-ReferenceEquals chain, sealed/singleton devirt). Three surfaces share it: the
    /// interpolation hole, the string-concat operand, and the direct .ToString() call. An arm whose
    /// most-derived impl is a user override direct-calls it (receiver as param0); an arm whose impl is
    /// BCL object.ToString itself assigns the C#-parity runtime type-name CONSTANT
    /// (<see cref="ClassAbi.RuntimeTypeName"/>). <paramref name="useOverrides"/> false = the
    /// base.ToString()-bound-to-object.ToString form: C# calls Object.ToString non-virtually, which still
    /// prints the RUNTIME type name (it reads GetType()), so every arm is a type-name constant there.
    /// Null parity: an explicit null guard runs BEFORE the bundle[0] typeobj read — C# yields "" for a
    /// null interpolation hole / concat operand (silent, <paramref name="nullIsError"/> false), while a
    /// direct null.ToString() would NRE (the established null-invoke deviation: LogError + "",
    /// <paramref name="nullIsError"/> true). A NON-null no-match (laundered value) is always
    /// LogError + "" (the chain-armor polarity).</summary>
    internal CLeaf EmitClassToStringDispatch(INamedTypeSymbol recvTy, CLeaf recv,
        bool nullIsError, bool useOverrides)
    {
        var slot = _state.Program.SyntheticObjectToStringSlot;
        var targets = _state.Program
            .RequireSyntheticDispatch(
                recvTy, slot).RuntimeTargets;
        AssertClosedVirtualDispatch(recvTy, targets, slot);

        var recvSlot = _state.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
        EmitAssign(recvSlot, recv);
        var destSlot = _state.Builder.AllocScratch(StorageTypes.String);

        void EmitNoMatch()
        {
            EmitExternVoid(UdonAbi.DebugLogError,
                new List<CLeaf> { Const(
                    $"USugar: ToString dispatch on '{recvTy.Name}' matched no minted class — non-class "
                    + $"receiver ({_classSymbol.Name}). Returning \"\".",
                    StorageTypes.String) });
            EmitAssign(destSlot, Const("", StorageTypes.String));
        }

        _builder.EmitIf(NullableAbi.IsNull(_builder, SlotRef(recvSlot)), _ =>
        {
            if (nullIsError)
                EmitExternVoid(UdonAbi.DebugLogError,
                    new List<CLeaf> { Const(
                        $"USugar: NullReferenceException — ToString() on a null '{recvTy.Name}' receiver "
                        + $"({_classSymbol.Name}). Returning \"\".",
                        StorageTypes.String) });
            EmitAssign(destSlot, Const("", StorageTypes.String));
        }, _ =>
        {
            if (targets.Count == 0)
            {
                // Closed-world: no minted implementor means the receiver had to be null (handled above) —
                // a non-null value here is laundered, never a silent fall-through.
                EmitNoMatch();
                return;
            }
            if (recvTy.IsSealed || targets.Count == 1)
            {
                EmitAssign(destSlot, ClassToStringArmValue(targets[0], SlotRef(recvSlot), useOverrides));
                return;
            }

            var typeObjSlot = _state.Builder.AllocScratch(StorageTypes.String);
            EmitAssign(typeObjSlot, AggregateAbi.ReadSlot(_builder, SlotRef(recvSlot), BundleAbi.Type, StorageTypes.String));
            var matched = _state.Builder.AllocScratch(StorageTypes.Boolean);
            EmitAssign(matched, Const(false, StorageTypes.Boolean));

            foreach (var t in targets)
            {
                var eq = ExternCall(UdonAbi.StringEquality,
                    new List<CLeaf> { SlotRef(typeObjSlot), LoadField(t.TypeObjVar, StorageTypes.String) },
                    StorageTypes.Boolean);
                _builder.EmitIf(eq, _ =>
                {
                    EmitAssign(matched, Const(true, StorageTypes.Boolean));
                    EmitAssign(destSlot, ClassToStringArmValue(t, SlotRef(recvSlot), useOverrides));
                }, null);
            }

            var noMatch = ExternCall(UdonAbi.BooleanNot,
                new List<CLeaf> { SlotRef(matched) }, StorageTypes.Boolean);
            _builder.EmitIf(noMatch, _ => EmitNoMatch(), null);
        });

        return SlotRef(destSlot);
    }

    /// <summary>One ToString dispatch arm's value: a user-override impl is an ordinary direct call
    /// (recursion spill/reload rides EmitCallToMethod, so the call graph sees a precise edge); a BCL
    /// object.ToString impl — or any impl under a base-bound non-virtual form — is the concrete type's
    /// runtime-name constant.</summary>
    CLeaf ClassToStringArmValue(VDispatchTarget t, CLeaf recvRef, bool useOverrides)
        => useOverrides && IsUserClass(t.Impl.ContainingType)
            ? EmitCallToMethod(
                RequireRegisteredCallable(t.Impl),
                new List<CLeaf> { recvRef })
            : Const(ClassAbi.RuntimeTypeName(t.Concrete), StorageTypes.String);

    // ── Call helpers ──

    /// <summary>Wave-12 [V2]: TRUE auto-property detection — a compiler-generated backing field is
    /// associated with the property. The cross-arm `DeclaringSyntaxReferences.IsEmpty` checks were
    /// always FALSE for source `{ get; set; }` accessors (same trap UasmEmitter's field-declaration
    /// pass documents), so the SetProgramVariable/GetProgramVariable direct arms were dead and every
    /// cross property access dispatched accessor functions. Non-public autos take the cheaper
    /// direct-symbol arm because their backing symbol is present on the receiver heap.</summary>
    internal static bool IsNonPublicAutoCrossProperty(IMethodSymbol accessor, IPropertySymbol prop)
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
    /// normalization), which runs the receiver program's most-derived override. Reachable non-public
    /// accessors use an internal entry point registered with the program.</summary>
    internal CLeaf EmitCrossIndexerCall(IMethodSymbol accessor, CLeaf instanceVal, List<CLeaf> orderedArgs,
        bool reentrant = false)
    {
        RejectProgramLocalCrossBehaviourAccessor(accessor); // CW22
        var (exportName, paramIds, _) = GetCalleeLayout(accessor);
        var parameters = CrossCallParameters(accessor, paramIds, orderedArgs);
        var returns = accessor.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : GetCalleeReturns(accessor);
        var retType = accessor.ReturnsVoid ? "SystemVoid" : GetStorageTypeName(accessor.ReturnType);
        var value = CrossCall(
            instanceVal,
            exportName,
            parameters,
            returns,
            new StorageType(retType),
            reentrant);
        return accessor.ReturnsVoid
            ? value
            : MaterializeCrossProgramValue(
                value, accessor.ReturnType);
    }

    /// <summary>[W6] gate shared by the read/write/compound indexer sites: a user-behaviour indexer
    /// reference through a non-this receiver (the struct/extern receivers keep their own arms).</summary>
    internal static bool IsVariableReceiverBehaviourIndexer(IPropertyReferenceOperation op)
        => op.Property.IsIndexer
           && op.Instance != null && op.Instance is not IInstanceReferenceOperation
           && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
           && op.Property.ContainingType.Name != "UdonSharpBehaviour";

    /// <summary>[W6] index arguments evaluated in source order, slotted by parameter ordinal
    /// (named/reordered index args bind by name, mirroring the [W1] convention).</summary>
    internal List<CLeaf> EvaluateIndexerArgs(IPropertyReferenceOperation op)
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
    internal bool IsResolvedConcreteNonBehaviour(ITypeSymbol type)
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
    internal void GuardInterfaceDispatchRepresentation(INamedTypeSymbol ifaceType, string memberName)
        => _state.Boundary.RequireInterfaceDispatchRepresentation(ifaceType, memberName);

    /// <summary>Wave-9 round-4 [X4]/[X5]/[X9]: gate + layout lookup for a USER-INTERFACE property or
    /// indexer accessor reached through an interface-typed receiver. The [W6] cross-indexer gate
    /// tests IsUdonSharpBehaviour(Property.ContainingType) — the INTERFACE for these sites — so
    /// indexer read/write/compound and the property compound/inc-dec WRITE-BACK fell through to
    /// extern resolution and emitted nonexistent IUdonEventReceiver.__get_Item/__set_Item/__set_P
    /// externs (UasmValidationException on legal C#). Mirrors the gates of the existing interface
    /// property get/set arms: user interface (SpecialType None), variable receiver, not a resolved
    /// concrete non-behaviour, and the accessor present in the planned interface layout.</summary>
    internal bool TryGetInterfaceAccessorLayout(IPropertyReferenceOperation op, IMethodSymbol accessor,
        out MethodLayout ml)
    {
        ml = null;
        var matched = accessor != null
            && op.Property.ContainingType is INamedTypeSymbol ifaceType
            && ExternResolver.IsUserInterface(ifaceType)
            && op.Instance != null && op.Instance is not IInstanceReferenceOperation
            && !IsResolvedConcreteNonBehaviour(op.Instance.Type)
            && _planner.GetLayout(ifaceType).Methods.TryGetValue(accessor, out ml);
        if (matched)
            GuardInterfaceDispatchRepresentation((INamedTypeSymbol)op.Property.ContainingType, accessor.Name);
        return matched;
    }

    /// <summary>Dispatch an interface property/indexer accessor through its canonical interface
    /// bridge (the `__iface_*` name every implementing class exports), exactly like an interface
    /// METHOD call: SetProgramVariable each ordinal-ordered arg (indexes, then the value for a
    /// setter), SendCustomEvent the bridge, GetProgramVariable the return. Tuple-returning accessors
    /// dispatch the bare export (no bridge), mirroring EmitInterfaceCall. Void accessors self-emit
    /// and return null — never wrap in EmitExprStmt.</summary>
    internal CLeaf EmitCrossBehaviourPropertyGet(IPropertyReferenceOperation op, CLeaf instanceVal,
        StorageType returnType)
    {
        CLeaf value;
        if (IsNonPublicAutoCrossProperty(op.Property.GetMethod, op.Property))
            value = LoadProgramVariable(
                instanceVal, op.Property.Name, returnType);
        else
        {
            var (getExportName, _, getRetId) =
                GetCalleeLayout(op.Property.GetMethod);
            var getReturns = getRetId != null
                ? new[] { new ReturnSlot(getRetId, returnType) }
                : System.Array.Empty<ReturnSlot>();
            value = CrossCall(
                instanceVal,
                getExportName,
                System.Array.Empty<CrossCallParameter>(),
                getReturns,
                returnType,
                TryMarkReentrantCrossDispatch(
                    op, op.Property.GetMethod));
        }
        return MaterializeCrossProgramValue(
            value, op.Property.Type);
    }

    /// <summary>
    /// The single source-semantic choke point for values received through GetProgramVariable /
    /// SendCustomEvent transport. Aggregate source types are values in C#, despite using a shared
    /// object[] carrier in Udon, so every receive mints independent storage. A missing/null foreign
    /// return becomes the source type's default value instead of being dereferenced as a bundle.
    /// Class, delegate, array, and scalar source types retain their reference/value carrier as-is.
    /// </summary>
    internal CLeaf MaterializeCrossProgramValue(
        CLeaf value,
        ITypeSymbol sourceType,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        if (_state.ResolveSourceType(
                    sourceType,
                    typeParameterMap)
                is not INamedTypeSymbol aggregate
            || !IsAggregateValue(aggregate))
            return value;

        var result = _builder.AllocScratch(
            new StorageType(AggregateAbi.ArrayType));
        var hasValue = ExternCall(
            UdonAbi.ObjectInequality,
            new List<CLeaf>
            {
                value,
                Const(null, StorageTypes.Object)
            },
            StorageTypes.Boolean);
        _builder.EmitIf(
            hasValue,
            _ => EmitAssign(
                result,
                AggregateAbi.DeepClone(
                    _builder,
                    value,
                    aggregate,
                    _state.Aggregates.GetLayout)),
            _ => EmitAssign(
                result,
                AggregateAbi.MintDefault(
                    _builder,
                    _state.Aggregates.GetLayout(aggregate),
                    _state.Aggregates.GetLayout,
                    GetStorageTypeName)));
        return SlotRef(result);
    }

    internal CLeaf EmitInterfaceAccessorCall(IMethodSymbol accessor, MethodLayout ml, CLeaf instanceVal,
        List<CLeaf> orderedArgs, bool reentrant = false)
    {
        RejectProgramLocalCrossBehaviourAccessor(accessor); // CW22
        var parameters = CrossCallParameters(accessor, ml.ParamIds, orderedArgs);
        var rets = ml.Returns.ToArray();
        if (rets.Length > 1)
            return CrossCall(instanceVal, ml.ExportName, parameters, rets, StorageTypes.Void, reentrant);
        var dispatchName = LayoutPlanBuilder.InterfaceDispatchName(accessor, ml);
        var retType = accessor.ReturnsVoid ? "SystemVoid" : GetStorageTypeName(accessor.ReturnType);
        var value = CrossCall(instanceVal, dispatchName, parameters,
            accessor.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : rets, new StorageType(retType), reentrant);
        return accessor.ReturnsVoid
            ? value
            : MaterializeCrossProgramValue(
                value, accessor.ReturnType);
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
    internal List<CrossCallParameter> CrossCallArguments(
        System.Collections.Immutable.ImmutableArray<IArgumentOperation> args, IMethodSymbol target,
        IReadOnlyList<string> paramIds)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (paramIds == null || paramIds.Count != target.Parameters.Length)
            throw new InvalidOperationException(
                $"Cross-call ABI for '{target}' has {paramIds?.Count ?? 0} parameter ids; "
                + $"expected {target.Parameters.Length}.");

        var byOrdinal = new CLeaf[target.Parameters.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Value.Type is { } argTy)
                RejectProgramLocalCrossBehaviourArgument(argTy);
            // CW7/CW23: a delegate-carrying argument crosses by VALUE — classify it through the
            // same ladder as the store surfaces (the static-type check above sees only the
            // signature, never the captured env).
            RejectUnsafeCrossProgramDelegateArgument(args[i]);
            var p = args[i].Parameter;
            var ordinal = p != null && p.Ordinal >= 0 && p.Ordinal < byOrdinal.Length ? p.Ordinal : i;
            byOrdinal[ordinal] = VisitExpression(args[i].Value);
        }
        var parameters = new List<CrossCallParameter>(byOrdinal.Length);
        for (int o = 0; o < byOrdinal.Length; o++)
        {
            if (byOrdinal[o] == null)
                throw new InvalidOperationException(
                    $"Cross-call argument {o} for '{target}' was not materialized.");
            parameters.Add(new CrossCallParameter(
                o, paramIds[o], GetStorageType(target.Parameters[o].Type), byOrdinal[o]));
        }
        return parameters;
    }

    internal List<CrossCallParameter> CrossCallParameters(IMethodSymbol target,
        IReadOnlyList<string> paramIds, IReadOnlyList<CLeaf> orderedArguments)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (paramIds == null || orderedArguments == null
            || paramIds.Count != target.Parameters.Length
            || orderedArguments.Count != target.Parameters.Length)
            throw new InvalidOperationException(
                $"Cross-call ABI for '{target}' requires {target.Parameters.Length} parameters, got "
                + $"{paramIds?.Count ?? 0} ids and {orderedArguments?.Count ?? 0} values.");

        var parameters = new List<CrossCallParameter>(target.Parameters.Length);
        for (int i = 0; i < target.Parameters.Length; i++)
            parameters.Add(new CrossCallParameter(
                i, paramIds[i], GetStorageType(target.Parameters[i].Type), orderedArguments[i]));
        return parameters;
    }

    internal (string exportName, string[] paramIds, string retId) GetCalleeLayout(IMethodSymbol target)
    {
        // SS2B: a hoisted closure callee (generic LF specs included) resolves through the registry.
        if (target.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
            && _state.Program.TryGetClosure(
                target,
                _state.ComposeClosureKeyArgs(target),
                out var calleeClosure))
            return (
                calleeClosure.Slot.VarPrefix,
                calleeClosure.ParamVarIds.ToArray(),
                calleeClosure.ReturnSlots is { Length: 1 } ? calleeClosure.ReturnSlots[0].Id : null);
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
    internal ReturnSlot[] GetCalleeReturns(IMethodSymbol target)
    {
        // SS2B: a hoisted closure callee (generic LF specs included) resolves through the registry.
        if (target.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
            && _state.Program.TryGetClosure(
                target,
                _state.ComposeClosureKeyArgs(target),
                out var retClosure))
            return retClosure.ReturnSlots.ToArray();
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
    internal CLeaf EmitCallToMethod(IMethodSymbol target, List<CLeaf> args, SyntaxNode callSite = null)
    {
        FlatFunction func;
        // SS2B: non-generic hoisted closures resolve per-spec (ambient args) with throw-on-miss —
        // a bare-symbol fallback here would silently call another spec's copy.
        if (target.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction)
            func = _state.Methods.RequireFunction(
                _state.Program.RequireClosure(
                    target,
                    _state.ComposeClosureKeyArgs(target)));
        else if (!_methodFunctions.TryGetValue(target, out func))
            throw new InvalidOperationException($"No FlatFunction registered for method '{target.Name}'");
        var retType = func.ReturnType ?? StorageTypes.Void;

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
        var sitePlan = CallableSitePlan.Direct(target, callSite,
            IsRecursiveEdge(_currentMethod, target), _state.Recursion);
        if (sitePlan.RecursiveEdge)
        {
            if (sitePlan.TailSpared)
                return InternalCall(func.Name, args, retType, tailSpared: true);
            RegisterCallableSiteSpill(sitePlan);
            _builder.CurrentFunction.AddRecursiveCallee(func.Name);
        }

        return InternalCall(func.Name, args, retType);
    }

    /// <summary>Loud-fail armor for the struct-member-REACHABILITY side of walk-scope drift — the
    /// analogue of <see cref="ClosureEnvLeaf"/> on the delegate-capture side. A user-struct member
    /// only reaches generic SDK ABI binding (a method or accessor path) when it has NO registered FlatFunction —
    /// i.e. a Phase-1 collector (CollectStructMethodsInOperation / CollectForeignStaticCallsInOperation)
    /// or callable binding did not cover this member/reach shape. Historically that
    /// silently minted a bogus <c>SystemObjectArray.__&lt;Name&gt;__…</c> extern that only UasmValidator
    /// or the VM caught, with a message that never named the root cause (this exact shape recurred as
    /// roadmap B41/B46/B47). Fail HERE, where the bogus extern would be born, with a diagnosis instead.
    /// Sound: the bound user-struct shape is false for every SDK/native/BCL type, so this can
    /// never fire on a legitimate extern call. The source location and operation kind are appended
    /// automatically by UasmEmitter.TagLocation (the statement/expression dispatch wraps every handler).</summary>
    internal void GuardUserStructMemberReachedExtern(ITypeSymbol containingType, string memberName)
    {
        // CA-M1: the same armor covers a v1 class member (object[]-emulated) — a class instance member
        // that reached the extern path was not routed to its FlatFunction (collector-scope drift), which
        // would otherwise mint a bogus SystemObjectArray.__<Name>__ extern.
        if (containingType is INamedTypeSymbol ct && IsObjectArrayEmulated(ct))
            throw new InvalidOperationException(
                $"user struct/class member '{ct.Name}.{memberName}' reached emission without a registered "
                + "FlatFunction — a Phase-1 collector or on-demand registration arm does not cover this "
                + "member/reach shape (collector-scope drift; see roadmap B46/B47 family).");
    }

    /// <summary>True when the dispatch invocation at <paramref name="dispatchOp"/> can re-enter the
    /// containing function (design §4.3: containing function on a synthetic-edge-inclusive SCC cycle
    /// AND the dispatch is non-tail — pre-computed syntax-keyed by BuildRecursionInfo). When true,
    /// also registers the frame: ensures the recursion stack and accumulates the named frame fields,
    /// so InsertRecursionSpills wraps the flagged dispatch arms with the spill/reload.</summary>
    internal bool MarkReentrantDispatch(IOperation dispatchOp)
    {
        var plan = CallableSitePlan.Delegate(dispatchOp?.Syntax, _state.Recursion);
        RegisterCallableSiteSpill(plan);
        return plan.Reentrant;
    }

    /// <summary>Wave-12 r2 [V1]: true when the cross dispatch at <paramref name="site"/> (a method
    /// invocation or property/indexer accessor access through a variable / interface-typed receiver)
    /// can re-enter the containing function: its local landing method is a recursion-cycle edge from
    /// the current method (BuildRecursionInfo's cross arms) and the site is not tail-spared. When
    /// true, also registers the frame (recursion stack + named spill fields) so InsertRecursionSpills
    /// wraps the flagged SendCustomEvent — with the param copy-ins inside the window
    /// (CExternCall.PreSpillStmts), because a same-program reentrant callee shares the caller's param
    /// heap vars and a copy-in preceding the save would be captured post-clobber.</summary>
    internal bool TryMarkReentrantCrossDispatch(IOperation site, IMethodSymbol staticCallee)
    {
        if (_currentMethod == null) return false;
        var callableSite = CallableSites.Require(site, staticCallee);
        var landing = RequireBoundCallSite(
            site, callableSite.Kind).RequireDispatch().Cross;
        var recursive = landing.HasLocalTarget
            && _state.Recursion.IsRecursiveEdge(_currentMethod, landing.LocalTarget);
        var plan = CallableSitePlan.Cross(staticCallee, landing, site?.Syntax, recursive,
            _state.Recursion);
        RegisterCallableSiteSpill(plan);
        return plan.Reentrant;
    }

    void RegisterCallableSiteSpill(CallableSitePlan plan)
    {
        if (!plan.RequiresFrameSpill) return;
        _state.Storage.EnsureRecursionStack();
        AccumulateRecursionSpillFields(_builder.CurrentFunction);
    }

    /// <summary>Accumulate the UNION of in-scope frame fields across every spill site: a later site has
    /// more locals in scope than an earlier one, and the post-pass uses a single field set for all sites.
    /// Over-spilling a not-yet-assigned local at an earlier site is inert (its garbage is saved/restored).</summary>
    void AccumulateRecursionSpillFields(FlatFunction cf)
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
    List<(string Name, StorageType Type)> CollectRecursionSpillFields()
    {
        var fields = new List<(string, StorageType)>();
        var seen = new HashSet<string>();
        void AddField(string id)
        {
            if (id == null || !seen.Add(id)) return;
            var t = _state.Storage.GetFieldType(id);
            if (t.HasValue) fields.Add((id, t.Value));
        }
        var pids = _state.Methods.CurrentClosureSpec
            ?.ParamVarIds.ToArray();
        if (pids == null && _currentMethod != null) _methodParamVarIds.TryGetValue(_currentMethod, out pids);
        if (_currentMethod != null && pids != null)
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
                    if (_state.TryGetEnvBinding(param, out _))
                        continue;
                }
                AddField(pids[i]);
            }
        AddField(_state.Methods.CurrentStructReceiverParamId);
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
