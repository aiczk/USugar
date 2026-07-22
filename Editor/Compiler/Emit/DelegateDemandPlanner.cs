using Microsoft.CodeAnalysis.Operations;

/// <summary>Pre-emission facade over the shared delegate binding resolver.</summary>
internal sealed class DelegateDemandPlanner : HandlerBase
{
    public DelegateDemandPlanner(EmitContext context) : base(context) { }

    public DelegateBindingPlan Plan(IDelegateCreationOperation operation)
        => PlanDelegateBridge(operation);
}
