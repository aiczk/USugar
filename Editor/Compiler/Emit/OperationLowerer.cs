using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Sole recursive dispatcher for Roslyn operations. Handlers recurse through this concrete owner;
/// no callback registration or late dispatch initialization exists.
/// </summary>
internal sealed class OperationLowerer
{
    const byte NoRoute = 0;
    const byte GeneralStatementRoute = 1;
    const byte LoopRoute = 2;
    const byte SwitchRoute = 3;
    const byte DeconstructionRoute = 4;
    const byte GeneralExpressionRoute = 5;
    const byte SimpleAssignmentRoute = 6;
    const byte CompoundAssignmentRoute = 7;
    const byte OperatorRoute = 8;
    const byte InvocationRoute = 9;
    const byte ArrayRoute = 10;
    const byte NullableRoute = 11;

    static readonly OperationKind[] HandledKinds =
        Enum.GetValues(typeof(OperationKind))
            .Cast<OperationKind>()
            .Distinct()
            .Where(kind =>
                StatementRoute(kind) != NoRoute
                || ExpressionRoute(kind) != NoRoute)
            .ToArray();

    readonly LoweringState _state;
    readonly StatementHandler _statements;
    readonly LoopHandler _loops;
    readonly SwitchHandler _switches;
    readonly DeconstructionAssignmentHandler _deconstructions;
    readonly ExpressionHandler _expressions;
    readonly SimpleAssignmentHandler _simpleAssignments;
    readonly CompoundAssignmentHandler _compoundAssignments;
    readonly OperatorHandler _operators;
    readonly InvocationHandler _invocations;
    readonly ArrayHandler _arrays;
    readonly NullableHandler _nullables;

    public LoweringServices Services { get; }
    internal IEnumerable<OperationKind> HandledOperationKinds
        => HandledKinds;

    public OperationLowerer(LoweringState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        state.SetOperationLowerer(this);
        Services = new LoweringServices(state);

        _statements = new StatementHandler(Services);
        _loops = new LoopHandler(Services);
        _switches = new SwitchHandler(Services);
        _deconstructions =
            new DeconstructionAssignmentHandler(Services);
        _expressions = new ExpressionHandler(Services);
        _simpleAssignments =
            new SimpleAssignmentHandler(Services);
        _compoundAssignments =
            new CompoundAssignmentHandler(Services);
        _operators = new OperatorHandler(Services);
        _invocations = new InvocationHandler(Services);
        _arrays = new ArrayHandler(Services);
        _nullables = new NullableHandler(Services);
    }

