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
}
