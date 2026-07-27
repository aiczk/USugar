using System;
using Xunit;

namespace USugar.Tests;

public class IteratorPolicyTests
{
    [Fact]
    public void IteratorMethod_IsRejectedBeforeLowering()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using System.Collections.Generic;
public class IteratorMethod : UdonSharpBehaviour
{
    IEnumerable<int> Values()
    {
        yield return 1;
    }
    void Start() { Values(); }
}", "IteratorMethod"));

        Assert.Contains("Iterator method 'Values' is not supported", ex.Message);
        Assert.Contains("no resumable call frame", ex.Message);
    }

    [Fact]
    public void LocalIterator_IsRejectedWithoutChangingItsOwner()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using System.Collections.Generic;
public class LocalIterator : UdonSharpBehaviour
{
    void Start()
    {
        IEnumerable<int> Values()
        {
            yield return 1;
        }
        Values();
    }
}", "LocalIterator"));

        Assert.Contains("Iterator method 'Values' is not supported", ex.Message);
    }

    [Fact]
    public void Foreach_RemainsAnArrayOnlyLanguageFeature()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using System.Collections.Generic;
public class EnumerableForeach : UdonSharpBehaviour
{
    IEnumerable<int> Values() => null;
    void Start()
    {
        foreach (var value in Values()) { }
    }
}", "EnumerableForeach"));

        Assert.Contains("Only arrays are supported", ex.Message);
    }
}
