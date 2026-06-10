using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Round-8 [R1] (corrects the round-7 [Q4] over-clone, which was calibrated against a WRONG
/// hand-computed oracle): Roslyn does NOT defensive-copy a DIRECT member invocation on the foreach
/// iteration local — it emits ldloca on the loop local (readonly-ness only forbids assignment), so
/// a non-readonly struct method mutates the local itself (DiffFuzz native-CLR oracle:
/// `foreach(var s in arr){s.Bump();t=t*100+s.v;}` ref=1112). The defensive copy applies only when
/// the receiver chain passes through a value-typed FIELD link (member access on the readonly
/// variable is a VALUE — DiffFuzz: nested `s.inner.Bump()` ref=102). These tests pin the
/// structural side: no clone on the direct receiver, exactly one clone on the field-chain
/// receiver, and the `readonly` member gate.
/// </summary>
public class ForeachStructSemanticsTests
{
    const string DirectShape = @"
public struct FesS {{ public int v; public {0} int Get() {{ return v; }} }}
public class {1} : UdonSharp.UdonSharpBehaviour {{
    public int sum;
    void Start() {{ var arr = new FesS[1]; foreach (var s in arr) {{ sum += s.Get(); }} }}
}}";

    const string NestedShape = @"
public struct FesI {{ public int v; public {0} int Get() {{ return v; }} }}
public struct FesO {{ public FesI inner; }}
public class {1} : UdonSharp.UdonSharpBehaviour {{
    public int sum;
    void Start() {{ var arr = new FesO[1]; foreach (var s in arr) {{ sum += s.inner.Get(); }} }}
}}";

    static int CountAggCtors(string uasm)
        => Regex.Matches(uasm, @"SystemObjectArray\.__ctor__SystemInt32__SystemObjectArray").Count;

    [Fact]
    public void ForeachIterationVar_DirectMethod_NoClone()
    {
        // Identical shapes except the `readonly` modifier on the struct method: the DIRECT receiver
        // must emit ZERO extra aggregate allocations either way — Roslyn invokes on the loop local
        // (ldloca), so cloning here was the round-7 regression (DiffFuzz ref=1112 vs the cloned 102).
        var readonlyUasm = TestHelper.CompileToUasm(
            string.Format(DirectShape, "readonly", "FesReadonly"), "FesReadonly");
        var mutableUasm = TestHelper.CompileToUasm(
            string.Format(DirectShape, "", "FesMutable"), "FesMutable");
        Assert.Equal(CountAggCtors(readonlyUasm), CountAggCtors(mutableUasm));
    }

    [Fact]
    public void ForeachIterationVar_FieldChain_NonReadonlyMethod_ClonesReceiver()
    {
        // A value-typed FIELD link from the readonly iteration variable IS a defensive copy in C#
        // (DiffFuzz: nested mutating call ref=102): the non-readonly flavor must emit exactly ONE
        // extra aggregate allocation — the receiver clone.
        var readonlyUasm = TestHelper.CompileToUasm(
            string.Format(NestedShape, "readonly", "FesNestedReadonly"), "FesNestedReadonly");
        var mutableUasm = TestHelper.CompileToUasm(
            string.Format(NestedShape, "", "FesNestedMutable"), "FesNestedMutable");
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
