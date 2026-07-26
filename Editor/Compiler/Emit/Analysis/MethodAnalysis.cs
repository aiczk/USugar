using System;
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

/// <summary>
/// Frozen reusable method facts. Construction consumes already-bound operation
/// roots and never retains a Compilation or SemanticModel.
/// </summary>
internal sealed class BoundMethodAnalysisTable
{
    readonly IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> _analyses;

    public BoundMethodAnalysisTable(BoundMethodBodyTable bodies)
    {
        if (bodies == null) throw new ArgumentNullException(nameof(bodies));
        var analyses = new Dictionary<IMethodSymbol, MethodAnalysis>(
            SymbolEqualityComparer.Default);
        foreach (var boundBody in bodies.Bodies)
        {
            var analysis = Analyze(boundBody.AnalysisRoot);
            if (analysis != null)
                analyses.Add(boundBody.MethodDefinition, analysis);
        }
        _analyses = new System.Collections.ObjectModel.ReadOnlyDictionary<
            IMethodSymbol, MethodAnalysis>(analyses);
    }

    static MethodAnalysis Analyze(IOperation body)
    {
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

        return new MethodAnalysis(
            body,
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                ILocalSymbol, IOperation>(initializers),
            Array.AsReadOnly(referencedTypes.ToArray()));
    }

    public MethodAnalysis Get(IMethodSymbol method)
    {
        if (method == null) return null;
        _analyses.TryGetValue(method.OriginalDefinition, out var analysis);
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
