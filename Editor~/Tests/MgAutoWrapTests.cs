using Xunit;

namespace USugar.Tests;

// MG auto-wrap A/B pins (design 2026-07-11 v2). Real-VM value gates live in the harness
// (MgAutoWrapVmTests); these pin compile acceptance, the cross-program boundary reject (the FATAL
// amendment - revert-red verified: disabling the classifier receiver arm makes CrossProgram fail),
// and the null-receiver timing deviation (C# binds-time NRE vs USugar dispatch-time LogError+default).
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

    // FATAL-amendment pin: a receiver-carrying MG delegate must NOT cross the program boundary.
    [Fact]
    public void ClassMg_CrossProgramStore_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"using UdonSharp;
public class NodeX { public int W; public int M(){ return W; } }
public class MgP4 : UdonSharpBehaviour {
    public MgP4 other;
    public System.Func<int> d;
    void Start(){ var n = new NodeX(); other.d = n.M; }
}", "MgP4"));
        Assert.NotNull(ex);
    }
}
