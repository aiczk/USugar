using Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace USugar.Tests;

/// <summary>
/// Tests for classification helpers moved to EmitContext and USugarCompilerHelper.
/// </summary>
public class ClassificationTests
{
    static (Compilation comp, INamedTypeSymbol cls) CompileClass(string source, string className = "Test")
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var comp = CSharpCompilation.Create("TestAsm", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);
        foreach (var decl in tree.GetRoot().DescendantNodes())
        {
            if (model.GetDeclaredSymbol(decl) is INamedTypeSymbol sym && sym.Name == className)
                return (comp, sym);
        }
        throw new System.InvalidOperationException($"Class {className} not found");
    }

    [Fact]
    public void IsExternNamespace_UnityEngine_ReturnsTrue()
    {
        var (comp, cls) = CompileClass("namespace UnityEngine { public class Foo {} }", "Foo");
        Assert.True(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsExternNamespace_System_ReturnsTrue()
    {
        var (comp, cls) = CompileClass("namespace System.Collections { public class Bar {} }", "Bar");
        Assert.True(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsExternNamespace_UserNamespace_ReturnsFalse()
    {
        var (comp, cls) = CompileClass("namespace MyGame.Utils { public class Helper {} }", "Helper");
        Assert.False(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsExternNamespace_GlobalNamespace_ReturnsFalse()
    {
        var (comp, cls) = CompileClass("public class Global {}", "Global");
        Assert.False(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsExternNamespace_UdonSharp_ReturnsFalse()
    {
        // IsExternNamespace excludes UdonSharp (unlike IsFrameworkNamespace which includes it)
        var (comp, cls) = CompileClass("namespace UdonSharp.Helpers { public class X {} }", "X");
        Assert.False(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsFrameworkNamespace_UdonSharp_ReturnsTrue()
    {
        var (comp, cls) = CompileClass("namespace UdonSharp.Helpers { public class X {} }", "X");
        Assert.True(USugarCompilerHelper.IsFrameworkNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsExternNamespace_VRC_ReturnsTrue()
    {
        var (comp, cls) = CompileClass("namespace VRC.SDK3 { public class Sdk {} }", "Sdk");
        Assert.True(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }

    [Fact]
    public void IsExternNamespace_TMPro_ReturnsTrue()
    {
        var (comp, cls) = CompileClass("namespace TMPro { public class Text {} }", "Text");
        Assert.True(USugarCompilerHelper.IsExternNamespace(cls.ContainingNamespace));
    }
}
