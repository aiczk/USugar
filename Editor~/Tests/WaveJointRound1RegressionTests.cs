using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// WaveJoint round-1 regression pins (2026-07-16).
/// C04: compound bitwise assignment on a small-int-backed enum fabricated a nonexistent
/// SystemByte/SystemInt16 op extern with a small result width — the registry's value-producing small-int
/// ops all return SystemInt32, and every caller computes small ops in promoted Int32 slots, so the
/// ResolveBinaryExtern enum arm must promote the underlying for And/Or/Xor.
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
}
