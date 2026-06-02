using Microsoft.CodeAnalysis;

public interface IExpressionHandler
{
    bool CanHandle(IOperation expression);
    // A-normal form: every expression result is an operand-safe leaf (CSlotRef/CConst/CFuncRef/CFieldAddr,
    // or null for a void call). Value-producing ops are bound to a slot at construction, so they never
    // surface here — making a producer-in-operand a compile error, not a runtime hazard.
    CLeaf Handle(IOperation expression);
}
