using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class CrossAssemblySourceGuardTests
{
    [Fact]
    public void RejectsBaseTypeFromAnotherProjectSourceAssembly()
    {
        var compilation = ConsumerCompilation(
            "using Shared; public class Behaviour : Base { }");

        var issues = CrossAssemblySourceGuard.FindIssues(
            compilation, ProjectAssemblies(), BehaviourRoots(compilation));

        var issue = Assert.Single(issues);
        Assert.Equal("Shared.Runtime", issue.ReferencedAssembly);
        Assert.Contains("Shared.Base", issue.SymbolName);
    }

    [Fact]
    public void RejectsHelperCallFromAnotherProjectSourceAssembly()
    {
        var compilation = ConsumerCompilation(
            "using Shared; public class Behaviour { int Run() => Helper.Value(); }");

        var issues = CrossAssemblySourceGuard.FindIssues(
            compilation, ProjectAssemblies(), BehaviourRoots(compilation));

        Assert.Contains(issues, issue =>
            issue.ReferencedAssembly == "Shared.Runtime"
            && issue.SymbolName.Contains("Helper"));
    }

    [Fact]
    public void AllowsMetadataOnlyAttributeFromAnotherProjectSourceAssembly()
    {
        var compilation = ConsumerCompilation(
            "using Shared; [Marker] public class Behaviour { }");

        Assert.Empty(CrossAssemblySourceGuard.FindIssues(
            compilation, ProjectAssemblies(), BehaviourRoots(compilation)));
    }

    [Fact]
    public void FollowsReachableLocalHelperTypes()
    {
        var compilation = ConsumerCompilation(@"
using Shared;
public class Behaviour { int Run() => LocalHelper.Run(); }
static class LocalHelper { public static int Run() => Helper.Value(); }");

        var issues = CrossAssemblySourceGuard.FindIssues(
            compilation, ProjectAssemblies(), BehaviourRoots(compilation));

        Assert.Contains(issues, issue => issue.SymbolName.Contains("Helper"));
    }

    [Fact]
    public void IgnoresUnreachableTypesInMixedAssembly()
    {
        var compilation = ConsumerCompilation(@"
using Shared;
public class Behaviour { }
public class Unrelated { int Run() => Helper.Value(); }");

        Assert.Empty(CrossAssemblySourceGuard.FindIssues(
            compilation, ProjectAssemblies(), BehaviourRoots(compilation)));
    }

    static HashSet<string> ProjectAssemblies()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            "Behaviour.Runtime",
            "Shared.Runtime"
        };

    static IEnumerable<INamedTypeSymbol> BehaviourRoots(CSharpCompilation compilation)
    {
        yield return compilation.GetTypeByMetadataName("Behaviour");
    }

    static CSharpCompilation ConsumerCompilation(string source)
    {
        var dependency = CSharpCompilation.Create(
            "Shared.Runtime",
            new[] { CSharpSyntaxTree.ParseText(@"
using System;
namespace Shared
{
    public class Base { public int M() => 1; }
    public static class Helper { public static int Value() => 2; }
    public sealed class MarkerAttribute : Attribute { }
}") },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emit = dependency.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

        return CSharpCompilation.Create(
            "Behaviour.Runtime",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Behaviour.cs") },
            new[] { MetadataReference.CreateFromImage(image.ToArray()) }
                .Concat(TestHelper.StandardRefs),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
