using System;
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
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (constants == null) throw new ArgumentNullException(nameof(constants));

        foreach (var (id, udonType, constValue) in constants)
        {
            if (!program.SymbolTable.TryGetAddressFromSymbol(id, out var addr))
                throw new InvalidOperationException(
                    $"Assembled program has no symbol for constant '{id}'.");
            if (udonType == "SystemType" && constValue is string udonTypeName)
            {
                var clrType = USugarTypeCacheManager.ResolveUdonType(udonTypeName)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve CLR type token '{udonTypeName}' "
                        + $"for constant '{id}'.");
                program.Heap.SetHeapVariable(addr, clrType, typeof(Type));
            }
            else
            {
                var value = constValue;
                var clrType = USugarTypeCacheManager.ResolveUdonType(udonType)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve heap type '{udonType}' for constant '{id}'.");
                if (value != null && !clrType.IsInstanceOfType(value))
                {
                    try
                    {
                        if (clrType.IsEnum)
                            value = Enum.ToObject(clrType, value);
                        else
                            value = Convert.ChangeType(value, clrType);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Constant '{id}' cannot be converted from "
                            + $"'{value.GetType().FullName}' to heap type "
                            + $"'{clrType.FullName}'.", ex);
                    }
                }
                program.Heap.SetHeapVariable(addr, value, clrType);
            }
        }
    }
}
