using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Immutable facts collected once from a method operation tree.</summary>
public sealed class MethodAnalysis
{
    public readonly IOperation Body;
    public readonly IReadOnlyDictionary<ILocalSymbol, IOperation> StableLocalInitializers;
    public readonly IReadOnlyList<ITypeSymbol> ReferencedTypes;

    internal MethodAnalysis(IOperation body,
        IReadOnlyDictionary<ILocalSymbol, IOperation> stableLocalInitializers,
        IReadOnlyList<ITypeSymbol> referencedTypes)
    {
        Body = body;
        StableLocalInitializers = stableLocalInitializers;
        ReferencedTypes = referencedTypes;
    }
}

/// <summary>Compilation-local owner of reusable Roslyn method analysis.</summary>
public sealed class MethodAnalysisCache
{
    readonly Compilation _compilation;
    readonly Dictionary<IMethodSymbol, MethodAnalysis> _cache = new(SymbolEqualityComparer.Default);

    public MethodAnalysisCache(Compilation compilation) => _compilation = compilation;

    public MethodAnalysis Get(IMethodSymbol method)
    {
        if (method == null) return null;
        if (_cache.TryGetValue(method, out var cached)) return cached;
        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;
        var syntax = syntaxRef.GetSyntax();
        var body = _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
        if (body == null) return null;

        var initializers = new Dictionary<ILocalSymbol, IOperation>(SymbolEqualityComparer.Default);
        var unstable = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        var referencedTypes = new List<ITypeSymbol>();
        foreach (var operation in body.DescendantsAndSelf())
        {
            if (operation is IVariableDeclaratorOperation declaration)
            {
                referencedTypes.Add(declaration.Symbol.Type);
                if (declaration.Initializer?.Value == null || initializers.ContainsKey(declaration.Symbol))
                    unstable.Add(declaration.Symbol);
                else
                    initializers.Add(declaration.Symbol, declaration.Initializer.Value);
            }

            var written = WrittenLocal(operation);
            if (written != null) unstable.Add(written);
            var referenced = operation switch
            {
                ILocalReferenceOperation local => local.Local.Type,
                IParameterReferenceOperation parameter => parameter.Parameter.Type,
                IFieldReferenceOperation field => field.Field.Type,
                _ => null,
            };
            if (referenced != null) referencedTypes.Add(referenced);
        }
        foreach (var local in unstable) initializers.Remove(local);

        var analysis = new MethodAnalysis(body, initializers, referencedTypes);
        _cache.Add(method, analysis);
        return analysis;
    }

    static ILocalSymbol WrittenLocal(IOperation operation)
    {
        static ILocalSymbol Local(IOperation candidate)
            => ValueClassifier.UnwrapConversions(candidate) is ILocalReferenceOperation reference
                ? reference.Local : null;
        return operation switch
        {
            ISimpleAssignmentOperation assignment => Local(assignment.Target),
            ICompoundAssignmentOperation assignment => Local(assignment.Target),
            IIncrementOrDecrementOperation increment => Local(increment.Target),
            IArgumentOperation argument when argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out
                => Local(argument.Value),
            _ => null,
        };
    }
}
