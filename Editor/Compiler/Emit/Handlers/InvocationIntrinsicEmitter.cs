using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Matches and emits the closed set of sequence-based invocation intrinsics.</summary>
internal sealed class InvocationIntrinsicEmitter
{
    readonly InvocationHandler _owner;
    LoweringServices _lowering => _owner.Lowering;

    internal InvocationIntrinsicEmitter(InvocationHandler owner)
        => _owner = owner ?? throw new System.ArgumentNullException(nameof(owner));

    static readonly InvocationIntrinsicRegistry InvocationIntrinsics
        = new(new[]
        {
            new InvocationIntrinsicRule(
                "unity-object-instantiate",
                new IntrinsicKey(
                    new[] { "UnityEngine.Object" },
                    new[] { "Instantiate" },
                    genericArity: -1,
                    minimumParameters: 1, maximumParameters: 4),
                (handler, operation, target) =>
                    handler.Intrinsics.EmitInstantiateIntrinsic(operation, target)),
            new InvocationIntrinsicRule(
                "multidimensional-array",
                new IntrinsicKey(
                    new[] { "System.Array" },
                    new[]
                    {
                        "Clone",
                        "CopyTo",
                        "Copy",
                        "ConstrainedCopy",
                        "GetValue",
                        "SetValue",
                        "GetLength",
                        "GetLongLength",
                        "GetLowerBound",
                        "GetUpperBound",
                    },
                    genericArity: 0,
                    minimumParameters: 0,
                    maximumParameters: 5),
                (handler, operation, target) =>
                    handler.Intrinsics.EmitNdimArrayIntrinsic(operation, target),
                (handler, operation, target) =>
                    operation.Instance != null
                    && NdimArrayAbi.IsNdimArray(operation.Instance.Type)
                    || operation.Instance == null
                    && target.Name is "Copy" or "ConstrainedCopy"
                    && operation.Arguments.Any(argument =>
                        NdimArrayAbi.IsNdimArray(
                            LoweringServices.UnwrapConversions(
                                argument.Value).Type))),
            new InvocationIntrinsicRule(
                "aggregate-array-value-copy",
                new IntrinsicKey(
                    new[] { "System.Array" },
                    new[] { "Clone", "CopyTo", "Copy", "ConstrainedCopy" },
                    genericArity: 0,
                    minimumParameters: 0, maximumParameters: 5),
                (handler, operation, target) =>
                    handler.Intrinsics.EmitAggregateArrayCopyIntrinsic(operation, target),
                (handler, operation, target) =>
                    handler.Intrinsics.IsAggregateArrayCopyIntrinsic(operation, target)),
            new InvocationIntrinsicRule(
                "generic-component-query",
                new IntrinsicKey(
                    new[] { "UnityEngine.Component", "UnityEngine.GameObject" },
                    new[]
                    {
                        "GetComponent",
                        "GetComponents",
                        "GetComponentInChildren",
                        "GetComponentsInChildren",
                        "GetComponentInParent",
                        "GetComponentsInParent",
                    },
                    genericArity: 1,
                    minimumParameters: 0, maximumParameters: 1,
                    constrainedOrdinal: 0, constrainedTypeName: "System.Boolean"),
                (handler, operation, target) =>
                    handler.Externs.EmitGetComponentGeneric(operation, target)),
        });

    static readonly UdonAbiKey InstantiateGameObjectExtern =
        UdonAbiKey.Method("VRCInstantiate", "Instantiate", new[] { "UnityEngineGameObject" }, "UnityEngineGameObject");
    static readonly UdonAbiKey GameObjectTransformGetter =
        UdonAbiKey.Method("UnityEngineGameObject", "get_transform", "UnityEngineTransform");
    static readonly UdonAbiKey TransformSetParent =
        UdonAbiKey.Method("UnityEngineTransform", "SetParent", new[] { "UnityEngineTransform", "SystemBoolean" }, "SystemVoid");
    static readonly UdonAbiKey TransformSetPositionAndRotation =
        UdonAbiKey.Method("UnityEngineTransform", "SetPositionAndRotation", new[] { "UnityEngineVector3", "UnityEngineQuaternion" }, "SystemVoid");

