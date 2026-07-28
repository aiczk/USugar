using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class BoundMethodBodyTableTests
{
    [Fact]
    public void MaterializerSharesOneOperationTreeAcrossCallableConsumers()
    {
        var compilation = TestHelper.BuildCompilation(@"
using System;
public class BodySnapshotClass
{
    int Value => 3;
    int Accessor { get => 4; }

    void Run()
    {
        int Local() => Value;
        Func<int> read = () => Local();
        _ = read();
    }
}", "BodySnapshotClass", out var classSymbol);
        var run = classSymbol.GetMembers("Run")
            .OfType<IMethodSymbol>().Single();
        var getter = classSymbol.GetMembers("Value")
            .OfType<IPropertySymbol>().Single().GetMethod;
        var accessorGetter = classSymbol.GetMembers("Accessor")
            .OfType<IPropertySymbol>().Single().GetMethod;
        var getOperationCount = 0;
        var materializer = new BoundMethodBodyTable.Materializer(
            compilation,
            syntax =>
            {
                getOperationCount++;
                return compilation.GetSemanticModel(
                        syntax.SyntaxTree)
                    .GetOperation(syntax);
            });

        var runBody = materializer.Get(run);
        var local = runBody.AnalysisRoot.DescendantsAndSelf()
            .OfType<ILocalFunctionOperation>().Single();
        var lambda = runBody.AnalysisRoot.DescendantsAndSelf()
            .OfType<IAnonymousFunctionOperation>().Single();

        Assert.Same(runBody, materializer.Get(run));
        Assert.Same(
            local,
            materializer.Get(local.Symbol).Root);
        Assert.Same(
            lambda,
            materializer.Get(lambda.Symbol).Root);

        var getterBody = materializer.Get(getter);
        Assert.NotNull(getterBody.AnalysisRoot);
        Assert.Same(getterBody, materializer.Get(getter));
        var accessorBody = materializer.Get(accessorGetter);
        Assert.NotNull(accessorBody.AnalysisRoot);
        Assert.Same(
            accessorBody,
            materializer.Get(accessorGetter));

        var frozen = materializer.Freeze(new[]
        {
            run,
            local.Symbol,
            lambda.Symbol,
            getter,
            accessorGetter,
        });
        Assert.Same(runBody, frozen.Require(run));
        Assert.Same(
            materializer.Get(local.Symbol),
            frozen.Require(local.Symbol));
        Assert.Same(getterBody, frozen.Require(getter));
        Assert.Same(
            accessorBody,
            frozen.Require(accessorGetter));
        Assert.Equal(3, getOperationCount);
    }

    [Fact]
    public void SourceLessCallableRemainsAnExplicitBodylessSnapshot()
    {
        var compilation = TestHelper.BuildCompilation(
            "public class BodySnapshotClass { }",
            "BodySnapshotClass",
            out _);
        var toString = compilation
            .GetSpecialType(SpecialType.System_Object)
            .GetMembers("ToString")
            .OfType<IMethodSymbol>()
            .Single(method =>
                !method.IsStatic
                && method.Parameters.Length == 0);
        var getOperationCount = 0;
        var materializer = new BoundMethodBodyTable.Materializer(
            compilation,
            syntax =>
            {
                getOperationCount++;
                return compilation.GetSemanticModel(
                        syntax.SyntaxTree)
                    .GetOperation(syntax);
            });

        var body = materializer.Get(toString);
        var frozen = materializer.Freeze(
            new[] { toString });

        Assert.False(body.HasSourceDeclaration);
        Assert.Null(body.AnalysisRoot);
        Assert.Same(body, frozen.Require(toString));
        Assert.Equal(0, getOperationCount);
    }
}
