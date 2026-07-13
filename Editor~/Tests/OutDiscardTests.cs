using Xunit;

namespace USugar.Tests;

// M4 wave finding (2026-07-11, L1s_r2_c11): an `out _` discard argument to an internal call staged a
// null CValue into CInternalCall.Args (the discard read-leg returned null), crashing CoreVerify with
// an NRE (ICE). Fixed: the discard read leg returns a fresh typed scratch — sound because a callee
// always assigns an out param before any read.
public class OutDiscardTests
{
    [Fact]
    public void OutDiscard_LocalFunction_BothPositions()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class OutDis1 : UdonSharpBehaviour {
    public int seed; public int result;
    void Start(){
        void Split(int n, out int keep, out int drop){ keep = n * 2 + seed; drop = n - seed; }
        Split(seed + 5, out int k1, out _);
        Split(seed - 1, out _, out int d2);
        result = k1 * 1000 + d2;
    }
}", "OutDis1");

    [Fact]
    public void OutDiscard_ExternTryParse_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class OutDis2 : UdonSharpBehaviour {
    public int result;
    void Start(){ result = int.TryParse(""42"", out _) ? 1 : 0; }
}", "OutDis2");

    [Fact]
    public void OutDiscard_GenericLocalFunction_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class OutDis3 : UdonSharpBehaviour {
    public int seed; public int result;
    void Start(){
        void Give<T>(int n, out int v){ v = n + (default(T) == null ? 1 : 2); }
        Give<string>(seed, out _);
        Give<int>(seed, out int got);
        result = got;
    }
}", "OutDis3");
}
