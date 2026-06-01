using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Verifies emit-time detection of lambda capture aliasing.
///
/// Pre-v2.2: silent overwrite (Warning only). v2.2: emit-time Error.
/// Detection: AnalyzeDataFlow() per lambda body, aggregated in EmitContext.AllLambdaCaptures.
/// Trigger: a captured local symbol shared by 2+ distinct lambdas assigned to delegate fields.
/// </summary>
public class LambdaAliasingTests
{
    [Fact]
    public void SingleLambda_SingleCapture_NoError()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class SingleCapture : UdonSharpBehaviour {
    public Action _cb;
    void Start() {
        int x = 42;
        _cb = () => { int y = x + 1; };
    }
}", "SingleCapture", out var emitter);
        Assert.NotNull(uasm);
        Assert.DoesNotContain(emitter.Diagnostics, d =>
            d.Severity == "Error" && d.Message.Contains("shared by"));
    }

    [Fact]
    public void TwoLambdas_SameLocalCapture_RaisesError()
    {
        // Two distinct lambdas in the same method capture the same local
        // and are stored to two delegate fields → aliasing error.
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TwoLambdaSameCapture : UdonSharpBehaviour {
    public Action _a;
    public Action _b;
    void Start() {
        int shared = 10;
        _a = () => { int y = shared + 1; };
        _b = () => { int y = shared + 2; };
    }
}", "TwoLambdaSameCapture", out var emitter);

        var aliasingErrors = emitter.Diagnostics
            .Where(d => d.Severity == "Error" && d.Message.Contains("shared"))
            .ToArray();
        Assert.Single(aliasingErrors);
        Assert.Contains("'shared'", aliasingErrors[0].Message);
        Assert.Contains("flat-heap field", aliasingErrors[0].Message);
    }

    [Fact]
    public void TwoLambdas_DifferentLocals_NoError()
    {
        // Two delegate fields, each capturing a different local — no aliasing.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DistinctCaptures : UdonSharpBehaviour {
    public Action _a;
    public Action _b;
    void Start() {
        int x = 1;
        int y = 2;
        _a = () => { int t = x + 1; };
        _b = () => { int t = y + 2; };
    }
}", "DistinctCaptures", out var emitter);
        Assert.NotNull(uasm);
        var dbg = string.Join("\n", emitter.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}"));
        Assert.True(
            !emitter.Diagnostics.Any(d => d.Severity == "Error" && d.Message.Contains("shared")),
            $"Expected no aliasing errors, got:\n{dbg}");
    }

    [Fact]
    public void DifferentMethods_UnrelatedLocals_NoError()
    {
        // Two methods, two locals with same name, one lambda each — no aliasing.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TwoMethods : UdonSharpBehaviour {
    public Action _a;
    public Action _b;
    void Start() {
        int local = 1;
        _a = () => { int t = local + 1; };
    }
    void Other() {
        int local = 2;
        _b = () => { int t = local + 2; };
    }
}", "TwoMethods", out var emitter);
        Assert.NotNull(uasm);
        Assert.DoesNotContain(emitter.Diagnostics, d =>
            d.Severity == "Error" && d.Message.Contains("shared"));
    }

    [Fact]
    public void TwoPrivateDelegateFields_SameCapture_RaisesError()
    {
        // Stage A.4: private delegate fields (stored as SystemUInt32 JUMP addresses)
        // also alias when capturing the same local. Pre-A.4 this was undetected.
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class TwoPrivateAliased : UdonSharpBehaviour {
    Action _a;
    Action _b;
    void Start() {
        int shared = 10;
        _a = () => { int y = shared + 1; };
        _b = () => { int y = shared + 2; };
    }
}", "TwoPrivateAliased", out var emitter);

        var aliasingErrors = emitter.Diagnostics
            .Where(d => d.Severity == "Error" && d.Message.Contains("shared"))
            .ToArray();
        Assert.Single(aliasingErrors);
        Assert.Contains("'shared'", aliasingErrors[0].Message);
    }

    [Fact]
    public void NestedLambda_OuterLocalCapturedByBoth_RaisesError()
    {
        // Outer lambda assigned to _a captures `outer`. Inner lambda assigned to _b
        // also captures `outer` (defined in the outer method's scope). Two distinct
        // IAnonymousFunctionOperations capture the same symbol → aliasing.
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class NestedCapture : UdonSharpBehaviour {
    public Action _a;
    public Action _b;
    void Start() {
        int outer = 5;
        _a = () => {
            int t = outer + 1;
            _b = () => { int u = outer + 2; };
        };
    }
}", "NestedCapture", out var emitter);

        var aliasingErrors = emitter.Diagnostics
            .Where(d => d.Severity == "Error" && d.Message.Contains("shared"))
            .ToArray();
        Assert.Single(aliasingErrors);
        Assert.Contains("'outer'", aliasingErrors[0].Message);
    }
}
