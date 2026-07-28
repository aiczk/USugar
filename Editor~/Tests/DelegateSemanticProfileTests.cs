using System;
using Xunit;

namespace USugar.Tests;

public class DelegateSemanticProfileTests
{
    [Fact]
    public void CallableOperations_RemainSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class CallableDelegateHost : UdonSharpBehaviour
{
    public int result;
    void AddOne() { result++; }
    void Start()
    {
        Action callback = AddOne;
        callback += AddOne;
        callback -= AddOne;
        if (callback != null)
            callback();
    }
}", "CallableDelegateHost");

        Assert.Contains("JUMP_INDIRECT", uasm);
    }

    [Fact]
    public void DelegateValueEquality_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class DelegateEqualityHost : UdonSharpBehaviour
{
    bool Same(Action left, Action right)
        => left == right;
}", "DelegateEqualityHost"));

        Assert.Contains("callable-only", error.Message);
        Assert.Contains("delegate-to-delegate equality", error.Message);
    }

    [Fact]
    public void TupleEqualityContainingDelegate_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class DelegateTupleEqualityHost : UdonSharpBehaviour
{
    bool Same((Action, int) left, (Action, int) right)
        => left == right;
}", "DelegateTupleEqualityHost"));

        Assert.Contains("tuple equality", error.Message);
        Assert.Contains("callable-only", error.Message);
    }

    [Theory]
    [InlineData("callback.Equals(other)")]
    [InlineData("callback.GetHashCode() == 0")]
    [InlineData("callback.ToString() == null")]
    [InlineData("callback.GetType() == null")]
    [InlineData("callback.GetInvocationList() == null")]
    [InlineData("callback.Target == null")]
    [InlineData("callback.Method == null")]
    public void DelegateObjectSurface_IsRejected(
        string expression)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm($@"
using System;
using UdonSharp;
public class DelegateObjectHost : UdonSharpBehaviour
{{
    bool Observe(Action callback, Action other)
        => {expression};
}}", "DelegateObjectHost"));

        Assert.Contains("callable-only", error.Message);
    }

    [Fact]
    public void StaticObjectEquals_OnDelegates_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class DelegateObjectEqualsHost : UdonSharpBehaviour
{
    bool Same(Action left, Action right)
        => object.Equals(left, right);
}", "DelegateObjectEqualsHost"));

        Assert.Contains("object.Equals", error.Message);
    }

    [Fact]
    public void PersistedObjectErasure_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class DelegateErasureHost : UdonSharpBehaviour
{
    object Hide(Action callback) => callback;
}", "DelegateErasureHost"));

        Assert.Contains("conversion from delegate", error.Message);
    }

    [Fact]
    public void ImmediateSameSignatureRoundTrip_RemainsSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class DelegateRoundTripHost : UdonSharpBehaviour
{
    public int result;
    int Read() => 3;
    void Start()
    {
        Func<int> callback = Read;
        result = ((Func<int>)(object)callback)();
    }
}", "DelegateRoundTripHost");

        Assert.Contains("JUMP_INDIRECT", uasm);
    }
}
