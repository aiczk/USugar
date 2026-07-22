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
        var function = context.Module.AddFunction(plan.FunctionName, plan.ExportName);
        context.Methods.AddSyntheticCallable(plan.FunctionName, function, plan.SignatureMethod,
            plan.TargetMethod, MethodContext.CallableKind.Bridge);
        _builder.SetFunction(function);
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

    public CLeaf Dispatch(BridgePlan plan, List<CLeaf> arguments,
        Func<CLeaf> runtime = null, Func<CLeaf> delegatePayload = null)
    {
        switch (plan.Dispatch.Kind)
        {
            case BridgeDispatchKind.Direct:
            {
                var call = _builder.InternalCall(plan.Dispatch.DirectTarget.Name,
                    arguments, plan.Dispatch.ReturnType);
                if (plan.Dispatch.ReturnType != StorageTypes.Void) return call;
                _builder.EmitExprStmt(call);
                return null;
            }
            case BridgeDispatchKind.Runtime:
                return runtime != null
                    ? runtime()
                    : throw new InvalidOperationException("A runtime bridge requires a dispatch emitter.");
            case BridgeDispatchKind.DelegatePayload:
                return delegatePayload != null
                    ? delegatePayload()
                    : throw new InvalidOperationException("A delegate-payload bridge requires a dispatch emitter.");
            default:
                throw new InvalidOperationException($"Unknown bridge dispatch kind: {plan.Dispatch.Kind}.");
        }
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
