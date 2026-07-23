using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
    const string InstantiateGameObjectExtern =
        "VRCInstantiate.__Instantiate__UnityEngineGameObject__UnityEngineGameObject";
    const string GameObjectTransformGetter =
        "UnityEngineGameObject.__get_transform__UnityEngineTransform";
    const string TransformSetParent =
        "UnityEngineTransform.__SetParent__UnityEngineTransform_SystemBoolean__SystemVoid";
    const string TransformSetPositionAndRotation =
        "UnityEngineTransform.__SetPositionAndRotation__UnityEngineVector3_UnityEngineQuaternion__SystemVoid";

    /// <summary>
    /// C# APIs whose Udon implementation is a sequence rather than one extern.
    /// They are intercepted before ordinary ABI binding.
    /// </summary>
    bool TryEmitInvocationIntrinsic(IInvocationOperation operation,
        IMethodSymbol target, out CLeaf result)
    {
        result = null;
        if (!IsUnityObjectInstantiate(target))
            return false;
        result = EmitInstantiateIntrinsic(operation, target);
        return true;
    }

    static bool IsUnityObjectInstantiate(IMethodSymbol method)
        => method != null
           && method.IsStatic
           && method.Name == "Instantiate"
           && method.ContainingType?.ToDisplayString(
               SymbolDisplayFormat.CSharpErrorMessageFormat) == "UnityEngine.Object";

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
