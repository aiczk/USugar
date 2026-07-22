using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits method-group bridges whose receiver is staged through delegate environment storage.</summary>
public sealed class ReceiverBridgeEmitter
{
    readonly EmitContext _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _convention;

    public ReceiverBridgeEmitter(EmitContext context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention)
    {
        _context = context;
        _bridge = bridge;
        _convention = convention;
    }

    public void EmitPending()
    {
        var emitted = new HashSet<string>();
        foreach (var (member, bridgeName) in _context.Synthetics.ReceiverBridges)
        {
            if (!emitted.Add(bridgeName)) continue;
            DelegateAbi.ValidateNoRefOutParams(member);
            if (member.ContainingType?.TypeKind == TypeKind.Interface)
                EmitInterfaceBody(bridgeName, member);
            else
                EmitBody(bridgeName, member, _context.Methods.Functions[member]);
        }
    }

    void EmitInterfaceBody(string bridgeName, IMethodSymbol member)
    {
        var builder = _context.Builder;
        var signaturePart = DelegateAbi.BuildSigPart(member, null);
        var returnType = _convention.Declare(signaturePart, member, null);
        var targetReturnType = member.ReturnsVoid
            ? StorageTypes.Void
            : ExternResolver.GetStorageType(new RuntimeType(member.ReturnType), _context.Generics.TypeParamMap);
        var bridgeFunction = _context.Module.AddFunction(bridgeName, bridgeName);
        var previousFunction = builder.CurrentFunction;
        builder.SetFunction(bridgeFunction);

        var environmentName = DelegateAbi.ConvEnvName(signaturePart);
        var environmentType = new StorageType(EnvEmit.EnvType);
        _context.Storage.TryDeclareVar(environmentName, environmentType);
        var receiver = _bridge.Load(environmentName, environmentType);
        var arguments = new List<CLeaf> { receiver };
        for (int i = 0; i < member.Parameters.Length; i++)
            arguments.Add(_bridge.Load(DelegateAbi.ConvArgName(signaturePart, i),
                ExternResolver.GetStorageType(new RuntimeType(member.Parameters[i].Type),
                    _context.Generics.TypeParamMap)));

        var conventionReturn = returnType != null ? DelegateAbi.ConvRetName(signaturePart) : null;
        var targets = _context.VirtualDispatch.ResolveInterfaceTargets(member.ContainingType, member);
        var matched = builder.AllocScratch(StorageTypes.Boolean);
        builder.EmitAssign(matched, builder.Const(false, StorageTypes.Boolean));
        var typeObj = AggregateAbi.ReadSlot(builder, receiver, 0, StorageTypes.String);
        foreach (var target in targets)
        {
            var isTarget = _bridge.CallExtern(StorageTypes.Boolean,
                "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
                typeObj, _bridge.Load(target.TypeObjVar, StorageTypes.String));
            builder.EmitIf(isTarget, _ =>
            {
                builder.EmitAssign(matched, builder.Const(true, StorageTypes.Boolean));
                var result = builder.InternalCall(_context.Methods.Functions[target.Impl].Name,
                    arguments, targetReturnType);
                if (conventionReturn != null) _bridge.Store(conventionReturn, result);
                else builder.EmitExprStmt(result);
            });
        }
        var missing = _bridge.CallExtern(StorageTypes.Boolean,
            "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean", builder.SlotRef(matched));
        builder.EmitIf(missing, _ =>
        {
            _bridge.CallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                builder.Const(
                    $"USugar: interface method-group receiver has no local implementation ({member.ContainingType.Name}.{member.Name})",
                    StorageTypes.String));
            if (conventionReturn != null && returnType != null)
                _bridge.Store(conventionReturn, InvocationHandler.DefaultConst(builder, returnType.Value));
        });
        builder.EmitReturn();
        if (previousFunction != null) builder.SetFunction(previousFunction);
    }

    void EmitBody(string bridgeName, Microsoft.CodeAnalysis.IMethodSymbol member, CFunction targetFunction)
    {
        var builder = _context.Builder;
        var signaturePart = DelegateAbi.BuildSigPart(member, null);
        var returnType = _convention.Declare(signaturePart, member, null);
        var targetReturnType = member.ReturnsVoid
            ? StorageTypes.Void
            : ExternResolver.GetStorageType(new RuntimeType(member.ReturnType), _context.Generics.TypeParamMap);

        var bridgeFunction = _context.Module.AddFunction(bridgeName, bridgeName);
        var previousFunction = builder.CurrentFunction;
        builder.SetFunction(bridgeFunction);

        var environmentName = DelegateAbi.ConvEnvName(signaturePart);
        var environmentType = new StorageType(EnvEmit.EnvType);
        _context.Storage.TryDeclareVar(environmentName, environmentType);
        var receiver = _bridge.Load(environmentName, environmentType);

        var arguments = new List<CLeaf> { receiver };
        for (int i = 0; i < member.Parameters.Length; i++)
            arguments.Add(_bridge.Load(DelegateAbi.ConvArgName(signaturePart, i),
                ExternResolver.GetStorageType(
                    new RuntimeType(member.Parameters[i].Type), _context.Generics.TypeParamMap)));

        var conventionReturn = returnType != null ? DelegateAbi.ConvRetName(signaturePart) : null;
        var receiverPresent = _bridge.CallExtern(StorageTypes.Boolean,
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            receiver, builder.Const(null, StorageTypes.Object));
        builder.EmitIf(receiverPresent,
            _ =>
            {
                var result = builder.InternalCall(targetFunction.Name, arguments, targetReturnType);
                if (conventionReturn != null) _bridge.Store(conventionReturn, result);
                else builder.EmitExprStmt(result);
            },
            _ =>
            {
                _bridge.CallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                    builder.Const(
                        $"USugar: null receiver \u2014 invoked a method-group delegate whose receiver is null ({member.ContainingType.Name}.{member.Name})",
                        StorageTypes.String));
                if (conventionReturn != null && returnType != null)
                    _bridge.Store(conventionReturn,
                        InvocationHandler.DefaultConst(builder, returnType.Value));
            });

        builder.EmitReturn();
        if (previousFunction != null) builder.SetFunction(previousFunction);
    }
}
