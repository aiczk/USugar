using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Finds runtime references from one Unity source assembly into another project-owned source
/// assembly. Referenced DLL symbols do not carry bodies/layout syntax in the consuming Roslyn
/// compilation, so treating them like SDK metadata can silently omit inherited Udon members.
/// </summary>
public static class CrossAssemblySourceGuard
{
    public sealed class Issue
    {
        public string FilePath { get; }
        public int Line { get; }
        public int Character { get; }
        public string ReferencedAssembly { get; }
        public string SymbolName { get; }
        public ISymbol Symbol { get; }

        public Issue(string filePath, int line, int character,
            string referencedAssembly, string symbolName, ISymbol symbol)
        {
            FilePath = filePath ?? "";
            Line = line;
            Character = character;
            ReferencedAssembly = referencedAssembly ?? "";
            SymbolName = symbolName ?? "";
            Symbol = symbol;
        }
    }

    public static IReadOnlyList<Issue> FindIssues(
        Compilation compilation,
        ISet<string> projectSourceAssemblyNames,
        IEnumerable<INamedTypeSymbol> rootTypes = null)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (projectSourceAssemblyNames == null)
            throw new ArgumentNullException(nameof(projectSourceAssemblyNames));

        var currentAssembly = compilation.AssemblyName ?? "";
        var issues = new List<Issue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pendingTypes = new Queue<INamedTypeSymbol>();
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        if (rootTypes == null)
        {
            foreach (var tree in compilation.SyntaxTrees)
                ScanNode(tree.GetRoot());
        }
        else
        {
            foreach (var type in rootTypes)
                Enqueue(type);
            while (pendingTypes.Count > 0)
            {
                var type = pendingTypes.Dequeue();
                Enqueue(type.BaseType);
                foreach (var iface in type.Interfaces)
                    Enqueue(iface);
                foreach (var syntax in type.DeclaringSyntaxReferences)
                    ScanNode(syntax.GetSyntax());
            }
        }

        return issues
            .OrderBy(issue => issue.FilePath, StringComparer.Ordinal)
            .ThenBy(issue => issue.Line)
            .ThenBy(issue => issue.Character)
            .ToArray();

        void Enqueue(INamedTypeSymbol type)
        {
            type = type?.OriginalDefinition;
            if (type == null
                || !string.Equals(type.ContainingAssembly?.Identity.Name, currentAssembly,
                    StringComparison.OrdinalIgnoreCase)
                || type.DeclaringSyntaxReferences.Length == 0
                || !seenTypes.Add(type))
                return;
            pendingTypes.Enqueue(type);
        }

        void ScanNode(SyntaxNode root)
        {
            var model = compilation.GetSemanticModel(root.SyntaxTree);
            foreach (var name in root.DescendantNodesAndSelf()
                .Where(node => node is IdentifierNameSyntax or GenericNameSyntax))
            {
                // Attributes are metadata-only consumers; their implementation body is never emitted
                // into the Udon program. Namespace imports likewise carry no runtime implementation.
                if (name.AncestorsAndSelf().Any(node =>
                    node is AttributeSyntax or UsingDirectiveSyntax))
                    continue;

                var info = model.GetSymbolInfo(name);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is IAliasSymbol alias) symbol = alias.Target;
                var referencedAssembly = symbol?.ContainingAssembly?.Identity.Name;
                if (string.IsNullOrEmpty(referencedAssembly)) continue;
                if (string.Equals(referencedAssembly, currentAssembly,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Enqueue(symbol as INamedTypeSymbol ?? symbol.ContainingType);
                    continue;
                }
                if (!projectSourceAssemblyNames.Contains(referencedAssembly)) continue;

                var span = name.GetLocation().GetLineSpan();
                var key = span.Path + "|" + span.StartLinePosition.Line + "|"
                    + span.StartLinePosition.Character + "|" + referencedAssembly;
                if (!seen.Add(key)) continue;
                issues.Add(new Issue(
                    span.Path,
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    referencedAssembly,
                    symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    symbol));
            }
        }
    }
}
