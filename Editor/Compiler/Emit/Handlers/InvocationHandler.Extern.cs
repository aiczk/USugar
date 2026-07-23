using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
    // ── Extern Method Call ──

    CLeaf EmitExternMethodCall(IInvocationOperation op, IMethodSymbol target)
    {
        if (TryEmitInvocationIntrinsic(op, target, out var intrinsicResult))
            return intrinsicResult;

        // Supported N-dim members were consumed by the intrinsic registry.
        // Anything left here would operate on the emulation bundle itself.
        if (op.Instance != null && NdimArrayAbi.IsNdimArray(op.Instance.Type))
        {
            NdimArrayAbi.RejectMember(target.Name);
            return null; // unreachable
        }

        // Supported aggregate-array copy operations were consumed by the
        // intrinsic registry; reject every remaining alias-producing member.
        RejectUnsafeAggregateArrayExtern(op, target);
        if (!target.IsStatic && op.Instance?.Type != null)
            _ctx.Boundary.RequireCanPassExternArgument(op.Instance.Type,
                $"receiver of {target.Name}", deferAggregateReceiverPolicy: true);
        if (!target.ReturnsVoid)
            _ctx.Boundary.RequireCanReturnFromExtern(target.ReturnType, target.Name);
        var objectIdentityExtern = target.Name == nameof(object.Equals)
            && target.ContainingType.SpecialType == SpecialType.System_Object;
        var objectMemberExtern = target.ContainingType.SpecialType
            is SpecialType.System_Object or SpecialType.System_ValueType;

        CLeaf instanceVal = null;
        if (!target.IsStatic)
        {
            if (op.Instance is IInstanceReferenceOperation)
                instanceVal = LoadField(_ctx.Storage.DeclareThisOnce(GetStorageType(target.ContainingType)), GetStorageType(target.ContainingType));
            else if (op.Instance is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef
                     && fieldRef.Field.Type.IsValueType && !fieldRef.Field.IsStatic)
            {
                // Value-type field on this: pass heap address directly so extern can modify in-place
                instanceVal = FieldAddr(_ctx.SourceStorageName(fieldRef.Field), GetStorageType(fieldRef.Field.Type));
            }
            // Local variable — value type: pass heap address directly so extern can modify in-place
            else if (op.Instance is ILocalReferenceOperation localRef
                     && localRef.Type.IsValueType
                     && _localBindings.TryGetValue(localRef.Local, out var localBind))
            {
                instanceVal = FieldAddr(localBind.Id, GetStorageType(localRef.Type));
            }
            // Parameter — value type: pass heap address directly so extern can modify in-place
            else if (op.Instance is IParameterReferenceOperation paramRef
                     && paramRef.Type.IsValueType)
            {
                var paramId = GetParamVarId(paramRef.Parameter);
                instanceVal = FieldAddr(paramId, GetStorageType(paramRef.Type));
            }
            else if (op.Instance != null)
                instanceVal = VisitExpression(op.Instance);
        }

        // Trailing `params` expansion: SOME Udon variadic externs (e.g. SendCustomNetworkEvent) take N discrete
        // SystemObject args — one extern overload per arity — instead of a single SystemObjectArray. Others
        // (e.g. string.Format) only expose the SystemObjectArray overload. So when Roslyn synthesised the
        // params array from loose call args (ArgumentKind.ParamArray), expand it into boxed elements ONLY IF
        // the per-arity expanded extern actually exists; otherwise keep the array form. (An explicitly-passed
        // array is ArgumentKind.Explicit and is always left as the array.)
        System.Collections.Generic.IReadOnlyList<IOperation> paramsElems = null;
        BoundExtern expandedParamsExtern = null;
        int lastParamIdx = target.Parameters.Length - 1;
        if (lastParamIdx >= 0 && target.Parameters[lastParamIdx].IsParams
            && op.Arguments.Length == target.Parameters.Length
            && op.Arguments[op.Arguments.Length - 1].ArgumentKind == ArgumentKind.ParamArray)
        {
            var paramArg = op.Arguments[op.Arguments.Length - 1].Value;
            while (paramArg is IConversionOperation pc) paramArg = pc.Operand;
            if (paramArg is IArrayCreationOperation pac)
            {
                var elems = pac.Initializer != null
                    ? (System.Collections.Generic.IReadOnlyList<IOperation>)pac.Initializer.ElementValues
                    : System.Array.Empty<IOperation>();
                var pts = new List<string>();
                for (int i = 0; i < lastParamIdx; i++) pts.Add(GetStorageTypeName(target.Parameters[i].Type));
                for (int k = 0; k < elems.Count; k++) pts.Add("SystemObject");
                var candidate = BindExternMethodCall(
                    target, op.Instance?.Type, pts.ToArray(), allowMissing: true);
                if (candidate != null)
                {
                    paramsElems = elems;
                    expandedParamsExtern = candidate;
                }
            }
        }

        // For out/ref params: pass the field's heap address directly via CFieldRef.
        // Udon VM extern writes to the pushed address, so the original variable
        // is updated in-place. No copy-back needed for simple field targets.
        // For complex lvalues (array elements, cross-behaviour fields), use a temp field + copy-back.
        var argVals = new List<CLeaf>();
        var outCopyBacks = new List<(int argIdx, string tempField, System.Action<CLeaf> store)>();
        for (int i = 0; i < op.Arguments.Length; i++)
        {
            // N-R1 (design 2026-07-04 §2/§4): a Rank>1 array's runtime value is an object[] bundle,
            // not a real System*Array — an extern parameter (however it's typed: object, Array, a
            // concrete SDK array, …) would silently receive the wrong shape. Checked on the ARGUMENT's
            // static type before any of the branches below (params expansion / ref-out / plain) so no
            // path can smuggle a bundle past this choke point. Unwrap conversions FIRST — passing a
            // T[,] to an `object`/`Array`-typed parameter wraps it in an implicit IConversionOperation
            // whose OWN Type is the widened target, hiding the T[,] source type from a direct check.
            if (NdimArrayAbi.IsNdimArray(UnwrapConversions(op.Arguments[i].Value).Type))
                throw new System.NotSupportedException(ExternResolver.MultidimExternArgMessage);

            var argumentType = UnwrapConversions(op.Arguments[i].Value).Type
                ?? op.Arguments[i].Parameter?.Type;
            if (argumentType != null)
                _ctx.Boundary.RequireCanPassExternArgument(argumentType,
                    op.Arguments[i].Parameter?.Name ?? $"argument {i}", objectIdentityExtern,
                    deferAggregateReceiverPolicy: objectMemberExtern);

            var param = target.Parameters[i];
            if (param.IsParams && paramsElems != null)
            {
                // Box each variadic element as a discrete SystemObject argument. Phase-A armor: the
                // N-R1 check above sees only the params ARRAY argument (static type object[]), so a
                // T[,] element smuggled through the expansion bypassed the choke — re-check per element
                // (the erasure choke in BoundaryChecker also contains the implicit element→object
                // conversion; this keeps the extern boundary sound on its own).
                foreach (var elem in paramsElems)
                {
                    if (NdimArrayAbi.IsNdimArray(UnwrapConversions(elem).Type))
                        throw new System.NotSupportedException(ExternResolver.MultidimExternArgMessage);
                    if (UnwrapConversions(elem).Type is { } elementType)
                        _ctx.Boundary.RequireCanPassExternArgument(elementType,
                            $"element of {param.Name}");
                    argVals.Add(VisitExpression(elem));
                }
                continue;
            }
            if (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref)
            {
                var fieldName = ResolveOutRefFieldName(op.Arguments[i].Value);
                if (fieldName != null)
                {
                    argVals.Add(FieldAddr(fieldName, GetStorageType(param.Type)));
                    continue;
                }
                // Complex lvalue: evaluate the receiver/index legs ONCE via the hardened
                // TryPrepareRefOutArg machinery (wave-9 [Y12]) and copy back through the SAME
                // legs — C# evaluates an argument's component expressions exactly once, at the
                // argument's syntax position, before the call. Every shape that reaches here and
                // compiles today is covered by TryPrepareRefOutArg (array element, aggregate
                // member, cross-behaviour field, captured env local/param) — anything it declines
                // is loud-rejected below instead of falling through a second, un-audited path.
                var paramType = GetStorageTypeName(param.Type);
                var tempField = _ctx.Storage.DeclareLocal("outref", new StorageType(paramType));
                var prepared = TryPrepareRefOutArg(op.Arguments[i]) ?? throw new System.NotSupportedException(
                    $"'{(param.RefKind == RefKind.Ref ? "ref" : "out")} {param.Name}' of '{target.Name}' cannot "
                    + $"bind to '{op.Arguments[i].Value.Syntax}' ({op.Arguments[i].Value.Kind}): this l-value "
                    + "shape has no ref/out extern binding (locals, parameters, behaviour fields, single-index "
                    + "array elements, and struct/tuple members are supported). Assign it to a local variable "
                    + "first, or restructure the expression.");
                if (param.RefKind == RefKind.Ref)
                    EmitStoreField(tempField, prepared.read());
                argVals.Add(FieldAddr(tempField, new StorageType(paramType)));
                outCopyBacks.Add((i, tempField, prepared.store));
                continue;
            }
            argVals.Add(VisitExpression(op.Arguments[i].Value));
        }

        // Build args list for extern call
        var externArgs = new List<CLeaf>();
        if (instanceVal != null)
            externArgs.Add(instanceVal);
        externArgs.AddRange(argVals);

        // Extern signature — the validated expanded form when trailing params were expanded, else the default.
        var sig = expandedParamsExtern ?? BindExternMethodCall(target, op.Instance?.Type);

        CLeaf result;
        if (!target.ReturnsVoid)
        {
            var returnType = GetStorageTypeName(target.ReturnType);
            // result is already a single-assignment scratch leaf (ExternCall binds it under ANF); the out/ref
            // copy-back below writes only the user's target lvalues, never this slot, so it survives unchanged.
            result = ExternCall(sig, externArgs, new StorageType(returnType));
        }
        else
        {
            EmitExternVoid(sig, externArgs);
            result = null;
        }

        // Copy-back for complex out/ref lvalues — always through the SAME legs TryPrepareRefOutArg
        // evaluated at copy-in (never a re-evaluating fallback).
        foreach (var (argIdx, tempField, store) in outCopyBacks)
        {
            var paramType = GetStorageTypeName(target.Parameters[argIdx].Type);
            var val = LoadField(tempField, new StorageType(paramType));
            store(val);
        }

        return result;
    }

    // ── CW26: rank-1 aggregate-element array Clone/CopyTo/Array.Copy (value-semantics lowering) ──

    /// <summary>Rank-1 array whose element is a value-semantic aggregate (user struct / tuple), else null.
    /// N-dim arrays never get here (the Rank>1 intercept runs first); class/scalar elements return null so
    /// their shallow externs stay untouched (shallow IS C# semantics for them).</summary>
    INamedTypeSymbol AggregateArrayElement(ITypeSymbol type)
        => type is IArrayTypeSymbol { Rank: 1 } arr
           && ResolveType(arr.ElementType) is INamedTypeSymbol elem && TypeClassifier.IsAggregateValue(elem)
            ? elem : null;

    void RejectUnsafeAggregateArrayExtern(IInvocationOperation op, IMethodSymbol target)
    {
        if (AggregateArrayElement(op.Instance?.Type) != null)
        {
            if (target.Name is "GetLength" or "GetLongLength" or "GetLowerBound" or "GetUpperBound")
                return;
            throw new System.NotSupportedException(
                $"Array member '{target.Name}' is not supported for user-struct or tuple arrays: "
                + "each element is an object[] value bundle, and the general Array extern would expose "
                + "or mutate the bundle reference instead of preserving C# value semantics. Use indexing, "
                + "or copy elements through typed code.");
        }

        if (target.ContainingType.SpecialType != SpecialType.System_Array) return;
        foreach (var argument in op.Arguments)
            if (AggregateArrayElement(UnwrapConversions(argument.Value).Type) != null)
                throw new System.NotSupportedException(
                    $"Array.{target.Name} is not supported for user-struct or tuple arrays: each element "
                    + "is an object[] value bundle and this extern has no aggregate value-semantics adapter. "
                    + "Use typed indexing or Array.Copy, which has dedicated lowering.");
    }

    bool TryEmitAggregateArrayCopyMember(IInvocationOperation op, IMethodSymbol target, out CLeaf result)
    {
        result = null;
        if (!target.IsStatic && op.Instance != null
            && AggregateArrayElement(op.Instance.Type) is INamedTypeSymbol recvElem)
        {
            var recvArr = (IArrayTypeSymbol)op.Instance.Type;
            var arrType = GetArrayType(recvArr);
            var elemType = GetArrayElemType(recvArr);
            if (target.Name == "Clone" && target.Parameters.Length == 0)
            {
                var srcVal = VisitExpression(op.Instance);
                var lenVal = ExternCall(UdonAbi.ArrayLength(arrType),
                    new List<CLeaf> { srcVal }, StorageTypes.Int32);
                var dstVal = ExternCall(UdonAbi.ArrayConstructor(arrType), new List<CLeaf> { lenVal }, new StorageType(arrType));
                EmitAggregateElementCopy(srcVal, null, dstVal, null, lenVal, recvElem, arrType, elemType,
                    bufferAgainstOverlap: false); // dst is fresh — cannot overlap the source
                result = dstVal;
                return true;
            }
            if (target.Name == "CopyTo")
            {
                if (target.Parameters.Length != 2 || target.Parameters[1].Type.SpecialType != SpecialType.System_Int32)
                    throw new System.NotSupportedException(
                        $"'{target.Name}' with a non-Int32 index on a struct/tuple-element array is not supported; "
                        + "use the System.Int32 overload.");
                var srcVal = VisitExpression(op.Instance);
                var argVals = EvaluateArgsByOrdinal(op);
                var lenVal = ExternCall(UdonAbi.ArrayLength(arrType),
                    new List<CLeaf> { srcVal }, StorageTypes.Int32);
                EmitAggregateElementCopy(srcVal, null, argVals[0], argVals[1], lenVal, recvElem, arrType, elemType,
                    bufferAgainstOverlap: true);
                return true;
            }
            return false; // other Array members keep their existing behavior
        }

        if (target.IsStatic && target.ContainingType?.SpecialType == SpecialType.System_Array
            && (target.Name == "Copy" || target.Name == "ConstrainedCopy"))
        {
            bool ranged = target.Parameters.Length == 5;
            var srcOp = UnwrapConversions(ArgByOrdinal(op, 0));
            var dstOp = UnwrapConversions(ArgByOrdinal(op, ranged ? 2 : 1));
            var srcElem = AggregateArrayElement(srcOp.Type);
            var dstElem = AggregateArrayElement(dstOp.Type);
            if (srcElem == null && dstElem == null)
                return false; // scalar/class-element arrays: the shallow extern is C#-consistent
            if (srcElem == null || dstElem == null || !SymbolEqualityComparer.Default.Equals(srcElem, dstElem))
                throw new System.NotSupportedException(
                    $"'Array.{target.Name}' between a struct/tuple-element array and a differently-typed array "
                    + "is not supported: the per-element value copy cannot be typed. Copy the elements in an "
                    + "explicit loop instead.");
            if (target.Parameters[target.Parameters.Length - 1].Type.SpecialType != SpecialType.System_Int32)
                throw new System.NotSupportedException(
                    $"the System.Int64 overload of 'Array.{target.Name}' on a struct/tuple-element array is not "
                    + "supported; use the System.Int32 overload.");
            var srcArrSym = (IArrayTypeSymbol)srcOp.Type;
            var arrType = GetArrayType(srcArrSym);
            var elemType = GetArrayElemType(srcArrSym);
            var argVals = EvaluateArgsByOrdinal(op);
            if (ranged)
                EmitAggregateElementCopy(argVals[0], argVals[1], argVals[2], argVals[3], argVals[4],
                    srcElem, arrType, elemType, bufferAgainstOverlap: true);
            else
                EmitAggregateElementCopy(argVals[0], null, argVals[1], null, argVals[2],
                    srcElem, arrType, elemType, bufferAgainstOverlap: true);
            return true;
        }
        return false;
    }

    IOperation ArgByOrdinal(IInvocationOperation op, int ordinal)
    {
        foreach (var a in op.Arguments)
            if (a.Parameter != null && a.Parameter.Ordinal == ordinal)
                return a.Value;
        throw new System.InvalidOperationException(
            $"missing argument for parameter ordinal {ordinal} of '{op.TargetMethod.Name}'");
    }

    /// <summary>Evaluate every argument in syntax (evaluation) order, binding the leaves by parameter
    /// ordinal — the w4 discipline (IInvocationOperation.Arguments can be syntax-ordered for named args).</summary>
    CLeaf[] EvaluateArgsByOrdinal(IInvocationOperation op)
    {
        var vals = new CLeaf[op.Arguments.Length];
        foreach (var a in op.Arguments)
            vals[a.Parameter.Ordinal] = VisitExpression(a.Value);
        return vals;
    }

    /// <summary>dst[dstStart+i] = DeepClone(src[srcStart+i]) for i in [0, len); a null start means 0.
    /// bufferAgainstOverlap stages the clones in a fresh temp array first (every source read completes
    /// before any destination write), so a same-array overlapping Copy/CopyTo behaves like C#'s buffered
    /// Array.Copy — the temp's elements are fresh bundles nothing else references, so the second leg's
    /// reference copy is safe.</summary>
    void EmitAggregateElementCopy(CLeaf srcVal, CLeaf srcStartVal, CLeaf dstVal, CLeaf dstStartVal, CLeaf lenVal,
        INamedTypeSymbol elemAgg, string arrType, string elemType, bool bufferAgainstOverlap)
    {
        var getSig = UdonAbi.ArrayGet(arrType, elemType);
        var setSig = UdonAbi.ArraySet(arrType, elemType);
        if (bufferAgainstOverlap)
        {
            var tempVal = ExternCall(UdonAbi.ArrayConstructor(arrType), new List<CLeaf> { lenVal }, new StorageType(arrType));
            EmitIndexedLoop(lenVal, iVal =>
            {
                var elemVal = ExternCall(getSig, new List<CLeaf> { srcVal, OffsetIndex(srcStartVal, iVal) }, new StorageType(AggregateAbi.ArrayType));
                EmitExternVoid(setSig, new List<CLeaf>
                    { tempVal, iVal, AggregateAbi.DeepClone(_builder, elemVal, elemAgg, _ctx.Aggregates.GetLayout) });
            });
            EmitIndexedLoop(lenVal, iVal =>
            {
                var elemVal = ExternCall(getSig, new List<CLeaf> { tempVal, iVal }, new StorageType(AggregateAbi.ArrayType));
                EmitExternVoid(setSig, new List<CLeaf> { dstVal, OffsetIndex(dstStartVal, iVal), elemVal });
            });
        }
        else
        {
            EmitIndexedLoop(lenVal, iVal =>
            {
                var elemVal = ExternCall(getSig, new List<CLeaf> { srcVal, OffsetIndex(srcStartVal, iVal) }, new StorageType(AggregateAbi.ArrayType));
                EmitExternVoid(setSig, new List<CLeaf>
                    { dstVal, OffsetIndex(dstStartVal, iVal), AggregateAbi.DeepClone(_builder, elemVal, elemAgg, _ctx.Aggregates.GetLayout) });
            });
        }
    }

    void EmitIndexedLoop(CLeaf lenVal, System.Action<CLeaf> body)
    {
        var iSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        _builder.EmitFor(
            b => { EmitAssign(iSlot, Const(0, StorageTypes.Int32)); },
            // cond MUST be the Func overload so it re-evaluates each iteration (the CLeaf overload
            // evaluates once, silently skipping the loop).
            () => ExternCall(UdonAbi.Int32LessThan,
                new List<CLeaf> { SlotRef(iSlot), lenVal }, StorageTypes.Boolean),
            b =>
            {
                EmitAssign(iSlot, ExternCall(UdonAbi.Int32Add,
                    new List<CLeaf> { SlotRef(iSlot), Const(1, StorageTypes.Int32) }, StorageTypes.Int32));
            },
            b => body(SlotRef(iSlot)));
    }

    CLeaf OffsetIndex(CLeaf startVal, CLeaf iVal)
        => startVal == null
            ? iVal
            : ExternCall(UdonAbi.Int32Add,
                new List<CLeaf> { startVal, iVal }, StorageTypes.Int32);

    // ── GetComponent<T> ──

    CLeaf EmitGetComponentGeneric(IInvocationOperation op, IMethodSymbol target)
    {
        var typeArg = target.TypeArguments[0];
        return ExternResolver.IsUdonSharpBehaviour(typeArg) ? EmitGetComponentShim(op, target) : EmitGetComponentExtern(op, target);
    }

    // Existing logic for Unity Component types (Transform, Collider, etc.)
    // Uses the __T / __TArray generic extern form (matches UdonSharp behavior).
    CLeaf EmitGetComponentExtern(IInvocationOperation op, IMethodSymbol target)
    {
        // Evaluate instance and arguments first
        CLeaf instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = LoadField(_ctx.Storage.DeclareThisOnce(StorageTypes.Transform), StorageTypes.Transform);
        else if (op.Instance != null)
            instanceVal = VisitExpression(op.Instance);

        // Evaluate explicit arguments (e.g., GetComponentInChildren<T>(bool includeInactive))
        var argVals = new List<CLeaf>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(VisitExpression(op.Arguments[i].Value));

        // __T externs use UnityEngineComponent as containing type.
        // If instance is a GameObject (not a Component), get .transform first.
        instanceVal = EnsureComponentInstance(op.Instance, instanceVal);

        // Build extern args: instance + explicit args + typeof(T)
        var externArgs = new List<CLeaf>();
        if (instanceVal != null)
            externArgs.Add(instanceVal);

        // Push explicit arguments FIRST, then SystemType (matches UdonSharp push order for __T externs)
        externArgs.AddRange(argVals);

        // typeof(T) as SystemType constant (after explicit args) — shared type-token choke point.
        externArgs.Add(ConstTypeToken(target.TypeArguments[0]));

        // Result type — typed as T for __T externs
        var isPlural = target.Name.StartsWith("GetComponents");
        var typeArgUdon = GetStorageTypeName(target.TypeArguments[0]);
        string tempType;
        if (isPlural && typeArgUdon == "VRCUdonCommonInterfacesIUdonEventReceiver")
            tempType = "UnityEngineComponentArray";
        else
            tempType = isPlural ? $"{typeArgUdon}Array" : typeArgUdon;

        // Build extern name with __T form
        const string containingType = "UnityEngineComponent";
        var methodName = target.Name;
        var resultPlaceholder = isPlural ? "TArray" : "T";
        var explicitParams = target.OriginalDefinition.Parameters;
        var parameterTypes = explicitParams
            .Select(parameter => GetStorageTypeName(parameter.Type))
            .ToArray();
        var externSig = UdonAbiKey.Method(
            containingType, methodName, parameterTypes, resultPlaceholder);

        return ExternCall(externSig, externArgs, new StorageType(tempType));
    }

    // ── GetComponent<T> USB Shim ──
    // Inline shim for USB-derived types: GetComponents(typeof(UdonBehaviour)) + __refl_typeid filter

    CLeaf EmitGetComponentShim(IInvocationOperation op, IMethodSymbol target)
    {
        var isSingular = !target.Name.StartsWith("GetComponents");

        // Evaluate instance
        CLeaf instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = LoadField(_ctx.Storage.DeclareThisOnce(StorageTypes.Transform), StorageTypes.Transform);
        else if (op.Instance != null)
            instanceVal = VisitExpression(op.Instance);

        // Evaluate explicit arguments (bool includeInactive)
        var argVals = new List<CLeaf>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(VisitExpression(op.Arguments[i].Value));

        // If instance is a GameObject, get .transform for Component-typed extern
        instanceVal = EnsureComponentInstance(op.Instance, instanceVal);

        // Determine which non-generic GetComponents extern to call
        var fetchExtern = ResolveShimFetchExtern(target.Name, op.Arguments.Length > 0);

        // Build args: instance + typeof(UdonBehaviour) + optional args
        var fetchArgs = new List<CLeaf>();
        if (instanceVal != null)
            fetchArgs.Add(instanceVal);
        var udonBehaviourType = Const("VRCUdonUdonBehaviour", StorageTypes.Type);
        fetchArgs.Add(udonBehaviourType);
        fetchArgs.AddRange(argVals);

        // Call GetComponents → ComponentArray (store to slot so it's evaluated once)
        var allComponentsSlot = _ctx.Builder.AllocScratch(StorageTypes.ComponentArray);
        EmitAssign(allComponentsSlot, ExternCall(fetchExtern, fetchArgs, StorageTypes.ComponentArray));
        var allComponents = SlotRef(allComponentsSlot);

        // Compute target type ID at compile time
        var targetTypeName = target.TypeArguments[0].ToDisplayString();
        long targetTypeId = UasmEmitter.ComputeTypeId(targetTypeName);
        var targetIdConst = Const(targetTypeId, StorageTypes.Int64);

        // Inheritance: if derived USB types exist, use __refl_typeids + Array.IndexOf
        bool useTypeIds = HasInheritedUsbTypes(target.TypeArguments[0]);
        var reflKeyConst = useTypeIds
            ? Const(EmitContext.ReflTypeIdsField, StorageTypes.String)
            : Const(EmitContext.ReflTypeIdField, StorageTypes.String);

        return isSingular
            ? EmitShimSingular(allComponents, targetIdConst, reflKeyConst, useTypeIds)
            : EmitShimPlural(allComponents, targetIdConst, reflKeyConst, useTypeIds);
    }

    /// <summary>
    /// If the instance is a GameObject, emit .transform to get a Component-typed instance.
    /// GetComponent __T externs and shim GetComponents externs use UnityEngineComponent
    /// as containing type, which requires the instance to be Component-typed in the heap.
    /// </summary>
    CLeaf EnsureComponentInstance(IOperation instanceOp, CLeaf instanceVal)
    {
        if (instanceVal == null || instanceOp == null)
            return instanceVal;
        var instanceUdon = GetStorageTypeName(instanceOp.Type);
        if (instanceUdon != "UnityEngineGameObject")
            return instanceVal;
        return ExternCall(
            UdonAbiKey.Method("UnityEngineGameObject", "get_transform", "UnityEngineTransform"),
            new List<CLeaf> { instanceVal },
            StorageTypes.Transform);
    }

    bool HasInheritedUsbTypes(ITypeSymbol targetType)
    {
        foreach (var kvp in _planner.AllLayouts)
        {
            var typeSymbol = kvp.Key;
            if (SymbolEqualityComparer.Default.Equals(typeSymbol, targetType))
                continue;
            var current = typeSymbol.BaseType;
            while (current != null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, targetType))
                    return true;
                current = current.BaseType;
            }
        }
        return false;
    }

    static UdonAbiKey ResolveShimFetchExtern(string methodName, bool hasBoolArg)
    {
        // Map singular→plural, all use non-generic SystemType overload
        var baseName = methodName;
        if (baseName == "GetComponent")
            baseName = "GetComponents";
        else if (baseName == "GetComponentInChildren")
            baseName = "GetComponentsInChildren";
        else if (baseName == "GetComponentInParent")
            baseName = "GetComponentsInParent";

        if (hasBoolArg)
            return UdonAbiKey.Method("UnityEngineComponent", baseName,
                new[] { "SystemType", "SystemBoolean" }, "UnityEngineComponentArray");
        return UdonAbiKey.Method("UnityEngineComponent", baseName,
            new[] { "SystemType" }, "UnityEngineComponentArray");
    }

    CLeaf EmitShimSingular(CLeaf allComponents, CLeaf targetIdConst, CLeaf reflKeyConst, bool useTypeIds)
    {
        // Get array length (store to slot so it's not re-evaluated each iteration)
        var lenSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(lenSlot, ExternCall(
            UdonAbiKey.Method("UnityEngineComponentArray", "get_Length", "SystemInt32"),
            new List<CLeaf> { allComponents }, StorageTypes.Int32));

        // Loop index (mutable across control flow)
        var idxSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(idxSlot, Const(0, StorageTypes.Int32));

        // Result slot (null initially — returns null if no match found)
        var resultSlot = _ctx.Builder.AllocScratch(StorageTypes.UdonEventReceiver);

        // while (idx < len) — Func overload so the counter-dependent condition re-evaluates each iteration.
        // The CLeaf overload evaluates it ONCE (idx still 0), so the loop never advances / never runs.
        _builder.EmitWhile(
            () => ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { SlotRef(idxSlot), SlotRef(lenSlot) },
                StorageTypes.Boolean),
            b =>
            {
                // element = allComponents[idx]
                var elementVal = ExternCall(
                    UdonAbi.ArrayGet("UnityEngineComponentArray", "UnityEngineComponent"),
                    new List<CLeaf> { allComponents, SlotRef(idxSlot) },
                    StorageTypes.Component);

                // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
                var idValueVal = LoadProgramVariable(
                    elementVal, reflKeyConst, StorageTypes.Object);

                // Null check: if (idValue != null)
                var nullConst = Const(null, StorageTypes.Object);
                var notNullVal = ExternCall(
                    UdonAbi.ObjectInequality,
                    new List<CLeaf> { idValueVal, nullConst },
                    StorageTypes.Boolean);

                _builder.EmitIf(notNullVal, thenB =>
                {
                    // Type check
                    var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

                    _builder.EmitIf(matchVal, matchB =>
                    {
                        // The type-id check proves this Component is the requested Udon behaviour.
                        // Preserve that proof as an explicit representation conversion instead of
                        // relying on the global reference-type assignment relaxation.
                        var behaviour = RepresentationCast(
                            elementVal,
                            StorageTypes.UdonEventReceiver,
                            RepresentationCastKind.VerifiedUdonBehaviourComponent);
                        EmitAssign(resultSlot, behaviour);
                        _builder.EmitBreak();
                    });
                });

                // idx++
                var oneConst = Const(1, StorageTypes.Int32);
                var nextIdxVal = ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { SlotRef(idxSlot), oneConst },
                    StorageTypes.Int32);
                EmitAssign(idxSlot, nextIdxVal);
            });

        return SlotRef(resultSlot);
    }

    CLeaf EmitShimPlural(CLeaf allComponents, CLeaf targetIdConst, CLeaf reflKeyConst, bool useTypeIds)
    {
        // Get array length (store to slot so it's not re-evaluated each iteration)
        var lenSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(lenSlot, ExternCall(
            UdonAbiKey.Method("UnityEngineComponentArray", "get_Length", "SystemInt32"),
            new List<CLeaf> { allComponents }, StorageTypes.Int32));

        var zeroConst = Const(0, StorageTypes.Int32);
        var oneConst = Const(1, StorageTypes.Int32);

        // === Pass 1: Count matches ===
        var countSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(countSlot, zeroConst);
        var idx1Slot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(idx1Slot, zeroConst);

        // while (idx1 < len) — Func overload (re-evaluate each iteration); CLeaf would evaluate idx1<len once.
        _builder.EmitWhile(
            () => ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { SlotRef(idx1Slot), SlotRef(lenSlot) },
                StorageTypes.Boolean),
            b =>
            {
                EmitShimTypeCheckBody(allComponents, idx1Slot, reflKeyConst, targetIdConst, useTypeIds,
                    matchAction: () =>
                    {
                        // count++
                        var newCountVal = ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf> { SlotRef(countSlot), oneConst },
                            StorageTypes.Int32);
                        EmitAssign(countSlot, newCountVal);
                    });

                // idx1++
                var nextIdx1Val = ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { SlotRef(idx1Slot), oneConst },
                    StorageTypes.Int32);
                EmitAssign(idx1Slot, nextIdx1Val);
            });

        // === Allocate result array ===
        var resultArr = ExternCall(
            UdonAbi.ArrayConstructor("UnityEngineComponentArray"),
            new List<CLeaf> { SlotRef(countSlot) },
            StorageTypes.ComponentArray);

        // === Pass 2: Fill result array ===
        var idx2Slot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(idx2Slot, zeroConst);
        var writeIdxSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
        EmitAssign(writeIdxSlot, zeroConst);

        // while (idx2 < len) — Func overload (re-evaluate each iteration); CLeaf would evaluate idx2<len once.
        _builder.EmitWhile(
            () => ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { SlotRef(idx2Slot), SlotRef(lenSlot) },
                StorageTypes.Boolean),
            b =>
            {
                // element = allComponents[idx2]
                var elementVal = ExternCall(
                    UdonAbi.ArrayGet("UnityEngineComponentArray", "UnityEngineComponent"),
                    new List<CLeaf> { allComponents, SlotRef(idx2Slot) },
                    StorageTypes.Component);

                // Type check
                var idValueVal = LoadProgramVariable(
                    elementVal, reflKeyConst, StorageTypes.Object);

                var nullConst = Const(null, StorageTypes.Object);
                var notNullVal = ExternCall(
                    UdonAbi.ObjectInequality,
                    new List<CLeaf> { idValueVal, nullConst },
                    StorageTypes.Boolean);

                _builder.EmitIf(notNullVal, thenB =>
                {
                    var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

                    _builder.EmitIf(matchVal, matchB =>
                    {
                        // result[writeIdx] = element
                        EmitExternVoid(UdonAbi.ArraySet("UnityEngineComponentArray", "UnityEngineComponent"),
                            new List<CLeaf> { resultArr, SlotRef(writeIdxSlot), elementVal });

                        // writeIdx++
                        var newWriteVal = ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf> { SlotRef(writeIdxSlot), oneConst },
                            StorageTypes.Int32);
                        EmitAssign(writeIdxSlot, newWriteVal);
                    });
                });

                // idx2++
                var nextIdx2Val = ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { SlotRef(idx2Slot), oneConst },
                    StorageTypes.Int32);
                EmitAssign(idx2Slot, nextIdx2Val);
            });

        return resultArr;
    }

    /// <summary>
    /// Returns an CValue that evaluates to true if the idValue matches the targetId.
    /// Handles both single-id and array-of-ids cases.
    /// </summary>
    CLeaf EmitShimTypeMatchExpr(CLeaf idValueVal, CLeaf targetIdConst, bool useTypeIds)
    {
        if (useTypeIds)
        {
            // Array.IndexOf(__refl_typeids, targetId) != -1
            var indexResult = ExternCall(
                UdonAbiKey.Method("SystemArray", "IndexOf", new[] { "SystemArray", "SystemObject" }, "SystemInt32"),
                new List<CLeaf> { idValueVal, targetIdConst },
                StorageTypes.Int32);

            var negOneConst = Const(-1, StorageTypes.Int32);
            return ExternCall(
                UdonAbiKey.Method("SystemInt32", "op_Inequality", new[] { "SystemInt32", "SystemInt32" }, "SystemBoolean"),
                new List<CLeaf> { indexResult, negOneConst },
                StorageTypes.Boolean);
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdVal = ExternCall(
                UdonAbiKey.Method("SystemConvert", "ToInt64", new[] { "SystemObject" }, "SystemInt64"),
                new List<CLeaf> { idValueVal },
                StorageTypes.Int64);

            // typeId == targetId
            return ExternCall(
                UdonAbiKey.Method("SystemInt64", "op_Equality", new[] { "SystemInt64", "SystemInt64" }, "SystemBoolean"),
                new List<CLeaf> { typeIdVal, targetIdConst },
                StorageTypes.Boolean);
        }
    }

    /// <summary>
    /// Emit the type-check body for shim loops (pass 1 count).
    /// Calls matchAction if the element's type ID matches.
    /// </summary>
    void EmitShimTypeCheckBody(CLeaf allComponents, int idxSlot, CLeaf reflKeyConst,
        CLeaf targetIdConst, bool useTypeIds, System.Action matchAction)
    {
        // element = allComponents[idx]
        var elementVal = ExternCall(
            UdonAbi.ArrayGet("UnityEngineComponentArray", "UnityEngineComponent"),
            new List<CLeaf> { allComponents, SlotRef(idxSlot) },
            StorageTypes.Component);

        // idValue = behaviour.GetProgramVariable(reflKey)
        var idValueVal = LoadProgramVariable(
            elementVal, reflKeyConst, StorageTypes.Object);

        // Null check
        var nullConst = Const(null, StorageTypes.Object);
        var notNullVal = ExternCall(
            UdonAbi.ObjectInequality,
            new List<CLeaf> { idValueVal, nullConst },
            StorageTypes.Boolean);

        _builder.EmitIf(notNullVal, thenB =>
        {
            var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

            _builder.EmitIf(matchVal, matchB =>
            {
                matchAction();
            });
        });
    }

    // ── Interface Call ──

    CLeaf EmitInterfaceCall(IInvocationOperation op, IMethodSymbol target)
    {
        // Use LayoutPlanner to get the interface's canonical naming
        var ifaceType = target.ContainingType as INamedTypeSymbol;
        MethodLayout ifaceMl = null;
        if (ifaceType != null)
        {
            var ifaceLayout = _planner.GetLayout(ifaceType);
            ifaceLayout.Methods.TryGetValue(target, out ifaceMl);
        }
        if (ifaceMl == null)
            throw new System.InvalidOperationException(
                $"Cannot resolve interface method layout for '{target.ContainingType?.Name ?? "(unknown)"}.{target.Name}'.");

        GuardInterfaceDispatchRepresentation(ifaceType, target.Name);

        var instanceVal = VisitExpression(op.Instance);

        // Build param pairs for CCrossCall
        // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
        // Wave-9 round-3 [W4]: evaluate in textual order (C# semantics) but slot each value at its
        // PARAMETER ordinal — op.Arguments is call-site-ordered for named/reordered args, so indexing
        // ParamIds by textual position bound names positionally (VM-proven ref=54 vs 45). The pairs
        // (= SetProgramVariable stores of already-evaluated leaves) are emitted in ordinal order so a
        // named call is byte-identical to its declaration-order twin (positional calls unchanged:
        // textual order IS ordinal order).
        var paramPairs = CrossCallArguments(op.Arguments, target, ifaceMl.ParamIds);

        // Wave-12 r2 [V1]: an interface dispatch whose local implementation is a recursion-cycle
        // edge from the current method re-enters this program when the receiver is `this` — flag
        // the SendCustomEvent as a spill site (R225 form: live locals after the call were clobbered).
        bool ifaceReentrant = TryMarkReentrantCrossDispatch(op, target);

        // Build returns
        var ifaceReturns = ifaceMl.Returns.ToArray();
        // Tuple-returning interface methods dispatch directly (no bridge) under the interface's bare name.
        if (ifaceReturns.Length > 1)
            return CrossCall(instanceVal, ifaceMl.ExportName, paramPairs, ifaceReturns, StorageTypes.Void, ifaceReentrant);

        // Non-tuple: dispatch the canonical interface-qualified bridge name (matches the emitted bridge export
        // and stays collision-free across overloads / multiple interfaces / explicit impls).
        var dispatchName = LayoutPlanner.InterfaceDispatchName(target, ifaceMl);
        var returnType = target.ReturnsVoid ? "SystemVoid" : GetStorageTypeName(target.ReturnType);
        return CrossCall(instanceVal, dispatchName, paramPairs,
            target.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : ifaceReturns, new StorageType(returnType), ifaceReentrant);
    }

    // ── Cross-Class Call ──

    CLeaf EmitCrossClassCall(IInvocationOperation op, IMethodSymbol target)
    {
        var (exportName, paramIds, _) = GetCalleeLayout(target);
        var instanceVal = VisitExpression(op.Instance);

        // Build param pairs for CCrossCall — by parameter ordinal, textual evaluation order
        // (wave-9 round-3 [W4]: named/reordered args used to bind positionally on this path).
        var paramPairs = CrossCallArguments(op.Arguments, target, paramIds);

        // Wave-12 r2 [V1]: a same-family variable-receiver dispatch on a recursion-cycle edge
        // re-enters this program when the receiver is `this` — flag the SendCustomEvent as a spill
        // site, with the param copy-ins inside the wrap (a self-recursive callee shares the
        // caller's param heap vars; VM-proven ref=36 vs 0 on the minimized field-receiver form).
        bool crossReentrant = TryMarkReentrantCrossDispatch(op, target);

        // Build returns
        var callReturns = GetCalleeReturns(target);
        if (callReturns.Length > 1)
            return CrossCall(instanceVal, exportName, paramPairs, callReturns, StorageTypes.Void, crossReentrant);

        var returnType = target.ReturnsVoid ? "SystemVoid" : GetStorageTypeName(target.ReturnType);
        return CrossCall(instanceVal, exportName, paramPairs,
            target.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : callReturns, new StorageType(returnType), crossReentrant);
    }

    // ── User Method Call ──

    /// <summary>Round-7 [Q2]/[Q5] + round-8 [R4] ref/out ARGUMENT guards, shared by the user-method,
    /// struct-instance ([R5]) and foreign-static ([R6]) call paths. Runs BEFORE argument evaluation.</summary>
    void GuardRefOutArguments(IInvocationOperation op, IMethodSymbol target)
        => GuardRefOutArguments(op.Arguments, target);

    // CW3/CW4: argument-list form — ctor sites (IObjectCreationOperation, `: base/this(...)` chain
    // initializers) and the virtual chain share the same guards as ordinary calls.
    void GuardRefOutArguments(IReadOnlyList<IArgumentOperation> arguments, IMethodSymbol target)
    {
        // Round-7 follow-up [Q2]: ref/out params are deliberately EXCLUDED from the recursion spill
        // set (a recursive call THREADING ITS OWN ref/out param must keep its mutations across the
        // call — wave-3 #3, RecRefRegressionTests / struct_ref_param tier). That exclusion is only
        // sound for self-threading: a recursion-cycle call passing a DIFFERENT lvalue overwrites the
        // one shared param heap var at copy-in and nothing restores the outer frame's value
        // (VM-proven: re-chained scalar/struct ref recursion 200 vs CLR 101). Loud per design §8-3.
        // Wave-9 round-8 [Y3]: gate on the UNFILTERED cycle-edge map, not IsRecursiveEdge — the
        // spill map keeps only non-tail edges, so a re-chained ref in pure RETURN position
        // (`return M(m-1, ref w);` — a tail call) bypassed the reject and silently corrupted the
        // outer frame's copy-back (VM-proven 21021 vs CLR 9021). Self-threading stays legal and
        // tail (no new spills).
        bool recursiveEdge = _ctx.RecursionContext.IsCycleEdge(_currentMethod, target);
        List<ISymbol> refRoots = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            var p = arguments[i].Parameter ?? target.Parameters[i];
            if (p.RefKind != RefKind.Ref && p.RefKind != RefKind.Out) continue;
            var a = UnwrapConversions(arguments[i].Value);
            if (recursiveEdge)
            {
                // Round-9: a cycle edge between DIFFERENT methods (override <-> base copy)
                // threading the CALLER's own ref/out parameter at the same position is the same
                // linear copy-in/copy-back identity chain as self-threading (W9P17 r7 precedent)
                // — the callee's cell is written at copy-in and the caller's cell restored by the
                // copy-back, so mutations thread through exactly like the self case; the symbols
                // differ only because override and base declaration are distinct. Self-recursion
                // keeps the strict same-symbol rule (a swapped or re-chained param still corrupts
                // the one shared cell).
                bool selfThreaded = a is IParameterReferenceOperation apr
                    && (SymbolEqualityComparer.Default.Equals(
                            apr.Parameter.OriginalDefinition, p.OriginalDefinition)
                        || (_currentMethod != null
                            && !SymbolEqualityComparer.Default.Equals(
                                _currentMethod.OriginalDefinition, target.OriginalDefinition)
                            && apr.Parameter.ContainingSymbol is IMethodSymbol argOwner
                            && SymbolEqualityComparer.Default.Equals(
                                argOwner.OriginalDefinition, _currentMethod.OriginalDefinition)
                            && apr.Parameter.Ordinal == p.Ordinal
                            && apr.Parameter.RefKind == p.RefKind));
                if (!selfThreaded)
                    throw new System.NotSupportedException(
                        $"recursive call to '{target.Name}' passes '{p.RefKind.ToString().ToLowerInvariant()} "
                        + $"{p.Name}' an lvalue other than the same parameter. Recursive frames share one "
                        + "heap var per parameter and ref/out params are not spilled (their mutations must "
                        + "thread through), so re-chaining a different variable corrupts the outer frame. "
                        + "Thread the method's own ref/out parameter, or pass by value.");
            }
            // Round-7 follow-up [Q5]: a ref/out argument rooted at a this-FIELD that the callee
            // (transitively) also touches directly is an alias the copy-in/copy-back convention
            // cannot honor — the callee's param reads see a stale snapshot and the copy-back
            // reverts the callee's direct field writes (VM-proven 19 vs CLR 59 / 1 vs 5). Loud per
            // §8-3. Callees that never touch the field keep the pinned convention (Inc/Swap).
            var aliasedField = TryGetThisRootedRefStorage(a);
            if (aliasedField != null && _ctx.RecursionContext.CalleeTouchesThisField(target, aliasedField))
                throw new System.NotSupportedException(
                    $"'{p.RefKind.ToString().ToLowerInvariant()} {p.Name}' of '{target.Name}' is passed "
                    + $"this-field '{aliasedField.Name}', which the callee (or a method it calls) also "
                    + "touches directly. The caller-side copy-in/copy-back convention snapshots the "
                    + "field, so the callee's reads through the parameter go stale and its direct field "
                    + "writes are reverted by the copy-back. Pass a local copy, or let the callee use "
                    + "the field directly.");
            // Round-8 [R4]: two ref/out arguments of ONE call resolving to the same storage root are
            // two independent heap vars under copy-in/copy-back — the callee never observes the
            // alias and the last copy-back silently wins (DiffFuzz: M(ref a, ref a) ref=5 vs VM 4,
            // local and this-field flavors). Loud per §8-3; distinct-storage Swap stays legal.
            var root = TryGetRefStorageRoot(a);
            if (root != null)
            {
                refRoots ??= new List<ISymbol>();
                if (refRoots.Any(r => SymbolEqualityComparer.Default.Equals(r, root)))
                    throw new System.NotSupportedException(
                        $"two ref/out arguments of '{target.Name}' resolve to the same storage "
                        + $"('{root.Name}'). Each ref/out parameter is an independent heap var under "
                        + "the copy-in/copy-back convention, so the callee never observes the alias "
                        + "and the last copy-back silently overwrites the other's result. Pass "
                        + "distinct variables, or pass by value.");
                refRoots.Add(root);
            }
        }
    }

    /// <summary>Round-8 [R5]/[R6]: by-ordinal ref/out copy-back, shared by the user-method,
    /// struct-instance and foreign-static call paths (the latter two used to drop it entirely —
    /// DiffFuzz: struct ref ref=136 vs VM 106 / out ref=10 vs 0; foreign static plain ref=6 vs 1,
    /// generic ref=9 vs 1). <paramref name="ordinalOffset"/> maps reduced-extension argument
    /// ordinals onto the original definition's params (the receiver occupies ordinal 0).</summary>
    void EmitRefOutCopyBack(IInvocationOperation op, IMethodSymbol target, int ordinalOffset = 0,
        Dictionary<int, System.Action<CLeaf>> preparedStores = null)
        => EmitRefOutCopyBack(op.Arguments, target, ordinalOffset, preparedStores);

    // CW3/CW4: argument-list form — shared with the ctor arms and the virtual chain's per-arm copy-back.
    void EmitRefOutCopyBack(IReadOnlyList<IArgumentOperation> arguments, IMethodSymbol target,
        int ordinalOffset = 0, Dictionary<int, System.Action<CLeaf>> preparedStores = null)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var param = arguments[i].Parameter ?? target.Parameters[i];
            if (param.RefKind != RefKind.Out && param.RefKind != RefKind.Ref) continue;
            // Index the param field by the argument's parameter ordinal, not its call-site position
            // (named/reordered args), matching the by-ordinal copy-in.
            // SS2B: a hoisted closure target resolves through the per-spec registry (same arm as
            // GetCalleeLayout/EmitCallToMethod — the definition-keyed map no longer holds closures).
            string[] paramIds;
            if (target.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
                && _ctx.Methods.TryGetClosureSpec(target, _ctx.ComposeClosureKeyArgs(target), out var refClosure))
                paramIds = refClosure.ParamVarIds;
            else
                paramIds = _methodParamVarIds[target]; // loud (KeyNotFound) if unregistered
            var argTarget = arguments[i].Value;
            var paramId = paramIds[param.Ordinal + ordinalOffset];
            var paramType = _ctx.Storage.GetFieldType(paramId);
            var paramVal = LoadField(paramId, paramType.Value);
            // Wave-9 round-8 [Y12]: a copy-back whose lvalue legs were evaluated at copy-in
            // (TryPrepareRefOutArg) stores through those SAME legs — AssignToTarget would
            // re-evaluate them AFTER the call (side-effecting legs ran twice and the write landed
            // in the cell chosen by the second evaluation).
            if (preparedStores != null && preparedStores.TryGetValue(i, out var preparedStore))
                preparedStore(paramVal);
            else
                AssignToTarget(argTarget, paramVal);
        }
    }

    /// <summary>Wave-9 round-8 [Y12]: evaluate a ref/out argument lvalue's receiver/index legs ONCE
    /// and return (the value read through those legs, the deferred copy-back store over the SAME
    /// legs). C# evaluates an argument's component expressions exactly once; re-evaluating them at
    /// copy-back (the retired direct AssignToTarget path) ran side-effecting legs twice and landed the
    /// write in the cell chosen by the SECOND evaluation (VM-proven: AddTo(ref arr[Idx()].v) with a
    /// k-mutating Idx — kk ref=1 vs 2, c0/c1 swapped cells; out and plain-int[]-element flavors
    /// identical). This is the ONLY ref/out argument-preparation path for EmitExternMethodCall — a
    /// simple direct-address target (plain local/param/this-field) never reaches here at all
    /// (ResolveOutRefFieldName takes it first); every complex shape that reaches here and compiles
    /// today is covered: single-index array elements (mirrors ArrayHandler.VisitArrayElementReference
    /// / PrepareArrayElementSet), aggregate struct/tuple member chains (mirrors the
    /// ExpressionHandler aggregate-member read / TryPrepareFieldSet's aggregate arm), cross-behaviour
    /// fields, and captured env locals/params. A shape that still returns null here is a genuine
    /// argument-site reject at the call in EmitExternMethodCall, not a silent continuation.</summary>
    (System.Func<CLeaf> read, System.Action<CLeaf> store)? TryPrepareRefOutArg(IArgumentOperation arg)
    {
        var param = arg.Parameter;
        if (param == null || (param.RefKind != RefKind.Ref && param.RefKind != RefKind.Out))
            return null;
        var target = UnwrapConversions(arg.Value);
        switch (target)
        {
            // `out _` — the value is thrown away, so the store leg is a no-op. The read leg must
            // still produce a WELL-TYPED placeholder: an INTERNAL call stages it positionally into
            // the callee's param field (a null CValue is a CoreVerify ICE — M4 wave L1s_r2_c11), and
            // the callee overwrites an out param before any read, so a fresh scratch is sound.
            case IDiscardOperation discard:
                return (() => SlotRef(_ctx.Builder.AllocScratch(GetStorageType(discard.Type))), _ => { });
            // N-dim array element (design 2026-07-04 §2): lift the single-index exclusion below —
            // PrepareNdimRefOutArg evaluates every index once and caches the bounds/backing/flat-index
            // plan, mirroring the single-index arm's (arrayVal, indexVal) caching.
            case IArrayElementReferenceOperation ndimArrayElem when ndimArrayElem.Indices.Length > 1:
                return PrepareNdimRefOutArg(ndimArrayElem);
            case IArrayElementReferenceOperation arrayElem
                when arrayElem.Indices.Length == 1
                     && arrayElem.Indices[0] is not IRangeOperation:
            {
                var arrayVal = VisitExpression(arrayElem.ArrayReference);
                var arrSym = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSym);
                // B40 follow-up: the RESOLVED int position (arr.Length - k for `^k`) is computed
                // ONCE here and closed over by both the read and store below — a side-effecting
                // `^Idx()` runs exactly once, matching every other ref/out leg in this method.
                var indexVal = ResolveArrayIndex(arrayVal, arrayType, arrayElem.Indices[0]);
                var elementType = GetArrayElemType(arrSym);
                return (() =>
                {
                    CLeaf elemVal = ExternCall(UdonAbi.ArrayGet(arrayType, elementType),
                        new List<CLeaf> { arrayVal, indexVal }, GetStorageType(arrayElem.Type));
                    if (arrayElem.Type is INamedTypeSymbol elemAgg && TypeClassifier.IsAggregateValue(elemAgg))
                        elemVal = AggregateAbi.DeepClone(_builder, elemVal, elemAgg, _ctx.Aggregates.GetLayout);
                    return elemVal;
                }, v => EmitExternVoid(
                    UdonAbi.ArraySet(arrayType, elementType),
                    new List<CLeaf> { arrayVal, indexVal, v }));
            }
            case IFieldReferenceOperation fieldRef
                when AggregateAbi.TryGetMemberTarget(fieldRef, out var aggInstance, out var aggMember)
                     && aggInstance.Type is INamedTypeSymbol aggContaining
                     && TypeClassifier.IsAggregateValue(aggContaining)
                     && _ctx.Aggregates.GetLayout(aggContaining).TryGetIndex(aggMember, out var memberIndex):
            {
                var arrExpr = LoadInstanceRaw(aggInstance);
                return (() =>
                {
                    CLeaf memberVal = AggregateAbi.ReadSlot(_builder, arrExpr, memberIndex, StorageTypes.Object);
                    if (fieldRef.Field.Type is INamedTypeSymbol memberAgg && TypeClassifier.IsAggregateValue(memberAgg))
                        memberVal = AggregateAbi.DeepClone(_builder, memberVal, memberAgg, _ctx.Aggregates.GetLayout);
                    return memberVal;
                }, v => AggregateAbi.WriteSlot(_builder, arrExpr, memberIndex, v));
            }
            // Round-9 [Y12]: BEHAVIOUR field through a non-this receiver (`hs[Pick()].pub`,
            // `other.pub`) — the pre-fix path re-evaluated the receiver legs at copy-back
            // (AssignToTarget's SetProgramVariable arm), so a side-effecting index leg ran twice
            // and the write landed in the cell chosen by the SECOND evaluation. Evaluate the
            // receiver ONCE here (materialized — the copy-back must hit the SAME instance even if
            // the receiver storage is reassigned during the call), read via GetProgramVariable,
            // store via SetProgramVariable through the cached reference.
            case IFieldReferenceOperation behField
                when behField.Instance != null
                     && behField.Instance is not IInstanceReferenceOperation
                     && ExternResolver.IsUdonSharpBehaviour(behField.Field.ContainingType):
            {
                var instanceVal = VisitExpression(behField.Instance);
                var instSlot = _ctx.Builder.AllocScratch(GetStorageType(behField.Instance.Type));
                EmitAssign(instSlot, instanceVal);
                var instRef = SlotRef(instSlot);
                var fieldType = GetStorageType(behField.Field.Type);
                return (() => LoadProgramVariable(
                        instRef, behField.Field.Name, fieldType),
                    v => StoreProgramVariable(
                        instRef, behField.Field.Name, fieldType, v));
            }
            // Captured local/parameter: no flat heap address (Stage 2 §4.1 — ResolveOutRefFieldName
            // returns null for these), so the direct-FieldAddr fast path above can't take them. A bare
            // variable reference has no side-effecting legs to double-evaluate; route through the same
            // env cell EnvEmit.Read/Write use elsewhere so this shape shares the ONE prepared mechanism
            // instead of a second read-then-AssignToLValue path.
            case ILocalReferenceOperation envLocalRef when _ctx.Closures.TryGetEnvBinding(envLocalRef.Local, out _):
            {
                var envType = GetStorageTypeName(envLocalRef.Type);
                return (() => EnvEmit.Read(_builder, _ctx, envLocalRef.Local, new StorageType(envType)),
                    v => EnvEmit.Write(_builder, _ctx, envLocalRef.Local, v));
            }
            case IParameterReferenceOperation envParamRef when _ctx.Closures.TryGetEnvBinding(envParamRef.Parameter, out _):
            {
                var envType = GetStorageTypeName(envParamRef.Type);
                return (() => EnvEmit.Read(_builder, _ctx, envParamRef.Parameter, new StorageType(envType)),
                    v => EnvEmit.Write(_builder, _ctx, envParamRef.Parameter, v));
            }
            // `out var x` declaring a captured local: same env-cell routing (the read side is never
            // invoked for an Out param, kept for symmetry with the Ref cases above).
            case IDeclarationExpressionOperation declExpr
                when declExpr.Expression is ILocalReferenceOperation declLocal
                     && _ctx.Closures.TryGetEnvBinding(declLocal.Local, out _):
            {
                var envType = GetStorageTypeName(declLocal.Type);
                return (() => EnvEmit.Read(_builder, _ctx, declLocal.Local, new StorageType(envType)),
                    v => EnvEmit.Write(_builder, _ctx, declLocal.Local, v));
            }
        }
        return null;
    }

    /// <summary>Round-9 [Y16]: true when an argument AFTER index <paramref name="argIndex"/>
    /// (evaluation order) can write observable state — an invocation (incl. delegate dispatch),
    /// assignment, increment/decrement, object creation, or a property read (computed getters may
    /// mutate). A ref/out argument's VALUE READ must then defer to just before the call: C# passes
    /// the LOCATION (computed at argument position) and the callee reads it at call time, so a
    /// later argument's write to that location is visible (VM-proven stale copy-in: plain array
    /// element, struct-array-element field leaf, and behaviour this-field flavors, all c0 ref=55
    /// vs 6). Side-effect-free later arguments keep the immediate read — byte-identical.</summary>
    static bool HasLaterEffectfulArg(IReadOnlyList<IArgumentOperation> arguments, int argIndex)
    {
        for (int j = argIndex + 1; j < arguments.Count; j++)
            if (IsPotentiallyEffectful(arguments[j].Value))
                return true;
        return false;
    }

    static bool IsPotentiallyEffectful(IOperation op)
    {
        if (op == null) return false;
        if (op is IInvocationOperation or IObjectCreationOperation or IAssignmentOperation
            or IIncrementOrDecrementOperation or IPropertyReferenceOperation)
            return true;
        foreach (var child in op.ChildOps())
            if (IsPotentiallyEffectful(child))
                return true;
        return false;
    }

    /// <summary>Evaluate op's POSITIONAL arguments, appending each into <paramref name="args"/> (after any
    /// pre-seeded receiver), and return the ref/out write-back stores keyed by argument index (null if none).
    /// Leg-bearing ref/out lvalue legs evaluate once ([Y12]); effectful value reads defer past later arguments
    /// ([Y16]) then patch into <paramref name="args"/> in order. This append-order marshalling is shared by
    /// every positional internal-call arm — formerly copy-pasted 4x, and a past copy dropped the copy-back
    /// (DiffFuzz ref=9 vs VM 1; ref=136 vs 106). The named/reordered path (MarshalArgumentsByOrdinal) is a
    /// distinct parameter-ORDINAL placement and is deliberately not folded in here.</summary>
    Dictionary<int, System.Action<CLeaf>> MarshalArguments(IInvocationOperation op, List<CLeaf> args)
    {
        Dictionary<int, System.Action<CLeaf>> prepared = null;
        List<(int slot, System.Func<CLeaf> read)> deferred = null;
        for (var i = 0; i < op.Arguments.Length; i++)
        {
            var (val, deferredRead, store) = EvaluateCallArgument(op.Arguments, i);
            if (store != null)
                (prepared ??= new Dictionary<int, System.Action<CLeaf>>())[i] = store;
            if (deferredRead != null)
            {
                (deferred ??= new List<(int, System.Func<CLeaf>)>()).Add((args.Count, deferredRead));
                args.Add(null);
            }
            else
                args.Add(val);
        }
        if (deferred != null)
            foreach (var (slot, read) in deferred)
                args[slot] = read();
        return prepared;
    }

    /// <summary>Evaluate ONE internal-call argument: ref/out lvalue legs evaluate now (C# computes
    /// the location at argument position; round-8 [Y12] one-evaluation contract), the value read
    /// defers past later effectful arguments ([Y16]), and the prepared copy-back store rides along.
    /// Exactly one of <c>value</c>/<c>deferredRead</c> is non-null.</summary>
    (CLeaf value, System.Func<CLeaf> deferredRead, System.Action<CLeaf> store) EvaluateCallArgument(
        IReadOnlyList<IArgumentOperation> arguments, int i)
    {
        var argOp = arguments[i].Value;
        var param = arguments[i].Parameter;
        bool refOut = param != null && (param.RefKind == RefKind.Ref || param.RefKind == RefKind.Out);
        if (!refOut)
            return (VisitExpression(argOp), null, null);
        bool defer = HasLaterEffectfulArg(arguments, i);
        if (TryPrepareRefOutArg(arguments[i]) is { } pre)
            return defer ? (null, pre.read, pre.store) : (pre.read(), null, pre.store);
        return defer
            ? ((CLeaf)null, () => VisitExpression(argOp), (System.Action<CLeaf>)null)
            : (VisitExpression(argOp), null, null);
    }

    /// <summary>Evaluate arguments in TEXTUAL order (C# evaluation order) but place each value at its
    /// PARAMETER's ordinal — IInvocationOperation/IObjectCreationOperation.Arguments can be in call-site
    /// (syntax) order for named/reordered calls, so positional append mis-routes them (diff-fuzz w4; the
    /// CW4 ctor twin silently swapped fields). Rides EvaluateCallArgument's [Y12] one-evaluation and
    /// [Y16] deferred-read contracts; a delegate-typed argument is an ordinary bundle value (design §2.4,
    /// no per-call-site convention rebinding) and aggregates clone on read. Returns the prepared ref/out
    /// stores (keyed by argument index) for EmitRefOutCopyBack. <paramref name="stage"/> materializes each
    /// value as it lands — the virtual chain stages to scratch so every dispatch arm re-reads one slot.</summary>
    Dictionary<int, System.Action<CLeaf>> MarshalArgumentsByOrdinal(
        IReadOnlyList<IArgumentOperation> arguments, IMethodSymbol target, List<CLeaf> args,
        System.Func<CLeaf, IArgumentOperation, CLeaf> stage = null)
    {
        var argSlots = new CLeaf[target.Parameters.Length];
        Dictionary<int, System.Action<CLeaf>> prepared = null;
        List<(int ordinal, int index, System.Func<CLeaf> read)> deferredReads = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            var param = arguments[i].Parameter ?? target.Parameters[i];
            var (val, deferredRead, store) = EvaluateCallArgument(arguments, i);
            if (store != null)
                (prepared ??= new Dictionary<int, System.Action<CLeaf>>())[i] = store;
            if (deferredRead != null)
                (deferredReads ??= new List<(int, int, System.Func<CLeaf>)>()).Add((param.Ordinal, i, deferredRead));
            else if (param.Ordinal >= 0 && param.Ordinal < argSlots.Length)
                argSlots[param.Ordinal] = stage == null ? val : stage(val, arguments[i]);
        }
        // [Y16]: deferred ref/out reads run AFTER every argument evaluation, just before the call.
        if (deferredReads != null)
            foreach (var (ordinal, index, read) in deferredReads)
                if (ordinal >= 0 && ordinal < argSlots.Length)
                {
                    var val = read();
                    argSlots[ordinal] = stage == null ? val : stage(val, arguments[index]);
                }
        args.AddRange(argSlots);
        return prepared;
    }

    CLeaf EmitUserMethodCall(IInvocationOperation op, IMethodSymbol target)
    {
        GuardRefOutArguments(op, target);

        // Recursion is handled centrally in EmitCallToMethod (software-stack spill/reload around the call).

        var args = new List<CLeaf>();
        var preparedRefOut = MarshalArgumentsByOrdinal(op.Arguments, target, args);

        // Stage 2 §5.6: a same-program CAPTURING local function called by NAME receives its env as a
        // trailing REAL argument (positional copy-in binds it to the callee's __envp param field) —
        // env resolved in the caller's frame via the binding-scope chain. Tail/spill classification
        // treats it like any argument (no new statement-form tail shape).
        if (_ctx.Closures.CaptureScope != null && _ctx.Closures.CaptureScope.IsCapturingClosure(target.OriginalDefinition))
            args.Add(ClosureEnvLeaf(target));

        // Under A-normal form EmitCallToMethod already materialized the call (a non-void call returns a CSlotRef
        // leaf, void returns null), so the call and its copy-in are sequenced before the copy-out below — no
        // manual re-sequencing of a lazy call is needed. The call SITE syntax rides along for the round-9
        // [Y3] per-site tail sparing on recursive edges.
        var result = EmitCallToMethod(target, args, op.Syntax);

        EmitRefOutCopyBack(op, target, 0, preparedRefOut);

        return result;
    }

    // ── Ref/Out copy-back helper ──

    /// <summary>Copy-back for a ref/out argument of a USER-METHOD call (EmitRefOutCopyBack) whose
    /// target TryPrepareRefOutArg declined to prepare a leg-caching store for — a plain local,
    /// parameter, or this-field, none of which have side-effecting receiver/index legs to
    /// double-evaluate, so a direct AssignToLValue write-back is already correct and byte-identical.
    /// EmitExternMethodCall no longer has a caller here: its ref/out branch requires
    /// TryPrepareRefOutArg to succeed (throwing a NotSupportedException at the argument site
    /// otherwise), so it never falls through to this second path.</summary>
    void AssignToTarget(IOperation target, CLeaf value) => AssignToLValue(target, value);

    // ── Out/Ref Field Resolution ──

    /// <summary>
    /// Resolve the UASM field name for an out/ref argument target.
    /// Returns null if the target cannot be resolved to a direct field reference.
    /// </summary>
    string ResolveOutRefFieldName(IOperation op)
    {
        while (op is IConversionOperation conv) op = conv.Operand;
        switch (op)
        {
            // Stage 2 §4.1: env cells have no heap ADDRESS — captured out/ref targets return null so
            // the caller's generic path stages a temp and copies back through the lvalue-store arms
            // (which route captured symbols into their env cells).
            case ILocalReferenceOperation localRef:
                if (_ctx.Closures.TryGetEnvBinding(localRef.Local, out _)) return null;
                return _localBindings.TryGetValue(localRef.Local, out var rb) ? rb.Id : null;
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                return _ctx.SourceStorageName(fieldRef.Field);
            case IParameterReferenceOperation paramRef:
                if (_ctx.Closures.TryGetEnvBinding(paramRef.Parameter, out _)) return null;
                return GetParamVarId(paramRef.Parameter);
            case IDeclarationExpressionOperation declExpr:
                if (declExpr.Expression is ILocalReferenceOperation declLocal)
                {
                    if (_ctx.Closures.TryGetEnvBinding(declLocal.Local, out _)) return null;
                    var type = GetStorageTypeName(declLocal.Type);
                    var localId = _ctx.Storage.DeclareLocal(declLocal.Local.Name, new StorageType(type));
                    _localBindings[declLocal.Local] = new EmitContext.LocalBinding(localId);
                    return localId;
                }
                return null;
            default:
                return null;
        }
    }

    // ── Extern Signature Helpers ──

    BoundExtern BindExternMethodCall(IMethodSymbol method, ITypeSymbol instanceType = null,
        string[] paramTypeOverride = null, bool allowMissing = false)
    {
        ITypeSymbol containingTypeSym = method.ContainingType;

        // Interface method on a type parameter: use the concrete type as containing type
        // e.g., IComparable<T>.CompareTo(T) with T=int → SystemInt32.__CompareTo__SystemInt32__SystemInt32
        if (containingTypeSym.TypeKind == TypeKind.Interface && instanceType != null
            && _typeParamMap != null
            && instanceType is ITypeParameterSymbol tp
            && _typeParamMap.TryGetValue(tp, out var concreteType))
            containingTypeSym = concreteType;

        // Wave-12 [V3]: an Object/ValueType/Enum-inherited member (Equals/GetHashCode/ToString) invoked
        // on a TYPE-PARAMETER receiver binds the effective-base-class symbol (e.g. System.ValueType.Equals
        // under a struct constraint), whose Udon-mapped containing type (SystemValueType) has no registered
        // extern — the invalid signature then fell into ResolveExtern's Component fallback chain and
        // silently adopted UnityEngineComponent.__Equals/__GetHashCode/__ToString for a boxed value
        // receiver (type-mismatched extern on the real VM). Monomorphization knows the exact runtime type,
        // so resolve the boxed virtual dispatch at compile time: re-route to the concrete type's own
        // extern (SystemInt32.__Equals__SystemObject__SystemBoolean etc. — all registered per primitive).
        // A user aggregate (object[]-emulated struct) has no such extern and C#'s ValueType semantics
        // (field-wise Equals, type-name ToString) cannot be expressed as one — loud per design §8-3.
        if (instanceType is ITypeParameterSymbol vtp && _typeParamMap != null
            && _typeParamMap.TryGetValue(vtp, out var vConcrete)
            && method.ContainingType.SpecialType is SpecialType.System_Object
                or SpecialType.System_ValueType or SpecialType.System_Enum)
        {
            if (vConcrete is INamedTypeSymbol vAgg && TypeClassifier.IsAggregateValue(vAgg))
                throw new System.NotSupportedException(
                    $"'{method.Name}' on type parameter '{vtp.Name}' instantiated with user-defined "
                    + $"struct '{vConcrete.Name}' is not supported: Udon has no extern for it and C#'s "
                    + "ValueType semantics cannot be emulated. Compare/format the struct's fields "
                    + "directly instead.");
            containingTypeSym = vConcrete;
        }
        // B59/B60: the SAME Object/ValueType/Enum-inherited member on a CONCRETE receiver (not a type
        // parameter) keeps the base owner (SystemEnum/SystemValueType — no registered extern). Route it
        // through the shared owner choke point: an enum resolves to the receiver's static type (→
        // underlying-primitive extern, B59); a user-struct receiver hits the designed reject (B60).
        else if (instanceType != null && instanceType is not ITypeParameterSymbol
            && method.ContainingType.SpecialType is SpecialType.System_Object
                or SpecialType.System_ValueType or SpecialType.System_Enum)
            containingTypeSym = ResolveExternOwnerType(method.ContainingType, instanceType, method.Name);

        // Armor: a user-struct member reaching generic extern construction means no CFunction was
        // registered for it (collector-scope drift) — fail with a diagnosis, not a bogus
        // SystemObjectArray.__<Name>__ extern (roadmap B46 family). An instance user-struct method is
        // pre-routed to EmitStructInstanceCall (InvocationHandler), so this catches static-on-struct and
        // any future uncovered call shape.
        GuardUserStructMemberReachedExtern(containingTypeSym, method.Name);

        var containingType = GetStorageTypeName(containingTypeSym);

        // Object.Instantiate → VRCInstantiate (Udon VM redirect)
        // Generic static Array methods (IndexOf<T>, LastIndexOf<T>, BinarySearch<T>, Reverse<T>):
        // UdonSharp resolves these to the non-generic overload (Array, object) instead of (T[], T).
        // The TArray/T version exists but causes HeapTypeMismatch (reads String[] as Object[]).
        if (method.IsGenericMethod && containingType == "SystemArray")
        {
            var nonGenericPts = method.OriginalDefinition.Parameters.Select(p =>
            {
                var t = p.Type;
                switch (t)
                {
                    case ITypeParameterSymbol:
                        return "SystemObject";
                    case IArrayTypeSymbol { ElementType: ITypeParameterSymbol }:
                        return "SystemArray";
                }
                var tn = GetStorageTypeName(t);
                if (p.RefKind != RefKind.None) tn += "Ref";
                return tn;
            }).ToArray();
            paramTypeOverride = nonGenericPts;
        }

        if (_ctx.Abi.TryBindMethod(
                method, containingType, type => GetStorageTypeName(type),
                paramTypeOverride, out var bound))
            return bound;
        if (allowMissing)
            return null;
        return _ctx.Abi.BindMethod(
            method, containingType, type => GetStorageTypeName(type), paramTypeOverride);
    }
}
