using System;
using System.Collections.Generic;

/// <summary>Emits exports that adapt interface layout fields to class implementations.</summary>
public sealed class InterfaceBridgeEmitter
{
    readonly LoweringState _context;
    readonly SyntheticBridgeBuilder _bridge;

    public InterfaceBridgeEmitter(LoweringState context, SyntheticBridgeBuilder bridge)
    {
        _context = context;
        _bridge = bridge;
    }

    public void Emit()
    {
        var builder = _context.Builder;
        foreach (var (interfaceMethod, interfaceLayout, implementationMethod, classLayout)
            in _context.Planner.ComputeBridges(_context.ClassSymbol))
        {
            for (int i = 0; i < interfaceMethod.Parameters.Length; i++)
            {
                if (interfaceLayout.ParamIds[i] == classLayout.ParamIds[i]) continue;
                _context.Storage.TryDeclareVar(interfaceLayout.ParamIds[i],
                    _context.ResolveStorageType(interfaceMethod.Parameters[i].Type));
            }

            if (interfaceLayout.ReturnId != null
                && interfaceLayout.ReturnId != classLayout.ReturnId)
                _context.Storage.TryDeclareVar(interfaceLayout.ReturnId,
                    _context.ResolveStorageType(interfaceMethod.ReturnType));

            var exportName = LayoutPlanner.InterfaceDispatchName(interfaceMethod, interfaceLayout);
            if (implementationMethod == null
                || !_context.Methods.Functions.TryGetValue(implementationMethod, out var implementation))
                throw new InvalidOperationException(
                    $"Interface bridge for '{interfaceLayout.ExportName}': "
                    + $"no function found for implementation of '{interfaceMethod.Name}'.");

            var plan = new BridgePlan($"__bridge_{exportName}", exportName, interfaceMethod,
                BridgeDispatchAdapter.Direct(implementation,
                    implementation.ReturnType ?? StorageTypes.Void),
                BuildArgumentAdapters(interfaceMethod, interfaceLayout),
                BuildReturnAdapter(interfaceLayout, classLayout), implementationMethod);
            _bridge.Emit(_context, plan, () =>
            {
            var arguments = _bridge.LoadArguments(plan);

            var result = _bridge.Dispatch(plan, arguments);
            _bridge.StoreReturn(plan, result);

            });
        }
    }

    List<BridgeArgumentAdapter> BuildArgumentAdapters(
        Microsoft.CodeAnalysis.IMethodSymbol method, MethodLayout layout)
    {
        var adapters = new List<BridgeArgumentAdapter>();
        for (int i = 0; i < method.Parameters.Length; i++)
            adapters.Add(new BridgeArgumentAdapter(layout.ParamIds[i],
                _context.ResolveStorageType(method.Parameters[i].Type)));
        return adapters;
    }

    static BridgeReturnAdapter BuildReturnAdapter(MethodLayout interfaceLayout, MethodLayout classLayout)
        => interfaceLayout.ReturnId != null
            && classLayout.ReturnId != null
            && interfaceLayout.ReturnId != classLayout.ReturnId
                ? new BridgeReturnAdapter(BridgeReturnKind.Field, interfaceLayout.ReturnId)
                : BridgeReturnAdapter.None;
}
