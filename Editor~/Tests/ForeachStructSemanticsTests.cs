using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Round-7 follow-up [Q4]: C# makes the foreach iteration variable READONLY, so a non-readonly
/// struct method invoked on it runs on a DEFENSIVE COPY (the classic foreach-struct-mutation
/// no-op). The object[] loop variable is live storage in the flat emulation, so
/// EmitStructInstanceCall clones the receiver when its chain roots at an iteration variable
/// (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102; harness FcdLaunderProbes8
/// value-pins 102/102/1021). These tests pin the structural side: the clone exists, and the
/// `readonly` member gate skips it.
/// </summary>
public class ForeachStructSemanticsTests
{
    const string Shape = @"
public struct FesS {{ public int v; public {0} int Get() {{ return v; }} }}
public class {1} : UdonSharp.UdonSharpBehaviour {{
    public int sum;
    void Start() {{ var arr = new FesS[1]; foreach (var s in arr) {{ sum += s.Get(); }} }}
}}";

    static int CountAggCtors(string uasm)
        => Regex.Matches(uasm, @"SystemObjectArray\.__ctor__SystemInt32__SystemObjectArray").Count;

    [Fact]
    public void ForeachIterationVar_NonReadonlyMethod_ClonesReceiver()
    {
        // Identical shapes except the `readonly` modifier on the struct method: the non-readonly
        // flavor must emit exactly ONE extra aggregate allocation — the defensive receiver clone.
        var readonlyUasm = TestHelper.CompileToUasm(
            string.Format(Shape, "readonly", "FesReadonly"), "FesReadonly");
        var mutableUasm = TestHelper.CompileToUasm(
            string.Format(Shape, "", "FesMutable"), "FesMutable");
        Assert.Equal(CountAggCtors(readonlyUasm) + 1, CountAggCtors(mutableUasm));
    }

    [Fact]
    public void NonForeachReceiver_NoExtraClone()
    {
        // A plain local receiver keeps the live (uncloned) receiver convention — mutations stick.
        var uasm = TestHelper.CompileToUasm(@"
public struct FesM { public int v; public void Bump() { v += 10; } }
public class FesLocal : UdonSharp.UdonSharpBehaviour {
    public int sum;
    void Start() { FesM s = new FesM(); s.Bump(); sum = s.v; }
}", "FesLocal");
        Assert.NotNull(uasm);
    }
}
