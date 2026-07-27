using Xunit;

namespace USugar.Tests;

public class IteratorProtocolTests
{
    [Fact]
    public void IteratorMethod_EmitsFactoryAndResumeProtocol()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
public class Yld : UdonSharpBehaviour {
    public int result;
    System.Collections.IEnumerator Steps(){
        yield return null;
        result = 2;
    }
    void Start(){
        var it = Steps();
        if (it.MoveNext()) result = 1;
        if (it.MoveNext()) result = 3;
    }
}", "Yld");
        Assert.Contains("__iter_", uasm);
        Assert.Contains("SystemObjectArray.__Set__", uasm);
    }

    [Fact]
    public void GenericEnumerable_ForeachUsesLazyResumeProtocol()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
using System.Collections.Generic;
public class YldEach : UdonSharpBehaviour {
    public int seed;
    public int result;
    IEnumerable<int> Values(int n) {
        int value = seed;
        for (int i = 0; i < n; i++) {
            value += i;
            yield return value;
        }
    }
    void Start() {
        foreach (var value in Values(3))
            result += value;
    }
}", "YldEach");
        Assert.Contains("__iter_", uasm);
        Assert.Contains("SystemBoolean", uasm);
        Assert.Contains("SystemObjectArray.__Get__", uasm);
    }

    [Fact]
    public void Enumerator_CurrentAndIndependentInstancesCompile()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
using System.Collections.Generic;
public class YldPair : UdonSharpBehaviour {
    public int result;
    IEnumerator<int> Values(int n) {
        int value = n;
        yield return value++;
        yield return value;
    }
    void Start() {
        var a = Values(1);
        var b = Values(10);
        a.MoveNext();
        b.MoveNext();
        result = a.Current * 100 + b.Current;
    }
}", "YldPair");
        Assert.Contains("__iter_", uasm);
        Assert.Contains("SystemObjectArray.__Get__", uasm);
    }

    [Fact]
    public void IteratorCapturedEnvironmentIsPartOfSuspendedFrame()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
using System;
using System.Collections.Generic;
public class YldCapture : UdonSharpBehaviour {
    public int seed;
    public int result;
    IEnumerable<int> Values() {
        int value = seed;
        Func<int> read = () => value;
        yield return read();
        value++;
        yield return read();
    }
    void Start() {
        foreach (var value in Values()) result += value;
    }
}", "YldCapture");
        Assert.Contains("__iter_", uasm);
        Assert.Contains("__env", uasm);
    }

    [Fact]
    public void LocalIteratorDoesNotTurnItsContainingMethodIntoAFactory()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
using System.Collections.Generic;
public class YldLocal : UdonSharpBehaviour {
    public int result;
    void Start() {
        IEnumerable<int> Values() {
            yield return 2;
            yield return 3;
        }
        foreach (var value in Values()) result += value;
    }
}", "YldLocal");
        Assert.Contains("__iter_", uasm);
    }

    [Fact]
    public void NullIteratorOperationsAreGuardedBeforeBundleAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
using System.Collections.Generic;
public class YldNull : UdonSharpBehaviour {
    public int result;
    void Start() {
        IEnumerator<int> iterator = null;
        if (iterator.MoveNext()) result = iterator.Current;
        iterator.Dispose();
    }
}", "YldNull");
        Assert.Contains(
            "SystemType.__IsInstanceOfType__SystemObject__SystemBoolean",
            uasm);
        Assert.Contains(
            "UnityEngineDebug.__LogError__SystemObject__SystemVoid",
            uasm);
    }
}
