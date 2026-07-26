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
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (methods == null) throw new ArgumentNullException(nameof(methods));
        var bodies = new Dictionary<IMethodSymbol, BoundMethodBody>(
            SymbolEqualityComparer.Default);
        foreach (var rawMethod in methods)
        {
            var method = rawMethod?.OriginalDefinition;
            if (method == null || bodies.ContainsKey(method)) continue;
            var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
            {
                bodies.Add(method, new BoundMethodBody(
                    method, null, null, null));
                continue;
            }
            var declaration = syntaxRef.GetSyntax();
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            var root = model.GetOperation(declaration);
            IOperation expressionBody = declaration switch
            {
                PropertyDeclarationSyntax property
                    when property.ExpressionBody != null
                    => model.GetOperation(property.ExpressionBody.Expression),
                AccessorDeclarationSyntax accessor
                    when accessor.ExpressionBody != null
                    => model.GetOperation(accessor.ExpressionBody.Expression),
                _ => null,
            };
            bodies.Add(method, new BoundMethodBody(
                method, declaration, root, expressionBody));
        }
        return new BoundMethodBodyTable(bodies);
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
