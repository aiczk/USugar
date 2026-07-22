using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns closure-scope analysis state and env-record lookup registries for one emitter.
/// </summary>
public sealed class ClosureContext
{
    public CaptureScopeAnalysis CaptureScope { get; private set; }
    public ClosureIdentityPlan IdentityPlan { get; private set; }

    public void SetIdentityPlan(ClosureIdentityPlan value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (IdentityPlan != null)
            throw new InvalidOperationException("ClosureContext.IdentityPlan is write-once.");
        IdentityPlan = value;
    }

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
        // Class receiver capture (design 2026-07-10 v2 §1.5): the receiver's synthetic key is the
        // member method's OriginalDefinition — re-key a constructed generic spec the same way params
        // are re-keyed below, so a caller holding the spec symbol resolves instead of loud-throwing.
        if (symbol is IMethodSymbol receiverMethod
            && CaptureScope.CapturedSlots.TryGetValue(receiverMethod.OriginalDefinition, out var receiverBinding))
        {
            binding = receiverBinding;
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

    // The former envp field map (RegisterEnvpField/TryGetEnvpField) is gone: every __envp id lives
    // on the per-spec ClosureSpec record (EnvpFieldId) since the per-spec separation, and the map
    // had become write-dead (2026-07-11 audit, two independent zero-caller confirmations).
}
