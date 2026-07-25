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
            _lowering.State.Boundary.RequireCanPassExternArgument(op.Instance.Type,
                $"receiver of {target.Name}", deferAggregateReceiverPolicy: true);
        if (!target.ReturnsVoid)
            _lowering.State.Boundary.RequireCanReturnFromExtern(target.ReturnType, target.Name);
        var objectIdentityExtern = target.Name == nameof(object.Equals)
            && target.ContainingType.SpecialType == SpecialType.System_Object;
        var objectMemberExtern = target.ContainingType.SpecialType
            is SpecialType.System_Object or SpecialType.System_ValueType;

        CLeaf instanceVal = null;
        if (!target.IsStatic)
        {
            if (op.Instance is IInstanceReferenceOperation)
                instanceVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(_lowering.GetStorageType(target.ContainingType)), _lowering.GetStorageType(target.ContainingType));
            else if (op.Instance is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef
                     && fieldRef.Field.Type.IsValueType && !fieldRef.Field.IsStatic)
            {
                // Value-type field on this: pass heap address directly so extern can modify in-place
                instanceVal = _lowering.FieldAddr(_lowering.State.SourceStorageName(fieldRef.Field), _lowering.GetStorageType(fieldRef.Field.Type));
            }
            // Local variable — value type: pass heap address directly so extern can modify in-place
            else if (op.Instance is ILocalReferenceOperation localRef
                     && localRef.Type.IsValueType
                     && _lowering.LocalBindings.TryGetValue(localRef.Local, out var localBind))
            {
                instanceVal = _lowering.FieldAddr(localBind.Id, _lowering.GetStorageType(localRef.Type));
            }
            // Parameter — value type: pass heap address directly so extern can modify in-place
            else if (op.Instance is IParameterReferenceOperation paramRef
                     && paramRef.Type.IsValueType)
            {
                var paramId = _lowering.GetParamVarId(paramRef.Parameter);
                instanceVal = _lowering.FieldAddr(paramId, _lowering.GetStorageType(paramRef.Type));
            }
            else if (op.Instance != null)
                instanceVal = _lowering.VisitExpression(op.Instance);
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
                for (int i = 0; i < lastParamIdx; i++) pts.Add(_lowering.GetStorageTypeName(target.Parameters[i].Type));
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
            if (NdimArrayAbi.IsNdimArray(LoweringServices.UnwrapConversions(op.Arguments[i].Value).Type))
                throw new System.NotSupportedException(ExternResolver.MultidimExternArgMessage);

            var argumentType = LoweringServices.UnwrapConversions(op.Arguments[i].Value).Type
                ?? op.Arguments[i].Parameter?.Type;
            if (argumentType != null)
                _lowering.State.Boundary.RequireCanPassExternArgument(argumentType,
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
                    if (NdimArrayAbi.IsNdimArray(LoweringServices.UnwrapConversions(elem).Type))
                        throw new System.NotSupportedException(ExternResolver.MultidimExternArgMessage);
                    if (LoweringServices.UnwrapConversions(elem).Type is { } elementType)
                        _lowering.State.Boundary.RequireCanPassExternArgument(elementType,
                            $"element of {param.Name}");
                    argVals.Add(_lowering.VisitExpression(elem));
                }
                continue;
            }
            if (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref)
            {
                var fieldName = ResolveOutRefFieldName(op.Arguments[i].Value);
                if (fieldName != null)
                {
                    argVals.Add(_lowering.FieldAddr(fieldName, _lowering.GetStorageType(param.Type)));
                    continue;
                }
                // Complex lvalue: evaluate the receiver/index legs ONCE via the hardened
                // TryPrepareRefOutArg machinery (wave-9 [Y12]) and copy back through the SAME
                // legs — C# evaluates an argument's component expressions exactly once, at the
                // argument's syntax position, before the call. Every shape that reaches here and
                // compiles today is covered by TryPrepareRefOutArg (array element, aggregate
                // member, cross-behaviour field, captured env local/param) — anything it declines
                // is loud-rejected below instead of falling through a second, un-audited path.
                var paramType = _lowering.GetStorageTypeName(param.Type);
                var tempField = _lowering.State.Storage.DeclareLocal("outref", new StorageType(paramType));
                var prepared = TryPrepareRefOutArg(op.Arguments[i]) ?? throw new System.NotSupportedException(
                    $"'{(param.RefKind == RefKind.Ref ? "ref" : "out")} {param.Name}' of '{target.Name}' cannot "
                    + $"bind to '{op.Arguments[i].Value.Syntax}' ({op.Arguments[i].Value.Kind}): this l-value "
                    + "shape has no ref/out extern binding (locals, parameters, behaviour fields, single-index "
                    + "array elements, and struct/tuple members are supported). Assign it to a local variable "
                    + "first, or restructure the expression.");
                if (param.RefKind == RefKind.Ref)
                    _lowering.EmitStoreField(tempField, prepared.read());
                argVals.Add(_lowering.FieldAddr(tempField, new StorageType(paramType)));
                outCopyBacks.Add((i, tempField, prepared.store));
                continue;
            }
            argVals.Add(_lowering.VisitExpression(op.Arguments[i].Value));
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
            var returnType = _lowering.GetStorageTypeName(target.ReturnType);
            // result is already a single-assignment scratch leaf (ExternCall binds it under ANF); the out/ref
            // copy-back below writes only the user's target lvalues, never this slot, so it survives unchanged.
            result = _lowering.ExternCall(sig, externArgs, new StorageType(returnType));
        }
        else
        {
            _lowering.EmitExternVoid(sig, externArgs);
            result = null;
        }

        // Copy-back for complex out/ref lvalues — always through the SAME legs TryPrepareRefOutArg
        // evaluated at copy-in (never a re-evaluating fallback).
        foreach (var (argIdx, tempField, store) in outCopyBacks)
        {
            var paramType = _lowering.GetStorageTypeName(target.Parameters[argIdx].Type);
            var val = _lowering.LoadField(tempField, new StorageType(paramType));
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
           && _lowering.ResolveType(arr.ElementType) is INamedTypeSymbol elem && TypeClassifier.IsAggregateValue(elem)
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
            if (AggregateArrayElement(LoweringServices.UnwrapConversions(argument.Value).Type) != null)
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
            var arrType = _lowering.GetArrayType(recvArr);
            var elemType = _lowering.GetArrayElemType(recvArr);
            if (target.Name == "Clone" && target.Parameters.Length == 0)
            {
                var srcVal = _lowering.VisitExpression(op.Instance);
                var lenVal = _lowering.ExternCall(UdonAbi.ArrayLength(arrType),
                    new List<CLeaf> { srcVal }, StorageTypes.Int32);
                var dstVal = _lowering.ExternCall(UdonAbi.ArrayConstructor(arrType), new List<CLeaf> { lenVal }, new StorageType(arrType));
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
                var srcVal = _lowering.VisitExpression(op.Instance);
                var argVals = EvaluateArgsByOrdinal(op);
                var lenVal = _lowering.ExternCall(UdonAbi.ArrayLength(arrType),
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
            var srcOp = LoweringServices.UnwrapConversions(ArgByOrdinal(op, 0));
            var dstOp = LoweringServices.UnwrapConversions(ArgByOrdinal(op, ranged ? 2 : 1));
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
            var arrType = _lowering.GetArrayType(srcArrSym);
            var elemType = _lowering.GetArrayElemType(srcArrSym);
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
            vals[a.Parameter.Ordinal] = _lowering.VisitExpression(a.Value);
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
            var tempVal = _lowering.ExternCall(UdonAbi.ArrayConstructor(arrType), new List<CLeaf> { lenVal }, new StorageType(arrType));
            EmitIndexedLoop(lenVal, iVal =>
            {
                var elemVal = _lowering.ExternCall(getSig, new List<CLeaf> { srcVal, OffsetIndex(srcStartVal, iVal) }, new StorageType(AggregateAbi.ArrayType));
                _lowering.EmitExternVoid(setSig, new List<CLeaf>
                    { tempVal, iVal, AggregateAbi.DeepClone(_lowering.Builder, elemVal, elemAgg, _lowering.State.Aggregates.GetLayout) });
            });
            EmitIndexedLoop(lenVal, iVal =>
            {
                var elemVal = _lowering.ExternCall(getSig, new List<CLeaf> { tempVal, iVal }, new StorageType(AggregateAbi.ArrayType));
                _lowering.EmitExternVoid(setSig, new List<CLeaf> { dstVal, OffsetIndex(dstStartVal, iVal), elemVal });
            });
        }
        else
        {
            EmitIndexedLoop(lenVal, iVal =>
            {
                var elemVal = _lowering.ExternCall(getSig, new List<CLeaf> { srcVal, OffsetIndex(srcStartVal, iVal) }, new StorageType(AggregateAbi.ArrayType));
                _lowering.EmitExternVoid(setSig, new List<CLeaf>
                    { dstVal, OffsetIndex(dstStartVal, iVal), AggregateAbi.DeepClone(_lowering.Builder, elemVal, elemAgg, _lowering.State.Aggregates.GetLayout) });
            });
        }
    }

    void EmitIndexedLoop(CLeaf lenVal, System.Action<CLeaf> body)
    {
        var iSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.Builder.EmitFor(
            b => { _lowering.EmitAssign(iSlot, _lowering.Const(0, StorageTypes.Int32)); },
            // cond MUST be the Func overload so it re-evaluates each iteration (the CLeaf overload
            // evaluates once, silently skipping the loop).
            () => _lowering.ExternCall(UdonAbi.Int32LessThan,
                new List<CLeaf> { _lowering.SlotRef(iSlot), lenVal }, StorageTypes.Boolean),
            b =>
            {
                _lowering.EmitAssign(iSlot, _lowering.ExternCall(UdonAbi.Int32Add,
                    new List<CLeaf> { _lowering.SlotRef(iSlot), _lowering.Const(1, StorageTypes.Int32) }, StorageTypes.Int32));
            },
            b => body(_lowering.SlotRef(iSlot)));
    }

    CLeaf OffsetIndex(CLeaf startVal, CLeaf iVal)
        => startVal == null
            ? iVal
            : _lowering.ExternCall(UdonAbi.Int32Add,
                new List<CLeaf> { startVal, iVal }, StorageTypes.Int32);

    // ── GetComponent<T> ──

    CLeaf EmitGetComponentGeneric(IInvocationOperation op, IMethodSymbol target)
    {
        var typeArg = target.TypeArguments[0];
        if (_lowering.State.Session.ObjectArrayBehaviourAliases.IsAlias(typeArg, _lowering.TypeParamMap))
            throw new System.NotSupportedException(
                $"GetComponent<{_lowering.ResolveType(typeArg).ToDisplayString()}> is invalid: this type is used "
                + "as a legacy object[] nominal alias in the same compilation and therefore has "
                + "SystemObjectArray storage, not a scene-component representation.");
        if (ExternResolver.IsUdonSharpBehaviour(typeArg))
            return EmitGetComponentShim(op, target);
        return IsGenericComponentGetterKey(target, _lowering.TypeTokenName(typeArg))
            ? EmitGetComponentExtern(op, target)
            : EmitGetComponentErasedQuery(op, target);
    }

    /// <summary>
    /// The generic component-query node OWNED BY the type token, i.e. the registration that makes the
    /// token a legal runtime dispatch key. `UnityEngineComponent.__GetComponent__T` forwards to
    /// `UdonWrapper.GetComponent__T`, which reads the token off the heap and indexes
    /// `_componentGetterModules[token]` with a BARE indexer — a miss is a KeyNotFoundException thrown
    /// out of the EXTERN, which halts the behaviour. The dictionary is populated from every wrapper
    /// module implementing IUdonComponentGetterModule, and that same interface is what registers the
    /// module's own `__GetComponent*__T` node under its own type name. So the token is a legal key
    /// exactly when this key resolves — one interface causing both facts, not a numeric coincidence.
    /// Asking <see cref="UdonAbiCatalog"/> keeps the answer on the installed SDK's own registry, the
    /// single ground truth every other extern USugar emits is already trusted against.
    /// </summary>
    bool IsGenericComponentGetterKey(IMethodSymbol target, string tokenUdonType)
    {
        var parameterTypes = target.OriginalDefinition.Parameters
            .Select(parameter => _lowering.GetStorageTypeName(parameter.Type))
            .ToArray();
        var key = UdonAbiKey.Method(tokenUdonType, target.Name, parameterTypes,
            target.Name.StartsWith("GetComponents") ? "TArray" : "T");

        // UdonAbiKey normalizes its owner through the extern-OWNERSHIP remap (VRC_Pickup's members are
        // registered under VRCPickup). That remap is about calling members ON a receiver and says
        // nothing about the token's runtime identity, so if it rewrote the name the catalog would
        // answer about a different type than the one being baked. Treat any divergence as "not a key".
        return key.Owner == tokenUdonType && _lowering.State.AbiCatalog.Contains(key);
    }

    // ── GetComponent<T> where T is not a legal generic-dispatch key ──
    // Route through the non-generic `GetComponent(System.Type)` family, whose wrapper does not index
    // any per-T dictionary: it calls the erased UnityEngine overload and writes a Component. The
    // token is still baked, but as an ordinary argument the SDK filters on, never as a dispatch key.
    CLeaf EmitGetComponentErasedQuery(IInvocationOperation op, IMethodSymbol target)
    {
        var typeArg = target.TypeArguments[0];
        var typeArgUdon = _lowering.GetStorageTypeName(typeArg);
        var isPlural = target.Name.StartsWith("GetComponents");

        // The plural erased extern returns UnityEngineComponentArray. That IS the destination for an
        // UdonBehaviour element type (GetStorageTypeName collapses it), so no conversion is needed.
        // For any other element type the destination is `{T}Array`, and narrowing a Component[] to it
        // would need a real materialization whose `{T}Array.__ctor` the SDK does not register for
        // exactly these type arguments. Reject loudly rather than emit an unassemblable array slot.
        if (isPlural && typeArgUdon != "VRCUdonCommonInterfacesIUdonEventReceiver")
            throw new System.NotSupportedException(
                $"{target.Name}<{_lowering.ResolveType(typeArg).ToDisplayString()}> cannot be lowered: the SDK "
                + $"registers no generic component getter for '{_lowering.TypeTokenName(typeArg)}', so the query "
                + "must use the erased Component[] overload, and narrowing that to "
                + $"'{typeArgUdon}Array' has no registered array constructor.");

        CLeaf instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(StorageTypes.Transform), StorageTypes.Transform);
        else if (op.Instance != null)
            instanceVal = _lowering.VisitExpression(op.Instance);

        var argVals = new List<CLeaf>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(_lowering.VisitExpression(op.Arguments[i].Value));

        instanceVal = EnsureComponentInstance(target, op.Instance, instanceVal);

        // Erased operand order is (instance, SystemType, bool?) — the token precedes the bool, the
        // opposite of the __T forms. ResolveErasedQueryExtern names the parameters in the same order.
        var externArgs = new List<CLeaf>();
        if (instanceVal != null)
            externArgs.Add(instanceVal);
        externArgs.Add(_lowering.ConstTypeToken(typeArg));
        externArgs.AddRange(argVals);

        var erasedResult = isPlural ? StorageTypes.ComponentArray : StorageTypes.Component;
        var fetched = _lowering.ExternCall(
            ResolveErasedQueryExtern(target.Name, op.Arguments.Length > 0), externArgs, erasedResult);

        if (isPlural)
            return fetched;
        if (typeArgUdon == "VRCUdonCommonInterfacesIUdonEventReceiver")
            return AsUdonBehaviour(fetched);
        if (typeArgUdon == "UnityEngineComponent")
            return fetched;
        return _lowering.RepresentationCast(fetched, new StorageType(typeArgUdon),
            RepresentationCastKind.ErasedComponentQueryResult);
    }

    /// <summary>The non-generic component-query extern for one query name. Shares its shape with
    /// <see cref="ResolveShimFetchExtern"/> but keeps the query's own arity: the USB shim must widen
    /// singular→plural to scan every behaviour, an erased query must not.</summary>
    static UdonAbiKey ResolveErasedQueryExtern(string methodName, bool hasBoolArg)
    {
        string resultType;
        switch (methodName)
        {
            case "GetComponent":
            case "GetComponentInChildren":
            case "GetComponentInParent":
                resultType = "UnityEngineComponent";
                break;
            case "GetComponents":
            case "GetComponentsInChildren":
            case "GetComponentsInParent":
                resultType = "UnityEngineComponentArray";
                break;
            default:
                throw new System.NotSupportedException(
                    $"'{methodName}' is not a component query, so it has no erased overload.");
        }

        var parameterTypes = hasBoolArg
            ? new[] { "SystemType", "SystemBoolean" }
            : new[] { "SystemType" };
        return UdonAbiKey.Method("UnityEngineComponent", methodName, parameterTypes, resultType);
    }

    // Existing logic for Unity Component types (Transform, Collider, etc.)
    // Uses the __T / __TArray generic extern form (matches UdonSharp behavior).
    CLeaf EmitGetComponentExtern(IInvocationOperation op, IMethodSymbol target)
    {
        // Evaluate instance and arguments first
        CLeaf instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(StorageTypes.Transform), StorageTypes.Transform);
        else if (op.Instance != null)
            instanceVal = _lowering.VisitExpression(op.Instance);

        // Evaluate explicit arguments (e.g., GetComponentInChildren<T>(bool includeInactive))
        var argVals = new List<CLeaf>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(_lowering.VisitExpression(op.Arguments[i].Value));

        instanceVal = EnsureComponentInstance(target, op.Instance, instanceVal);

        // Build extern args: instance + explicit args + typeof(T)
        var externArgs = new List<CLeaf>();
        if (instanceVal != null)
            externArgs.Add(instanceVal);

        // Push explicit arguments FIRST, then SystemType (matches UdonSharp push order for __T externs)
        externArgs.AddRange(argVals);

        // typeof(T) as SystemType constant (after explicit args) — shared type-token choke point.
        externArgs.Add(_lowering.ConstTypeToken(target.TypeArguments[0]));

        // Result type — typed as T for __T externs
        var isPlural = target.Name.StartsWith("GetComponents");
        var typeArgUdon = _lowering.GetStorageTypeName(target.TypeArguments[0]);
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
            .Select(parameter => _lowering.GetStorageTypeName(parameter.Type))
            .ToArray();
        var externSig = UdonAbiKey.Method(
            containingType, methodName, parameterTypes, resultPlaceholder);

        return _lowering.ExternCall(externSig, externArgs, new StorageType(tempType));
    }

    // ── GetComponent<T> USB Shim ──
    // Inline shim for USB-derived types: GetComponents(typeof(UdonBehaviour)) + __refl_typeid filter

    CLeaf EmitGetComponentShim(IInvocationOperation op, IMethodSymbol target)
    {
        var isSingular = !target.Name.StartsWith("GetComponents");

        // Evaluate instance
        CLeaf instanceVal = null;
        if (op.Instance is IInstanceReferenceOperation)
            instanceVal = _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(StorageTypes.Transform), StorageTypes.Transform);
        else if (op.Instance != null)
            instanceVal = _lowering.VisitExpression(op.Instance);

        // Evaluate explicit arguments (bool includeInactive)
        var argVals = new List<CLeaf>();
        for (int i = 0; i < op.Arguments.Length; i++)
            argVals.Add(_lowering.VisitExpression(op.Arguments[i].Value));

        instanceVal = EnsureComponentInstance(target, op.Instance, instanceVal);

        // Determine which non-generic GetComponents extern to call
        var fetchExtern = ResolveShimFetchExtern(target.Name, op.Arguments.Length > 0);

        // Build args: instance + typeof(UdonBehaviour) + optional args
        var fetchArgs = new List<CLeaf>();
        if (instanceVal != null)
            fetchArgs.Add(instanceVal);
        var udonBehaviourType = _lowering.Const("VRCUdonUdonBehaviour", StorageTypes.Type);
        fetchArgs.Add(udonBehaviourType);
        fetchArgs.AddRange(argVals);

        // Call GetComponents → ComponentArray (store to slot so it's evaluated once)
        var allComponentsSlot = _lowering.State.Builder.AllocScratch(StorageTypes.ComponentArray);
        _lowering.EmitAssign(allComponentsSlot, _lowering.ExternCall(fetchExtern, fetchArgs, StorageTypes.ComponentArray));
        var allComponents = _lowering.SlotRef(allComponentsSlot);

        // Compute target type ID at compile time
        long targetTypeId = UdonBehaviourTypeMetadata.TypeId(target.TypeArguments[0]);
        var targetIdConst = _lowering.Const(targetTypeId, StorageTypes.Int64);

        // Inheritance: if derived USB types exist, use __refl_typeids + Array.IndexOf
        bool useTypeIds = UdonBehaviourTypeMetadata.LookupRequiresAssignableIds(
            target.TypeArguments[0], _lowering.Planner.Census);
        var reflKeyConst = useTypeIds
            ? _lowering.Const(RuntimeReflectionFields.AssignableTypeIds, StorageTypes.String)
            : _lowering.Const(RuntimeReflectionFields.TypeId, StorageTypes.String);

        return isSingular
            ? EmitShimSingular(allComponents, targetIdConst, reflKeyConst, useTypeIds)
            : EmitShimPlural(allComponents, targetIdConst, reflKeyConst, useTypeIds);
    }

    /// <summary>
    /// Routes an explicit component-query receiver through .transform, exactly as stock UdonSharp
    /// does (BoundInvocationExpression.TryCreateGetComponentInvocation, tagged "udon-workaround"):
    /// the SDK's component getters select how to fetch operand 0 from the slot's DECLARED type, so
    /// an already-Component-typed slot is not proof and the hop is unconditional. Implicit `this` is
    /// exempt because StorageContext mints it as a Transform slot.
    /// </summary>
    CLeaf EnsureComponentInstance(IMethodSymbol target, IOperation instanceOp, CLeaf instanceVal)
    {
        if (instanceOp == null || instanceVal == null)
            throw new System.InvalidOperationException(
                "A component query has no receiver to normalize.");
        if (instanceOp is IInstanceReferenceOperation)
            return instanceVal;

        // The intrinsic key admitted this call by its declaring type, so the same symbol decides
        // which transform getter applies — asking the receiver expression instead would be a second
        // producer of the classification that already let the call in.
        var declaring = target.ContainingType?.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat);
        switch (declaring)
        {
            case "UnityEngine.GameObject":
                return TransformOf("UnityEngineGameObject", instanceVal);
            case "UnityEngine.Component":
                return TransformOf("UnityEngineComponent", instanceVal);
            default:
                throw new System.NotSupportedException(
                    $"A component query declared on '{declaring ?? "(unknown)"}' has no receiver "
                    + "normalization, so its operand cannot be proven Component-typed.");
        }
    }

    CLeaf TransformOf(string owner, CLeaf instanceVal)
        => _lowering.ExternCall(
            UdonAbiKey.Method(owner, "get_transform", "UnityEngineTransform"),
            new List<CLeaf> { instanceVal },
            StorageTypes.Transform);

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
        var lenSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(lenSlot, _lowering.ExternCall(
            UdonAbiKey.Method("UnityEngineComponentArray", "get_Length", "SystemInt32"),
            new List<CLeaf> { allComponents }, StorageTypes.Int32));

        // Loop index (mutable across control flow)
        var idxSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(idxSlot, _lowering.Const(0, StorageTypes.Int32));

        // Result slot (null initially — returns null if no match found)
        var resultSlot = _lowering.State.Builder.AllocScratch(StorageTypes.UdonEventReceiver);

        // while (idx < len) — Func overload so the counter-dependent condition re-evaluates each iteration.
        // The CLeaf overload evaluates it ONCE (idx still 0), so the loop never advances / never runs.
        _lowering.Builder.EmitWhile(
            () => _lowering.ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { _lowering.SlotRef(idxSlot), _lowering.SlotRef(lenSlot) },
                StorageTypes.Boolean),
            b =>
            {
                // element = allComponents[idx]
                var elementVal = _lowering.ExternCall(
                    UdonAbi.ArrayGet("UnityEngineComponentArray", "UnityEngineComponent"),
                    new List<CLeaf> { allComponents, _lowering.SlotRef(idxSlot) },
                    StorageTypes.Component);
                var behaviourVal = AsUdonBehaviour(elementVal);

                // idValue = behaviour.GetProgramVariable("__refl_typeid" or "__refl_typeids")
                var idValueVal = _lowering.LoadProgramVariable(
                    behaviourVal, reflKeyConst, StorageTypes.Object);

                // Null check: if (idValue != null)
                var nullConst = _lowering.Const(null, StorageTypes.Object);
                var notNullVal = _lowering.ExternCall(
                    UdonAbi.ObjectInequality,
                    new List<CLeaf> { idValueVal, nullConst },
                    StorageTypes.Boolean);

                _lowering.Builder.EmitIf(notNullVal, thenB =>
                {
                    // Type check
                    var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

                    _lowering.Builder.EmitIf(matchVal, matchB =>
                    {
                        // The type-id check proves this Component is the requested Udon behaviour.
                        // Preserve that proof as an explicit representation conversion instead of
                        // relying on the global reference-type assignment relaxation.
                        _lowering.EmitAssign(resultSlot, behaviourVal);
                        _lowering.Builder.EmitBreak();
                    });
                });

                // idx++
                var oneConst = _lowering.Const(1, StorageTypes.Int32);
                var nextIdxVal = _lowering.ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { _lowering.SlotRef(idxSlot), oneConst },
                    StorageTypes.Int32);
                _lowering.EmitAssign(idxSlot, nextIdxVal);
            });

        return _lowering.SlotRef(resultSlot);
    }

    CLeaf EmitShimPlural(CLeaf allComponents, CLeaf targetIdConst, CLeaf reflKeyConst, bool useTypeIds)
    {
        // Get array length (store to slot so it's not re-evaluated each iteration)
        var lenSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(lenSlot, _lowering.ExternCall(
            UdonAbiKey.Method("UnityEngineComponentArray", "get_Length", "SystemInt32"),
            new List<CLeaf> { allComponents }, StorageTypes.Int32));

        var zeroConst = _lowering.Const(0, StorageTypes.Int32);
        var oneConst = _lowering.Const(1, StorageTypes.Int32);

        // === Pass 1: Count matches ===
        var countSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(countSlot, zeroConst);
        var idx1Slot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(idx1Slot, zeroConst);

        // while (idx1 < len) — Func overload (re-evaluate each iteration); CLeaf would evaluate idx1<len once.
        _lowering.Builder.EmitWhile(
            () => _lowering.ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { _lowering.SlotRef(idx1Slot), _lowering.SlotRef(lenSlot) },
                StorageTypes.Boolean),
            b =>
            {
                EmitShimTypeCheckBody(allComponents, idx1Slot, reflKeyConst, targetIdConst, useTypeIds,
                    matchAction: () =>
                    {
                        // count++
                        var newCountVal = _lowering.ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf> { _lowering.SlotRef(countSlot), oneConst },
                            StorageTypes.Int32);
                        _lowering.EmitAssign(countSlot, newCountVal);
                    });

                // idx1++
                var nextIdx1Val = _lowering.ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { _lowering.SlotRef(idx1Slot), oneConst },
                    StorageTypes.Int32);
                _lowering.EmitAssign(idx1Slot, nextIdx1Val);
            });

        // === Allocate result array ===
        var resultArr = _lowering.ExternCall(
            UdonAbi.ArrayConstructor("UnityEngineComponentArray"),
            new List<CLeaf> { _lowering.SlotRef(countSlot) },
            StorageTypes.ComponentArray);

        // === Pass 2: Fill result array ===
        var idx2Slot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(idx2Slot, zeroConst);
        var writeIdxSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(writeIdxSlot, zeroConst);

        // while (idx2 < len) — Func overload (re-evaluate each iteration); CLeaf would evaluate idx2<len once.
        _lowering.Builder.EmitWhile(
            () => _lowering.ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { _lowering.SlotRef(idx2Slot), _lowering.SlotRef(lenSlot) },
                StorageTypes.Boolean),
            b =>
            {
                // element = allComponents[idx2]
                var elementVal = _lowering.ExternCall(
                    UdonAbi.ArrayGet("UnityEngineComponentArray", "UnityEngineComponent"),
                    new List<CLeaf> { allComponents, _lowering.SlotRef(idx2Slot) },
                    StorageTypes.Component);
                var behaviourVal = AsUdonBehaviour(elementVal);

                // Type check
                var idValueVal = _lowering.LoadProgramVariable(
                    behaviourVal, reflKeyConst, StorageTypes.Object);

                var nullConst = _lowering.Const(null, StorageTypes.Object);
                var notNullVal = _lowering.ExternCall(
                    UdonAbi.ObjectInequality,
                    new List<CLeaf> { idValueVal, nullConst },
                    StorageTypes.Boolean);

                _lowering.Builder.EmitIf(notNullVal, thenB =>
                {
                    var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

                    _lowering.Builder.EmitIf(matchVal, matchB =>
                    {
                        // result[writeIdx] = element
                        _lowering.EmitExternVoid(UdonAbi.ArraySet("UnityEngineComponentArray", "UnityEngineComponent"),
                            new List<CLeaf> { resultArr, _lowering.SlotRef(writeIdxSlot), elementVal });

                        // writeIdx++
                        var newWriteVal = _lowering.ExternCall(
                            UdonAbi.Int32Add,
                            new List<CLeaf> { _lowering.SlotRef(writeIdxSlot), oneConst },
                            StorageTypes.Int32);
                        _lowering.EmitAssign(writeIdxSlot, newWriteVal);
                    });
                });

                // idx2++
                var nextIdx2Val = _lowering.ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { _lowering.SlotRef(idx2Slot), oneConst },
                    StorageTypes.Int32);
                _lowering.EmitAssign(idx2Slot, nextIdx2Val);
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
            var indexResult = _lowering.ExternCall(
                UdonAbiKey.Method("SystemArray", "IndexOf", new[] { "SystemArray", "SystemObject" }, "SystemInt32"),
                new List<CLeaf> { idValueVal, targetIdConst },
                StorageTypes.Int32);

            var negOneConst = _lowering.Const(-1, StorageTypes.Int32);
            return _lowering.ExternCall(
                UdonAbiKey.Method("SystemInt32", "op_Inequality", new[] { "SystemInt32", "SystemInt32" }, "SystemBoolean"),
                new List<CLeaf> { indexResult, negOneConst },
                StorageTypes.Boolean);
        }
        else
        {
            // typeId = Convert.ToInt64(idValue)
            var typeIdVal = _lowering.ExternCall(
                UdonAbiKey.Method("SystemConvert", "ToInt64", new[] { "SystemObject" }, "SystemInt64"),
                new List<CLeaf> { idValueVal },
                StorageTypes.Int64);

            // typeId == targetId
            return _lowering.ExternCall(
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
        var elementVal = _lowering.ExternCall(
            UdonAbi.ArrayGet("UnityEngineComponentArray", "UnityEngineComponent"),
            new List<CLeaf> { allComponents, _lowering.SlotRef(idxSlot) },
            StorageTypes.Component);
        var behaviourVal = AsUdonBehaviour(elementVal);

        // idValue = behaviour.GetProgramVariable(reflKey)
        var idValueVal = _lowering.LoadProgramVariable(
            behaviourVal, reflKeyConst, StorageTypes.Object);

        // Null check
        var nullConst = _lowering.Const(null, StorageTypes.Object);
        var notNullVal = _lowering.ExternCall(
            UdonAbi.ObjectInequality,
            new List<CLeaf> { idValueVal, nullConst },
            StorageTypes.Boolean);

        _lowering.Builder.EmitIf(notNullVal, thenB =>
        {
            var matchVal = EmitShimTypeMatchExpr(idValueVal, targetIdConst, useTypeIds);

            _lowering.Builder.EmitIf(matchVal, matchB =>
            {
                matchAction();
            });
        });
    }

    CLeaf AsUdonBehaviour(CLeaf component)
        => _lowering.RepresentationCast(
            component,
            StorageTypes.UdonEventReceiver,
            RepresentationCastKind.VerifiedUdonBehaviourComponent);

    // ── Interface Call ──

    CLeaf EmitInterfaceCall(IInvocationOperation op, IMethodSymbol target)
    {
        // Use LayoutPlanner to get the interface's canonical naming
        var ifaceType = target.ContainingType as INamedTypeSymbol;
        MethodLayout ifaceMl = null;
        if (ifaceType != null)
        {
            var ifaceLayout = _lowering.Planner.GetLayout(ifaceType);
            ifaceLayout.Methods.TryGetValue(target, out ifaceMl);
        }
        if (ifaceMl == null)
            throw new System.InvalidOperationException(
                $"Cannot resolve interface method layout for '{target.ContainingType?.Name ?? "(unknown)"}.{target.Name}'.");

        _lowering.GuardInterfaceDispatchRepresentation(ifaceType, target.Name);

        var instanceVal = _lowering.VisitExpression(op.Instance);

        // Build param pairs for CCrossCall
        // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
        // Wave-9 round-3 [W4]: evaluate in textual order (C# semantics) but slot each value at its
        // PARAMETER ordinal — op.Arguments is call-site-ordered for named/reordered args, so indexing
        // ParamIds by textual position bound names positionally (VM-proven ref=54 vs 45). The pairs
        // (= SetProgramVariable stores of already-evaluated leaves) are emitted in ordinal order so a
        // named call is byte-identical to its declaration-order twin (positional calls unchanged:
        // textual order IS ordinal order).
        var paramPairs = _lowering.CrossCallArguments(op.Arguments, target, ifaceMl.ParamIds);

        // Wave-12 r2 [V1]: an interface dispatch whose local implementation is a recursion-cycle
        // edge from the current method re-enters this program when the receiver is `this` — flag
        // the SendCustomEvent as a spill site (R225 form: live locals after the call were clobbered).
        bool ifaceReentrant = _lowering.TryMarkReentrantCrossDispatch(op, target);

        // Build returns
        var ifaceReturns = ifaceMl.Returns.ToArray();
        // Tuple-returning interface methods dispatch directly (no bridge) under the interface's bare name.
        if (ifaceReturns.Length > 1)
            return _lowering.CrossCall(instanceVal, ifaceMl.ExportName, paramPairs, ifaceReturns, StorageTypes.Void, ifaceReentrant);

        // Non-tuple: dispatch the canonical interface-qualified bridge name (matches the emitted bridge export
        // and stays collision-free across overloads / multiple interfaces / explicit impls).
        var dispatchName = LayoutPlanner.InterfaceDispatchName(target, ifaceMl);
        var returnType = target.ReturnsVoid ? "SystemVoid" : _lowering.GetStorageTypeName(target.ReturnType);
        return _lowering.CrossCall(instanceVal, dispatchName, paramPairs,
            target.ReturnsVoid ? System.Array.Empty<ReturnSlot>() : ifaceReturns, new StorageType(returnType), ifaceReentrant);
    }

    // ── Cross-Class Call ──

    CLeaf EmitCrossClassCall(IInvocationOperation op, IMethodSymbol target)
    {
        var (exportName, paramIds, _) = _lowering.GetCalleeLayout(target);
        var instanceVal = _lowering.VisitExpression(op.Instance);

        // Build param pairs for CCrossCall — by parameter ordinal, textual evaluation order
        // (wave-9 round-3 [W4]: named/reordered args used to bind positionally on this path).
        var paramPairs = _lowering.CrossCallArguments(op.Arguments, target, paramIds);

        // Wave-12 r2 [V1]: a same-family variable-receiver dispatch on a recursion-cycle edge
        // re-enters this program when the receiver is `this` — flag the SendCustomEvent as a spill
        // site, with the param copy-ins inside the wrap (a self-recursive callee shares the
        // caller's param heap vars; VM-proven ref=36 vs 0 on the minimized field-receiver form).
        bool crossReentrant = _lowering.TryMarkReentrantCrossDispatch(op, target);

        // Build returns
        var callReturns = _lowering.GetCalleeReturns(target);
        if (callReturns.Length > 1)
            return _lowering.CrossCall(instanceVal, exportName, paramPairs, callReturns, StorageTypes.Void, crossReentrant);

        var returnType = target.ReturnsVoid ? "SystemVoid" : _lowering.GetStorageTypeName(target.ReturnType);
        return _lowering.CrossCall(instanceVal, exportName, paramPairs,
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
        bool recursiveEdge = _lowering.State.RecursionContext.IsCycleEdge(_lowering.CurrentMethod, target);
        List<ISymbol> refRoots = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            var p = arguments[i].Parameter ?? target.Parameters[i];
            if (p.RefKind != RefKind.Ref && p.RefKind != RefKind.Out) continue;
            var a = LoweringServices.UnwrapConversions(arguments[i].Value);
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
                        || (_lowering.CurrentMethod != null
                            && !SymbolEqualityComparer.Default.Equals(
                                _lowering.CurrentMethod.OriginalDefinition, target.OriginalDefinition)
                            && apr.Parameter.ContainingSymbol is IMethodSymbol argOwner
                            && SymbolEqualityComparer.Default.Equals(
                                argOwner.OriginalDefinition, _lowering.CurrentMethod.OriginalDefinition)
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
            var aliasedField = LoweringServices.TryGetThisRootedRefStorage(a);
            if (aliasedField != null && _lowering.State.RecursionContext.CalleeTouchesThisField(target, aliasedField))
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
            var root = LoweringServices.TryGetRefStorageRoot(a);
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
                && _lowering.State.Methods.TryGetClosureSpec(target, _lowering.State.ComposeClosureKeyArgs(target), out var refClosure))
                paramIds = refClosure.ParamVarIds;
            else
                paramIds = _lowering.MethodParamVarIds[target]; // loud (KeyNotFound) if unregistered
            var argTarget = arguments[i].Value;
            var paramId = paramIds[param.Ordinal + ordinalOffset];
            var paramType = _lowering.State.Storage.GetFieldType(paramId);
            var paramVal = _lowering.LoadField(paramId, paramType.Value);
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
        var target = LoweringServices.UnwrapConversions(arg.Value);
        switch (target)
        {
            // `out _` — the value is thrown away, so the store leg is a no-op. The read leg must
            // still produce a WELL-TYPED placeholder: an INTERNAL call stages it positionally into
            // the callee's param field (a null CValue is a CoreVerify ICE — M4 wave L1s_r2_c11), and
            // the callee overwrites an out param before any read, so a fresh scratch is sound.
            case IDiscardOperation discard:
                return (() => _lowering.SlotRef(_lowering.State.Builder.AllocScratch(_lowering.GetStorageType(discard.Type))), _ => { });
            // N-dim array element (design 2026-07-04 §2): lift the single-index exclusion below —
            // PrepareNdimRefOutArg evaluates every index once and caches the bounds/backing/flat-index
            // plan, mirroring the single-index arm's (arrayVal, indexVal) caching.
            case IArrayElementReferenceOperation ndimArrayElem when ndimArrayElem.Indices.Length > 1:
                return _lowering.PrepareNdimRefOutArg(ndimArrayElem);
            case IArrayElementReferenceOperation arrayElem
                when arrayElem.Indices.Length == 1
                     && arrayElem.Indices[0] is not IRangeOperation:
            {
                var arrayVal = _lowering.VisitExpression(arrayElem.ArrayReference);
                var arrSym = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = _lowering.GetArrayType(arrSym);
                // B40 follow-up: the RESOLVED int position (arr.Length - k for `^k`) is computed
                // ONCE here and closed over by both the read and store below — a side-effecting
                // `^Idx()` runs exactly once, matching every other ref/out leg in this method.
                var indexVal = _lowering.ResolveArrayIndex(arrayVal, arrayType, arrayElem.Indices[0]);
                var elementType = _lowering.GetArrayElemType(arrSym);
                return (() =>
                {
                    CLeaf elemVal = _lowering.ExternCall(UdonAbi.ArrayGet(arrayType, elementType),
                        new List<CLeaf> { arrayVal, indexVal }, _lowering.GetStorageType(arrayElem.Type));
                    if (arrayElem.Type is INamedTypeSymbol elemAgg && TypeClassifier.IsAggregateValue(elemAgg))
                        elemVal = AggregateAbi.DeepClone(_lowering.Builder, elemVal, elemAgg, _lowering.State.Aggregates.GetLayout);
                    return elemVal;
                }, v => _lowering.EmitExternVoid(
                    UdonAbi.ArraySet(arrayType, elementType),
                    new List<CLeaf> { arrayVal, indexVal, v }));
            }
            case IFieldReferenceOperation fieldRef
                when AggregateAbi.TryGetMemberTarget(fieldRef, out var aggInstance, out var aggMember)
                     && aggInstance.Type is INamedTypeSymbol aggContaining
                     && TypeClassifier.IsAggregateValue(aggContaining)
                     && _lowering.State.Aggregates.GetLayout(aggContaining).TryGetIndex(aggMember, out var memberIndex):
            {
                var arrExpr = _lowering.LoadInstanceRaw(aggInstance);
                return (() =>
                {
                    CLeaf memberVal = AggregateAbi.ReadSlot(_lowering.Builder, arrExpr, memberIndex, StorageTypes.Object);
                    if (fieldRef.Field.Type is INamedTypeSymbol memberAgg && TypeClassifier.IsAggregateValue(memberAgg))
                        memberVal = AggregateAbi.DeepClone(_lowering.Builder, memberVal, memberAgg, _lowering.State.Aggregates.GetLayout);
                    return memberVal;
                }, v => AggregateAbi.WriteSlot(_lowering.Builder, arrExpr, memberIndex, v));
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
                var instanceVal = _lowering.VisitExpression(behField.Instance);
                var instSlot = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(behField.Instance.Type));
                _lowering.EmitAssign(instSlot, instanceVal);
                var instRef = _lowering.SlotRef(instSlot);
                var fieldType = _lowering.GetStorageType(behField.Field.Type);
                return (() => _lowering.LoadProgramVariable(
                        instRef, behField.Field.Name, fieldType),
                    v => _lowering.StoreProgramVariable(
                        instRef, behField.Field.Name, fieldType, v));
            }
            // Captured local/parameter: no flat heap address (Stage 2 §4.1 — ResolveOutRefFieldName
            // returns null for these), so the direct-FieldAddr fast path above can't take them. A bare
            // variable reference has no side-effecting legs to double-evaluate; route through the same
            // env cell EnvEmit.Read/Write use elsewhere so this shape shares the ONE prepared mechanism
            // instead of a second read-then-AssignToLValue path.
            case ILocalReferenceOperation envLocalRef when _lowering.State.Closures.TryGetEnvBinding(envLocalRef.Local, out _):
            {
                var envType = _lowering.GetStorageTypeName(envLocalRef.Type);
                return (() => EnvEmit.Read(_lowering.Builder, _lowering.State, envLocalRef.Local, new StorageType(envType)),
                    v => EnvEmit.Write(_lowering.Builder, _lowering.State, envLocalRef.Local, v));
            }
            case IParameterReferenceOperation envParamRef when _lowering.State.Closures.TryGetEnvBinding(envParamRef.Parameter, out _):
            {
                var envType = _lowering.GetStorageTypeName(envParamRef.Type);
                return (() => EnvEmit.Read(_lowering.Builder, _lowering.State, envParamRef.Parameter, new StorageType(envType)),
                    v => EnvEmit.Write(_lowering.Builder, _lowering.State, envParamRef.Parameter, v));
            }
            // `out var x` declaring a captured local: same env-cell routing (the read side is never
            // invoked for an Out param, kept for symmetry with the Ref cases above).
            case IDeclarationExpressionOperation declExpr
                when declExpr.Expression is ILocalReferenceOperation declLocal
                     && _lowering.State.Closures.TryGetEnvBinding(declLocal.Local, out _):
            {
                var envType = _lowering.GetStorageTypeName(declLocal.Type);
                return (() => EnvEmit.Read(_lowering.Builder, _lowering.State, declLocal.Local, new StorageType(envType)),
                    v => EnvEmit.Write(_lowering.Builder, _lowering.State, declLocal.Local, v));
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
            return (_lowering.VisitExpression(argOp), null, null);
        bool defer = HasLaterEffectfulArg(arguments, i);
        if (TryPrepareRefOutArg(arguments[i]) is { } pre)
            return defer ? (null, pre.read, pre.store) : (pre.read(), null, pre.store);
        return defer
            ? ((CLeaf)null, () => _lowering.VisitExpression(argOp), (System.Action<CLeaf>)null)
            : (_lowering.VisitExpression(argOp), null, null);
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
        if (_lowering.State.Closures.CaptureScope != null && _lowering.State.Closures.CaptureScope.IsCapturingClosure(target.OriginalDefinition))
            args.Add(_lowering.ClosureEnvLeaf(target));

        // Under A-normal form EmitCallToMethod already materialized the call (a non-void call returns a CSlotRef
        // leaf, void returns null), so the call and its copy-in are sequenced before the copy-out below — no
        // manual re-sequencing of a lazy call is needed. The call SITE syntax rides along for the round-9
        // [Y3] per-site tail sparing on recursive edges.
        var result = _lowering.EmitCallToMethod(target, args, op.Syntax);

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
    void AssignToTarget(IOperation target, CLeaf value) => _lowering.AssignToLValue(target, value);

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
                if (_lowering.State.Closures.TryGetEnvBinding(localRef.Local, out _)) return null;
                return _lowering.LocalBindings.TryGetValue(localRef.Local, out var rb) ? rb.Id : null;
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                return _lowering.State.SourceStorageName(fieldRef.Field);
            case IParameterReferenceOperation paramRef:
                if (_lowering.State.Closures.TryGetEnvBinding(paramRef.Parameter, out _)) return null;
                return _lowering.GetParamVarId(paramRef.Parameter);
            case IDeclarationExpressionOperation declExpr:
                if (declExpr.Expression is ILocalReferenceOperation declLocal)
                {
                    if (_lowering.State.Closures.TryGetEnvBinding(declLocal.Local, out _)) return null;
                    var type = _lowering.GetStorageTypeName(declLocal.Type);
                    var localId = _lowering.State.Storage.DeclareLocal(declLocal.Local.Name, new StorageType(type));
                    _lowering.LocalBindings[declLocal.Local] = new LocalBinding(localId);
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
            && _lowering.TypeParamMap != null
            && instanceType is ITypeParameterSymbol tp
            && _lowering.TypeParamMap.TryGetValue(tp, out var concreteType))
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
        if (instanceType is ITypeParameterSymbol vtp && _lowering.TypeParamMap != null
            && _lowering.TypeParamMap.TryGetValue(vtp, out var vConcrete)
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
            containingTypeSym = _lowering.ResolveExternOwnerType(method.ContainingType, instanceType, method.Name);

        // Armor: a user-struct member reaching generic extern construction means no StructuredFunction was
        // registered for it (collector-scope drift) — fail with a diagnosis, not a bogus
        // SystemObjectArray.__<Name>__ extern (roadmap B46 family). An instance user-struct method is
        // pre-routed to EmitStructInstanceCall (InvocationHandler), so this catches static-on-struct and
        // any future uncovered call shape.
        _lowering.GuardUserStructMemberReachedExtern(containingTypeSym, method.Name);

        var containingType = _lowering.GetStorageTypeName(containingTypeSym);

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
                var tn = _lowering.GetStorageTypeName(t);
                if (p.RefKind != RefKind.None) tn += "Ref";
                return tn;
            }).ToArray();
            paramTypeOverride = nonGenericPts;
        }

        if (_lowering.State.Abi.TryBindMethod(
                method, containingType, type => _lowering.GetStorageTypeName(type),
                paramTypeOverride, out var bound))
            return bound;
        if (allowMissing)
            return null;
        return _lowering.State.Abi.BindMethod(
            method, containingType, type => _lowering.GetStorageTypeName(type), paramTypeOverride);
    }
}
