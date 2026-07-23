using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// WaveJoint round-1 regression pins (2026-07-16).
/// C04: compound bitwise assignment on a small-int-backed enum fabricated a nonexistent
/// SystemByte/SystemInt16 op extern with a small result width — the registry's value-producing small-int
/// ops all return SystemInt32, and every caller computes small ops in promoted Int32 slots, so the
/// Built-in ABI resolution must promote enum underlyings for And/Or/Xor.
/// D02: an object[]-emulated value type (user struct / tuple / anonymous type) in an implicit stringify
/// surface (interpolation hole, string concat operand, compound `s += x`) silently printed
/// "System.Object[]" — RejectImplicitToString gains an aggregate arm and the compound-concat surface
/// now runs the same choke as the binary surface.
/// </summary>
public class WaveJointRound1RegressionTests
{
    // ── C04: small-int-backed enum compound bitwise ──

    [Fact]
    public void ByteEnumCompoundOr_ComputesInPromotedInt32()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum WjrF : byte { N = 0, B = 2, D = 8 }
public class WjrByteOr : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ WjrF f = (WjrF)(seed & 7); f |= WjrF.D; r = (int)f; }
}", "WjrByteOr");
        Assert.Contains("SystemInt32.__op_LogicalOr__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.DoesNotContain("SystemByte.__op_LogicalOr", uasm);
    }

    [Fact]
    public void ByteEnumCompoundAndXor_ComputeInPromotedInt32()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum WjrG : byte { N = 0, A = 1, B = 2, D = 8 }
public class WjrByteAndXor : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ WjrG f = (WjrG)(seed & 7); f &= ~WjrG.B; f ^= WjrG.A; r = (int)f; }
}", "WjrByteAndXor");
        Assert.Contains("SystemInt32.__op_LogicalAnd__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.Contains("SystemInt32.__op_LogicalXor__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.DoesNotContain("SystemByte.__op_LogicalAnd", uasm);
        Assert.DoesNotContain("SystemByte.__op_LogicalXor", uasm);
    }

    [Fact]
    public void ShortEnumCompoundOr_ComputesInPromotedInt32()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum WjrH : short { N = 0, A = 1, B = 2, D = 8 }
public class WjrShortOr : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ WjrH f = (WjrH)(seed & 7); f |= WjrH.D; r = (int)f; }
}", "WjrShortOr");
        Assert.Contains("SystemInt32.__op_LogicalOr__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.DoesNotContain("SystemInt16.__op_LogicalOr", uasm);
    }

    [Fact]
    public void ByteEnumEquality_KeepsUnpromotedByteExtern()
    {
        // Guard the bool-producing comparison path: SystemByte HAS equality/relational externs and the
        // operand slots stay byte-typed there — the promotion is scoped to the value-producing ops only.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum WjrI : byte { N = 0, D = 8 }
public class WjrByteEq : UdonSharpBehaviour {
    public int seed; public bool r;
    void Start(){ WjrI f = (WjrI)(seed & 7); r = f == WjrI.D; }
}", "WjrByteEq");
        Assert.Contains("SystemByte.__op_Equality__SystemByte_SystemByte__SystemBoolean", uasm);
    }

    [Fact]
    public void IntEnumCompoundOr_UnchangedInt32Extern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum WjrJ { N = 0, D = 8 }
public class WjrIntOr : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ WjrJ f = (WjrJ)(seed & 7); f |= WjrJ.D; r = (int)f; }
}", "WjrIntOr");
        Assert.Contains("SystemInt32.__op_LogicalOr__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    // ── D02: aggregate (object[]-emulated value type) implicit stringify is a loud reject ──

    [Fact]
    public void StructInterpolation_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct WjrS1 { public int v; public override string ToString(){ return ""s"" + v; } }
public class WjrStructInterp : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ WjrS1 t; t.v = seed; s = $""{t}""; }
}", "WjrStructInterp"));
        Assert.Contains("System.Object[]", ex.Message);
    }

    [Fact]
    public void StructConcat_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct WjrS2 { public int v; public override string ToString(){ return ""s"" + v; } }
public class WjrStructConcat : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ WjrS2 t; t.v = seed; s = ""x"" + t; }
}", "WjrStructConcat"));
        Assert.Contains("System.Object[]", ex.Message);
    }

    [Fact]
    public void StructCompoundConcat_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct WjrS3 { public int v; public override string ToString(){ return ""s"" + v; } }
public class WjrStructCompound : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ WjrS3 t; t.v = seed; s = ""x""; s += t; }
}", "WjrStructCompound"));
        Assert.Contains("System.Object[]", ex.Message);
    }

    [Fact]
    public void PlainStructInterpolation_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct WjrS4 { public int v; }
public class WjrPlainStruct : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ WjrS4 t; t.v = seed; s = $""{t}""; }
}", "WjrPlainStruct"));
    }

    [Fact]
    public void TupleInterpolation_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class WjrTuple : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ (int, int) t = (seed, seed + 1); s = $""{t}""; }
}", "WjrTuple"));
    }

    [Fact]
    public void NdimCompoundConcat_IsRejected()
    {
        // The compound `s += x` surface now runs the same RejectImplicitToString choke as the binary
        // surface — pin that an ndim operand stays a loud reject there.
        Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class WjrNdim : UdonSharpBehaviour {
    public string s;
    void Start(){ int[,] m = new int[2,2]; s = ""x""; s += m; }
}", "WjrNdim"));
    }

    [Fact]
    public void StructDirectToStringCall_StillCompiles()
    {
        // The explicit call is statically bound to the struct's override and stays legal — the reject is
        // scoped to the implicit surfaces where the bundle would launder past ToString entirely.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct WjrS5 { public int v; public override string ToString(){ return ""s"" + v; } }
public class WjrStructDirect : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ WjrS5 t; t.v = seed; s = t.ToString(); }
}", "WjrStructDirect");
        Assert.NotNull(uasm);
    }
}
