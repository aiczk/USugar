using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Validates dependencies among per-program static initializers.</summary>
public static class StaticInitializationPlan
{
    public static void ValidateCycles(Compilation compilation,
        IReadOnlyList<(string FieldId, IOperation InitOp, ITypeSymbol FieldType)> initializers,
        Func<IMethodSymbol, IOperation> getMethodBody)
    {
        var opByOwner = new Dictionary<ISymbol, IOperation>(SymbolEqualityComparer.Default);
        foreach (var (_, op, _) in initializers)
        {
            SyntaxNode declaration = op.Syntax.FirstAncestorOrSelf<VariableDeclaratorSyntax>()
                ?? (SyntaxNode)op.Syntax.FirstAncestorOrSelf<PropertyDeclarationSyntax>();
            if (declaration == null) continue;
            var owner = compilation.GetSemanticModel(declaration.SyntaxTree).GetDeclaredSymbol(declaration);
            if (owner != null) opByOwner[owner] = op;
        }

        IEnumerable<ISymbol> Dependencies(IOperation root)
        {
            var dependencies = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var visitedMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            void Walk(IOperation body)
            {
                if (body == null) return;
                foreach (var child in body.DescendantsAndSelf())
                {
                    ISymbol referenced = child switch
                    {
                        IFieldReferenceOperation field => field.Field,
                        IPropertyReferenceOperation property => property.Property,
                        _ => null,
                    };
                    if (referenced != null && opByOwner.ContainsKey(referenced))
                        dependencies.Add(referenced);

                    IMethodSymbol called = child switch
                    {
                        IInvocationOperation invocation => invocation.TargetMethod,
                        IPropertyReferenceOperation property => property.Property.GetMethod,
                        _ => null,
                    };
                    if (called is { IsStatic: true } && called.DeclaringSyntaxReferences.Length > 0
                        && visitedMethods.Add(called.OriginalDefinition))
                        Walk(getMethodBody(called));
                }
            }
            Walk(root);
            return dependencies;
        }

        var visiting = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var path = new List<ISymbol>();
        void Visit(ISymbol owner)
        {
            if (visited.Contains(owner)) return;
            if (!visiting.Add(owner))
            {
                var start = path.FindIndex(x => SymbolEqualityComparer.Default.Equals(x, owner));
                var cycle = path.Skip(start).Concat(new[] { owner })
                    .Select(x => x.ContainingType.Name + "." + x.Name);
                throw new NotSupportedException(
                    "Static initializer cycle is not supported by the per-program static owner ABI: "
                    + string.Join(" -> ", cycle)
                    + ". Initialize one member explicitly from Start() to break the cycle.");
            }
            path.Add(owner);
            foreach (var dependency in Dependencies(opByOwner[owner])) Visit(dependency);
            path.RemoveAt(path.Count - 1);
            visiting.Remove(owner);
            visited.Add(owner);
        }

        foreach (var owner in opByOwner.Keys.ToArray()) Visit(owner);
    }
}
