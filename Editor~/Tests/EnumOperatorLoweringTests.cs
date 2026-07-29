using System;
using Xunit;

namespace USugar.Tests;

public class EnumOperatorLoweringTests
{
    const string Int32Add =
        "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32";
    static string CompileBody(string name, string body)
        => TestHelper.CompileToUasm($@"
using UdonSharp;
public class {name} : UdonSharpBehaviour {{
    public UnityEngine.KeyCode keyField;
    public int intField;
    void Start() {{ {body} }}
}}", name);

    [Theory]
    [InlineData("EnumIncPostfix", "keyField++;")]
    [InlineData("EnumIncPrefix", "++keyField;")]
    [InlineData("EnumCompoundAdd", "keyField += 1;")]
    public void RegisteredEnumAdditionProducer_Rejects(
        string name, string body)
    {
        var error = Assert.Throws<NotSupportedException>(
            () => CompileBody(name, body));
        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }

    [Theory]
    [InlineData("EnumDecPostfix", "keyField--;")]
    [InlineData("EnumDecPrefix", "--keyField;")]
    [InlineData("EnumCompoundSubtract", "keyField -= 1;")]
    public void RegisteredEnumSubtractionProducer_Rejects(
        string name, string body)
    {
        var error = Assert.Throws<NotSupportedException>(
            () => CompileBody(name, body));
        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }

    [Fact]
    public void RegisteredEnumConstantProducer_RemainsSupported()
    {
        var uasm = CompileBody(
            "EnumConstantProducer",
            "keyField = UnityEngine.KeyCode.Space;");
        Assert.Contains(
            "keyField: %UnityEngineKeyCode",
            uasm);
    }

    [Fact]
    public void PlainInt32IncrementKeepsItsNativeOperator()
    {
        var uasm = CompileBody("Int32Inc", "intField++;");
        Assert.Contains(Int32Add, uasm);
        Assert.Contains("intField: %SystemInt32", uasm);
    }
}
