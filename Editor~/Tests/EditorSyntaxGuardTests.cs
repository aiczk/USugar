using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace USugar.Tests;

public class EditorSyntaxGuardTests
{
    [Fact]
    public void UnityIntegrationSourcesParseAsCSharp9()
    {
        var packageRoot = FindPackageRoot();
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var failures = Directory.GetFiles(
                Path.Combine(packageRoot, "Editor"), "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => CSharpSyntaxTree.ParseText(
                    File.ReadAllText(path), parseOptions, path)
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Editor integration sources must remain valid Unity C# 9:\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void HandwrittenCompilerTypesDoNotUsePartialDeclarations()
    {
        var packageRoot = FindPackageRoot();
        var compilerRoot = Path.Combine(packageRoot, "Editor", "Compiler");
        var failures = Directory.GetFiles(
                compilerRoot, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path =>
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
                return tree.GetRoot()
                    .DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(type => type.Modifiers.Any(SyntaxKind.PartialKeyword))
                    .Select(type =>
                    {
                        var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        return $"{Path.GetRelativePath(packageRoot, path)}:{line}: {type.Identifier.ValueText}";
                    });
            })
            .ToArray();

        Assert.True(failures.Length == 0,
            "Compiler implementation types must be cohesive composition units, not partial declarations. "
            + "This does not restrict partial types in compiled user source.\n"
            + string.Join("\n", failures));
    }

    static string FindPackageRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current != null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Editor"))
                && Directory.Exists(Path.Combine(current.FullName, "Editor~")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the USugar package root above '{AppContext.BaseDirectory}'.");
    }
}
