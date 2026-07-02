using System;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 1 — tracked pins for the three VM-proven findings (real-VM value pins live in
/// the local harness corpora _w12_r1_min.json and _w12_r1_fixprobe.json):
///
/// [V1] Non-tail recursion with a per-frame closure/delegate invocation overflowed the 512-entry
///      __recurStack ~20% earlier than the equivalent plain-call recursion (VM-proven VmFault at
///      102 frames on legal, compile-clean code). The §5.4 same-signature widening marked the
///      per-frame dispatch site Reentrant — every same-sig bridge-bearing method joined its callee
///      set — so each frame spilled its WHOLE frame at the dispatch too, tipping the innermost
///      frame over the budget. Fixed: per-site dispatch-target provenance
///      (UasmEmitter.TryResolvePreciseDispatchTargets) — a dispatch reading a local whose every
///      write is a delegate creation has a provably exact callee set (locals are not
///      foreign-writable through the sanctioned surface); it is Reentrant only when one of its own
///      callees reaches the containing SCC. Fields, params, elements, and foreign receivers keep
///      the blanket widening.
///
/// [V2] Cross-behaviour access to a NON-public property through a variable receiver targeted a
///      SendCustomEvent name matching no .export (EmitMethods only exports public accessors) — a
///      silent no-op on device (VM-proven: setter body never ran, ref result=24 vs 0). Fixed:
///      non-public NON-auto accessors reject loudly (mirrors the [W6] indexer and [J2] method
///      gates); non-public AUTO properties route through SetProgramVariable/GetProgramVariable on
///      the declared backing symbol (needs no entry point) — the direct arms were dead pre-fix
///      because DeclaringSyntaxReferences.IsEmpty is always false for source accessors. Public
///      accessor dispatch is byte-unchanged.
///
/// [V3] An Object/ValueType-inherited member (Equals/GetHashCode/ToString) on a TYPE-PARAMETER
///      receiver built its extern from the effective base class (SystemValueType.__Equals…, not a
///      registered extern), and ResolveExtern's Component fallback chain laundered it into
///      UnityEngineComponent.__Equals/__GetHashCode/__ToString — a Component extern applied to a
///      boxed value. Fixed: monomorphization re-routes the containing type to the concrete type
///      argument's own extern; user-struct type arguments reject loudly (ValueType field-wise
///      semantics are inexpressible); ResolveExtern no longer falls back for System.* containing
///      types (they can never be Component-derived).
/// </summary>
public class Wave12Round1RegressionTests
{
    static int Count(string s, string sub) => Regex.Matches(s, Regex.Escape(sub)).Count;

    // ── [V1] per-frame closure helper: the dispatch site must NOT spill the frame ──

    const string ClosureHelperRecursion = @"
using System;
using UdonSharp;
public class W12ErD : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    void Start() { M(n % 3 + 100); result = trace; }
    int M(int m) {
        int local = m % 13;
        Func<int, int> c = k => { trace = (trace * 3 + local + k) % 99991; return local + k; };
        int r = c(m);
        if (m <= 0) return r;
        int inner = M(m - 1);
        return inner + r + local;
    }
}";

    [Fact]
    public void ClosureHelperRecursion_DispatchSite_DoesNotSpillFrame()
    {
        // VM-proven ErD_D100: pre-fix 32 __recurStack pushes (recursion site 5 SET+5 GET, dispatch
        // arms 5-6 SET+GET each) → VmFault at 102 frames (5*102+6 > 512). Post-fix only the
        // recursion site spills: 10 pushes (5 spilled slots × save+restore), 102 frames fit.
        var uasm = TestHelper.CompileToUasm(ClosureHelperRecursion, "W12ErD");
        Assert.Equal(10, Count(uasm, "PUSH, __recurStack"));
    }

    [Fact]
    public void ClosureHelperRecursion_FieldRoutedDispatch_KeepsBlanketSpill()
    {
        // Soundness control: the same shape dispatching a FIELD (foreign-writable — a same-sig
        // bundle for M can be wired in from another program) must keep the widening's dispatch
        // spill: strictly more spill traffic than the local-provenance flavor's 10.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12ErDField : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    public Func<int, int> c;
    void Start() { c = H; M(n % 3 + 4); result = trace; }
    int H(int k) { trace = (trace * 3 + k) % 99991; return k; }
    int M(int m) {
        int local = m % 13;
        int r = c(m);
        if (m <= 0) return r;
        int inner = M(m - 1);
        return inner + r + local;
    }
}", "W12ErDField");
        Assert.True(Count(uasm, "PUSH, __recurStack") > 10,
            "a foreign-writable field dispatch inside the cycle must keep the §5.4 blanket spill");
    }

    [Fact]
    public void ClosureHelperRecursion_PoisonedLocal_KeepsBlanketSpill()
    {
        // Soundness control: the dispatched local is reassigned from a FIELD on one path — its
        // provenance is no longer creation-only, so the site keeps the blanket treatment.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12ErDPoison : UdonSharpBehaviour {
    public int n; public int trace; public int result;
    public Func<int, int> wired;
    void Start() { wired = H; M(n % 3 + 4); result = trace; }
    int H(int k) { trace = (trace * 3 + k) % 99991; return k; }
    int M(int m) {
        int local = m % 13;
        Func<int, int> c = k => { trace = (trace * 3 + local + k) % 99991; return local + k; };
        if (m % 2 == 0) c = wired;
        int r = c(m);
        if (m <= 0) return r;
        int inner = M(m - 1);
        return inner + r + local;
    }
}", "W12ErDPoison");
        Assert.True(Count(uasm, "PUSH, __recurStack") > 10,
            "a local with a non-creation write must keep the §5.4 blanket spill");
    }

    // ── [V2] non-public property accessor through a variable receiver ──

    [Fact]
    public void PrivateNonAutoSetter_VariableReceiver_RejectsLoudly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12PrivSet : UdonSharpBehaviour {
    public int seed; public int result;
    W12PrivSet other; int _store;
    int Seed { get { return _store; } set { _store = value * 3; } }
    void Start() { other = this; other.Seed = seed; result = other.Seed; }
}", "W12PrivSet"));
        Assert.Contains("needs a public setter", ex.Message);
    }

    [Fact]
    public void PrivateNonAutoGetter_VariableReceiver_RejectsLoudly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12PrivGet : UdonSharpBehaviour {
    public int seed; public int result;
    W12PrivGet other; int _store;
    int Seed { get { return _store + seed; } }
    void Start() { other = this; result = other.Seed; }
}", "W12PrivGet"));
        Assert.Contains("needs a public getter", ex.Message);
    }

    [Fact]
    public void PublicNonAutoProperty_VariableReceiver_StaysDispatched()
    {
        // Control: public accessors export and dispatch — the gate must be exactly
        // non-public-narrow. The setter dispatch stages the value then SendCustomEvents the
        // exported accessor.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12PubProp : UdonSharpBehaviour {
    public int seed; public int result;
    W12PubProp other; int _store;
    public int Seed { get { return _store; } set { _store = value * 3; } }
    void Start() { other = this; other.Seed = seed; result = other.Seed; }
}", "W12PubProp");
        Assert.Contains("SendCustomEvent", uasm);
        Assert.Contains(".export __0_set_Seed", uasm);
        Assert.Contains(".export get_Seed", uasm);
    }

    [Fact]
    public void PrivateAutoProperty_VariableReceiver_UsesDirectSymbolAccess()
    {
        // A non-public auto-property's accessors are never exported, but its backing symbol IS
        // declared — the cross access must go through SetProgramVariable/GetProgramVariable on the
        // symbol (no entry point needed), not through a never-exported accessor dispatch (the
        // pre-fix silent no-op: DeclaringSyntaxReferences.IsEmpty is always false for source
        // accessors, so the direct arms were dead).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12PrivAuto : UdonSharpBehaviour {
    public int seed; public int result;
    W12PrivAuto other;
    int Seed { get; set; }
    void Start() { other = this; other.Seed = seed * 2; result = other.Seed; }
}", "W12PrivAuto");
        Assert.Contains("__SetProgramVariable__SystemString_SystemObject__SystemVoid", uasm);
        Assert.Contains("__GetProgramVariable__SystemString__SystemObject", uasm);
        Assert.DoesNotContain("SendCustomEvent", uasm);
    }

    // ── [V3] Object/ValueType members on a type-parameter receiver ──

    [Fact]
    public void TypeParamReceiver_StructConstrainedEquals_RoutesToConcreteExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12Eq : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { result = G<int>(seed); }
    int G<T>(T v) where T : struct {
        T dummy = v;
        bool eq = dummy.Equals(default(T));
        return eq ? 1 : 0;
    }
}", "W12Eq");
        Assert.Contains("SystemInt32.__Equals__SystemObject__SystemBoolean", uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    [Fact]
    public void TypeParamReceiver_GetHashCodeAndToString_RouteToConcreteExterns()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W12HashStr : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { result = G<int>(seed) + S<long>((long)seed).Length; }
    int G<T>(T v) where T : struct { T dummy = v; return dummy.GetHashCode(); }
    string S<T>(T v) where T : struct { T dummy = v; return dummy.ToString(); }
}", "W12HashStr");
        Assert.Contains("SystemInt32.__GetHashCode__SystemInt32", uasm);
        Assert.Contains("SystemInt64.__ToString__SystemString", uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    [Fact]
    public void TypeParamReceiver_UserStructEquals_RejectsLoudly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct W12S { public int a; }
public class W12EqS : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { W12S s; s.a = seed; result = G<W12S>(s); }
    int G<T>(T v) where T : struct {
        T dummy = v;
        return dummy.Equals(default(T)) ? 1 : 0;
    }
}", "W12EqS"));
        Assert.Contains("ValueType semantics", ex.Message);
    }
}
