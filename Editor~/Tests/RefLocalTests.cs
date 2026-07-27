using Xunit;

namespace USugar.Tests;

/// <summary>
/// Ref locals lower to a runtime-selected prepared storage location. Reads and
/// writes both dispatch through the same selector, so aliases to locals, array
/// elements, aggregate slots, and delegate cells share one implementation.
/// </summary>
public class RefLocalTests
{
    [Fact]
    public void RefLocal_Declaration_AliasesLocal()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class RefLoc1 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int x = 1; ref int r = ref x; r = 5; sum = x; }
}", "RefLoc1");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void RefLocal_ToArrayElement_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class RefLoc2 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int[] a = new int[1]; ref int r = ref a[0]; r = 7; sum = a[0]; }
}", "RefLoc2");
        Assert.Contains(
            "SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid",
            uasm);
    }

    [Fact]
    public void RefLocal_ToStruct_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
public struct RefS { public int v; }
public class RefLoc3 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { RefS s = new RefS(); ref RefS r = ref s; r.v = 9; sum = s.v; }
}", "RefLoc3");
        Assert.Contains(
            "SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            uasm);
    }

    [Fact]
    public void RefLocal_DelegateTyped_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class RefLoc4 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    int M2() { return 2; }
    int M11() { return 11; }
    void Start() { System.Func<int> a = M2; ref System.Func<int> r = ref a; r = M11; sum = a(); }
}", "RefLoc4");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void RefOutParams_StillCompile()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class RefLoc5 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Twice(ref int v) { v *= 2; }
    int Give(out int r) { r = 3; return 1; }
    void Start() { int x = 5; Twice(ref x); int y; int k = Give(out y); sum = x * 100 + y * 10 + k; }
}", "RefLoc5");
        Assert.NotNull(uasm);
    }
}
