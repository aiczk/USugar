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

    // Stage 2 M2 (design 4.1): resolve a symbol's env binding (owning scope, 1-based env slot).
    // Single source of truth is CaptureScope.CapturedSlots; this adds the generic-spec re-keying
    // (a constructed spec's IParameterSymbol never compares equal to the definition's). A symbol
    // that resolves here must NEVER get a flat LocalBindings field - every read/write routes
    // through the env record.
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

    // Hoisted closure method -> the param FIELD id of its hidden trailing __envp parameter.
    // KEYING DISCIPLINE (Stage 2 M5 gotcha-3, fixed in 5064f77: a definition key was last-spec-wins
    // and wired one generic spec's body to another spec's field - VM-proven wrong-value fault).
    // Intentionally MIXED-key: a capturing generic specialization pinned to per-instantiation
    // storage registers under its CONSTRUCTED symbol; a closure with only one instantiation
    // registers under its DEFINITION. Callers never touch this directly - RegisterEnvpField /
    // TryGetEnvpField encode the constructed-first / definition-fallback lookup in one place.
    readonly Dictionary<IMethodSymbol, string> _envpParamFields = new(SymbolEqualityComparer.Default);

    public void RegisterEnvpField(IMethodSymbol closureKey, string envpFieldId)
        => _envpParamFields[closureKey] = envpFieldId;

    public bool TryGetEnvpField(IMethodSymbol closure, out string envpFieldId)
        => _envpParamFields.TryGetValue(closure, out envpFieldId)
           || _envpParamFields.TryGetValue(closure.OriginalDefinition, out envpFieldId);
}
