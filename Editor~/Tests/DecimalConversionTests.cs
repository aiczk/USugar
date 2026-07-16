using Xunit;

namespace USugar.Tests;

// B72-axis finding (2026-07-16, first run of the UasmValidator COPY declared-type check): decimal↔numeric
// conversions fell through the numeric-conversion arm — its ExternResolver.IsNumericType guard lacks
// Decimal, so the arm's own decimal truncation branch was dead code — to the identity passthrough, which
// COPY'd raw mistyped boxes (a boxed Int32 into a %SystemDecimal slot and a boxed Decimal into a
// %SystemInt32 slot; both directions surfaced in the stock UdonSharp ArithmeticTest corpus). These pin
// the conversion externs; the COPY axis inside CompileToUasm re-flags any regression to a raw COPY.
public class DecimalConversionTests
{
    [Fact]
    public void DecimalToInt_TruncatesThenConverts()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DecToInt : UdonSharpBehaviour {
    public decimal d;
    public int result;
    void Start() { result = (int)d; }
}", "DecToInt");
        Assert.Contains("SystemMath.__Truncate__SystemDecimal__SystemDecimal", uasm);
        Assert.Contains("SystemConvert.__ToInt32__SystemDecimal__SystemInt32", uasm);
    }

    [Fact]
    public void IntToDecimal_Converts()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class IntToDec : UdonSharpBehaviour {
    public int seed;
    public decimal result;
    void Start() { result = seed; }
}", "IntToDec");
        Assert.Contains("SystemConvert.__ToDecimal__SystemInt32__SystemDecimal", uasm);
    }
}
