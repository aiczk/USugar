using Microsoft.CodeAnalysis;

public interface IExpressionHandler
{
    bool CanHandle(IOperation expression);
    string Handle(IOperation expression);
}
