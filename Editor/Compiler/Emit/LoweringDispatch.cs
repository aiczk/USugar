using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Recursive operation dispatch wired once after handler construction.</summary>
public sealed class LoweringDispatch
{
    Action<IOperation> _visitOperation;
    Func<IOperation, CLeaf> _visitExpression;
    Func<IOperation, LoweredValue> _visitLoweredExpression;
    Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> _emitPatternCheck;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("Lowering dispatch has not been initialized.");
    public Func<IOperation, CLeaf> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("Lowering dispatch has not been initialized.");
    public Func<IOperation, LoweredValue> VisitLoweredExpression => _visitLoweredExpression
        ?? throw new InvalidOperationException("Lowering dispatch has not been initialized.");
    public Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("Lowering dispatch has not been initialized.");

    public void Initialize(
        Action<IOperation> visitOperation,
        Func<IOperation, CLeaf> visitExpression,
        Func<IOperation, LoweredValue> visitLoweredExpression,
        Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> emitPatternCheck)
    {
        if (_visitOperation != null)
            throw new InvalidOperationException("Lowering dispatch is already initialized.");
        _visitOperation = visitOperation ?? throw new ArgumentNullException(nameof(visitOperation));
        _visitExpression = visitExpression ?? throw new ArgumentNullException(nameof(visitExpression));
        _visitLoweredExpression = visitLoweredExpression
            ?? throw new ArgumentNullException(nameof(visitLoweredExpression));
        _emitPatternCheck = emitPatternCheck
            ?? throw new ArgumentNullException(nameof(emitPatternCheck));
    }
}
