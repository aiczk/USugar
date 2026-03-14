using Microsoft.CodeAnalysis;

public interface IExpressionHandler
{
    bool CanHandle(IOperation expression);
    HExpr Handle(IOperation expression);
}
