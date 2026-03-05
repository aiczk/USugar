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

    internal static void ApplyConstantValues(IUdonProgram program, VariableTable vars)
    {
        foreach (var entry in vars.GetConstEntries())
        {
            var addr = program.SymbolTable.GetAddressFromSymbol(entry.Id);
            if (entry.UdonType == "SystemType" && entry.ConstValue is string udonTypeName)
            {
                var clrType = USugarTypeCacheManager.ResolveUdonType(udonTypeName);
                if (clrType != null)
                    program.Heap.SetHeapVariable(addr, clrType, typeof(Type));
                else
                    USugarLog.Warn($"Could not resolve type: {udonTypeName}");
            }
            else
            {
                var value = entry.ConstValue;
                var valueType = value.GetType();
                var clrType = USugarTypeCacheManager.ResolveUdonType(entry.UdonType);
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
                            $"[USugar] Constant conversion failed for '{entry.Id}': "
                          + $"cannot convert {valueType.Name} to {clrType.Name}. {ex.Message}");
                    }
                }
                program.Heap.SetHeapVariable(addr, value, valueType);
            }
        }
    }
}
