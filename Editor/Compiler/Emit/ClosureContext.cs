using System.Collections.Generic;

/// <summary>
/// Mutable closure-frame slots produced during body emission. All closure
/// semantics live in BoundProgram; this state contains emitted IR addresses
/// only.
/// </summary>
public sealed class ClosureContext
{
    public readonly Dictionary<(object Func, int ScopeId), int>
        ScopeEnvSlots = new();
}
