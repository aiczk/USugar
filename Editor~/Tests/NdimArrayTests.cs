using Xunit;

namespace USugar.Tests;

/// <summary>
/// N-dimensional array (design 2026-07-04) structural acceptance lattice + N-R1..R4 reject pins.
/// Layer1SilentWrongTests carries the flipped former-reject baseline (creation/element-read/write/
/// field compile); this file covers the rest of the lattice: compound/ref-out/Length/GetLength/Rank/
/// GetUpperBound/foreach/param/return, the new N-R1 extern-argument-boundary reject, the (audit-
/// surfaced) N-R4 Array-member reject, and a rank-4 compile-only probe (§7-3). Real-VM value
/// verification (round-trips, side-effecting-index single evaluation, D-N1 log+default/skip,
/// foreach row-major order) lives in the gitignored local harness (Editor~/_local_harness).
/// </summary>
public class NdimArrayTests
{
    // ── Acceptance lattice ──

    [Fact]
    public void CompoundAssignment_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Compound : UdonSharpBehaviour {
    public int[,] a;
    public void M() { a[0,1] += 5; }
}", "NA_Compound");
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32", uasm);
    }

    [Fact]
    public void IncrementDecrement_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_IncDec : UdonSharpBehaviour {
    public int[,] a;
    public void M() { a[0,1]++; a[1,0]--; }
}", "NA_IncDec");
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32", uasm);
    }

    [Fact]
    public void RefOutArgument_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_RefOut : UdonSharpBehaviour {
    int[,] a;
    void Helper(ref int x) { x = x + 1; }
    public void M() { a = new int[2,2]; Helper(ref a[0,1]); }
}", "NA_RefOut");
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32", uasm);
    }

    [Fact]
    public void OutArgument_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Out : UdonSharpBehaviour {
    int[,] a;
    void Helper(out int x) { x = 9; }
    public void M() { a = new int[2,2]; Helper(out a[1,1]); }
}", "NA_Out");
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32", uasm);
    }

    [Fact]
    public void Length_ReturnsBackingLength_NotBundleLength()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Length : UdonSharpBehaviour {
    public int result;
    void Start() { int[,] a = new int[2,3]; result = a.Length; }
}", "NA_Length");
        // Length must read the FLAT BACKING (SystemInt32Array), never the bundle (SystemObjectArray).
        Assert.Contains("SystemArray.__get_Length__SystemInt32", uasm);
    }

    [Fact]
    public void GetLength_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_GetLength : UdonSharpBehaviour {
    public int result;
    void Start() { int[,] a = new int[2,3]; result = a.GetLength(1); }
}", "NA_GetLength");
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    [Fact]
    public void Rank_IsCompileTimeConstant()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Rank : UdonSharpBehaviour {
    public int result;
    void Start() { int[,,] a = new int[2,2,2]; result = a.Rank; }
}", "NA_Rank");
        Assert.DoesNotContain("get_Rank", uasm); // no runtime extern call — folded to a literal 3
    }

    [Fact]
    public void GetUpperBound_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_UpperBound : UdonSharpBehaviour {
    public int result;
    void Start() { int[,] a = new int[2,3]; result = a.GetUpperBound(1); }
}", "NA_UpperBound");
        Assert.Contains("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void Foreach_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Foreach : UdonSharpBehaviour {
    public int sum;
    void Start() { int[,] a = new int[2,2] { {1,2}, {3,4} }; foreach (var v in a) sum += v; }
}", "NA_Foreach");
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void Param_And_Return_Compile()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_ParamReturn : UdonSharpBehaviour {
    int[,] Make() { return new int[2,2]; }
    int Read(int[,] a) { return a[0,0]; }
    public int result;
    void Start() { result = Read(Make()); }
}", "NA_ParamReturn");
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void FieldWholeValueAssignment_IsReferenceCopy()
    {
        // C# array assignment is a reference copy — mutating through either field must be visible via
        // the other (both fields hold the SAME bundle reference).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_RefCopy : UdonSharpBehaviour {
    int[,] a; int[,] b;
    public void M() { a = new int[2,2]; b = a; b[0,0] = 5; }
}", "NA_RefCopy");
        Assert.Contains("a: %SystemObjectArray", uasm);
        Assert.Contains("b: %SystemObjectArray", uasm);
    }

    [Fact]
    public void Rank4_CompilesButIsNotExecuted()
    {
        // §7-3: no rank cap in the general formula — a rank-4 case must at least COMPILE (execution is
        // only exercised through rank-3 in the real-VM harness).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Rank4 : UdonSharpBehaviour {
    public int result;
    void Start() {
        int[,,,] a = new int[2,2,2,2];
        a[0,1,0,1] = 7;
        result = a[0,1,0,1] + a.Rank + a.GetLength(3);
    }
}", "NA_Rank4");
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32", uasm);
    }

    [Fact]
    public void BoundsLogError_IsEmittedAtEveryElementAccess()
    {
        // D-N1: the LogError call is unconditionally present in the codegen (it's the else-arm of a
        // runtime if — always emitted, regardless of whether the OOB branch is ever taken at runtime).
        // Real-VM value verification of the branch itself (default-on-read / skip-on-write) is in the
        // harness (VM_NdimReadOob / VM_NdimWriteOob / VM_NdimRmwOob).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_LogErrorPresent : UdonSharpBehaviour {
    public int result;
    void Start() { int[,] a = new int[2,2]; result = a[0,0]; }
}", "NA_LogErrorPresent");
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }

    // ── N-R1: extern-argument-boundary reject ──

    [Fact]
    public void ExternArgument_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_ExternArg : UdonSharpBehaviour {
    public int[,] a;
    public void M() { UnityEngine.Debug.Log(a); }
}", "NA_ExternArg"));
        Assert.Contains("extern call boundary", ex.Message);
    }

    [Fact]
    public void ArrayCopyStaticIntrinsic_Compiles()
    {
        // Array.Copy(src, dst, n) resolves to a real SystemArray extern — a Rank>1 bundle passed as an
        // argument here is exactly the N-R1 hazard (its runtime shape is not a real System*Array).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_ArrayCopy : UdonSharpBehaviour {
    public int[,] a;
    public int[,] b;
    public void M() { System.Array.Copy(a, b, 4); }
}", "NA_ArrayCopy");
        Assert.Contains(
            "SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid",
            uasm);
    }

    // ── N-R2: [UdonSynced] reject (already-free via IsSyncableType — pin only) ──

    [Fact]
    public void UdonSynced_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Synced : UdonSharpBehaviour {
    [UdonSynced] public int[,] a;
}", "NA_Synced"));
        Assert.Contains("not supported by", ex.Message);
    }

    // ── N-R3: is-test reject (already-free via IsRuntimeDistinguishable — pin only) ──

    [Fact]
    public void IsTest_UsesBundleRuntimeIdentity()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_IsTest : UdonSharpBehaviour {
    public object o;
    public bool result;
    void Start() { result = o is int[,]; }
}", "NA_IsTest");
        Assert.Contains(
            "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
            uasm);
    }

    // ── N-R4: unsupported Array member reject (audit-surfaced — Clone is a REAL valid extern for
    // SystemObjectArray, so this is NOT already covered by "unresolved extern"; see the N-M0 report) ──

    [Fact]
    public void Clone_DeepCopiesLogicalArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Clone : UdonSharpBehaviour {
    public int[,] a;
    public void M() { var b = a.Clone(); }
}", "NA_Clone");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void CopyTo_CopiesLogicalElements()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_CopyTo : UdonSharpBehaviour {
    public int[,] a;
    public object[] dst;
    public void M() { a.CopyTo(dst, 0); }
}", "NA_CopyTo");
        Assert.Contains(
            "SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            uasm);
    }

    // ── Jagged control (must still compile unchanged — no regression) ──

    [Fact]
    public void Jagged_StillCompiles()
    {
        // Control: int[][] (rank-1-of-rank-1, NOT a bundle) must keep compiling unchanged — jagged
        // arrays are the pre-existing supported alternative to true multidim, untouched by this design.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NA_Jagged : UdonSharpBehaviour {
    public int M() { int[][] a = new int[2][]; a[0] = new int[3]; a[0][2] = 5; return a[0][2]; }
}", "NA_Jagged");
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32", uasm);
    }
}
