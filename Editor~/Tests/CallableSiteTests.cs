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
        c.P += 2;
        c.P++;
        c.E += null;
        c.E -= null;
        var sum = c + c;
        int converted = c;
        Action group = c.M;
        if (c is Deconstructable(var da, var db)) { }
    }
}
class Deconstructable { public void Deconstruct(out int a, out int b) { a = 0; b = 0; } }");
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

        var properties = operation.DescendantsAndSelf().OfType<IPropertyReferenceOperation>().ToArray();
        var read = properties.Single(p => p.Parent is IVariableInitializerOperation);
        var write = properties.Single(p => p.Parent is ISimpleAssignmentOperation);
        var compound = properties.Single(p => p.Parent is ICompoundAssignmentOperation);
        var increment = properties.Single(p => p.Parent is IIncrementOrDecrementOperation);
        Assert.Equal(new[] { CallableSiteKind.PropertyGet },
            CallableSites.FromOperation(read).Select(s => s.Kind));
        Assert.Equal(new[] { CallableSiteKind.PropertySet },
            CallableSites.FromOperation(write).Select(s => s.Kind));
        Assert.Equal(new[] { CallableSiteKind.PropertyGet, CallableSiteKind.PropertySet },
            CallableSites.FromOperation(compound).Select(s => s.Kind));
        Assert.Equal(new[] { CallableSiteKind.PropertyGet, CallableSiteKind.PropertySet },
            CallableSites.FromOperation(increment).Select(s => s.Kind));

        var methodGroup = operation.DescendantsAndSelf().OfType<IMethodReferenceOperation>().Single();
        var deconstruct = operation.DescendantsAndSelf().OfType<IRecursivePatternOperation>().Single();
        Assert.Equal(new[] { CallableSiteKind.Method },
            CallableSites.FromOperation(methodGroup).Select(s => s.Kind));
        Assert.Equal(new[] { CallableSiteKind.Method },
            CallableSites.FromOperation(deconstruct).Select(s => s.Kind));
        Assert.False(methodGroup is IInvocationOperation);
        Assert.False(deconstruct is IInvocationOperation);
    }
}
