using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits plain delegate bridges and variance signature adapters.</summary>
public sealed class DelegateBridgeEmitter
{
    readonly LoweringState _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _convention;
    readonly SyntheticDemandPlan _demands;

    internal DelegateBridgeEmitter(LoweringState context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention, SyntheticDemandPlan demands)
    {
        _context = context;
        _bridge = bridge;
        _convention = convention;
        _demands = demands;
    }

    public void EmitPending()
    {
        EmitPlainBridges();
        EmitSignatureAdapters();
    }

    public void EmitLayoutBridges()
    {
        var classLayout = _context.Planner.GetLayout(_context.ClassSymbol);
        foreach (var (method, bridge) in classLayout.DelegateBridges)
        {
            if (!_context.Methods.Functions.TryGetValue(method, out var target)) continue;
            EmitBody(bridge.BridgeExportName, method, null, target, method);
        }

        foreach (var (interfaceMethod, interfaceLayout, implementationMethod, _)
            in _context.Planner.ComputeBridges(_context.ClassSymbol))
        {
            if (interfaceMethod.MethodKind != MethodKind.Ordinary
                || implementationMethod == null
                || !_context.Methods.Functions.TryGetValue(implementationMethod, out var target))
                continue;
            var bridgeName = DelegateAbi.BridgeName(
                LayoutPlanBuilder.InterfaceDispatchName(interfaceMethod, interfaceLayout));
            EmitBody(bridgeName, interfaceMethod, null, target, implementationMethod);
        }
    }

    void EmitPlainBridges()
    {
        foreach (var demand in _demands.DelegateBridges)
        {
            var method = demand.Binding.TargetMethod;
            var bridgeName = demand.Binding.BridgeName;
            if (!TryResolveTarget(method, bridgeName, out var target)) continue;
            DelegateAbi.ValidateNoRefOutParams(method);
            EmitBody(bridgeName, demand.SignatureMethod, demand.TypeParameterMap, target, method);
        }
    }

    void EmitSignatureAdapters()
    {
        foreach (var demand in _demands.SignatureAdapterBridges)
        {
            var targetMethod = demand.Binding.TargetMethod;
            var adapterName = demand.Binding.BridgeName;
            if (!TryResolveTarget(targetMethod, adapterName, out var target)) continue;
            DelegateAbi.ValidateNoRefOutParams(targetMethod);
            EmitBody(adapterName, demand.SignatureMethod, demand.TypeParameterMap, target, targetMethod);
        }
    }

    bool TryResolveTarget(IMethodSymbol method, string bridgeName, out StructuredFunction target)
        => _demands.TryGetClosureBridge(bridgeName, out target)
            || _context.Methods.Functions.TryGetValue(method, out target);

    void EmitBody(string bridgeName, IMethodSymbol signatureMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        StructuredFunction target, IMethodSymbol closureCheckMethod)
    {
        var builder = _context.Builder;
        var signaturePart = DelegateAbi.BuildSigPart(
            signatureMethod, _context.Types, typeParameterMap);
        var returnType = _convention.Declare(signaturePart, signatureMethod, typeParameterMap);
        var targetReturnType = closureCheckMethod.ReturnsVoid
            ? StorageTypes.Void
            : _context.ResolveStorageType(closureCheckMethod.ReturnType, typeParameterMap);

        var conventionReturn = returnType != null ? DelegateAbi.ConvRetName(signaturePart) : null;
        var capturing = _context.Captures != null
            && _context.Captures.IsCapturingClosure(closureCheckMethod);
        var argumentAdapters = new List<BridgeArgumentAdapter>();
        for (int i = 0; i < signatureMethod.Parameters.Length; i++)
            argumentAdapters.Add(new BridgeArgumentAdapter(
                DelegateAbi.ConvArgName(signaturePart, i),
                _context.ResolveStorageType(signatureMethod.Parameters[i].Type, typeParameterMap)));
        var returnAdapter = conventionReturn == null
            ? BridgeReturnAdapter.None
            : new BridgeReturnAdapter(BridgeReturnKind.Convention, conventionReturn);
        var plan = new BridgePlan(bridgeName, bridgeName, signatureMethod,
            BridgeDispatchAdapter.Direct(target, targetReturnType), argumentAdapters, returnAdapter,
            closureCheckMethod);
        _bridge.Emit(_context, plan, () =>
        {
            var arguments = _bridge.LoadArguments(plan);

            void EmitCall()
            {
                var result = _bridge.Dispatch(plan, arguments);
                _bridge.StoreReturn(plan, result);
            }

            if (capturing)
                EmitGuardedClosureCall(signaturePart, closureCheckMethod, arguments, conventionReturn,
                    returnType, EmitCall);
            else
                EmitCall();
        });
    }

    void EmitGuardedClosureCall(string signaturePart, IMethodSymbol closureMethod,
        List<CLeaf> arguments, string conventionReturn, StorageType? returnType, System.Action emitCall)
    {
        var builder = _context.Builder;
        var environmentName = DelegateAbi.ConvEnvName(signaturePart);
        var environmentType = new StorageType(EnvEmit.EnvType);
        var environment = _bridge.Load(environmentName, environmentType);
        arguments.Add(environment);

        var environmentPresent = _bridge.CallExtern(StorageTypes.Boolean,
            UdonAbi.ObjectInequality,
            environment, builder.Const(null, StorageTypes.Object));
        builder.EmitIf(environmentPresent,
            _ =>
            {
                var kind = _bridge.CallExtern(StorageTypes.String,
                    UdonAbi.ArrayGet("SystemObjectArray", "SystemObject"),
                    environment, builder.Const(EnvAbi.Kind, StorageTypes.Int32));
                var kindValid = _bridge.CallExtern(StorageTypes.Boolean,
                    UdonAbi.StringEquality,
                    kind, builder.Const(EnvAbi.KindTag, StorageTypes.String));
                builder.EmitIf(kindValid, _ => emitCall(),
                    _ => EmitInvalidEnvironment(closureMethod, conventionReturn, returnType));
            },
            _ => EmitMissingEnvironment(closureMethod, conventionReturn, returnType));
    }

    void EmitInvalidEnvironment(IMethodSymbol method, string conventionReturn, StorageType? returnType)
    {
        EmitEnvironmentError(
            $"USugar: invalid closure environment \u2014 invoked a captured delegate with a non-env payload ({method.Name})",
            conventionReturn, returnType);
    }

    void EmitMissingEnvironment(IMethodSymbol method, string conventionReturn, StorageType? returnType)
    {
        EmitEnvironmentError(
            $"USugar: missing closure environment \u2014 invoked a captured delegate whose bundle carries no env ({method.Name})",
            conventionReturn, returnType);
    }

    void EmitEnvironmentError(string message, string conventionReturn, StorageType? returnType)
    {
        _bridge.CallExternVoid(UdonAbi.DebugLogError,
            _context.Builder.Const(message, StorageTypes.String));
        if (conventionReturn != null && returnType != null)
            _bridge.Store(conventionReturn,
                InvocationHandler.DefaultConst(_context.Builder, returnType.Value));
    }
}
