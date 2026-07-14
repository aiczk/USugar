using Microsoft.CodeAnalysis;

/// <summary>CA call-graph rewrite (M0): one target a call/reference operation reaches at runtime, tagged by
/// the role that consumes it. The same method is yielded once per role it plays (a method group is a
/// reach/registration edge AND an escape target). Definition-granularity in M0 — per-spec type-arg binding
/// is a later lazy instantiation-set consumed only at typeobj mint sites.</summary>
public enum TargetRole { CallEdge, ReachForeignStatic, ReachStructMember, ReachBaseInstance, EscapeTarget }

public readonly struct ResolvedTarget
{
    public readonly IMethodSymbol Method;
    public readonly TargetRole Role;
    public ResolvedTarget(IMethodSymbol method, TargetRole role) { Method = method; Role = role; }
}
