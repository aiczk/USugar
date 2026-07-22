using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits variance wrappers that dispatch a delegate bundle stored as payload.</summary>
public sealed class WrapperBridgeEmitter
{
    readonly EmitContext _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _convention;

    public WrapperBridgeEmitter(EmitContext context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention)
    {
        _context = context;
        _bridge = bridge;
        _convention = convention;
    }

    public void EmitPending()
    {
        foreach (var (name, (outerInvoke, innerInvoke, typeParameterMap))
            in _context.Synthetics.WrapperSigs)
            Emit(name, outerInvoke, innerInvoke, typeParameterMap);
    }

    void Emit(string name, IMethodSymbol outerInvoke, IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        var builder = _context.Builder;
        var outerSignature = DelegateAbi.BuildSigPart(outerInvoke, typeParameterMap);
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
            BridgeReceiverKind.Payload, BridgeDispatchAdapter.DelegatePayload(
                returnType ?? StorageTypes.Void),
            argumentAdapters, returnAdapter);
        _bridge.Emit(_context, plan, () =>
        {
        var innerSlot = builder.AllocScratch(StorageTypes.ObjectArray);
        builder.EmitAssign(innerSlot,
            _bridge.Load(DelegateAbi.ConvEnvName(outerSignature), StorageTypes.ObjectArray));

        var arguments = _bridge.LoadArguments(plan).ToArray();

        var innerSignature = DelegateAbi.BuildSigPart(innerInvoke, typeParameterMap);
        _convention.Declare(innerSignature, innerInvoke, typeParameterMap);
        _context.Storage.TryDeclareVar(
            DelegateAbi.ConvEnvName(innerSignature), new StorageType(EnvEmit.EnvType));

        var result = _bridge.Dispatch(plan, new List<CLeaf>(arguments), delegatePayload: () =>
            new InvocationHandler(_context).EmitFanoutElementDispatch(
                builder.SlotRef(innerSlot), innerInvoke, typeParameterMap, arguments));
        _bridge.StoreReturn(plan, result);

        });
    }
}
