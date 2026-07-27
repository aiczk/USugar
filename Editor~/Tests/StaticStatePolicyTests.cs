using System;
using Xunit;

namespace USugar.Tests;

public class StaticStatePolicyTests
{
    [Fact]
    public void ConstAndFoldableStaticReadonly_AreStorageFree()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticConstants : UdonSharpBehaviour
{
    const int A = 2;
    static readonly int B = A + 3;
    public int result;
    void Start() => result = B;
}", "StaticConstants");

        Assert.DoesNotContain("__static_", uasm);
    }

    [Fact]
    public void StaticMethodAndComputedProperty_CompileWithoutStaticState()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticComputation : UdonSharpBehaviour
{
    static int Twice(int value) => value * 2;
    static int Answer => Twice(21);
    public int result;
    void Start() => result = Answer;
}", "StaticComputation");
    }

    [Theory]
    [InlineData("static int value;")]
    [InlineData("static readonly int[] value = { 1, 2 };")]
    [InlineData("static int value { get; set; }")]
    [InlineData("static event System.Action value;")]
    public void RuntimeStaticState_IsRejectedAtDeclaration(
        string declaration)
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm($@"
using UdonSharp;
public class RuntimeStaticState : UdonSharpBehaviour
{{
    {declaration}
    void Start() {{ }}
}}", "RuntimeStaticState"));

        Assert.Contains("Static state", ex.Message);
        Assert.Contains("C# static semantics", ex.Message);
    }

    [Fact]
    public void ExternalSourceStaticState_IsRejectedWhenUsed()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(new[]
            {
                @"public static class SharedState { public static int Value; }",
                @"using UdonSharp;
public class StaticStateReader : UdonSharpBehaviour
{
    public int result;
    void Start() => result = SharedState.Value;
}",
            }, "StaticStateReader"));

        Assert.Contains("Static state 'SharedState.Value'", ex.Message);
    }
}
