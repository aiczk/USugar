using System;
using System.Collections.Generic;

/// <summary>Emits exports that adapt interface layout fields to class implementations.</summary>
public sealed class InterfaceBridgeEmitter
{
    readonly EmitContext _context;
    readonly SyntheticBridgeBuilder _bridge;

    public InterfaceBridgeEmitter(EmitContext context, SyntheticBridgeBuilder bridge)
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
                implementation, BridgeReceiverKind.None, BridgeDispatchKind.Direct,
                interfaceLayout.ReturnId == null ? BridgeReturnKind.None : BridgeReturnKind.Field);
            _bridge.Emit(_context, plan, () =>
            {
            var arguments = new List<CLeaf>();
            for (int i = 0; i < interfaceMethod.Parameters.Length; i++)
                arguments.Add(_bridge.Load(interfaceLayout.ParamIds[i],
                    _context.ResolveStorageType(interfaceMethod.Parameters[i].Type)));

            var result = _bridge.CallInternal(implementation, arguments.ToArray());
            if (result != null && interfaceLayout.ReturnId != null
                && classLayout.ReturnId != null
                && interfaceLayout.ReturnId != classLayout.ReturnId)
                _bridge.Store(interfaceLayout.ReturnId, result);

            });
        }
    }
}
