using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Pre-emission inventory of every delegate creation in registered source bodies.</summary>
internal static class DelegateDemandCensus
{
    public static IReadOnlyCollection<string> Collect(
        IEnumerable<MethodContext.RegisteredCallableBody> bodies,
        BoundMethodBodyTable methodBodies,
        IEnumerable<IOperation> fieldInitializers)
    {
        if (methodBodies == null)
            throw new ArgumentNullException(nameof(methodBodies));
        var sites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in bodies)
            Collect(
                methodBodies.Require(body.Method.OriginalDefinition).AnalysisRoot,
                body.OwnerSpecs,
                sites,
                true);
        foreach (var initializer in fieldInitializers)
            Collect(initializer, System.Collections.Immutable.ImmutableArray<IMethodSymbol>.Empty, sites, true);
        return sites;
    }

    static void Collect(IOperation operation,
        System.Collections.Immutable.ImmutableArray<IMethodSymbol> ownerSpecs,
        HashSet<string> sites, bool root)
    {
        if (operation == null) return;
        if (!root && operation is ILocalFunctionOperation or IAnonymousFunctionOperation) return;
        if (operation is IDelegateCreationOperation) sites.Add(SiteKey(operation.Syntax, ownerSpecs));
        foreach (var child in operation.ChildOps()) Collect(child, ownerSpecs, sites, false);
    }

    public static string SiteKey(SyntaxNode syntax,
        System.Collections.Immutable.ImmutableArray<IMethodSymbol> ownerSpecs)
    {
        if (syntax == null) throw new ArgumentNullException(nameof(syntax));
        var owners = new List<string>(ownerSpecs.Length);
        foreach (var owner in ownerSpecs)
            owners.Add((owner.OriginalDefinition.GetDocumentationCommentId()
                        ?? owner.OriginalDefinition.ToDisplayString())
                       + "@" + ClassTypeObjectContext.SpecKey(owner.ContainingType)
                       + "@" + string.Join(",", System.Linq.Enumerable.Select(
                           owner.TypeArguments, ClassTypeObjectContext.SpecKey)));
        return syntax.SyntaxTree.FilePath + ":" + syntax.Span.Start + ":" + syntax.Span.Length
               + "|" + string.Join(">", owners);
    }
}
