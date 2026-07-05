using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Stage 2 M4 — generics tier reduction (design §8) + the generic-LF-as-method-group registration fix
/// (fcd54). Behavioural values are verified on the real Udon VM by the fcd corpus (fcd45a ri=9/rl=9,
/// fcd54 result=7); these tracked pins lock the emission-SHAPE and reject-tier invariants:
/// the former <c>ClosurePin.Capturing</c> tier is retired so a NON-type-param-dependent capturing
/// closure in a multi-instantiation generic compiles (its capture lives in a per-activation env, the
/// hoist is shared T-free), while the <c>TypeParamDependent</c> tier SURVIVES — a closure whose
/// signature or body references the enclosing generic's type parameter still pins the definition to one
/// instantiation, with its own distinguishable reject message. A mixed definition (one non-T closure +
/// one T-dependent closure) rejects as a whole (per-definition granularity, no partial legalization).
/// </summary>
public class Stage2M4GenericsTests
{
    // ── §8.1 the retired Capturing tier: non-T capture across two INSTANCE-method instantiations is legal ──

    [Fact]
    public void NonTypeParamCapturingGeneric_TwoInstantiations_StaysLegal()
    {
        // fcd45a shape: G<T> is an INSTANCE method holding a closure capturing an int local (no T in the
        // closure's sig/body). Both G<int> and G<long> compile; the capture has no flat cell (it is in a
        // per-activation env record) and both specs are emitted. Instance-method captures de-alias across
        // instantiations — divergent-value VM-proven (MinA3 Gen<int>/Gen<long> Match). (The aliasing that
        // B64 rejects is confined to STATIC generic methods, whose inlined specialization shares one hoist
        // without a per-activation env — see B64C in the round-3 tracked pins.)
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M4NonTCap : UdonSharpBehaviour {
    public int seed; public int ri; public int rl;
    void Start() { ri = G<int>(seed); rl = G<long>(seed); }
    int G<T>(int baseVal) {
        int cap = baseVal + 1;
        Func<int> f = () => cap * 2;
        T[] arr = new T[1];
        return f() + arr.Length;
    }
}", "M4NonTCap");
        Assert.Contains("_G_SystemInt32", uasm);
        Assert.Contains("_G_SystemInt64", uasm);
        Assert.DoesNotContain("__lcl_cap_", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
    }

    // ── §8.2 TypeParamDependent tier survives, distinguishable message, per-definition granularity ──

    [Fact]
    public void MixedGeneric_NonTPlusTDependentClosure_RejectsWithTypeParamMessage()
    {
        // fcd45b shape: f1 captures a non-T int (would be legal alone), f2 references T (`new T[]`) so
        // it is type-param-dependent. Per §8.2 the whole definition is pinned to one instantiation — no
        // partial legalization — and the SECOND distinct instantiation is loud with the TypeParamDependent
        // message (NOT the retired capture message), so it is distinguishable from the fcd45a legalization.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M4Mixed : UdonSharpBehaviour {
    public int seed; public int ri2; public int rl2;
    void Start() { ri2 = G2<int>(seed); rl2 = G2<long>(seed); }
    int G2<T>(int baseVal) {
        int cap = baseVal + 1;
        Func<int> f1 = () => cap * 2;
        Func<int> f2 = () => { T[] a = new T[1]; return a.Length + cap; };
        return f1() + f2();
    }
}", "M4Mixed"));
        Assert.Contains("type parameters", ex.Message);
        Assert.DoesNotContain("captures locals/parameters", ex.Message);
    }

    // ── fcd54: a generic capturing local function referenced ONLY as a method group registers ──

    [Fact]
    public void GenericCapturingLocalFunction_AsMethodGroup_Registers()
    {
        // fcd54 shape: Lf<T2> has its OWN type parameter, captures a non-T outer local, and is converted
        // to a delegate value as a method group `Lf<int>` — never invoked directly, so no [Y8] invoke
        // arm registered its specialization. HEAD threw 'Lambda/local function Lf not registered'; the
        // M4 fix registers the resolved spec on demand (RegisterGenericSpecialization, incl. the __envp
        // for the capture). VM-proven the capture crosses the bridge (result=7). The spec function and a
        // capturing env ctor are emitted, and the dispatch bridge for the spec is present.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M4GenLfMg : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        int cap = seed;
        int Lf<T2>(T2 x) { T2[] a = new T2[1]; return cap + a.Length; }
        Func<int, int> d = Lf<int>;
        result = d(0) + cap;
    }
}", "M4GenLfMg");
        Assert.Contains("_Lf_SystemInt32", uasm);
        Assert.Contains("__dlg_", uasm);
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
    }
}