    /// <summary>
    /// C# APIs whose Udon implementation is a sequence rather than one extern.
    /// They are intercepted before ordinary ABI binding.
    /// </summary>
    internal bool TryEmitInvocationIntrinsic(IInvocationOperation operation,
        IMethodSymbol target, out CLeaf result)
        => InvocationIntrinsics.TryLower(_owner, operation, target, out result);

    internal CLeaf EmitNdimArrayIntrinsic(IInvocationOperation operation, IMethodSymbol target)
    {
        if (!NdimArrayAbi.TryGetMethod(target.Name, out var methodKind))
            throw new System.InvalidOperationException(
                $"Intrinsic registry admitted unknown N-dim array method '{target.Name}'.");
        if (methodKind is NdimArrayAbi.MethodKind.Copy
            or NdimArrayAbi.MethodKind.ConstrainedCopy)
            return EmitNdimStaticCopy(operation);

        var bundle = _lowering.VisitExpression(operation.Instance);
        var arrayType =
            (IArrayTypeSymbol)operation.Instance.Type;
        return methodKind switch
        {
            NdimArrayAbi.MethodKind.Clone =>
                _lowering.Ndim.EmitNdimClone(
                    bundle, arrayType),
            NdimArrayAbi.MethodKind.CopyTo =>
                EmitNdimCopyTo(
                    operation, bundle, arrayType),
            NdimArrayAbi.MethodKind.GetLength =>
                _lowering.Ndim.EmitNdimGetLength(
                    bundle,
                    _lowering.EmitArrayDimension(
                        operation.Arguments[0].Value),
                    arrayType),
            NdimArrayAbi.MethodKind.GetLongLength =>
                _lowering.Ndim.EmitNdimGetLongLength(
                    bundle,
                    _lowering.EmitArrayDimension(
                        operation.Arguments[0].Value),
                    arrayType),
            NdimArrayAbi.MethodKind.GetLowerBound =>
                _lowering.Ndim.EmitNdimGetLowerBound(
                    _lowering.EmitArrayDimension(
                        operation.Arguments[0].Value),
                    arrayType),
            NdimArrayAbi.MethodKind.GetUpperBound =>
                _lowering.Ndim.EmitNdimGetUpperBound(
                    bundle,
                    _lowering.EmitArrayDimension(
                        operation.Arguments[0].Value),
                    arrayType),
            NdimArrayAbi.MethodKind.GetValue =>
                EmitNdimGetValue(
                    operation, bundle,
                    (IArrayTypeSymbol)operation.Instance.Type),
            NdimArrayAbi.MethodKind.SetValue =>
                EmitNdimSetValue(
                    operation, bundle,
                    (IArrayTypeSymbol)operation.Instance.Type),
            _ => throw new System.InvalidOperationException(
                $"Unknown N-dim array method kind: {methodKind}"),
        };
    }

    CLeaf EmitNdimCopyTo(
        IInvocationOperation operation,
        CLeaf source,
        IArrayTypeSymbol sourceType)
    {
        var destinationOperation =
            LoweringServices.UnwrapConversions(
                operation.Arguments[0].Value);
        if (destinationOperation.Type
            is not IArrayTypeSymbol destinationType)
            throw new System.NotSupportedException(
                "Array.CopyTo requires a statically known "
                + "array destination.");
        var destination =
            _lowering.VisitExpression(
                destinationOperation);
        var destinationIndex =
            _lowering.EmitArrayDimension(
                operation.Arguments[1].Value);
        var length = _lowering.Ndim.EmitNdimLength(
            source, sourceType);
        _lowering.Ndim.EmitLinearCopy(
            source, sourceType,
            _lowering.Const(
                0, StorageTypes.Int32),
            destination, destinationType,
            destinationIndex, length);
        return null;
    }

