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
    public void AddCallable_PlansAbiBeforeFunctionMaterialization()
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
        var function = new FlatFunction("M");
        var parameters = new[] { "value" };
        var returns = new[] { new ReturnSlot("result", StorageTypes.Int32) };

        var callable = context.AddCallable(
            method, slot, "M", null, "this",
            parameters, new[] { StorageTypes.Int32 }, returns,
            MethodContext.ReceiverAbi.ObjectArray);

        Assert.Same(callable, context.Callables[method]);
        Assert.Empty(context.Functions);
        Assert.Equal(slot, context.Slots[method]);
        Assert.Equal(parameters, context.ParamVarIds[method]);
        Assert.Equal(returns, context.Returns[method]);
        Assert.Equal(MethodContext.ReceiverAbi.ObjectArray, callable.Receiver);
        Assert.Throws<ArgumentException>(() =>
            context.AddCallable(
                method, slot, "M2", null, "this",
                parameters, new[] { StorageTypes.Int32 }, returns));

        context.FreezeCallableRegistry();
        Assert.Throws<InvalidOperationException>(() =>
            context.AddCallable(
                method, slot, "Late", null, "this",
                parameters, new[] { StorageTypes.Int32 }, returns));
        context.AddMaterializedFunction(callable, function);
        Assert.Same(function, context.Functions[method]);
    }

    [Fact]
    public void AddSyntheticCallable_UsesSharedCallableRecord()
    {
        var context = new MethodContext();
        var function = new FlatFunction("__bridge_M");

        var callable = context.AddSyntheticCallable("__bridge_M", function, null, null,
            MethodContext.CallableKind.Bridge);

        Assert.Same(callable, context.SyntheticCallables["__bridge_M"]);
        Assert.Same(function, context.RequireFunction(callable));
        Assert.Equal(MethodContext.CallableKind.Bridge, callable.Kind);
        Assert.Equal("__bridge_M", callable.Name);
        Assert.Throws<ArgumentException>(() => context.AddSyntheticCallable(
            "__bridge_M", function, null, null, MethodContext.CallableKind.Bridge));
    }
}
