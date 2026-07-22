using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>Emits plain delegate bridges and variance signature adapters.</summary>
public sealed class DelegateBridgeEmitter
{
    readonly EmitContext _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly DelegateConventionStorage _convention;

    public DelegateBridgeEmitter(EmitContext context, SyntheticBridgeBuilder bridge,
        DelegateConventionStorage convention)
    {
        _context = context;
        _bridge = bridge;
        _convention = convention;
    }

    public void EmitPending()
    {
        EmitPlainBridges();
        EmitSignatureAdapters();
    }

    void EmitPlainBridges()
    {
        var emitted = new HashSet<string>();
        foreach (var (method, bridgeName, typeParameterMap) in _context.Synthetics.DelegateBridges)
        {
            if (!emitted.Add(bridgeName)) continue;
            if (!TryResolveTarget(method, bridgeName, out var target)) continue;
            DelegateAbi.ValidateNoRefOutParams(method);
            EmitBody(bridgeName, method, typeParameterMap, target, method);
        }
    }

    void EmitSignatureAdapters()
    {
        var emitted = new HashSet<string>();
        foreach (var (targetMethod, delegateInvoke, adapterName, typeParameterMap)
            in _context.Synthetics.SigAdapterBridges)
        {
            if (!emitted.Add(adapterName)) continue;
            if (!TryResolveTarget(targetMethod, adapterName, out var target)) continue;
            DelegateAbi.ValidateNoRefOutParams(targetMethod);
            EmitBody(adapterName, delegateInvoke, typeParameterMap, target, targetMethod);
        }
    }

    bool TryResolveTarget(IMethodSymbol method, string bridgeName, out CFunction target)
        => _context.Synthetics.ClosureBridgeFuncs.TryGetValue(bridgeName, out target)
            || _context.Methods.Functions.TryGetValue(method, out target);

    void EmitBody(string bridgeName, IMethodSymbol signatureMethod,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap,
        CFunction target, IMethodSymbol closureCheckMethod)
    {
        var builder = _context.Builder;
        var signaturePart = DelegateAbi.BuildSigPart(signatureMethod, typeParameterMap);
        var returnTypeName = _convention.Declare(signaturePart, signatureMethod, typeParameterMap);
        var targetReturnType = closureCheckMethod.ReturnsVoid
            ? StorageTypes.Void
            : ExternResolver.GetStorageType(
                new RuntimeType(closureCheckMethod.ReturnType), typeParameterMap);

        var bridgeFunction = _context.Module.AddFunction(bridgeName, bridgeName);
        var previousFunction = builder.CurrentFunction;
        builder.SetFunction(bridgeFunction);

        var arguments = new List<CLeaf>();
        for (int i = 0; i < signatureMethod.Parameters.Length; i++)
            arguments.Add(_bridge.Load(DelegateAbi.ConvArgName(signaturePart, i),
                ExternResolver.GetStorageType(
                    new RuntimeType(signatureMethod.Parameters[i].Type), typeParameterMap)));

        var conventionReturn = returnTypeName != null ? DelegateAbi.ConvRetName(signaturePart) : null;
        void EmitCall()
        {
            var result = builder.InternalCall(target.Name, arguments, targetReturnType);
            if (conventionReturn != null) _bridge.Store(conventionReturn, result);
            else builder.EmitExprStmt(result);
        }

        if (_context.Closures.CaptureScope != null
            && _context.Closures.CaptureScope.IsCapturingClosure(closureCheckMethod))
            EmitGuardedClosureCall(signaturePart, closureCheckMethod, arguments, conventionReturn,
                returnTypeName, EmitCall);
        else
            EmitCall();

        builder.EmitReturn();
        if (previousFunction != null) builder.SetFunction(previousFunction);
    }

    void EmitGuardedClosureCall(string signaturePart, IMethodSymbol closureMethod,
        List<CLeaf> arguments, string conventionReturn, string returnTypeName, System.Action emitCall)
    {
        var builder = _context.Builder;
        var environmentName = DelegateAbi.ConvEnvName(signaturePart);
        var environmentType = new StorageType(EnvEmit.EnvType);
        _context.Storage.TryDeclareVar(environmentName, environmentType);
        var environment = _bridge.Load(environmentName, environmentType);
        arguments.Add(environment);

        var environmentPresent = _bridge.CallExtern(StorageTypes.Boolean,
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            environment, builder.Const(null, StorageTypes.Object));
        builder.EmitIf(environmentPresent,
            _ =>
            {
                var kind = _bridge.CallExtern(StorageTypes.String,
                    ExternResolver.BuildArrayGetSignature("SystemObjectArray", "SystemObject"),
                    environment, builder.Const(EnvAbi.Kind, StorageTypes.Int32));
                var kindValid = _bridge.CallExtern(StorageTypes.Boolean,
                    "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
                    kind, builder.Const(EnvAbi.KindTag, StorageTypes.String));
                builder.EmitIf(kindValid, _ => emitCall(),
                    _ => EmitInvalidEnvironment(closureMethod, conventionReturn, returnTypeName));
            },
            _ => EmitMissingEnvironment(closureMethod, conventionReturn, returnTypeName));
    }

    void EmitInvalidEnvironment(IMethodSymbol method, string conventionReturn, string returnTypeName)
    {
        EmitEnvironmentError(
            $"USugar: invalid closure environment \u2014 invoked a captured delegate with a non-env payload ({method.Name})",
            conventionReturn, returnTypeName);
    }

    void EmitMissingEnvironment(IMethodSymbol method, string conventionReturn, string returnTypeName)
    {
        EmitEnvironmentError(
            $"USugar: missing closure environment \u2014 invoked a captured delegate whose bundle carries no env ({method.Name})",
            conventionReturn, returnTypeName);
    }

    void EmitEnvironmentError(string message, string conventionReturn, string returnTypeName)
    {
        _bridge.CallExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
            _context.Builder.Const(message, StorageTypes.String));
        if (conventionReturn != null)
            _bridge.Store(conventionReturn,
                InvocationHandler.DefaultConst(_context.Builder, returnTypeName));
    }
}
