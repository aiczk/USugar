using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits variance wrappers that dispatch a delegate bundle stored as payload.</summary>
public sealed class WrapperBridgeEmitter
{
    readonly EmitContext _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _convention;
    readonly SyntheticDemandPlan _demands;

    internal WrapperBridgeEmitter(EmitContext context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention, SyntheticDemandPlan demands)
    {
        _context = context;
        _bridge = bridge;
        _convention = convention;
        _demands = demands;
    }

    public void EmitPending()
    {
        foreach (var demand in _demands.WrapperSignatures.Values)
            Emit(demand.Binding.BridgeName, demand.OuterInvoke, demand.InnerInvoke,
                demand.TypeParameterMap);
    }

    void Emit(string name, IMethodSymbol outerInvoke, IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        var builder = _context.Builder;
        var outerSignature = DelegateAbi.BuildSigPart(
            outerInvoke, _context.Session.Types, typeParameterMap);
        _context.Storage.EnsureRecursionStack();
        var returnType = _convention.Declare(
            outerSignature, outerInvoke, typeParameterMap, out var argumentTypes);
        _context.Storage.TryDeclareVar(
            DelegateAbi.ConvEnvName(outerSignature), new StorageType(EnvEmit.EnvType));

        var argumentAdapters = new List<BridgeArgumentAdapter>();
        for (int i = 0; i < outerInvoke.Parameters.Length; i++)
            argumentAdapters.Add(new BridgeArgumentAdapter(
                DelegateAbi.ConvArgName(outerSignature, i), argumentTypes[i], true));
        var returnAdapter = returnType == null
            ? BridgeReturnAdapter.None
            : new BridgeReturnAdapter(BridgeReturnKind.Convention,
                DelegateAbi.ConvRetName(outerSignature));
        var plan = new BridgePlan(name, name, outerInvoke,
            BridgeDispatchAdapter.DelegatePayload(
                returnType ?? StorageTypes.Void),
            argumentAdapters, returnAdapter);
        _bridge.Emit(_context, plan, () =>
        {
        var innerSlot = builder.AllocScratch(StorageTypes.ObjectArray);
        builder.EmitAssign(innerSlot,
            _bridge.Load(DelegateAbi.ConvEnvName(outerSignature), StorageTypes.ObjectArray));

        var arguments = _bridge.LoadArguments(plan).ToArray();

        var innerSignature = DelegateAbi.BuildSigPart(
            innerInvoke, _context.Session.Types, typeParameterMap);
        _convention.Declare(innerSignature, innerInvoke, typeParameterMap);
        _context.Storage.TryDeclareVar(
            DelegateAbi.ConvEnvName(innerSignature), new StorageType(EnvEmit.EnvType));

        var result = _bridge.Dispatch(plan, new List<CLeaf>(arguments), delegatePayload: () =>
            new InvocationHandler(new LoweringServices(_context)).EmitFanoutElementDispatch(
                builder.SlotRef(innerSlot), innerInvoke, typeParameterMap, arguments));
        _bridge.StoreReturn(plan, result);

        });
    }
}
