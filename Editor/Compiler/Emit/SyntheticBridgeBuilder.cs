using System;
using System.Collections.Generic;

/// <summary>Core IR operations shared by synthetic delegate, receiver, and multicast bridges.</summary>
public sealed class SyntheticBridgeBuilder
{
    readonly CoreBuilder _builder;

    public SyntheticBridgeBuilder(CoreBuilder builder) => _builder = builder;

    public void Emit(EmitContext context, BridgePlan plan, Action body)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (body == null) throw new ArgumentNullException(nameof(body));
        var previousFunction = _builder.CurrentFunction;
        _builder.SetFunction(context.Module.AddFunction(plan.FunctionName, plan.ExportName));
        try
        {
            body();
            _builder.EmitReturn();
        }
        finally
        {
            if (previousFunction != null) _builder.SetFunction(previousFunction);
        }
    }

    public CLeaf Load(string fieldName, StorageType type) => _builder.LoadField(fieldName, type);
    public void Store(string fieldName, CLeaf value) => _builder.EmitStoreField(fieldName, value);

    public List<CLeaf> LoadArguments(BridgePlan plan)
    {
        var arguments = new List<CLeaf>(plan.Arguments.Count);
        foreach (var adapter in plan.Arguments)
        {
            var value = Load(adapter.SourceField, adapter.Type);
            if (adapter.Materialize)
            {
                var slot = _builder.AllocScratch(adapter.Type);
                _builder.EmitAssign(slot, value);
                value = _builder.SlotRef(slot);
            }
            arguments.Add(value);
        }
        return arguments;
    }

    public bool StoreReturn(BridgePlan plan, CLeaf value)
    {
        if (plan.Return.Kind == BridgeReturnKind.None || value == null) return false;
        Store(plan.Return.DestinationField, value);
        return true;
    }

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
