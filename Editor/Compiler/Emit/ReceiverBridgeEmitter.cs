using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits method-group bridges whose receiver is staged through delegate environment storage.</summary>
public sealed class ReceiverBridgeEmitter
{
    readonly LoweringState _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _convention;
    readonly SyntheticDemandPlan _demands;

    internal ReceiverBridgeEmitter(LoweringState context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention, SyntheticDemandPlan demands)
    {
        _context = context;
        _bridge = bridge;
        _convention = convention;
        _demands = demands;
    }

    public void EmitPending()
    {
        foreach (var demand in _demands.ReceiverBridges)
        {
            var member = demand.Binding.TargetMethod;
            var bridgeName = demand.Binding.BridgeName;
            if (member.ContainingType?.TypeKind == TypeKind.Interface)
                EmitInterfaceBody(bridgeName, member);
            else
                EmitBody(bridgeName, member, _context.Methods.Functions[member]);
        }
    }

    void EmitInterfaceBody(string bridgeName, IMethodSymbol member)
    {
        var builder = _context.Builder;
        var signaturePart = DelegateAbi.BuildSigPart(
            member, _context.Types);
        var returnType = _convention.Declare(signaturePart, member, null);
        var targetReturnType = member.ReturnsVoid
            ? StorageTypes.Void
            : _context.ResolveStorageType(member.ReturnType);
        var argumentAdapters = BuildArgumentAdapters(member, signaturePart);
        var conventionReturn = returnType != null ? DelegateAbi.ConvRetName(signaturePart) : null;
        var returnAdapter = conventionReturn == null
            ? BridgeReturnAdapter.None
            : new BridgeReturnAdapter(BridgeReturnKind.Convention, conventionReturn);
        var plan = new BridgePlan(bridgeName, bridgeName, member,
            BridgeDispatchAdapter.Runtime(targetReturnType),
            argumentAdapters, returnAdapter, member);
        _bridge.Emit(_context, plan, () =>
        {
        var environmentName = DelegateAbi.ConvEnvName(signaturePart);
        var environmentType = new StorageType(EnvEmit.EnvType);
        var receiver = _bridge.Load(environmentName, environmentType);
        var arguments = _bridge.LoadArguments(plan);
        arguments.Insert(0, receiver);

        _bridge.Dispatch(plan, arguments, runtime: () =>
        {
            var targets = _context.Program
                .RequireSyntheticDispatch(
                    member.ContainingType, member).RuntimeTargets;
            var matched = builder.AllocScratch(StorageTypes.Boolean);
            builder.EmitAssign(matched, builder.Const(false, StorageTypes.Boolean));
            var typeObj = AggregateAbi.ReadSlot(builder, receiver, 0, StorageTypes.String);
            foreach (var target in targets)
            {
                var isTarget = _bridge.CallExtern(StorageTypes.Boolean,
                    UdonAbi.StringEquality,
                    typeObj, _bridge.Load(target.TypeObjVar, StorageTypes.String));
                builder.EmitIf(isTarget, _ =>
                {
                    builder.EmitAssign(matched, builder.Const(true, StorageTypes.Boolean));
                    var result = builder.InternalCall(_context.Methods.Functions[target.Impl].Name,
                        arguments, targetReturnType);
                    _bridge.CopyRefParameters(
                        _context, member,
                        _context.Methods.Functions[target.Impl]);
                    if (!_bridge.StoreReturn(plan, result)) builder.EmitExprStmt(result);
                });
            }
            var missing = _bridge.CallExtern(StorageTypes.Boolean,
                UdonAbi.BooleanNot, builder.SlotRef(matched));
            builder.EmitIf(missing, _ =>
            {
                _bridge.CallExternVoid(UdonAbi.DebugLogError,
                    builder.Const(
                        $"USugar: interface method-group receiver has no local implementation ({member.ContainingType.Name}.{member.Name})",
                        StorageTypes.String));
                if (conventionReturn != null && returnType != null)
                    _bridge.Store(conventionReturn, InvocationHandler.DefaultConst(builder, returnType.Value));
            });
            return null;
        });
        });
    }

    void EmitBody(string bridgeName, Microsoft.CodeAnalysis.IMethodSymbol member, FlatFunction targetFunction)
    {
        var builder = _context.Builder;
        var signaturePart = DelegateAbi.BuildSigPart(
            member, _context.Types);
        var returnType = _convention.Declare(signaturePart, member, null);
        var targetReturnType = member.ReturnsVoid
            ? StorageTypes.Void
            : _context.ResolveStorageType(member.ReturnType);

        var argumentAdapters = BuildArgumentAdapters(member, signaturePart);
        var conventionReturn = returnType != null ? DelegateAbi.ConvRetName(signaturePart) : null;
        var returnAdapter = conventionReturn == null
            ? BridgeReturnAdapter.None
            : new BridgeReturnAdapter(BridgeReturnKind.Convention, conventionReturn);
        var plan = new BridgePlan(bridgeName, bridgeName, member,
            BridgeDispatchAdapter.Direct(targetFunction, targetReturnType),
            argumentAdapters, returnAdapter, member);
        _bridge.Emit(_context, plan, () =>
        {
        var environmentName = DelegateAbi.ConvEnvName(signaturePart);
        var environmentType = new StorageType(EnvEmit.EnvType);
        var receiver = _bridge.Load(environmentName, environmentType);

        var arguments = _bridge.LoadArguments(plan);
        arguments.Insert(0, receiver);

        var receiverPresent = _bridge.CallExtern(StorageTypes.Boolean,
            UdonAbi.ObjectInequality,
            receiver, builder.Const(null, StorageTypes.Object));
        builder.EmitIf(receiverPresent,
            _ =>
            {
                var result = _bridge.Dispatch(plan, arguments);
                _bridge.CopyRefParameters(
                    _context, member, targetFunction);
                _bridge.StoreReturn(plan, result);
            },
            _ =>
            {
                _bridge.CallExternVoid(UdonAbi.DebugLogError,
                    builder.Const(
                        $"USugar: null receiver \u2014 invoked a method-group delegate whose receiver is null ({member.ContainingType.Name}.{member.Name})",
                        StorageTypes.String));
                if (conventionReturn != null && returnType != null)
                    _bridge.Store(conventionReturn,
                        InvocationHandler.DefaultConst(builder, returnType.Value));
            });

        });
    }

    List<BridgeArgumentAdapter> BuildArgumentAdapters(IMethodSymbol method, string signaturePart)
    {
        var adapters = new List<BridgeArgumentAdapter>();
        for (int i = 0; i < method.Parameters.Length; i++)
            adapters.Add(new BridgeArgumentAdapter(DelegateAbi.ConvArgName(signaturePart, i),
                _context.ResolveStorageType(method.Parameters[i].Type)));
        return adapters;
    }
}
