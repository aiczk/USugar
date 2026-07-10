using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns transient control-flow stacks used while emitting statements.
/// </summary>
public sealed class ControlFlowContext
{
    // ?. receiver leaf. For a delegate-typed receiver this is the BUNDLE leaf itself (design 2.6) -
    // d?.Invoke() dispatches on it, and any delegate-valued expression is a legal receiver.
    public readonly Stack<CLeaf> ConditionalAccessStack = new();

    public readonly Stack<List<(CLeaf Val, ITypeSymbol Type)>> UsingDisposableStack = new();

    // Using-stack depths at loop/switch entry - limits Dispose emission for break/continue to
    // scopes inside the loop.
    public readonly Stack<int> LoopUsingDepthStack = new();

    // Top is non-null inside a switch body, null sentinel inside a loop body -
    // StatementHandler.VisitBranch distinguishes switch breaks (goto end label) from loop breaks.
    public readonly Stack<string> SwitchBreakLabels = new();

    // goto-case / goto-default -> sanitized UASM landing label, per enclosing switch (innermost on
    // top). Roslyn's target name ("case 2:", "default") is not a valid UASM label token, so the
    // case-body label and the goto both resolve through this shared map.
    public readonly Stack<Dictionary<string, string>> GotoCaseLabels = new();

    int _switchLabelCounter;

    public string NextSwitchEndLabel() => $"__switchEnd_{++_switchLabelCounter}";
}
