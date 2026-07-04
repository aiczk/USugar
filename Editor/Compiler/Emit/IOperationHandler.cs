using Microsoft.CodeAnalysis;

// HandledKinds is the single dispatch truth: UasmEmitter builds one kind→handler table per array
// (statement / expression), rejecting duplicate kinds at construction. A kind listed here must be accepted in Handle().
public interface IHandler
{
    OperationKind[] HandledKinds { get; }
}

public interface IOperationHandler : IHandler
{
    void Handle(IOperation operation);
}
