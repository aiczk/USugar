using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class CallableSiteTests
{
    [Fact]
    public void FromOperation_NormalizesAllCallableLanguageShapes()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
using System;
class C
{
    public C() { }
    public int P { get; set; }
    public event Action E;
    public void M() { }
    public static C operator +(C x, C y) => x;
    public static implicit operator int(C x) => 1;
    void Use(C c)
    {
        c.M();
        var n = new C();
        var p = c.P;
        c.P = 1;
        c.E += null;
        c.E -= null;
        var sum = c + c;
        int converted = c;
    }
}");
        var compilation = CSharpCompilation.Create("CallableSites", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var use = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Use");
        var operation = model.GetOperation(use);

        var kinds = operation.DescendantsAndSelf()
            .SelectMany(CallableSites.FromOperation)
            .Select(site => site.Kind)
            .ToHashSet();

        Assert.Contains(CallableSiteKind.Method, kinds);
        Assert.Contains(CallableSiteKind.Constructor, kinds);
        Assert.Contains(CallableSiteKind.PropertyGet, kinds);
        Assert.Contains(CallableSiteKind.PropertySet, kinds);
        Assert.Contains(CallableSiteKind.EventAdd, kinds);
        Assert.Contains(CallableSiteKind.EventRemove, kinds);
        Assert.Contains(CallableSiteKind.Operator, kinds);
        Assert.Contains(CallableSiteKind.Conversion, kinds);
    }
}
