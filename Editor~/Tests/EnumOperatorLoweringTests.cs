using System.Linq;
using Xunit;

namespace USugar.Tests;

public class EnumOperatorLoweringTests
{
    const string Int32Add =
        "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32";
    const string Int32Subtract =
        "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32";

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
    public void RegisteredEnumAdditionResolvesToItsUnderlyingExtern(
        string name, string body)
    {
        var uasm = CompileBody(name, body);
        Assert.Contains(Int32Add, uasm);
        Assert.Contains("keyField: %UnityEngineKeyCode", uasm);
        Assert.DoesNotContain("UnityEngineKeyCode.__op_", uasm);
    }

    [Theory]
    [InlineData("EnumDecPostfix", "keyField--;")]
    [InlineData("EnumDecPrefix", "--keyField;")]
    [InlineData("EnumCompoundSubtract", "keyField -= 1;")]
    public void RegisteredEnumSubtractionResolvesToItsUnderlyingExtern(
        string name, string body)
    {
        var uasm = CompileBody(name, body);
        Assert.Contains(Int32Subtract, uasm);
        Assert.DoesNotContain("UnityEngineKeyCode.__op_", uasm);
    }

    [Fact]
    public void IncrementAndCompoundAddShareOneOperatorProducer()
    {
        var increment = CompileBody("EnumProducerInc", "keyField++;");
        var compound = CompileBody("EnumProducerCompound", "keyField += 1;");
        Assert.Equal(
            ExternNamesOf(increment, "op_Addition"),
            ExternNamesOf(compound, "op_Addition"));
    }

    [Fact]
    public void PlainInt32IncrementKeepsItsNativeOperator()
    {
        var uasm = CompileBody("Int32Inc", "intField++;");
        Assert.Contains(Int32Add, uasm);
        Assert.Contains("intField: %SystemInt32", uasm);
    }

    static string[] ExternNamesOf(string uasm, string member)
        => uasm.Split('\n')
            .Where(line => line.Contains("EXTERN") && line.Contains(member))
            .Select(line => line.Trim())
            .Distinct()
            .OrderBy(line => line, System.StringComparer.Ordinal)
            .ToArray();
}
