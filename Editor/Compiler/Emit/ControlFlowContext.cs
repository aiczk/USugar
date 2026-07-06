using System.Collections.Generic;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns transient control-flow stacks used while emitting statements.
/// </summary>
public sealed class ControlFlowContext
{
    public readonly Stack<CLeaf> ConditionalAccessStack = new();

    public readonly Stack<List<(CLeaf Val, ITypeSymbol Type)>> UsingDisposableStack = new();

    public readonly Stack<int> LoopUsingDepthStack = new();

    public readonly Stack<string> SwitchBreakLabels = new();

    public readonly Stack<Dictionary<string, string>> GotoCaseLabels = new();

    int _switchLabelCounter;

    public string NextSwitchEndLabel() => $"__switchEnd_{++_switchLabelCounter}";
}
