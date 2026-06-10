using Xunit;

namespace USugar.Tests;

/// <summary>
/// Round-7 [P2]: ref locals used to compile clean but emit as plain VALUE copies
/// (ILocalSymbol.IsRef was never consulted), silently decoupling the alias on the flat-heap VM
/// (VM-proven: write-through 1 vs CLR 5, read-through 1 vs 5, ref to array element 0 vs 7, ref to
/// struct + member write 0 vs 9, delegate flavor 2 vs 11). The VM has no variable aliases, so the
/// declaration is a loud reject per design §8-3; ref/out PARAMS keep their caller-side copy-back
/// convention (struct_ref_param sentinel) and stay legal.
/// </summary>
public class RefLocalTests
{
    [Fact]
    public void RefLocal_Declaration_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class RefLoc1 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int x = 1; ref int r = ref x; r = 5; sum = x; }
}", "RefLoc1"));
        Assert.Contains("ref local", ex.Message);
    }

    [Fact]
    public void RefLocal_ToArrayElement_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class RefLoc2 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int[] a = new int[1]; ref int r = ref a[0]; r = 7; sum = a[0]; }
}", "RefLoc2"));
        Assert.Contains("ref local", ex.Message);
    }

    [Fact]
    public void RefLocal_ToStruct_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public struct RefS { public int v; }
public class RefLoc3 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { RefS s = new RefS(); ref RefS r = ref s; r.v = 9; sum = s.v; }
}", "RefLoc3"));
        Assert.Contains("ref local", ex.Message);
    }

    [Fact]
    public void RefLocal_DelegateTyped_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
public class RefLoc4 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    int M2() { return 2; }
    int M11() { return 11; }
    void Start() { System.Func<int> a = M2; ref System.Func<int> r = ref a; r = M11; sum = a(); }
}", "RefLoc4"));
        Assert.Contains("ref local", ex.Message);
    }

    [Fact]
    public void RefOutParams_StillCompile()
    {
        // ref/out PARAMS are the supported alias-free convention — only ref LOCALS reject.
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