    public void VisitOperation(IOperation operation)
    {
        if (operation == null)
            throw new NotSupportedException("VisitOperation called with null operation");
        while (operation is IParenthesizedOperation parenthesized)
            operation = parenthesized.Operand;
        try
        {
            if (TryHandleStatement(operation))
                return;
        }
        catch (Exception exception)
        {
            throw TagLocation(exception, operation);
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
        try
        {
            if (TryLowerExpression(operation, out var leaf))
                return LoweredValue.Create(
                    _state, operation, leaf);
        }
        catch (Exception exception)
        {
            throw TagLocation(exception, operation);
        }
        throw new NotSupportedException(
            $"Unsupported expression: {operation.Kind} ({operation.GetType().Name})");
    }

    public CLeaf EmitPatternCheck(
        CLeaf value, ITypeSymbol valueType, IPatternOperation pattern)
        => _operators.EmitPatternCheckImpl(value, valueType, pattern);

    bool TryHandleStatement(IOperation operation)
    {
        switch (StatementRoute(operation.Kind))
        {
            case GeneralStatementRoute:
                _statements.Handle(operation);
                return true;
            case LoopRoute:
                _loops.Handle(operation);
                return true;
            case SwitchRoute:
                _switches.Handle(operation);
                return true;
            case DeconstructionRoute:
                _deconstructions.Handle(operation);
                return true;
            default:
                return false;
        }
    }

    bool TryLowerExpression(
        IOperation operation, out CLeaf result)
    {
        switch (ExpressionRoute(operation.Kind))
        {
            case GeneralExpressionRoute:
                result = _expressions.Handle(operation);
                return true;
            case SimpleAssignmentRoute:
                result = _simpleAssignments.Handle(operation);
                return true;
            case CompoundAssignmentRoute:
                result = _compoundAssignments.Handle(operation);
                return true;
            case OperatorRoute:
                result = _operators.Handle(operation);
                return true;
            case InvocationRoute:
                result = _invocations.Handle(operation);
                return true;
            case ArrayRoute:
                result = _arrays.Handle(operation);
                return true;
            case NullableRoute:
                result = _nullables.Handle(operation);
                return true;
            default:
                result = null;
                return false;
        }
    }

    static byte StatementRoute(OperationKind kind)
        => kind switch
        {
            OperationKind.Block
                or OperationKind.ExpressionStatement
                or OperationKind.VariableDeclarationGroup
                or OperationKind.Conditional
                or OperationKind.Return
                or OperationKind.Branch
                or OperationKind.Labeled
                or OperationKind.LocalFunction
                or OperationKind.Using
                or OperationKind.UsingDeclaration
                or OperationKind.Empty
                => GeneralStatementRoute,
            OperationKind.Loop => LoopRoute,
            OperationKind.Switch => SwitchRoute,
            OperationKind.DeconstructionAssignment
                => DeconstructionRoute,
            _ => NoRoute,
        };

    static byte ExpressionRoute(OperationKind kind)
        => kind switch
        {
            OperationKind.Literal
                or OperationKind.LocalReference
                or OperationKind.FieldReference
                or OperationKind.EventReference
                or OperationKind.ParameterReference
                or OperationKind.InstanceReference
                or OperationKind.Conversion
                or OperationKind.DefaultValue
                or OperationKind.TypeOf
                or OperationKind.NameOf
                or OperationKind.SizeOf
                or OperationKind.DeclarationExpression
                or OperationKind.Discard
                or OperationKind.DelegateCreation
                or OperationKind.Tuple
                => GeneralExpressionRoute,
            OperationKind.SimpleAssignment
                => SimpleAssignmentRoute,
            OperationKind.CompoundAssignment
                or OperationKind.Increment
                or OperationKind.Decrement
                or OperationKind.EventAssignment
                => CompoundAssignmentRoute,
            OperationKind.BinaryOperator
                or OperationKind.UnaryOperator
                or OperationKind.Conditional
                or OperationKind.IsType
                or OperationKind.IsPattern
                or OperationKind.SwitchExpression
                or OperationKind.TupleBinaryOperator
                => OperatorRoute,
            OperationKind.Invocation
                or OperationKind.ObjectCreation
                or OperationKind.PropertyReference
                or OperationKind.InterpolatedString
                or OperationKind.TypeParameterObjectCreation
                or OperationKind.AnonymousObjectCreation
                or OperationKind.With
                => InvocationRoute,
            OperationKind.ArrayCreation
                or OperationKind.ArrayElementReference
                => ArrayRoute,
            OperationKind.ConditionalAccess
                or OperationKind.Coalesce
                or OperationKind.ConditionalAccessInstance
                or OperationKind.CoalesceAssignment
                => NullableRoute,
            _ => NoRoute,
        };

    static Exception TagLocation(Exception exception, IOperation operation)
    {
        if (exception.Data.Contains("usugar_located") || operation?.Syntax == null)
            return exception;
        var span = operation.Syntax.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var character = span.StartLinePosition.Character + 1;
        var where =
            $"{line},{character}";
        var snippet = operation.Syntax.ToString()
            .Replace("\r", " ").Replace("\n", " ");
        if (snippet.Length > 100) snippet = snippet.Substring(0, 100) + "…";
        var wrapped = new NotSupportedException(
            $"{exception.Message}  [at ({where}) {operation.Kind}: `{snippet}`]",
            exception);
        wrapped.Data["usugar_located"] = true;
        wrapped.Data["usugar_file"] =
            span.Path ?? operation.Syntax.SyntaxTree.FilePath ?? "";
        wrapped.Data["usugar_line"] = line;
        wrapped.Data["usugar_character"] = character;
        return wrapped;
    }

    internal static bool TryGetTaggedLocation(
        Exception exception,
        out string file,
        out int line,
        out int character)
    {
        file = exception?.Data["usugar_file"] as string;
        line = exception?.Data["usugar_line"] is int taggedLine
            ? taggedLine
            : 0;
        character =
            exception?.Data["usugar_character"] is int taggedCharacter
                ? taggedCharacter
                : 0;
        return !string.IsNullOrEmpty(file);
    }
}
