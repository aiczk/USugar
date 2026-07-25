using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
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
                    handler.EmitInstantiateIntrinsic(operation, target)),
            new InvocationIntrinsicRule(
                "multidimensional-array-shape",
                new IntrinsicKey(
                    new[] { "System.Array" },
                    new[] { "GetLength", "GetUpperBound" },
                    genericArity: 0,
                    minimumParameters: 1, maximumParameters: 1,
                    constrainedOrdinal: 0, constrainedTypeName: "System.Int32"),
                (handler, operation, target) =>
                    handler.EmitNdimArrayIntrinsic(operation, target),
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
                    handler.EmitAggregateArrayCopyIntrinsic(operation, target),
                (handler, operation, target) =>
                    handler.IsAggregateArrayCopyIntrinsic(operation, target)),
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
                    handler.EmitGetComponentGeneric(operation, target)),
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
    bool TryEmitInvocationIntrinsic(IInvocationOperation operation,
        IMethodSymbol target, out CLeaf result)
        => InvocationIntrinsics.TryLower(this, operation, target, out result);

    CLeaf EmitNdimArrayIntrinsic(IInvocationOperation operation, IMethodSymbol target)
    {
        var bundle = VisitExpression(operation.Instance);
        if (!NdimArrayAbi.TryGetMethod(target.Name, out var methodKind))
            throw new System.InvalidOperationException(
                $"Intrinsic registry admitted unknown N-dim array method '{target.Name}'.");
        var dimension = VisitExpression(operation.Arguments[0].Value);
        return methodKind switch
        {
            NdimArrayAbi.MethodKind.GetLength => EmitNdimGetLength(bundle, dimension),
            NdimArrayAbi.MethodKind.GetUpperBound => EmitNdimGetUpperBound(bundle, dimension),
            _ => throw new System.InvalidOperationException(
                $"Unknown N-dim array method kind: {methodKind}"),
        };
    }

    bool IsAggregateArrayCopyIntrinsic(
        IInvocationOperation operation, IMethodSymbol target)
    {
        if (!target.IsStatic && operation.Instance != null
            && AggregateArrayElement(operation.Instance.Type) != null)
            return target.Name == "Clone" && target.Parameters.Length == 0
                   || target.Name == "CopyTo";
        if (!target.IsStatic
            || target.ContainingType?.SpecialType != SpecialType.System_Array
            || target.Name is not ("Copy" or "ConstrainedCopy"))
            return false;
        foreach (var argument in operation.Arguments)
            if (AggregateArrayElement(UnwrapConversions(argument.Value).Type) != null)
                return true;
        return false;
    }

    CLeaf EmitAggregateArrayCopyIntrinsic(
        IInvocationOperation operation, IMethodSymbol target)
    {
        if (TryEmitAggregateArrayCopyMember(operation, target, out var result))
            return result;
        throw new System.InvalidOperationException(
            $"Intrinsic registry admitted non-aggregate Array.{target.Name}.");
    }

    CLeaf EmitInstantiateIntrinsic(IInvocationOperation operation, IMethodSymbol target)
    {
        if (operation.Arguments.Length < 1 || operation.Arguments.Length > 4)
            throw new System.NotSupportedException(
                $"UnityEngine.Object.Instantiate overload '{target.ToDisplayString()}' is not supported.");

        // C# materializes all arguments left-to-right before entering the call.
        var arguments = new List<CLeaf>(operation.Arguments.Length);
        foreach (var argument in operation.Arguments)
            arguments.Add(VisitExpression(argument.Value));

        if (arguments[0].Type != StorageTypes.GameObject)
            throw new System.NotSupportedException(
                $"UnityEngine.Object.Instantiate can only instantiate a GameObject in Udon; "
                + $"'{target.Parameters[0].Type.ToDisplayString()}' lowers to '{arguments[0].Type}'.");

        var instantiated = ExternCall(
            _ctx.Abi.BindExact(InstantiateGameObjectExtern),
            new List<CLeaf> { arguments[0] },
            StorageTypes.GameObject);

        if (arguments.Count > 1)
        {
            var transform = ExternCall(
                _ctx.Abi.BindExact(GameObjectTransformGetter),
                new List<CLeaf> { instantiated },
                StorageTypes.Transform);

            if (arguments.Count == 2 && arguments[1].Type == StorageTypes.Transform)
                EmitSetParent(transform, arguments[1], Const(false, StorageTypes.Boolean));
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
                EmitSetParent(transform, arguments[3], Const(true, StorageTypes.Boolean));
            }
            else
                throw new System.NotSupportedException(
                    $"UnityEngine.Object.Instantiate overload '{target.ToDisplayString()}' "
                    + "has no Udon semantic lowering.");
        }

        var returnType = GetStorageType(target.ReturnType);
        if (returnType == StorageTypes.GameObject)
            return instantiated;
        var returnSlot = _builder.AllocScratch(returnType);
        EmitAssign(returnSlot, instantiated);
        return SlotRef(returnSlot);
    }

    void EmitSetParent(CLeaf transform, CLeaf parent, CLeaf worldPositionStays)
        => EmitExternVoid(
            _ctx.Abi.BindExact(TransformSetParent),
            new List<CLeaf> { transform, parent, worldPositionStays });

    void EmitSetPositionAndRotation(CLeaf transform, CLeaf position, CLeaf rotation)
        => EmitExternVoid(
            _ctx.Abi.BindExact(TransformSetPositionAndRotation),
            new List<CLeaf> { transform, position, rotation });
}
