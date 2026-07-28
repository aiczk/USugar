using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Roslyn operations materialized before body emission for one source method definition.
/// Lowering never asks a SemanticModel to reconstruct a body.
/// </summary>
internal sealed class BoundMethodBody
{
    public readonly IMethodSymbol MethodDefinition;
    public readonly SyntaxNode Declaration;
    public readonly IOperation Root;
    public readonly IOperation ExpressionBody;
    public readonly IReadOnlyDictionary<ILocalSymbol, IOperation>
        StableLocalInitializers;
    public readonly IReadOnlyList<ITypeSymbol> ReferencedTypes;

    public IOperation AnalysisRoot => Root ?? ExpressionBody;
    public IOperation CallableRoot => AnalysisRoot switch
    {
        ILocalFunctionOperation localFunction => localFunction.Body,
        IAnonymousFunctionOperation anonymousFunction
            => anonymousFunction.Body,
        _ => AnalysisRoot,
    };
    public bool HasSourceDeclaration => Declaration != null;

    public BoundMethodBody(
        IMethodSymbol methodDefinition,
        SyntaxNode declaration,
        IOperation root,
        IOperation expressionBody)
    {
        MethodDefinition = methodDefinition?.OriginalDefinition
            ?? throw new ArgumentNullException(nameof(methodDefinition));
        Declaration = declaration;
        Root = root;
        ExpressionBody = expressionBody;
        var analysis = Analyze(AnalysisRoot);
        StableLocalInitializers = analysis.StableLocalInitializers;
        ReferencedTypes = analysis.ReferencedTypes;
    }

    static (
        IReadOnlyDictionary<ILocalSymbol, IOperation> StableLocalInitializers,
        IReadOnlyList<ITypeSymbol> ReferencedTypes)
        Analyze(IOperation body)
    {
        var initializers = new Dictionary<ILocalSymbol, IOperation>(
            SymbolEqualityComparer.Default);
        var unstable = new HashSet<ILocalSymbol>(
            SymbolEqualityComparer.Default);
        var referencedTypes = new List<ITypeSymbol>();
        if (body != null)
            foreach (var operation in body.DescendantsAndSelf())
            {
                if (operation is IVariableDeclaratorOperation declaration)
                {
                    referencedTypes.Add(declaration.Symbol.Type);
                    if (declaration.Initializer?.Value == null
                        || initializers.ContainsKey(declaration.Symbol))
                        unstable.Add(declaration.Symbol);
                    else
                        initializers.Add(
                            declaration.Symbol,
                            declaration.Initializer.Value);
                }

                var written = WrittenLocal(operation);
                if (written != null) unstable.Add(written);
                var referenced = operation switch
                {
                    ILocalReferenceOperation local => local.Local.Type,
                    IParameterReferenceOperation parameter
                        => parameter.Parameter.Type,
                    IFieldReferenceOperation field => field.Field.Type,
                    _ => null,
                };
                if (referenced != null)
                    referencedTypes.Add(referenced);
            }
        foreach (var local in unstable)
            initializers.Remove(local);
        return (
            new ReadOnlyDictionary<ILocalSymbol, IOperation>(initializers),
            Array.AsReadOnly(referencedTypes.ToArray()));
    }

    static ILocalSymbol WrittenLocal(IOperation operation)
    {
        static ILocalSymbol Local(IOperation candidate)
            => ValueClassifier.UnwrapConversions(candidate)
                is ILocalReferenceOperation reference
                ? reference.Local
                : null;
        return operation switch
        {
            ISimpleAssignmentOperation assignment
                => Local(assignment.Target),
            ICompoundAssignmentOperation assignment
                => Local(assignment.Target),
            IIncrementOrDecrementOperation increment
                => Local(increment.Target),
            IArgumentOperation argument
                when argument.Parameter?.RefKind
                    is RefKind.Ref or RefKind.Out
                => Local(argument.Value),
            _ => null,
        };
    }
}

/// <summary>Deeply frozen source-body snapshot keyed by method definition.</summary>
internal sealed class BoundMethodBodyTable
{
    readonly IReadOnlyDictionary<IMethodSymbol, BoundMethodBody> _bodies;
    public IReadOnlyCollection<BoundMethodBody> Bodies { get; }

    BoundMethodBodyTable(IDictionary<IMethodSymbol, BoundMethodBody> bodies)
    {
        _bodies = new ReadOnlyDictionary<IMethodSymbol, BoundMethodBody>(
            new Dictionary<IMethodSymbol, BoundMethodBody>(
                bodies, SymbolEqualityComparer.Default));
        Bodies = Array.AsReadOnly(_bodies.Values.ToArray());
    }

