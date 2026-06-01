using Microsoft.CodeAnalysis;

public interface IExpressionHandler
{
    bool CanHandle(IOperation expression);
    CValue Handle(IOperation expression);
}
