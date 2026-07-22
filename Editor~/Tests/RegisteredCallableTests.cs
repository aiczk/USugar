using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace USugar.Tests;

public class RegisteredCallableTests
{
    [Fact]
    public void AddCallable_ExposesRecordBackedReadOnlyViews()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { int M(int value) => value; }");
        var compilation = CSharpCompilation.Create("RegisteredCallable", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var method = (IMethodSymbol)model.GetDeclaredSymbol(
            tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single());
        var context = new MethodContext();
        var slot = context.Reserve(i => "m" + i);
        var function = new CFunction("M");
        var parameters = new[] { "value" };
        var returns = new[] { new ReturnSlot("result", StorageTypes.Int32) };

        var callable = context.AddCallable(method, function, slot, parameters, returns,
            MethodContext.ReceiverAbi.ObjectArray);

        Assert.Same(callable, context.Callables[method]);
        Assert.Same(function, context.Functions[method]);
        Assert.Equal(slot, context.Slots[method]);
        Assert.Same(parameters, context.ParamVarIds[method]);
        Assert.Same(returns, context.Returns[method]);
        Assert.Equal(MethodContext.ReceiverAbi.ObjectArray, callable.Receiver);
        Assert.Throws<ArgumentException>(() =>
            context.AddCallable(method, function, slot, parameters, returns));
    }

    [Fact]
    public void AddSyntheticCallable_UsesSharedCallableRecord()
    {
        var context = new MethodContext();
        var function = new CFunction("__bridge_M");

        var callable = context.AddSyntheticCallable("__bridge_M", function, null, null,
            MethodContext.CallableKind.Bridge);

        Assert.Same(callable, context.SyntheticCallables["__bridge_M"]);
        Assert.Same(function, callable.Function);
        Assert.Equal(MethodContext.CallableKind.Bridge, callable.Kind);
        Assert.Equal("__bridge_M", callable.Name);
        Assert.Throws<ArgumentException>(() => context.AddSyntheticCallable(
            "__bridge_M", function, null, null, MethodContext.CallableKind.Bridge));
    }
}
