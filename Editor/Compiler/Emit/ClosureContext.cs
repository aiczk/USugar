using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns closure-scope analysis state and env-record lookup registries for one emitter.
/// </summary>
public sealed class ClosureContext
{
    public CaptureScopeAnalysis CaptureScope { get; private set; }

    public void SetCaptureScope(CaptureScopeAnalysis value)
    {
        if (CaptureScope != null)
            throw new InvalidOperationException("ClosureContext.CaptureScope is write-once (set once in Emit, never reassigned).");
        CaptureScope = value;
    }

    public bool TryGetEnvBinding(ISymbol symbol, out (CaptureScope Scope, int Slot) binding)
    {
        binding = default;
        if (CaptureScope == null || symbol == null) return false;
        if (CaptureScope.CapturedSlots.TryGetValue(symbol, out var direct))
        {
            binding = direct;
            return true;
        }
        if (symbol is IParameterSymbol p
            && p.ContainingSymbol is IMethodSymbol m
            && !ReferenceEquals(m, m.OriginalDefinition))
        {
            var defParams = m.OriginalDefinition.Parameters;
            if (p.Ordinal < defParams.Length
                && CaptureScope.CapturedSlots.TryGetValue(defParams[p.Ordinal], out var reKeyed))
            {
                binding = reKeyed;
                return true;
            }
        }
        return false;
    }

    // Stage 2 M2: (function, capture-bearing scope id) -> the scratch slot holding that scope's LIVE
    // env-record reference in that function's frame.
    public readonly Dictionary<(object Func, int ScopeId), int> ScopeEnvSlots = new();

    readonly Dictionary<IMethodSymbol, string> _envpParamFields = new(SymbolEqualityComparer.Default);

    public void RegisterEnvpField(IMethodSymbol closureKey, string envpFieldId)
        => _envpParamFields[closureKey] = envpFieldId;

    public bool TryGetEnvpField(IMethodSymbol closure, out string envpFieldId)
        => _envpParamFields.TryGetValue(closure, out envpFieldId)
           || _envpParamFields.TryGetValue(closure.OriginalDefinition, out envpFieldId);
}
