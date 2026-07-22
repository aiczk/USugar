using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

internal sealed class CallableParameterPlan
{
    public readonly Func<int, string> Id;
    public readonly StorageType Type;

    public CallableParameterPlan(Func<int, string> id, StorageType type)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Type = type;
    }
}

internal sealed class CallableReturnPlan
{
    public readonly Func<int, string> Id;
    public readonly StorageType Type;

    public CallableReturnPlan(Func<int, string> id, StorageType type)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Type = type;
    }
}

/// <summary>Complete function/storage ABI for one named callable registration.</summary>
internal sealed class CallableLayoutPlan
{
    public IMethodSymbol Method;
    public Func<int, string> FunctionName;
    public string ExportName;
    public Func<int, string> SlotPrefix = index => index.ToString();
    public Func<int, string> ReceiverId;
    public MethodContext.ReceiverAbi Receiver;
    public IReadOnlyList<CallableParameterPlan> Parameters = Array.Empty<CallableParameterPlan>();
    public IReadOnlyList<CallableReturnPlan> Returns = Array.Empty<CallableReturnPlan>();
    public MethodLayout Layout;
    public ImmutableArray<ITypeSymbol> ClosureKeyArgs;
    public ImmutableArray<IMethodSymbol> ClosureOwnerSpecs;
    public Func<int, string> EnvironmentId;

    public bool IsClosure => !ClosureKeyArgs.IsDefault;
}

/// <summary>Single authority for slot allocation, storage declaration, function ABI, and record creation.</summary>
internal sealed class CallableRegistrar
{
    readonly EmitContext _context;

    public CallableRegistrar(EmitContext context) => _context = context;

    public MethodContext.RegisteredCallable Register(CallableLayoutPlan plan)
    {
        if (plan?.Method == null || plan.FunctionName == null)
            throw new ArgumentException("A callable layout requires a method and function name.");
        var slot = _context.Methods.Reserve(plan.SlotPrefix);
        var index = slot.Index;
        var function = _context.Module.AddFunction(plan.FunctionName(index), plan.ExportName);

        if (plan.ReceiverId != null)
        {
            var receiverId = plan.ReceiverId(index);
            _context.Storage.DeclareVar(receiverId, StorageTypes.ObjectArray);
            function.ParamFieldNames.Add(receiverId);
        }

        var parameterIds = new string[plan.Parameters.Count + (plan.EnvironmentId != null ? 1 : 0)];
        for (var i = 0; i < plan.Parameters.Count; i++)
        {
            var parameter = plan.Parameters[i];
            var id = parameter.Id(index);
            _context.Storage.DeclareVar(id, parameter.Type);
            function.ParamFieldNames.Add(id);
            parameterIds[i] = id;
        }
        string environmentId = null;
        if (plan.EnvironmentId != null)
        {
            environmentId = plan.EnvironmentId(index);
            _context.Storage.DeclareVar(environmentId, new StorageType(EnvEmit.EnvType));
            function.ParamFieldNames.Add(environmentId);
            parameterIds[parameterIds.Length - 1] = environmentId;
        }

        var returns = new ReturnSlot[plan.Returns.Count];
        for (var i = 0; i < plan.Returns.Count; i++)
        {
            var result = plan.Returns[i];
            var slotId = result.Id(index);
            _context.Storage.DeclareVar(slotId, result.Type);
            returns[i] = new ReturnSlot(slotId, result.Type);
            function.ReturnSlots.Add(returns[i]);
        }
        function.ReturnType = returns.Length == 1 ? returns[0].StorageType
            : returns.Length > 1 ? StorageTypes.Void : function.ReturnType;

        if (plan.IsClosure)
            return _context.Methods.AddClosureCallable(plan.Method, plan.ClosureKeyArgs,
                plan.ClosureOwnerSpecs, function, slot, parameterIds, returns, environmentId);
        return _context.Methods.AddCallable(plan.Method, function, slot, parameterIds,
            returns, plan.Receiver, plan.Layout);
    }
}
