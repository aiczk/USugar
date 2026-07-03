using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// The extern (foreign, non-UdonSharp) ref/out copy-back path double-evaluates (or mis-times)
/// complex lvalue legs — the wave-9 [Y12]/[Y16] fixes hardened the USER-method ref/out path
/// (TryPrepareRefOutArg/EmitRefOutCopyBack) but EmitExternMethodCall's own copy-back
/// (InvocationHandler.Extern.cs) still reads the initial value via a raw VisitExpression and
/// writes back via the legacy AssignToTarget, which re-evaluates the array index / behaviour
/// receiver legs a second time after the call.
/// </summary>
public class ExternRefOutSideEffectTests
{
    [Fact]
    public void RefExtern_ArrayElement_IndexLegEvaluatesOnce()
    {
        // Mathf.SmoothDamp(float, float, ref float, float) is a genuine Udon extern with a
        // RefKind.Ref (not Out) parameter. Pre-fix: the index leg (Math.Abs) is evaluated once at
        // the copy-in read (EmitExternMethodCall line ~109) and AGAIN at copy-back (AssignToTarget's
        // array-element arm re-evaluates arrayElem.Indices[0]) — two occurrences instead of one.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UnityEngine;
using UdonSharp;
public class ExtRefArrLeg : UdonSharpBehaviour {
    public int seed; public float v0; public float v1;
    void Start() {
        float[] vel = new float[2];
        Mathf.SmoothDamp(1f, 2f, ref vel[Math.Abs(seed) % 2], 0.5f);
        v0 = vel[0]; v1 = vel[1];
    }
}", "ExtRefArrLeg");
        AssertExternCount(uasm, "SystemMath.__Abs__SystemInt32__SystemInt32", 1);
    }

