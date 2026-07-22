using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace USugar.Tests;

public class CallableRegistrationPlanTests
{
    [Fact]
    public void Builder_FreezesRegistrationGatesOnce()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
class C
{
    void Own() { int Local() => 1; }
    static void Foreign() { }
    void StructMember() { }
    void BaseCopy() { }
}");
        var compilation = CSharpCompilation.Create("RegistrationPlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(d => d.Identifier.ValueText, d => (IMethodSymbol)model.GetDeclaredSymbol(d));
        var localSyntax = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var local = (IMethodSymbol)model.GetDeclaredSymbol(localSyntax);
        var reach = new ReachableBodies
        {
            ForeignStatics = new[] { methods["Foreign"], local },
            StructMembers = new[] { methods["StructMember"] },
            BaseCopies = new[] { methods["Own"], methods["BaseCopy"] },
        };
        var builder = new ClassCompilePlanBuilder(
            () => new[] { methods["Own"] }, _ => reach,
            () => Array.Empty<IOperation>());

        var plan = builder.Build();

        Assert.Equal(new[] { methods["Foreign"] }, plan.Registration.ForeignStatics);
        Assert.Equal(new[] { methods["StructMember"] }, plan.Registration.StructMethods);
        Assert.Equal(new[] { methods["BaseCopy"] }, plan.Registration.BaseInstanceMethods);
    }
}