    CLeaf EmitNdimStaticCopy(
        IInvocationOperation operation)
    {
        var sourceOperation =
            LoweringServices.UnwrapConversions(
                operation.Arguments[0].Value);
        var destinationOperation =
            LoweringServices.UnwrapConversions(
                operation.Arguments[
                    operation.Arguments.Length == 3
                        ? 1 : 2].Value);
        if (sourceOperation.Type
                is not IArrayTypeSymbol sourceType
            || destinationOperation.Type
                is not IArrayTypeSymbol destinationType)
            throw new System.NotSupportedException(
                "Array.Copy requires statically known "
                + "array operands.");

        var source =
            _lowering.VisitExpression(sourceOperation);
        CLeaf sourceIndex;
        CLeaf destinationIndex;
        CLeaf length;
        CLeaf destination;
        if (operation.Arguments.Length == 3)
        {
            destination =
                _lowering.VisitExpression(
                    destinationOperation);
            sourceIndex = _lowering.Const(
                0, StorageTypes.Int32);
            destinationIndex = _lowering.Const(
                0, StorageTypes.Int32);
            length = _lowering.EmitArrayDimension(
                operation.Arguments[2].Value);
        }
        else
        {
            sourceIndex = _lowering.EmitArrayDimension(
                operation.Arguments[1].Value);
            destination =
                _lowering.VisitExpression(
                    destinationOperation);
            destinationIndex =
                _lowering.EmitArrayDimension(
                    operation.Arguments[3].Value);
            length = _lowering.EmitArrayDimension(
                operation.Arguments[4].Value);
        }
        _lowering.Ndim.EmitLinearCopy(
            source, sourceType, sourceIndex,
            destination, destinationType,
            destinationIndex, length);
        return null;
    }

    CLeaf EmitNdimGetValue(
        IInvocationOperation operation,
        CLeaf bundle,
        IArrayTypeSymbol arrayType)
    {
        var indexes = EmitNdimIndexes(
            operation, 0, arrayType);
        return _lowering.Ndim.EmitNdimGetValue(
            bundle, indexes, arrayType);
    }

    CLeaf EmitNdimSetValue(
        IInvocationOperation operation,
        CLeaf bundle,
        IArrayTypeSymbol arrayType)
    {
        if (operation.Arguments.Length < 2)
            throw new System.NotSupportedException(
                "Array.SetValue requires a value and one index "
                + "per dimension.");
        var rawValue = LoweringServices.UnwrapConversions(
            operation.Arguments[0].Value);
        var value = _lowering.VisitExpression(rawValue);
        var destinationType = _lowering.GetStorageType(
            arrayType.ElementType);
        if (value.Type != destinationType)
            value = _lowering.Builder.RepresentationCast(
                value, destinationType,
                RepresentationCastKind.ArraySetValueUnbox);
        var indexes = EmitNdimIndexes(
            operation, 1, arrayType);
        _lowering.Ndim.EmitNdimSetValue(
            bundle, value, indexes, arrayType);
        return null;
    }

    List<CLeaf> EmitNdimIndexes(
        IInvocationOperation operation,
        int firstArgument,
        IArrayTypeSymbol arrayType)
    {
        var indexes = new List<CLeaf>();
        if (operation.Arguments.Length
                == firstArgument + 1
            && LoweringServices.UnwrapConversions(
                    operation.Arguments[firstArgument]
                        .Value)
                is { Type: IArrayTypeSymbol
                    {
                        Rank: 1
                    } indexArrayType }
                    indexArrayOperation)
        {
            var indexArray =
                _lowering.VisitExpression(
                    indexArrayOperation);
            var arrayStorage =
                _lowering.GetStorageType(
                    indexArrayType);
            var elementStorage =
                _lowering.GetStorageType(
                    indexArrayType.ElementType);
            for (var dimension = 0;
                 dimension < arrayType.Rank;
                 dimension++)
            {
                CLeaf index = _lowering.ExternCall(
                    UdonAbi.ArrayGet(
                        arrayStorage.Name,
                        elementStorage.Name),
                    new List<CLeaf>
                    {
                        indexArray,
                        _lowering.Const(
                            dimension,
                            StorageTypes.Int32)
                    },
                    elementStorage);
                if (elementStorage != StorageTypes.Int32)
                    index = _lowering.EmitNarrowingConvert(
                        index, elementStorage.Name,
                        StorageTypes.Int32.Name);
                indexes.Add(index);
            }
            return indexes;
        }

        for (var i = firstArgument;
             i < operation.Arguments.Length;
             i++)
            indexes.Add(_lowering.EmitArrayDimension(
                operation.Arguments[i].Value));
        return indexes;
    }