    public static BoundMethodBodyTable Materialize(
        Compilation compilation,
        IEnumerable<IMethodSymbol> methods)
        => new Materializer(compilation).Freeze(methods);

    /// <summary>
    /// Per-program source-body authority. A method definition is materialized at most once; reach,
    /// capture, recursion, binding, and emission retain the resulting <see cref="BoundMethodBody"/>
    /// instance instead of asking Roslyn to reconstruct parallel operation trees.
    /// </summary>
    internal sealed class Materializer
    {
        readonly Compilation _compilation;
        readonly Func<SyntaxNode, IOperation> _getOperation;
        readonly Dictionary<IMethodSymbol, BoundMethodBody> _bodies = new(
            SymbolEqualityComparer.Default);
        bool _frozen;

        public Materializer(
            Compilation compilation,
            Func<SyntaxNode, IOperation> getOperation = null)
        {
            _compilation = compilation
                ?? throw new ArgumentNullException(nameof(compilation));
            _getOperation = getOperation
                ?? (syntax => _compilation
                    .GetSemanticModel(syntax.SyntaxTree)
                    .GetOperation(syntax));
        }

        public BoundMethodBody Get(IMethodSymbol rawMethod)
        {
            var method = rawMethod?.OriginalDefinition;
            if (method == null) return null;
            if (_bodies.TryGetValue(method, out var existing))
                return existing;
            if (_frozen)
                throw new InvalidOperationException(
                    $"Method body '{method}' was requested after the "
                    + "source-body snapshot was frozen.");

            var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
            {
                var sourceLess = new BoundMethodBody(
                    method, null, null, null);
                _bodies.Add(method, sourceLess);
                return sourceLess;
            }

            var declaration = syntaxRef.GetSyntax();
            var operationSyntax = declaration switch
            {
                PropertyDeclarationSyntax property
                    when property.ExpressionBody != null
                    => property.ExpressionBody.Expression,
                _ => declaration,
            };
            var operation = _getOperation(operationSyntax);
            var expressionBody =
                ReferenceEquals(operationSyntax, declaration)
                    ? null : operation;
            var body = new BoundMethodBody(
                method,
                declaration,
                expressionBody == null ? operation : null,
                expressionBody);
            _bodies.Add(method, body);
            IndexNestedCallables(body.AnalysisRoot);
            return body;
        }

        public IOperation GetOperation(IMethodSymbol method)
            => Get(method)?.AnalysisRoot;

        public void RegisterNestedCallables(
            IEnumerable<IOperation> roots)
        {
            if (roots == null)
                throw new ArgumentNullException(nameof(roots));
            if (_frozen)
                throw new InvalidOperationException(
                    "Nested callables cannot be registered after the "
                    + "source-body snapshot was frozen.");
            foreach (var root in roots)
                IndexNestedCallables(root);
        }

        void IndexNestedCallables(IOperation root)
        {
            if (root == null) return;
            foreach (var operation in root.DescendantsAndSelf())
            {
                var method = operation switch
                {
                    ILocalFunctionOperation localFunction
                        => localFunction.Symbol,
                    IAnonymousFunctionOperation anonymousFunction
                        => anonymousFunction.Symbol,
                    _ => null,
                };
                RegisterNestedCallable(method, operation);
            }
        }

        public BoundMethodBody RegisterNestedCallable(
            IMethodSymbol rawMethod,
            IOperation operation)
        {
            var method = rawMethod?.OriginalDefinition;
            if (method == null || operation == null)
                return null;
            if (_bodies.TryGetValue(method, out var existing))
                return existing;
            if (_frozen)
                throw new InvalidOperationException(
                    $"Nested callable '{method}' was discovered after "
                    + "the source-body snapshot was frozen.");
            var body = new BoundMethodBody(
                method, operation.Syntax, operation, null);
            _bodies.Add(method, body);
            return body;
        }

        public BoundMethodBodyTable Freeze(
            IEnumerable<IMethodSymbol> methods)
        {
            if (methods == null)
                throw new ArgumentNullException(nameof(methods));
            if (_frozen)
                throw new InvalidOperationException(
                    "The source-body snapshot was frozen twice.");
            foreach (var method in methods)
                Get(method);
            _frozen = true;
            return new BoundMethodBodyTable(_bodies);
        }
    }

    public BoundMethodBody Get(IMethodSymbol method)
    {
        if (method == null) return null;
        _bodies.TryGetValue(method.OriginalDefinition, out var body);
        return body;
    }

    public BoundMethodBody Require(IMethodSymbol method)
        => Get(method) ?? throw new InvalidOperationException(
            $"Method body '{method}' was absent from the bound program.");
}
