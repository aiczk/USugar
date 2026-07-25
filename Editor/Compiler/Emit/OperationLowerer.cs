using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Sole recursive dispatcher for Roslyn operations. Handlers recurse through this concrete owner;
/// no callback registration or late dispatch initialization exists.
/// </summary>
public sealed class OperationLowerer
{
    readonly LoweringState _state;
    readonly Dictionary<OperationKind, IOperationHandler> _statements;
    readonly Dictionary<OperationKind, IExpressionHandler> _expressions;
    readonly OperatorHandler _operators;

    public LoweringServices Services { get; }
    internal IEnumerable<OperationKind> HandledOperationKinds
        => _statements.Keys.Concat(_expressions.Keys).Distinct();

    public OperationLowerer(LoweringState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        state.SetOperationLowerer(this);
        Services = new LoweringServices(state);

        var statement = new StatementHandler(Services);
        var loop = new LoopHandler(Services);
        var @switch = new SwitchHandler(Services);
        var deconstruction = new DeconstructionAssignmentHandler(Services);
        var simpleAssignment = new SimpleAssignmentHandler(Services);
        var compoundAssignment = new CompoundAssignmentHandler(Services);
        _operators = new OperatorHandler(Services);

        _statements = BuildDispatch<IOperationHandler>(
            statement, loop, @switch, deconstruction);
        _expressions = BuildDispatch<IExpressionHandler>(
            new ExpressionHandler(Services),
            simpleAssignment,
            compoundAssignment,
            _operators,
            new InvocationHandler(Services),
            new ArrayHandler(Services),
            new NullableHandler(Services));
    }

    public void VisitOperation(IOperation operation)
    {
        if (operation == null)
            throw new NotSupportedException("VisitOperation called with null operation");
        while (operation is IParenthesizedOperation parenthesized)
            operation = parenthesized.Operand;
        if (_statements.TryGetValue(operation.Kind, out var handler))
        {
            try
            {
                handler.Handle(operation);
                return;
            }
            catch (Exception exception)
            {
                throw TagLocation(exception, operation);
            }
        }
        throw new NotSupportedException(
            $"Unsupported operation: {operation.Kind} ({operation.GetType().Name})");
    }

    public CLeaf VisitExpression(IOperation operation)
        => VisitLoweredExpression(operation).Leaf;

    public LoweredValue VisitLoweredExpression(IOperation operation)
    {
        if (operation == null)
            throw new NotSupportedException("VisitExpression called with null operation");
        while (operation is IParenthesizedOperation parenthesized)
            operation = parenthesized.Operand;
        if (_expressions.TryGetValue(operation.Kind, out var handler))
        {
            try
            {
                return LoweredValue.Create(_state, operation, handler.Handle(operation));
            }
            catch (Exception exception)
            {
                throw TagLocation(exception, operation);
            }
        }
        throw new NotSupportedException(
            $"Unsupported expression: {operation.Kind} ({operation.GetType().Name})");
    }

    public CLeaf EmitPatternCheck(
        CLeaf value, ITypeSymbol valueType, IPatternOperation pattern)
        => _operators.EmitPatternCheckImpl(value, valueType, pattern);

    static Dictionary<OperationKind, T> BuildDispatch<T>(params T[] handlers)
        where T : IHandler
    {
        var table = new Dictionary<OperationKind, T>();
        foreach (var handler in handlers)
            foreach (var kind in handler.HandledKinds)
            {
                if (table.TryGetValue(kind, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate handler for OperationKind.{kind}: "
                        + $"{existing.GetType().Name} and {handler.GetType().Name}");
                table[kind] = handler;
            }
        return table;
    }

    static Exception TagLocation(Exception exception, IOperation operation)
    {
        if (exception.Data.Contains("usugar_located") || operation?.Syntax == null)
            return exception;
        var span = operation.Syntax.GetLocation().GetLineSpan();
        var where =
            $"{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}";
        var snippet = operation.Syntax.ToString()
            .Replace("\r", " ").Replace("\n", " ");
        if (snippet.Length > 100) snippet = snippet.Substring(0, 100) + "…";
        var wrapped = new NotSupportedException(
            $"{exception.Message}  [at ({where}) {operation.Kind}: `{snippet}`]",
            exception);
        wrapped.Data["usugar_located"] = true;
        return wrapped;
    }
}
