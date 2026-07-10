using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

// Per-spec closure separation (design 2026-07-10 v3, B64/B70/B89 root fix) — permanent structural pins.
// VM-behavioral twins (divergent-value Match oracles) live in the gitignored harness PerSpecClosureTests.
public class PerSpecClosureKeyTests
{
    // ── Key provenance (design §2B): the composite closure key compares enclosing-spec type args by
    // CLR SYMBOL identity, never by Udon type-name strings. Two user interfaces launder onto one Udon
    // tag (the B66/B76 class), so a GetUdonTypeName-keyed registry would unsoundly SHARE one closure
    // copy across Mk<IKp1>/Mk<IKp2> — this pin asserts the two instantiations emit two distinct
    // hoisted lambda functions. Anti-resurrection: fails if anyone re-keys the registry on names. ──
    [Fact]
    public void LaunderedTypeArgs_TwoInterfaceInstantiations_EmitTwoClosureCopies()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System; using UdonSharp;
public interface IKp1 {}
public interface IKp2 {}
public static class HKp {
    public static Func<int> Mk<T>(int c) { return () => c + 1; }
}
public class KpCls : UdonSharpBehaviour {
    public int seed; public int result;
    void Start(){ var a = HKp.Mk<IKp1>(seed); var b = HKp.Mk<IKp2>(seed + 5); result = a() + b(); }
}", "KpCls");
        var lambdaFuncs = Regex.Matches(uasm, @"__\d+_lambda\b")
            .Select(m => m.Value).Distinct().Count();
        Assert.True(lambdaFuncs >= 2,
            $"Expected two per-spec lambda copies for the laundered interface args, found {lambdaFuncs}.");
    }
}