    internal bool IsAggregateArrayCopyIntrinsic(
        IInvocationOperation operation, IMethodSymbol target)
    {
        if (!target.IsStatic && operation.Instance != null
            && _owner.Externs.AggregateArrayElement(operation.Instance.Type) != null)
            return target.Name == "Clone" && target.Parameters.Length == 0
                   || target.Name == "CopyTo";
        if (!target.IsStatic
            || target.ContainingType?.SpecialType != SpecialType.System_Array
            || target.Name is not ("Copy" or "ConstrainedCopy"))
            return false;
        foreach (var argument in operation.Arguments)
            if (_owner.Externs.AggregateArrayElement(LoweringServices.UnwrapConversions(argument.Value).Type) != null)
                return true;
        return false;
    }

    internal CLeaf EmitAggregateArrayCopyIntrinsic(
        IInvocationOperation operation, IMethodSymbol target)
    {
        if (_owner.Externs.TryEmitAggregateArrayCopyMember(operation, target, out var result))
            return result;
        throw new System.InvalidOperationException(
            $"Intrinsic registry admitted non-aggregate Array.{target.Name}.");
    }

    internal CLeaf EmitInstantiateIntrinsic(IInvocationOperation operation, IMethodSymbol target)
    {
        if (operation.Arguments.Length < 1 || operation.Arguments.Length > 4)
            throw new System.NotSupportedException(
                $"UnityEngine.Object.Instantiate overload '{target.ToDisplayString()}' is not supported.");

        // C# materializes all arguments left-to-right before entering the call.
        var arguments = new List<CLeaf>(operation.Arguments.Length);
        foreach (var argument in operation.Arguments)
            arguments.Add(_lowering.VisitExpression(argument.Value));

        if (arguments[0].Type != StorageTypes.GameObject)
            throw new System.NotSupportedException(
                $"UnityEngine.Object.Instantiate can only instantiate a GameObject in Udon; "
                + $"'{target.Parameters[0].Type.ToDisplayString()}' lowers to '{arguments[0].Type}'.");

        var instantiated = _lowering.ExternCall(
            _lowering.State.BoundAbi.RequireExact(InstantiateGameObjectExtern),
            new List<CLeaf> { arguments[0] },
            StorageTypes.GameObject);

        if (arguments.Count > 1)
        {
            var transform = _lowering.ExternCall(
                _lowering.State.BoundAbi.RequireExact(GameObjectTransformGetter),
                new List<CLeaf> { instantiated },
                StorageTypes.Transform);

            if (arguments.Count == 2 && arguments[1].Type == StorageTypes.Transform)
                EmitSetParent(transform, arguments[1], _lowering.Const(false, StorageTypes.Boolean));
            else if (arguments.Count == 3
                     && arguments[1].Type.Name == "UnityEngineVector3"
                     && arguments[2].Type.Name == "UnityEngineQuaternion")
                EmitSetPositionAndRotation(transform, arguments[1], arguments[2]);
            else if (arguments.Count == 3
                     && arguments[1].Type == StorageTypes.Transform
                     && arguments[2].Type == StorageTypes.Boolean)
                EmitSetParent(transform, arguments[1], arguments[2]);
            else if (arguments.Count == 4
                     && arguments[1].Type.Name == "UnityEngineVector3"
                     && arguments[2].Type.Name == "UnityEngineQuaternion"
                     && arguments[3].Type == StorageTypes.Transform)
            {
                EmitSetPositionAndRotation(transform, arguments[1], arguments[2]);
                EmitSetParent(transform, arguments[3], _lowering.Const(true, StorageTypes.Boolean));
            }
            else
                throw new System.NotSupportedException(
                    $"UnityEngine.Object.Instantiate overload '{target.ToDisplayString()}' "
                    + "has no Udon semantic lowering.");
        }

        var returnType = _lowering.GetStorageType(target.ReturnType);
        if (returnType == StorageTypes.GameObject)
            return instantiated;
        var returnSlot = _lowering.Builder.AllocScratch(returnType);
        _lowering.EmitAssign(returnSlot, instantiated);
        return _lowering.SlotRef(returnSlot);
    }

    void EmitSetParent(CLeaf transform, CLeaf parent, CLeaf worldPositionStays)
        => _lowering.EmitExternVoid(
            _lowering.State.BoundAbi.RequireExact(TransformSetParent),
            new List<CLeaf> { transform, parent, worldPositionStays });

    void EmitSetPositionAndRotation(CLeaf transform, CLeaf position, CLeaf rotation)
        => _lowering.EmitExternVoid(
            _lowering.State.BoundAbi.RequireExact(TransformSetPositionAndRotation),
            new List<CLeaf> { transform, position, rotation });
}
