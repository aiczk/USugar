using Xunit;

namespace USugar.Tests;

public class GenericUserInterfaceStorageTests
{
    [Theory]
    [InlineData("M<int>(0, b)", "int M<T>(T dummy, IBox<int> x) { return x == null ? 7 : x.Get(); }")]
    [InlineData("M<int>(b)", "int M<T>(IBox<T> x) { return x == null ? 7 : x.Get(); }")]
    [InlineData("M<int>(0)", "int M<T>(T dummy) { IBox<int> x = b; return x == null ? 7 : x.Get(); }")]
    public void GenericUserInterfaceUnderMonomorphizationStoresAsEventReceiver(
        string call, string method)
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IBox<T> { int Get(); }
public class Boxed : UdonSharpBehaviour, IBox<int> { public int Get() { return 7; } }
public class GStore : UdonSharpBehaviour {
    public int result;
    public IBox<int> b;
    void Start() { result = " + call + @"; }
    " + method + @"
}", "GStore");

        Assert.Contains("%VRCUdonCommonInterfacesIUdonEventReceiver", uasm);
        Assert.DoesNotContain("IBoxSystemInt32", uasm);
    }

    [Fact]
    public void NonGenericUserInterfaceUnderMonomorphizationIsUnchanged()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IPlain { int Get(); }
public class Plain : UdonSharpBehaviour, IPlain { public int Get() { return 7; } }
public class GPlain : UdonSharpBehaviour {
    public int result;
    public IPlain b;
    void Start() { result = M<int>(0, b); }
    int M<T>(T dummy, IPlain x) { return x == null ? 7 : x.Get(); }
}", "GPlain");

        Assert.Contains("%VRCUdonCommonInterfacesIUdonEventReceiver", uasm);
    }
}
