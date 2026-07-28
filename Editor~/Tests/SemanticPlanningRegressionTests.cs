using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class SemanticPlanningRegressionTests
{
    [Fact]
    public void NestedGenericOwnerChain_IsPartOfMethodIdentityAndEnvironment()
    {
        var compilation = TestHelper.BuildCompilation(@"
public class PlanOwner<T>
{
    public class Inner<U> { public void Read() { } }
}
public class PlanIdentityHost
{
    public PlanOwner<int>.Inner<string> A;
    public PlanOwner<float>.Inner<string> B;
}", "PlanIdentityHost", out var host);
        var fields = host.GetMembers().OfType<IFieldSymbol>().ToArray();
        var first = ((INamedTypeSymbol)fields[0].Type).GetMembers("Read")
            .OfType<IMethodSymbol>().Single();
        var second = ((INamedTypeSymbol)fields[1].Type).GetMembers("Read")
            .OfType<IMethodSymbol>().Single();

        var firstKey = SpecializationKey.ForMethod(first);
        var secondKey = SpecializationKey.ForMethod(second);
        Assert.False(firstKey.Equals(secondKey));
        Assert.Equal(2, firstKey.Arguments.Length);
        Assert.Equal(
            compilation.GetSpecialType(SpecialType.System_Int32),
            firstKey.Arguments[0],
            SymbolEqualityComparer.Default);
        Assert.Equal(
            compilation.GetSpecialType(SpecialType.System_String),
            firstKey.Arguments[1],
            SymbolEqualityComparer.Default);

        var environment = TypeEnvironment.ForMethod(first);
        var innerDefinition = first.OriginalDefinition.ContainingType;
        var outerParameter = innerDefinition.ContainingType
            .OriginalDefinition.TypeParameters.Single();
        var innerParameter = innerDefinition
            .OriginalDefinition.TypeParameters.Single();
        Assert.Equal(
            compilation.GetSpecialType(SpecialType.System_Int32),
            environment[outerParameter],
            SymbolEqualityComparer.Default);
        Assert.Equal(
            compilation.GetSpecialType(SpecialType.System_String),
            environment[innerParameter],
            SymbolEqualityComparer.Default);
    }

    [Fact]
    public void NestedGenericOwnerSpecializations_BindDistinctMethodPayloads()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlanOuter<T>
{
    public class Inner<U>
    {
        public T owner;
        public U inner;
        public int Read() { return 1; }
    }
}
public class PlanNestedHost : UdonSharpBehaviour
{
    public int result;
    void Start()
    {
        var a = new PlanOuter<int>.Inner<string>();
        var b = new PlanOuter<float>.Inner<string>();
        result = a.Read() + b.Read();
    }
}", "PlanNestedHost");

        Assert.Contains("Read", uasm);
    }

    [Fact]
    public void GenericUsing_RegistersClosedDisposeSpecialization()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct PlanResource<T> : IDisposable
{
    public T value;
    public void Dispose() { }
}
public class PlanUsingHost : UdonSharpBehaviour
{
    public int result;
    void Work<T>()
    {
        using (PlanResource<T> resource = new PlanResource<T>())
            result = result + 1;
    }
    void Start() { Work<int>(); }
}", "PlanUsingHost");

        Assert.Contains("Dispose", uasm);
    }

    [Fact]
    public void GenericUsingExpression_RegistersClosedDisposeSpecialization()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct PlanExpressionResource<T> : IDisposable
{
    public T value;
    public void Dispose() { }
}
public class PlanUsingExpressionHost : UdonSharpBehaviour
{
    public int result;
    void Work<T>()
    {
        PlanExpressionResource<T> resource = default;
        using (resource)
            result = result + 1;
    }
    void Start() { Work<int>(); }
}", "PlanUsingExpressionHost");

        Assert.Contains("Dispose", uasm);
    }

    [Fact]
    public void GenericAggregateFieldRead_DeepClonesClosedValueType()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public struct PlanCloneInner { public int value; }
public struct PlanCloneBox<T>
{
    public T value;
    public T Get() { return value; }
}
public class PlanCloneHost : UdonSharpBehaviour
{
    public int result;
    void Start()
    {
        PlanCloneInner source = default;
        source.value = 7;
        PlanCloneBox<PlanCloneInner> box = default;
        box.value = source;
        PlanCloneInner copy = box.Get();
        copy.value = 99;
        result = box.value.value;
    }
}", "PlanCloneHost", out var emitter);

        var getter = Assert.Single(
            emitter.FlatModule.Functions.Where(function =>
                function.Name.Contains("Get", StringComparison.Ordinal)));
        Assert.Contains(
            getter.Blocks.SelectMany(block => block.Instructions)
                .OfType<CExprStmt>()
                .Select(statement => statement.Expr)
                .OfType<CExternCall>(),
            call => call.Sig.Text.Contains(
                "SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
                StringComparison.Ordinal));
    }
}
