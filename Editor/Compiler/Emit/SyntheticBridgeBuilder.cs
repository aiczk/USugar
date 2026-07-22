using System.Collections.Generic;

/// <summary>Core IR operations shared by synthetic delegate, receiver, and multicast bridges.</summary>
public sealed class SyntheticBridgeBuilder
{
    readonly CoreBuilder _builder;

    public SyntheticBridgeBuilder(CoreBuilder builder) => _builder = builder;

    public CLeaf Load(string fieldName, StorageType type) => _builder.LoadField(fieldName, type);
    public void Store(string fieldName, CLeaf value) => _builder.EmitStoreField(fieldName, value);

    public CLeaf CallExtern(StorageType returnType, ExternSignature signature, params CLeaf[] args)
        => _builder.ExternCall(signature, new List<CLeaf>(args), returnType);

    public void CallExternVoid(ExternSignature signature, params CLeaf[] args)
        => _builder.EmitExternVoid(signature, new List<CLeaf>(args));

    public CLeaf CallInternal(CFunction function, params CLeaf[] args)
    {
        var returnType = function.ReturnType ?? StorageTypes.Void;
        var call = _builder.InternalCall(function.Name, new List<CLeaf>(args), returnType);
        if (returnType != StorageTypes.Void) return call;
        _builder.EmitExprStmt(call);
        return null;
    }

    public CLeaf ConstInt(int value) => _builder.Const(value, StorageTypes.Int32);
}
