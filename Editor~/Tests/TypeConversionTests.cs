using Xunit;

namespace USugar.Tests;

/// <summary>
/// Stage A acceptance tests for v2.2 type conversion correctness/efficiency.
/// Pre-quantified thresholds (set BEFORE implementation per Plan critique recommendation).
/// </summary>
public class TypeConversionTests
{
    static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Regression: byte chain `d = (byte)(a + b + c)` already emits the minimal
    /// promote/demote sequence. Each byte operand is promoted once (3 × ToInt32),
    /// and the final result is demoted once (1 × ToByte). The arithmetic itself
    /// runs in int. No double round-trip in the chain.
    /// </summary>
    [Fact]
    public void ByteChain_MinimalPromoteDemote()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ByteChainCheck : UdonSharpBehaviour {
    public byte a, b, c, d;
    void Start() { d = (byte)(a + b + c); }
}", "ByteChainCheck");

        var promotes = CountOccurrences(uasm, "SystemConvert.__ToInt32__SystemByte__SystemInt32");
        var demotes = CountOccurrences(uasm, "SystemConvert.__ToByte__SystemInt32__SystemByte");

        Assert.Equal(3, promotes); // one per byte operand
        Assert.Equal(1, demotes);  // single narrowing of the final int result
    }

    /// <summary>
    /// Stage A.1 (T16'): `byte a; a += 1` must produce an explicit ToInt32 promotion
    /// for the byte operand before the int-typed addition extern.
    ///
    /// Pre-T16' state (failing): CompoundAssignmentHandler rewrites the operator signature
    /// to SystemInt32 but pushes the byte slot directly, relying on Udon VM's implicit
    /// boxed-value coercion. This is fragile across SDK updates.
    /// Post-T16' state (passing): explicit SystemConvert.__ToInt32__SystemByte__SystemInt32
    /// is emitted on the operand path, matching ExpressionHandler's binary-op behaviour.
    /// </summary>
    [Fact]
    public void ByteCompound_OperandIsExplicitlyPromoted()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ByteCompoundCheck : UdonSharpBehaviour {
    public byte a;
    void Start() { a += 1; }
}", "ByteCompoundCheck");

        var promotes = CountOccurrences(uasm, "SystemConvert.__ToInt32__SystemByte__SystemInt32");
        Assert.True(promotes >= 1,
            $"Expected at least one explicit ToInt32 promote for the byte operand, got {promotes}.\nUASM:\n{uasm}");

        // Demotion back to byte must still happen exactly once.
        var demotes = CountOccurrences(uasm, "SystemConvert.__ToByte__SystemInt32__SystemByte");
        Assert.Equal(1, demotes);
    }

    /// <summary>
    /// Stage A.1 (T16'): `byte a; a++` must also produce explicit promotion.
    /// </summary>
    [Fact]
    public void ByteIncrement_OperandIsExplicitlyPromoted()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ByteIncCheck : UdonSharpBehaviour {
    public byte a;
    void Start() { a++; }
}", "ByteIncCheck");

        var promotes = CountOccurrences(uasm, "SystemConvert.__ToInt32__SystemByte__SystemInt32");
        Assert.True(promotes >= 1,
            $"Expected at least one explicit ToInt32 promote for byte ++, got {promotes}.\nUASM:\n{uasm}");
    }

    /// <summary>
    /// Stage A.2 (T15'): switch over enum with constant case labels must NOT emit
    /// runtime SystemConvert.__ToInt32__SystemObject conversions for the case constants.
    /// The enum value itself (`k`) must still be converted at runtime, so we expect
    /// exactly one ToInt32 from object — not one per case.
    ///
    /// Pre-T15' state (failing): SwitchHandler.BuildClauseCondition unconditionally calls
    /// EmitEnumToUnderlying on case values, which emits a runtime extern even for known
    /// constants. With 3 cases this produces 1 (k) + 3 (cases) = 4 ToInt32 calls.
    /// Post-T15' state (passing): case constants are folded at compile time, leaving
    /// only the 1 runtime conversion of the switched value.
    /// </summary>
    [Fact]
    public void EnumSwitch_CaseConstants_AreCompileTimeFolded()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class EnumSwitchCheck : UdonSharpBehaviour {
    public enum K { A, B, C }
    public K k;
    void Start() {
        switch (k) {
            case K.A: break;
            case K.B: break;
            case K.C: break;
        }
    }
}", "EnumSwitchCheck");

        var objectToInt = CountOccurrences(uasm, "SystemConvert.__ToInt32__SystemObject__SystemInt32");
        Assert.Equal(1, objectToInt);
    }

    [Fact]
    public void NullableByteNarrowing_ReBoxesWithByteTag_NotIdentityPassthrough()
    {
        // `byte? c = (byte)(a+b)` (nullable byte operands) lowers a lifted int? -> byte conversion. The byte? must
        // re-box a real byte (SystemConvert.__ToByte narrowing), NOT pass the boxed int through unchanged — else a
        // later `.Value`'s strict ToInt32(SystemByte) InvalidCasts on the boxed int at runtime.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NblByteNarrow : UdonSharpBehaviour {
    public byte bf; public byte cf; public int outv;
    void Start() { byte? a = bf; byte? b = cf; byte? c = (byte)(a + b); outv = c.Value; }
}", "NblByteNarrow");
        Assert.Contains("SystemConvert.__ToByte__SystemInt32__SystemByte", uasm);
    }

    [Fact]
    public void BareToNullableByteNarrowing_EmitsNarrowing()
    {
        // `byte? c = (byte?)(intExpr)` (bare source, nullable dest) must narrow to byte, not box a raw int.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BareNblByte : UdonSharpBehaviour {
    public byte bf; public byte cf; public int outv;
    void Start() { byte? c = (byte?)(bf + cf); outv = c.Value; }
}", "BareNblByte");
        Assert.Contains("SystemConvert.__ToByte__SystemInt32__SystemByte", uasm);
    }

    [Fact]
    public void LongToUlong_BitReinterprets_NotCheckedConvert()
    {
        // (ulong)longVal is an unchecked bit reinterpret in C#; Convert.ToUInt64 is CHECKED and throws on a
        // negative value. USugar must round-trip the bytes via BitConverter, not emit SystemConvert.ToUInt64.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class LongUlongCast : UdonSharpBehaviour {
    public long a; public int outv;
    void Start() { ulong u = (ulong)a; outv = (int)(u & 255UL); }
}", "LongUlongCast");
        Assert.Contains("SystemBitConverter.__ToUInt64__SystemByteArray_SystemInt32__SystemUInt64", uasm);
        Assert.DoesNotContain("SystemConvert.__ToUInt64__SystemInt64__SystemUInt64", uasm);
    }
}
