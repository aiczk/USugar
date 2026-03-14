using System;
using UnityEngine;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Assembles UASM text into IUdonProgram and applies constant values to the program heap.
/// </summary>
static class USugarConstantApplier
{
    internal static IUdonProgram AssembleUasm(string uasm, uint heapSize)
    {
        var method = USugarReflectionTargets.AssembleMethod;
        if (method == null)
        {
            USugarLog.Error("CompilerUdonInterface.Assemble not found");
            return null;
        }
        try
        {
            return method.Invoke(null, new object[] { uasm, heapSize }) as IUdonProgram;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            USugarLog.Error($"UASM assembly failed: {ex.InnerException?.Message ?? ex.Message}\n{ex.InnerException?.StackTrace ?? ex.StackTrace}");
            return null;
        }
    }

    internal static void ApplyConstantValues(IUdonProgram program,
        System.Collections.Generic.List<(string Id, string UdonType, object Value)> constants)
    {
        foreach (var (id, udonType, constValue) in constants)
        {
            var addr = program.SymbolTable.GetAddressFromSymbol(id);
            if (udonType == "SystemType" && constValue is string udonTypeName)
            {
                var clrType = USugarTypeCacheManager.ResolveUdonType(udonTypeName);
                if (clrType != null)
                    program.Heap.SetHeapVariable(addr, clrType, typeof(Type));
                else
                    USugarLog.Warn($"Could not resolve type: {udonTypeName}");
            }
            else
            {
                var value = constValue;
                var valueType = value.GetType();
                var clrType = USugarTypeCacheManager.ResolveUdonType(udonType);
                if (clrType != null && clrType != valueType)
                {
                    try
                    {
                        if (clrType.IsEnum)
                            value = Enum.ToObject(clrType, value);
                        else
                            value = Convert.ChangeType(value, clrType);
                        valueType = clrType;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[USugar] Constant conversion failed for '{id}': "
                          + $"cannot convert {valueType.Name} to {clrType.Name}. {ex.Message}");
                    }
                }
                program.Heap.SetHeapVariable(addr, value, valueType);
            }
        }
    }
}
