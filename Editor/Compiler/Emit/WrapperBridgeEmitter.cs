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
        var returnTypeName = _convention.Declare(
            outerSignature, outerInvoke, typeParameterMap, out var argumentTypes);
        _context.Storage.TryDeclareVar(
            DelegateAbi.ConvEnvName(outerSignature), new StorageType(EnvEmit.EnvType));

        var wrapperFunction = _context.Module.AddFunction(name, name);
        var previousFunction = builder.CurrentFunction;
        builder.SetFunction(wrapperFunction);

        var innerSlot = builder.AllocScratch(StorageTypes.ObjectArray);
        builder.EmitAssign(innerSlot,
            _bridge.Load(DelegateAbi.ConvEnvName(outerSignature), StorageTypes.ObjectArray));

        var argumentSlots = new int[outerInvoke.Parameters.Length];
        var arguments = new CLeaf[argumentSlots.Length];
        for (int i = 0; i < argumentSlots.Length; i++)
        {
            argumentSlots[i] = builder.AllocScratch(argumentTypes[i]);
            builder.EmitAssign(argumentSlots[i],
                _bridge.Load(DelegateAbi.ConvArgName(outerSignature, i), argumentTypes[i]));
            arguments[i] = builder.SlotRef(argumentSlots[i]);
        }

        var innerSignature = DelegateAbi.BuildSigPart(innerInvoke, typeParameterMap);
        _convention.Declare(innerSignature, innerInvoke, typeParameterMap);
        _context.Storage.TryDeclareVar(
            DelegateAbi.ConvEnvName(innerSignature), new StorageType(EnvEmit.EnvType));

        var result = new InvocationHandler(_context).EmitFanoutElementDispatch(
            builder.SlotRef(innerSlot), innerInvoke, typeParameterMap, arguments);
        if (returnTypeName != null && result != null)
            _bridge.Store(DelegateAbi.ConvRetName(outerSignature), result);

        builder.EmitReturn();
        if (previousFunction != null) builder.SetFunction(previousFunction);
    }
}
