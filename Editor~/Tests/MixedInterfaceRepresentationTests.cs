using Xunit;

namespace USugar.Tests;

public class MixedInterfaceRepresentationTests
{
    const string Source = @"
using UdonSharp;
public interface IMixedValue { int Read(); int Value { get; } }
public class MixedBehaviour : UdonSharpBehaviour, IMixedValue {
    public int Read() => 1;
    public int Value => 1;
}
public class MixedClass : IMixedValue {
    public int Read() => 2;
    public int Value => 2;
}
public class MixedInterfaceHost : UdonSharpBehaviour {
    public MixedBehaviour behaviour;
    public bool choose;
    public int result;
    void Start() {
        IMixedValue value = choose ? (IMixedValue)behaviour : new MixedClass();
        result = Read(value);
    }
    int Read(IMixedValue value) { return value.Read(); }
}
";

    [Fact]
    public void MixedBehaviourAndUserClass_MethodDispatch_ThrowsSharedDiagnostic()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() =>
            TestHelper.CompileToUasm(Source, "MixedInterfaceHost"));
        Assert.Contains("different runtime representations", ex.Message);
        Assert.Contains("IMixedValue", ex.Message);
    }

    [Fact]
    public void MixedBehaviourAndUserClass_PropertyDispatch_ThrowsSharedDiagnostic()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(
            Source.Replace("return value.Read();", "return value.Value;"), "MixedInterfaceHost"));
        Assert.Contains("different runtime representations", ex.Message);
        Assert.Contains("IMixedValue", ex.Message);
    }
}
