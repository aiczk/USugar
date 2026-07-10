using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns per-method emission bookkeeping for one class emission.
/// </summary>
public sealed class MethodContext
{
    public readonly Dictionary<IMethodSymbol, CFunction> Functions = new(SymbolEqualityComparer.Default);

    public readonly Dictionary<IMethodSymbol, EmitContext.MethodSlot> Slots = new(SymbolEqualityComparer.Default);

    public readonly Dictionary<IMethodSymbol, ReturnSlot[]> Returns = new(SymbolEqualityComparer.Default);

    public readonly Dictionary<IMethodSymbol, string[]> ParamVarIds = new(SymbolEqualityComparer.Default);

    public IMethodSymbol CurrentMethod;

    // When emitting a user-struct method/ctor, the receiver object[] param var id; otherwise null.
    // Makes this / this.field resolve to the receiver array instead of the Behaviour.
    public string CurrentStructReceiverParamId;

    public int NextMethodIndex;

    public readonly List<(IMethodSymbol Symbol, CFunction Func)> PendingLocalFunctions = new();

    public EmitContext.MethodSlot Register(IMethodSymbol method, Func<int, string> prefixFactory)
    {
        var idx = NextMethodIndex++;
        var slot = new EmitContext.MethodSlot(idx, prefixFactory(idx));
        Slots[method] = slot;
        return slot;
    }
}
