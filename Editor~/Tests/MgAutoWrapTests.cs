using Xunit;

namespace USugar.Tests;

// MG auto-wrap A/B pins (design 2026-07-11 v2). Real-VM value gates live in the harness
// (MgAutoWrapVmTests); these pin compile acceptance, portable class-receiver crossing, the remaining
// struct-receiver boundary reject, and the null-receiver timing deviation.
public class MgAutoWrapTests
{
    [Fact]
    public void ClassInstanceMg_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class NodeP { public int W; public int M(){ return W; } }
public class MgP1 : UdonSharpBehaviour {
    public int result;
    void Start(){ var n = new NodeP(); n.W = 3; System.Func<int> f = n.M; result = f(); }
}", "MgP1");

    [Fact]
    public void StructInstanceMg_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public struct SP { public int V; public int L(){ return V; } }
public class MgP2 : UdonSharpBehaviour {
    public int result;
    void Start(){ var s = new SP(); s.V = 4; System.Func<int> f = s.L; result = f(); }
}", "MgP2");

    [Fact]
    public void NullReceiverMg_CompilesWithDispatchGuard()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class NodeN { public int M(){ return 1; } }
public class MgP3 : UdonSharpBehaviour {
    public int result;
    void Start(){ NodeN n = null; System.Func<int> f = n.M; result = f(); }
}", "MgP3");

    [Fact]
    public void ClassMg_CrossProgramStore_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
public class NodeX { public int W; public int M(){ return W; } }
public class MgP4 : UdonSharpBehaviour {
    public MgP4 other;
    public System.Func<int> d;
    void Start(){ var n = new NodeX(); other.d = n.M; }
}", "MgP4");
        Assert.Contains("__SetProgramVariable__", uasm);
        Assert.Contains("_rcv", uasm);
    }

    [Fact]
    public void StructMg_CrossProgramStore_Rejects()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"using UdonSharp;
public struct StructNodeX { public int W; public int M(){ return W; } }
public class MgP5 : UdonSharpBehaviour {
    public MgP5 other;
    public System.Func<int> d;
    void Start(){ var n = new StructNodeX(); other.d = n.M; }
}", "MgP5"));
        Assert.Contains("cross-program field", ex.Message);
    }
}
