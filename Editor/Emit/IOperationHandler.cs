using Microsoft.CodeAnalysis;

public interface IOperationHandler
{
    bool CanHandle(IOperation operation);
    void Handle(IOperation operation);
}