    [Fact]
    public void OutExtern_ArrayElement_IndexLegEvaluatesBeforeCall()
    {
        // int.TryParse(string, out int) is a genuine Udon extern with a RefKind.Out parameter.
        // C# evaluates an argument's component expressions (here, the index leg) at the argument's
        // syntax position, BEFORE the call — pre-fix, EmitExternMethodCall skips the copy-in read for
        // Out params entirely and only evaluates the index leg in the copy-back AFTER the call, so
        // the Math.Abs extern is emitted after (not before) the TryParse extern.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class ExtOutArrLeg : UdonSharpBehaviour {
    public int seed; public int r0; public int r1;
    void Start() {
        int[] a = new int[2];
        int.TryParse(""5"", out a[Math.Abs(seed) % 2]);
        r0 = a[0]; r1 = a[1];
    }
}", "ExtOutArrLeg");
        AssertExternCount(uasm, "SystemMath.__Abs__SystemInt32__SystemInt32", 1);
        string code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int absIdx = code.IndexOf("SystemMath.__Abs__SystemInt32__SystemInt32", StringComparison.Ordinal);
        int tryParseIdx = code.IndexOf("SystemInt32.__TryParse__SystemString_SystemInt32Ref__SystemBoolean", StringComparison.Ordinal);
        Assert.True(absIdx >= 0 && tryParseIdx >= 0, "expected externs missing");
        Assert.True(absIdx < tryParseIdx,
            "the index leg must be evaluated BEFORE the extern call, matching C# argument-evaluation order");
    }

    [Fact]
    public void RefExtern_BehaviourArrayElementField_ReceiverLegEvaluatesOnce()
    {
        // hs[Pick()].pub as a `ref` argument to a genuine extern (Mathf.SmoothDamp): pre-fix, the
        // receiver leg (hs[Pick()], an array Get) is read once at copy-in and re-evaluated at
        // copy-back (AssignToTarget's cross-behaviour-field arm), so Pick() runs twice and the
        // UnityEngineComponentArray.__Get__ extern appears twice instead of once.
        var uasm = TestHelper.CompileToUasm(@"
using UnityEngine;
using UdonSharp;
public class ExtRefBehField : UdonSharpBehaviour {
    ExtRefBehField[] hs;
    int cur;
    public float pub; public int trace; public float total;
    void Start() {
        hs = new ExtRefBehField[2];
        hs[0] = this;
        hs[1] = this;
        pub = 1.5f;
        Mathf.SmoothDamp(1f, 2f, ref hs[Pick()].pub, 0.5f);
        total = pub * 1000 + trace;
    }
    public int Pick() { trace = trace * 10 + (cur % 2 + 1); int r = cur % 2; cur = cur + 1; return r; }
}", "ExtRefBehField");
        AssertExternCount(uasm, "UnityEngineComponentArray.__Get__", 1);
    }

    [Fact]
    public void RefExtern_CapturedLocal_RoundTripsThroughEnvCell()
    {
        // A captured local has no flat heap field (Stage 2 §4.1) — ResolveOutRefFieldName returns
        // null for it, same as any complex lvalue. Pre-fix this fell through the legacy
        // VisitExpression + AssignToTarget path (which happens to be correct for a bare variable,
        // since there's no side-effecting leg to double-evaluate); now TryPrepareRefOutArg prepares
        // it directly via the env cell, the ONE mechanism. Assert the round-trip value is correct
        // and the lambda closure that captures the local later observes the mutation.
        var uasm = TestHelper.CompileToUasm(@"
using UnityEngine;
using UdonSharp;
using System;
public class ExtRefCapLocal : UdonSharpBehaviour {
    public float result;
    void Start() {
        float v = 1f;
        Action bump = () => { v = v + 100f; };
        Mathf.SmoothDamp(1f, 2f, ref v, 0.5f);
        bump();
        result = v;
    }
}", "ExtRefCapLocal");
        Assert.Contains("UnityEngineMathf.__SmoothDamp__", uasm);
        Assert.Contains("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void OutExtern_CapturedOutVar_RoundTripsThroughEnvCell()
    {
        // `out var x` declaring a CAPTURED local: ResolveOutRefFieldName returns null for it too
        // (same Stage 2 §4.1 rule), so it reaches the same TryPrepareRefOutArg call as the ref case.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class ExtOutCapVar : UdonSharpBehaviour {
    public int result;
    void Start() {
        int.TryParse(""5"", out int captured);
        Action bump = () => { captured = captured + 100; };
        bump();
        result = captured;
    }
}", "ExtOutCapVar");
        Assert.Contains("SystemInt32.__TryParse__SystemString_SystemInt32Ref__SystemBoolean", uasm);
    }

    [Fact]
    public void RefExtern_IndexSubscriptArrayElement_Compiles()
    {
        // `ref arr[^1]` (B40 follow-up): TryPrepareRefOutArg used to explicitly decline a from-end
        // Index-subscripted array element, and EmitExternMethodCall's fallback silently tried the
        // plain-write path (PrepareArrayElementSet), which doesn't special-case Index and crashed
        // with an unrelated, confusing "Unsupported unary operator: Hat". Now the array case lowers
        // `^1` the same way the read side does (arr.Length - 1) and prepares it as a normal ref/out leg.
        var uasm = TestHelper.CompileToUasm(@"
using UnityEngine;
using UdonSharp;
public class ExtRefIdxSub : UdonSharpBehaviour {
    public float result;
    void Start() {
        float[] arr = new float[3];
        Mathf.SmoothDamp(1f, 2f, ref arr[^1], 0.5f);
        result = arr[2];
    }
}", "ExtRefIdxSub");
        Assert.Contains("UnityEngineMathf.__SmoothDamp__", uasm);
        Assert.Contains("__get_Length__SystemInt32", uasm);
    }

    [Fact]
    public void RefExtern_IndexSubscriptArrayElement_IndexLegEvaluatesOnce()
    {
        // `ref arr[^Idx()]`: the resolved int position (arr.Length - Idx()) must be computed ONCE
        // and reused for both the copy-in read and the copy-back store — not re-lowered (which
        // would re-run the side-effecting `Idx()` a second time).
        var uasm = TestHelper.CompileToUasm(@"
using UnityEngine;
using UdonSharp;
public class ExtRefIdxSubOnce : UdonSharpBehaviour {
    public int calls;
    void Start() {
        float[] arr = new float[3];
        Mathf.SmoothDamp(1f, 2f, ref arr[^Idx()], 0.5f);
    }
    public int Idx() { calls = calls + 1; return 1; }
}", "ExtRefIdxSubOnce");
        AssertExternCount(uasm, "UnityEngineMathf.__SmoothDamp__", 1);
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int count = 0, idx = 0;
        const string tok = "__get_Length__SystemInt32";
        while ((idx = code.IndexOf(tok, idx, StringComparison.Ordinal)) >= 0) { count++; idx += tok.Length; }
        Assert.Equal(1, count);
    }

    static void AssertExternCount(string uasm, string extern_, int expected)
    {
        var code = uasm.Substring(uasm.IndexOf(".code_start", StringComparison.Ordinal));
        int actual = CountOccurrences(code, extern_);
        Assert.True(actual == expected,
            $"expected '{extern_}' to appear {expected} time(s) in the code section, got {actual}");
    }

    static int CountOccurrences(string text, string token)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        return count;
    }
}
