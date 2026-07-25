using System.Collections.Generic;
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
                "multidimensional-array-shape",
                new IntrinsicKey(
                    new[] { "System.Array" },
                    new[] { "GetLength", "GetUpperBound" },
                    genericArity: 0,
                    minimumParameters: 1, maximumParameters: 1,
                    constrainedOrdinal: 0, constrainedTypeName: "System.Int32"),
                (handler, operation, target) =>
                    handler.Intrinsics.EmitNdimArrayIntrinsic(operation, target),
                (handler, operation, target) =>
                    operation.Instance != null
                    && NdimArrayAbi.IsNdimArray(operation.Instance.Type)),
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
        var bundle = _lowering.VisitExpression(operation.Instance);
        if (!NdimArrayAbi.TryGetMethod(target.Name, out var methodKind))
            throw new System.InvalidOperationException(
                $"Intrinsic registry admitted unknown N-dim array method '{target.Name}'.");
        var dimension = _lowering.VisitExpression(operation.Arguments[0].Value);
        return methodKind switch
        {
            NdimArrayAbi.MethodKind.GetLength => _lowering.Ndim.EmitNdimGetLength(bundle, dimension),
            NdimArrayAbi.MethodKind.GetUpperBound => _lowering.Ndim.EmitNdimGetUpperBound(bundle, dimension),
            _ => throw new System.InvalidOperationException(
                $"Unknown N-dim array method kind: {methodKind}"),
        };
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
