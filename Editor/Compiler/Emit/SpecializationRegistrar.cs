using Microsoft.CodeAnalysis;

/// <summary>Registers the planner's closed named-method specializations before any body is emitted.</summary>
internal sealed class SpecializationRegistrar : HandlerBase
{
    public SpecializationRegistrar(EmitContext context) : base(context) { }

    public void Register(IMethodSymbol specialization)
        => RegisterGenericSpecialization(specialization);
}
