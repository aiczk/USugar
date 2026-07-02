using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Stage 2 M5 — fcd57 / gotcha-3 fix: <c>EnvpParamFields</c> is keyed by the CONSTRUCTED generic
/// specialization (not <c>OriginalDefinition</c>), so two specs of one capturing generic local
/// function each get their own hidden <c>__envp</c> field instead of the second spec's
/// registration silently overwriting the first's.
///
/// The failure this pins: <c>Lf&lt;T2&gt;(int x)</c> does not reference T2 in its signature's used
/// positions or body, so it is non-T-dependent and the <c>TypeParamDependent</c> tier does not
/// reject a second instantiation (design §8.1) — both <c>Lf&lt;int&gt;</c> and <c>Lf&lt;long&gt;</c>
/// compile. Pre-fix, <c>RegisterGenericSpecialization</c> wrote
/// <c>EnvpParamFields[constructed.OriginalDefinition]</c>, so registering the SECOND spec
/// overwrote the first spec's entry; <c>____1_Lf</c>'s body then read <c>__2_Lf__envp</c> instead
/// of its own <c>__1_Lf__envp</c> (VM-proven: null envp on first use -> VmFault, or a stale env on
/// a later call -> silent wrong value, depending on call order). Behavioural value is verified on
/// the real Udon VM by the fcd corpus (fcd57: resultI=11, resultL=12); this tracked pin locks the
/// per-spec emission SHAPE so a regression is caught even without the harness.
/// </summary>
public class Stage2M5EnvpKeyingTests
{
    [Fact]
    public void MultiSpecCapturingGenericLocalFunction_EachSpecReadsItsOwnEnvpField()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class M5EnvpKeying : UdonSharpBehaviour {
    public int seed; public int resultI; public int resultL;
    void Start() {
        int cap = seed;
        int Lf<T2>(int x) { return cap + x; }
        Func<int, int> fi = Lf<int>;
        Func<int, int> fl = Lf<long>;
        resultI = fi(1);
        resultL = fl(2);
    }
}", "M5EnvpKeying");

        // Both specializations are emitted (non-T-dependent multi-instantiation stays legal).
        Assert.Contains("____1_Lf_SystemInt32:", uasm);
        Assert.Contains("____2_Lf_SystemInt64:", uasm);

        // Each __envp field is declared once, per spec.
        Assert.Contains("__1_Lf__envp: %SystemObjectArray", uasm);
        Assert.Contains("__2_Lf__envp: %SystemObjectArray", uasm);

        // Structural core of the fix: spec 1's body must reference its OWN envp field before the
        // next label, never the other spec's — and symmetrically for spec 2.
        var spec1 = SliceBetweenLabels(uasm, "____1_Lf_SystemInt32:", "____2_Lf_SystemInt64:");
        Assert.Contains("__1_Lf__envp", spec1);
        Assert.DoesNotContain("__2_Lf__envp", spec1);

        var spec2 = SliceBetweenLabels(uasm, "____2_Lf_SystemInt64:", ".export __dlg_1");
        Assert.Contains("__2_Lf__envp", spec2);
        Assert.DoesNotContain("__1_Lf__envp", spec2);
    }

    static string SliceBetweenLabels(string uasm, string startLabel, string endLabel)
    {
        var start = uasm.IndexOf(startLabel, StringComparison.Ordinal);
        Assert.True(start >= 0, $"label not found: {startLabel}");
        var end = uasm.IndexOf(endLabel, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"label not found after {startLabel}: {endLabel}");
        return uasm.Substring(start, end - start);
    }
}
