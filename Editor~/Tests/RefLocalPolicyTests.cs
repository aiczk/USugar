using System;
using Xunit;

namespace USugar.Tests;

public class RefLocalPolicyTests
{
    [Fact]
    public void RefLocal_ToLocal_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public class RefLoc1 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int x = 1; ref int r = ref x; r = 5; sum = x; }
}", "RefLoc1"));
        Assert.Contains("ref local 'r' is not supported", ex.Message);
    }

    [Fact]
    public void RefLocal_ToArrayElement_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public class RefLoc2 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { int[] a = new int[1]; ref int r = ref a[0]; r = 7; sum = a[0]; }
}", "RefLoc2"));
        Assert.Contains("Udon has no variable aliases", ex.Message);
    }

    [Fact]
    public void RefLocal_ToStruct_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public struct RefS { public int v; }
public class RefLoc3 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { RefS s = new RefS(); ref RefS r = ref s; r.v = 9; sum = s.v; }
}", "RefLoc3"));
        Assert.Contains("Udon has no variable aliases", ex.Message);
    }

    [Fact]
    public void RefLocal_ToDelegate_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public class RefLoc4 : UdonSharp.UdonSharpBehaviour {
    public int sum;
    int M2() { return 2; }
    int M11() { return 11; }
    void Start() { System.Func<int> a = M2; ref System.Func<int> r = ref a; r = M11; sum = a(); }
}", "RefLoc4"));
        Assert.Contains("Udon has no variable aliases", ex.Message);
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
