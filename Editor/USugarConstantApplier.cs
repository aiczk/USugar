using System;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Applies compiler-owned constant values to an assembled Udon program heap.
/// </summary>
static class USugarConstantApplier
{
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
                var clrType = USugarTypeCacheManager.ResolveClrTypeToken(
                    udonTypeName)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve CLR type token '{udonTypeName}' "
                        + $"for constant '{id}'.");
                program.Heap.SetHeapVariable(addr, clrType, typeof(Type));
            }
            else
            {
                var value = constValue;
                var clrType = USugarTypeCacheManager.ResolveClrTypeToken(
                    udonType)
                    ?? throw new InvalidOperationException(
                        $"Could not resolve heap type '{udonType}' for constant '{id}'.");
                if (value != null && !clrType.IsInstanceOfType(value))
                {
                    try
                    {
                        value = HeapConstantConverter.ConvertTo(value, clrType);
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
