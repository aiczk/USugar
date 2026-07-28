using Xunit;

namespace USugar.Tests;

public class ArrayDimensionAbiTests
{
    [Theory]
    [InlineData("uint", "SystemUInt32")]
    [InlineData("long", "SystemInt64")]
    [InlineData("ulong", "SystemUInt64")]
    [InlineData("byte", "SystemByte")]
    public void NonInt32ArrayDimensionConvertsAtTheConstructorBoundary(
        string sourceType, string udonType)
    {
        var uasm = TestHelper.CompileToUasm($@"
using UdonSharp;
public class ArrayDimension_{sourceType} : UdonSharpBehaviour {{
    public {sourceType} size;
    public int length;
    void Start() {{ length = new float[size].Length; }}
}}", $"ArrayDimension_{sourceType}");

        Assert.Contains(
            $"SystemConvert.__ToInt32__{udonType}__SystemInt32",
            uasm);
        Assert.Contains(
            "SystemSingleArray.__ctor__SystemInt32__SystemSingleArray",
            uasm);
    }

    [Theory]
    [InlineData("uint", "SystemUInt32")]
    [InlineData("long", "SystemInt64")]
    [InlineData("ulong", "SystemUInt64")]
    public void NonInt32ArrayIndexConvertsAtTheAccessBoundary(
        string sourceType, string udonType)
    {
        var uasm = TestHelper.CompileToUasm($@"
using UdonSharp;
public class ArrayIndex_{sourceType} : UdonSharpBehaviour {{
    public {sourceType} index;
    public int result;
    void Start() {{
        int[] values = new int[2];
        values[index] = 3;
        result = values[index];
    }}
}}", $"ArrayIndex_{sourceType}");

        Assert.Contains(
            $"SystemConvert.__ToInt32__{udonType}__SystemInt32",
            uasm);
        Assert.Contains(
            "SystemInt32Array.__Get__SystemInt32__SystemInt32",
            uasm);
        Assert.Contains(
            "SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid",
            uasm);
    }
}
