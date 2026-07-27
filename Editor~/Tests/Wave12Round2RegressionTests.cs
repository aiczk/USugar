using System;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 2 — tracked pins for the two VM-proven findings (real-VM value pins live in
/// the local harness corpora _w12_r2_min.json and _w12_r2_fixprobe.json):
///
/// [V1] A recursive call dispatched through a same-typed/base-typed/interface-typed VARIABLE
///      receiver (SetProgramVariable + SendCustomEvent + GetProgramVariable) was not treated as a
///      recursion-cycle spill site: the interface flavor had no call-graph edge at all, and the
///      class flavor had the static edge but the cross emission path never became a spill site —
///      so when the receiver held `this` at runtime the SendCustomEvent re-entered the SAME
///      program synchronously and clobbered every live param/local of the caller frame (VM-proven
///      ref=36 vs 0 field/local/base flavors, 180 vs 0 interface+closure, 75 vs 60 property
///      accessor, 69 vs 27 mutual A&lt;-&gt;B). Fixed on both sides:
///      analysis — UasmEmitter.CrossDispatchLocalTarget adds edges for the LOCAL method a cross
///      dispatch can land on (most-derived override / interface implementation), mirrored in
///      IsInternalCallTo / PropertyAccessorMatches so tail classification sees the sites;
///      emission — LoweringServices.TryMarkReentrantCrossDispatch flags the CCrossCall (methods,
///      property/indexer accessors, interface bridges) so InsertRecursionSpills wraps the
///      SendCustomEvent, WITH the SetProgramVariable copy-ins INSIDE the window
///      (CExternCall.PreSpillStmts): a self-recursive callee shares the caller's own param heap
///      vars, so a copy-in preceding the save would be captured post-clobber and the reload would
///      restore the clobbered value.
///
/// [V2] A VARIANT delegate-VALUE conversion (a Func&lt;string&gt; value flowing into a
///      Func&lt;object&gt;-typed field/local/param/return through C# co/contravariance) compiled clean
///      and silently dropped values across the dispatch: the callee's bridge writes the __dlgc_
///      channel keyed by its OWN signature while the dispatch site reads the channel keyed by the
///      receiving STATIC delegate type (VM-proven: __dlgc_Void__SystemString__ret written,
///      __dlgc_Void__SystemObject__ret read → null → NRE / lost value; contravariant Action arm
///      loses the argument the same way). The 'variant delegate bindings' policy tier already
///      rejected variant METHOD-GROUP creations (DelegateAbi.ValidateDelegateBinding) — the
///      delegate-to-delegate conversion hole is now closed the same loud way in
///      ExpressionHandler.VisitConversion (sig-part inequality under the Udon type mapping).
///      Equal-signature conversions and delegate&lt;-&gt;object passthrough are untouched. Also
///      load-bearing for §5.4's sig-filter soundness (SigFilterCoupledToVarianceReject).
/// </summary>
public class Wave12Round2RegressionTests
{
    static int Count(string s, string sub) => Regex.Matches(s, Regex.Escape(sub)).Count;

    const string RecurStackPush = "PUSH, __recurStack";
    const string SpvExtern = "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid";
    const string SceExtern = "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid";

    // ── [V1] same-typed field receiver: the cross dispatch is a spill site, copy-in inside the window ──

    const string FieldReceiverSelfRecursion = @"
using UdonSharp;
public class W12R2MinF : UdonSharpBehaviour {
    public int n; public int result;
    W12R2MinF self;
    public int Work(int m) {
        if (m <= 0) return 0;
        int r = self.Work(m - 1);
        return m + r;
    }
    void Start() { self = this; result = Work(n % 4 + 5); }
}";

    [Fact]
    public void FieldReceiverSelfRecursion_CrossDispatch_IsSpillWrapped_WithCopyInInsideWindow()
    {
        // VM-proven ref=36 vs 0 (m read after the reentrant call). Three live values are wrapped
        // around the ONE cross dispatch (param m, local r, the receiver leaf still needed by the
        // GetProgramVariable return read): 3 saves + 3 reloads = 6 __recurStack pushes. The window
        // ORDER is the load-bearing part: every save push precedes the SetProgramVariable copy-in
        // (which writes the CALLER'S OWN param heap var when the receiver is `this`), and every
        // reload push follows the SendCustomEvent — a save between copy-in and dispatch would
        // capture the already-clobbered m and the reload would restore m-1 (wrong value class).
        var uasm = TestHelper.CompileToUasm(FieldReceiverSelfRecursion, "W12R2MinF");
        Assert.Equal(8, Count(uasm, RecurStackPush));
        int spv = uasm.IndexOf(SpvExtern, StringComparison.Ordinal);
        int sce = uasm.IndexOf(SceExtern, StringComparison.Ordinal);
        Assert.True(spv > 0 && sce > spv, "expected exactly one SPV+SCE cross pair in program order");
        Assert.Equal(3, Count(uasm.Substring(0, spv), RecurStackPush));   // full save before the copy-in
        Assert.Equal(5, Count(uasm.Substring(sce), RecurStackPush));      // full reload plus grow helper
    }

    // ── [V1] interface-typed receiver (R225 form, capturing closure in the implementing method) ──

    [Fact]
    public void InterfaceReceiverRecursion_CrossDispatch_IsSpillWrapped()
    {
        // VM-proven ref=180 vs 0. Pre-fix the interface dispatch had NO call-graph edge at all
        // (TargetMethod is the interface member, never in the method set), so Work was never a
        // cycle member and NOTHING spilled. Post-fix the implementation edge closes the self-loop
        // and the non-tail cross site wraps 5 live values (env carrier, closure result r, receiver,
        // m, bundle) = 10 pushes.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IW12R2Wkr { int Work(int m); }
public class W12R2IfaceR : UdonSharpBehaviour, IW12R2Wkr {
    public int n; public int result;
    IW12R2Wkr self;
    public int Work(int m) {
        int local = m * 4;
        Func<int, int> c = k => { local = local + k; return local; };
        int r = c(m);
        if (m <= 0) return r;
        return r + self.Work(m - 1);
    }
    void Start() { self = this; result = Work(n % 4 + 5); }
}", "W12R2IfaceR");
        Assert.Equal(12, Count(uasm, RecurStackPush));
    }

    // ── [V1] property accessor recursion through a variable receiver ──

    [Fact]
    public void CrossPropertyGetterRecursion_IsSpillWrapped()
    {
        // VM-proven ref=75 vs 60 (the getter's live local `mine` was clobbered by the reentrant
        // accessor activation). The cross getter read is an accessor CALL edge now; the non-tail
        // site wraps 3 live values = 6 pushes.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12R2PropR : UdonSharpBehaviour {
    public int n; public int result;
    W12R2PropR self;
    int depth;
    public int Acc {
        get {
            if (depth <= 0) return 0;
            depth = depth - 1;
            int mine = depth + 10;
            int inner = self.Acc;
            return mine + inner;
        }
    }
    void Start() { self = this; depth = n % 3 + 4; result = Acc; }
}", "W12R2PropR");
        Assert.Equal(8, Count(uasm, RecurStackPush));
    }

    [Fact]
    public void CrossPropertySetterRecursion_IsSpillWrapped_WithValueCopyInInsideWindow()
    {
        // Setter flavor (raw SPV+SCE pair, preSpillStmts=1): the setter's VALUE param is copied in
        // by the SetProgramVariable — for a self-recursive setter that IS the caller's own `value`
        // heap var, so the save must precede it. 2 live values (mine, value snapshot) = 4 pushes,
        // full save before the copy-in, full reload after the dispatch.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12R2SetR : UdonSharpBehaviour {
    public int n; public int result;
    W12R2SetR self;
    int depth; int acc;
    public int Level {
        get { return acc; }
        set {
            int mine = value * 10;
            if (depth > 0) { depth = depth - 1; self.Level = value + 1; }
            acc = acc + mine + value;
        }
    }
    void Start() { self = this; depth = n % 3 + 3; Level = 1; result = acc; }
}", "W12R2SetR");
        Assert.Equal(6, Count(uasm, RecurStackPush));
        int spv = uasm.IndexOf(SpvExtern, StringComparison.Ordinal);
        int sce = uasm.IndexOf(SceExtern, StringComparison.Ordinal);
        Assert.True(spv > 0 && sce > spv, "expected exactly one SPV+SCE cross pair in program order");
        Assert.Equal(2, Count(uasm.Substring(0, spv), RecurStackPush));
        Assert.Equal(4, Count(uasm.Substring(sce), RecurStackPush));
    }

    // ── [V1] controls: tail sites and non-cycle cross calls stay unwrapped ──

    [Fact]
    public void TailOnlyCrossSelfRecursion_HasNoSpill()
    {
        // Statement-form tail self-dispatch reads nothing of its frame afterwards — the recursion
        // map keeps only non-tail edges, so no spill site exists (deep tail recursion must never
        // burn __recurStack budget; §4.4 direction, same rule as direct calls).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12R2TailC : UdonSharpBehaviour {
    public int n; public int result; public int acc;
    W12R2TailC self;
    public void Work(int m) {
        acc = acc + m;
        if (m <= 0) return;
        self.Work(m - 1);
    }
    void Start() { self = this; Work(n % 4 + 5); result = acc; }
}", "W12R2TailC");
        Assert.Equal(0, Count(uasm, RecurStackPush));
    }

    [Fact]
    public void NonCycleCrossCall_HasNoRecursionStack()
    {
        // A variable-receiver cross call OUTSIDE any recursion cycle must not register the frame:
        // no __recurStack heap var is declared at all.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12R2NoCyc : UdonSharpBehaviour {
    public int n; public int result;
    W12R2NoCyc self;
    public int Helper(int m) { return m * 2 + 1; }
    void Start() { self = this; int a = self.Helper(n + 3); result = a + n; }
}", "W12R2NoCyc");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [V2] → Stage 1.75 §2.3 (B-2): variant delegate-value conversions mint a wrapper ──

    [Fact]
    public void CovariantDelegateValueConversion_FieldAssignment_MintsWrapper_Compiles()
    {
        // Verbatim minimized repro from the former reject pin (now VM-verified in
        // Editor~/_local_harness/VarianceVmTests.cs — CLR ref result=4).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R2VarF : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> cb;
    string MakeTag() { return ""tag"" + seed; }
    void Start() {
        Func<string> d = MakeTag;
        cb = d;
        object o = cb();
        result = ((string)o).Length;
    }
}", "W12R2VarF");
        Assert.Contains("__dlg_wrap_", uasm);
    }

    [Fact]
    public void ContravariantActionValueConversion_MintsWrapper_Compiles()
    {
        // Contravariant arm: the ARGUMENT channel, wrapped the same way the covariant return is. Real
        // VM values in Editor~/_local_harness/VarianceVmTests.cs.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R2VarC : UdonSharpBehaviour {
    public int seed; public int result;
    int acc;
    void TakeObj(object o) { acc = acc + ((string)o).Length; }
    void Start() {
        Action<object> a = TakeObj;
        Action<string> b = a;
        b(""xy"" + seed);
        result = acc;
    }
}", "W12R2VarC");
        Assert.Contains("__dlg_wrap_", uasm);
    }

    // ── [V2] controls: passthrough shapes stay legal ──

    [Fact]
    public void DelegateObjectRoundtrip_SameType_CrossStatement_Throws()
    {
        // A same-type delegate → object → cast-back that crosses STATEMENTS (`object o = d;` then
        // `(Func<string>)o`) now rejects: the bounded check only sees `o`, an object local whose
        // runtime delegate signature is not statically visible at the cast. The accepted
        // over-rejection (design §8-3) — the in-expression form `(Func<string>)(object)d` still
        // compiles (pinned in Wave12Round5.SameSigDelegateRoundtripInOneExpression_Compiles).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R2ObjRt : UdonSharpBehaviour {
    public int seed; public int result;
    string MakeTag() { return ""tag"" + seed; }
    void Start() {
        Func<string> d = MakeTag;
        object o = d;
        Func<string> e = (Func<string>)o;
        result = e().Length;
    }
}", "W12R2ObjRt"));
        Assert.Contains("carries no statically visible signature", ex.Message);
    }

    [Fact]
    public void LambdaAssignedToFuncObject_Compiles()
    {
        // A lambda's signature is INFERRED from the receiving delegate type — there is no
        // delegate-to-delegate conversion and nothing variant to reject.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R2LamO : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        int cap = seed;
        Func<object> cb = () => ""tag"" + cap;
        object o = cb();
        result = ((string)o).Length;
    }
}", "W12R2LamO");
        Assert.Contains("__dlg", uasm);
    }
}
