using System;
using Xunit;

namespace USugar.Tests;

public class InterpolatedStringSemanticTests
{
    [Fact]
    public void LiteralBraces_AreEscapedForCompositeFormat()
    {
        var (_, constants) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class InterpolationBraces : UdonSharpBehaviour
{
    public string result;
    void Start()
    {
        int value = 5;
        result = $""{{{value}}}"";
    }
}", "InterpolationBraces");

        Assert.Contains(
            constants,
            constant => constant.UdonType == "SystemString"
                        && Equals(constant.Value, "{{{0}}}"));
    }

    [Fact]
    public void LiteralOnlyInterpolatedString_DoesNotLeakFormatEscapes()
    {
        var (_, constants) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class InterpolationLiteralBraces : UdonSharpBehaviour
{
    public string result;
    void Start()
    {
        result = $""{{}}"";
    }
}", "InterpolationLiteralBraces");

        Assert.Contains(
            constants,
            constant => constant.UdonType == "SystemString"
                        && Equals(constant.Value, "{}"));
        Assert.DoesNotContain(
            constants,
            constant => constant.UdonType == "SystemString"
                        && Equals(constant.Value, "{{}}"));
    }

    [Fact]
    public void FoldedEnum_DecimalHexAndGeneralFormats_Compile()
    {
        var (uasm, constants) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum InterpolationTone : byte
{
    Ten = 10
}
public class InterpolationEnumFormats : UdonSharpBehaviour
{
    public string result;
    void Start()
    {
        InterpolationTone value = InterpolationTone.Ten;
        result = $""{value:D}|{value:X}|{value:G}"";
    }
}", "InterpolationEnumFormats");

        Assert.Contains(
            constants,
            constant => constant.UdonType == "SystemString"
                        && Equals(
                            constant.Value,
                            "{0:D}|{1:X}|{2:G}"));
        Assert.Contains("__enumstr_", uasm);
        Assert.Contains(
            "SystemString.__Format__SystemString_SystemObject_SystemObject_SystemObject__SystemString",
            uasm);
    }

    [Fact]
    public void NullableFoldedEnum_GeneralFormat_IsNullAware()
    {
        var (uasm, constants) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum InterpolationNullableTone : byte
{
    Ten = 10
}
public class InterpolationNullableEnum : UdonSharpBehaviour
{
    public bool present;
    public string result;
    void Start()
    {
        InterpolationNullableTone? value =
            present ? InterpolationNullableTone.Ten : null;
        result = $""[{value:G}]"";
    }
}", "InterpolationNullableEnum");

        Assert.Contains(
            constants,
            constant => constant.UdonType == "SystemString"
                        && Equals(constant.Value, "[{0:G}]"));
        Assert.Contains("__enumstr_", uasm);
        Assert.Contains(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            uasm);
    }

    [Fact]
    public void NullableTupleInterpolation_UsesNullAwareTupleString()
    {
        var (uasm, constants) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class InterpolationNullableTuple : UdonSharpBehaviour
{
    public bool present;
    public string result;
    void Start()
    {
        (int, int)? value = present ? (1, 2) : null;
        result = $""[{value}]"";
    }
}", "InterpolationNullableTuple");

        Assert.Contains(
            constants,
            constant => constant.UdonType == "SystemString"
                        && Equals(constant.Value, "[{0}]"));
        Assert.Contains(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            uasm);
        Assert.Contains(
            "SystemString.__Format__SystemString_SystemObject__SystemString",
            uasm);
    }

    [Fact]
    public void NullableStructInterpolation_ReachesToStringOverride()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct InterpolationNullableStruct
{
    public int value;
    public override string ToString()
    {
        return ""S"" + value;
    }
}
public class InterpolationNullableStructHost : UdonSharpBehaviour
{
    public bool present;
    public string result;
    void Start()
    {
        InterpolationNullableStruct? value =
            present
                ? new InterpolationNullableStruct { value = 7 }
                : (InterpolationNullableStruct?)null;
        result = $""[{value}]"";
    }
}", "InterpolationNullableStructHost");

        Assert.Contains("__1_ToString", uasm);
        Assert.Contains(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            uasm);
    }

    [Fact]
    public void HoleExpressions_AreStagedBeforeBundleToString()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class InterpolationStageValue
{
    public int value;
    public override string ToString()
    {
        value++;
        return ""T"";
    }
}
public class InterpolationStageHost : UdonSharpBehaviour
{
    public string result;
    void Start()
    {
        var value = new InterpolationStageValue();
        result = $""{value}{value.value}"";
    }
}", "InterpolationStageHost");

        var start = uasm.Substring(
            uasm.IndexOf(
                "_start__body:",
                StringComparison.Ordinal));
        var laterHoleRead = start.IndexOf(
            "SystemObjectArray.__Get__SystemInt32__SystemObject",
            StringComparison.Ordinal);
        var firstHoleStringification = start.IndexOf(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            StringComparison.Ordinal);

        Assert.True(laterHoleRead >= 0);
        Assert.True(firstHoleStringification > laterHoleRead);
    }

    [Fact]
    public void FoldedEnum_FlagsFormat_IsRejectedLoudly()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum InterpolationBits
{
    A = 1,
    B = 2
}
public class InterpolationEnumFlagsFormat : UdonSharpBehaviour
{
    public string result;
    void Start()
    {
        InterpolationBits value =
            InterpolationBits.A | InterpolationBits.B;
        result = $""{value:F}"";
    }
}", "InterpolationEnumFlagsFormat"));

        Assert.Contains("supports G, D, and X", error.Message);
    }

    [Fact]
    public void CompilerBundleIFormattable_IsRejectedLoudly()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public struct InterpolationFormattable : IFormattable
{
    public string ToString(
        string format, IFormatProvider provider)
    {
        return format;
    }
}
public class InterpolationFormattableHost : UdonSharpBehaviour
{
    public string result;
    void Start()
    {
        var value = new InterpolationFormattable();
        result = $""{value:X}"";
    }
}", "InterpolationFormattableHost"));

        Assert.Contains("IFormattable", error.Message);
        Assert.Contains(
            "explicitly before interpolation", error.Message);
    }
}
