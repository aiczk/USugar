using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// WaveJoint round-3 regression pins (2026-07-17).
/// A11: an inline-cast enum concat operand (`s += (E)(intExpr)`, `"" + (E)(intExpr)`) printed the
/// underlying number — UnwrapConversions stripped the user's enum cast along with the compiler's
/// boxing conversion, so the IsUserEnum/class checks saw the cast's INPUT type. The concat surfaces
/// now use UnwrapConcatOperand, which strips only value-preserving conversions (identity / boxing /
/// reference) and stops at value conversions — fixing the mirror direction too (`"" + (int)e`
/// name-stringified where C# prints the number).
/// </summary>
public class WaveJointRound3RegressionTests
{
    // ── A11: inline enum-cast concat operand synthesizes the name string ──

    [Fact]
    public void CompoundInlineCastEnum_MintsEnumNameHelper()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum Wj3E1 { Alpha, Bravo, Charlie }
public class Wj3A : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ s = """"; s += (Wj3E1)(seed % 3); }
}", "Wj3A");
        Assert.Contains("__enumstr_", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Bravo"));
    }

    [Fact]
    public void BinaryInlineCastEnum_MintsEnumNameHelper()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum Wj3E2 { Alpha, Bravo, Charlie }
public class Wj3B : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ s = """" + (Wj3E2)(seed % 3); }
}", "Wj3B");
        Assert.Contains("__enumstr_", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Bravo"));
    }

    [Fact]
    public void ExplicitIntCastOfEnum_DoesNotNameStringify()
    {
        // Mirror direction: `"" + (int)e` prints the NUMBER in C# — the old full unwrap landed on the
        // enum local and synthesized the name table. No __enumstr_ helper may be minted here.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum Wj3E3 { Alpha, Bravo, Charlie }
public class Wj3C : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ Wj3E3 e = (Wj3E3)(seed % 3); s = """" + (int)e; s += (int)e; }
}", "Wj3C");
        Assert.DoesNotContain("__enumstr_", uasm);
        Assert.DoesNotContain(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Bravo"));
    }
}
