using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Stage 2 M1 — structural unit tests for CaptureScopeAnalysis (design §2/§1.2). BEHAVIOR-NEUTRAL:
/// these assert ownership/slot/chain-shape FACTS on the analysis result, never UASM — the analysis
/// is not wired into codegen yet (that is Stage 2 M2). Every source snippet below is chosen to
/// compile cleanly under Stage 1's EXISTING capture-escape guards (captures stored into a delegate
/// FIELD, never through an array/return/cross-assign) so a test failure here can only mean the new
/// analysis is wrong, never an unrelated Stage-1 reject.
/// </summary>
public class CaptureScopeAnalysisTests
{
    static CaptureScope OwnerScope(CaptureScopeAnalysis a, string capturedName)
        => a.CapturedSlots.Single(kv => kv.Key.Name == capturedName).Value.Scope;

    static int SlotOf(CaptureScopeAnalysis a, string capturedName)
        => a.CapturedSlots.Single(kv => kv.Key.Name == capturedName).Value.Slot;

    static bool IsCaptured(CaptureScopeAnalysis a, string name)
        => a.CapturedSlots.Keys.Any(s => s.Name == name);

    static CaptureScope ClosureScopeNamed(CaptureScopeAnalysis a, string closureName)
        => a.ClosureScopes.Single(kv => kv.Key.Name == closureName).Value;

    static CaptureScope[] LambdaScopesInEncounterOrder(CaptureScopeAnalysis a)
        => a.Scopes.Where(s => s.ClosureSymbol?.MethodKind == MethodKind.LambdaMethod)
            .OrderBy(s => s.Id).ToArray();

    // ── §1.2 ownership table: pattern variables / out-vars / using declarations ──

    [Fact]
    public void IsPatternVariable_OutsideLoop_OwnedByEnclosingMethodScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class PatternVarClass : UdonSharpBehaviour {
    public Action _cb;
    object GetX() => 5;
    void Start() {
        object x = GetX();
        if (x is int y) {
            _cb = () => { int z = y; };
        }
    }
}", "PatternVarClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "y"));
        // The if-statement itself introduces no scope — only its braced WhenTrue block does — so the
        // pattern variable from the CONDITION belongs to the scope enclosing the whole if-statement.
        Assert.Equal(CaptureScopeKind.MethodEntry, OwnerScope(analysis, "y").Kind);
    }

    [Fact]
    public void OutVar_OwnedByEnclosingBlock()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class OutVarClass : UdonSharpBehaviour {
    public Action _cb;
    bool TryGet(out int v) { v = 7; return true; }
    void Start() {
        if (TryGet(out int v)) {
            _cb = () => { int z = v; };
        }
    }
}", "OutVarClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "v"));
        Assert.Equal(CaptureScopeKind.MethodEntry, OwnerScope(analysis, "v").Kind);
    }

    [Fact]
    public void UsingDeclaration_OwnedByEnclosingBlock()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct Res : System.IDisposable {
    public int v;
    public void Dispose() {}
}
public class UsingDeclClass : UdonSharpBehaviour {
    public Action _cb;
    void Start() {
        using var res = new Res();
        _cb = () => { int z = res.v; };
    }
}", "UsingDeclClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "res"));
        Assert.Equal(CaptureScopeKind.MethodEntry, OwnerScope(analysis, "res").Kind);
    }

    [Fact]
    public void SwitchSectionLocal_OwnedBySwitchScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class SwitchClass : UdonSharpBehaviour {
    public Action _cb;
    int GetMode() => 1;
    void Start() {
        int mode = GetMode();
        switch (mode) {
            case 1:
                int a = 10;
                _cb = () => { int y = a; };
                break;
            default:
                break;
        }
    }
}", "SwitchClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "a"));
        Assert.Equal(CaptureScopeKind.Switch, OwnerScope(analysis, "a").Kind);
        // 'mode' (the switch value) is NOT owned by the switch scope — it's declared before the switch.
        Assert.NotEqual(CaptureScopeKind.Switch,
            analysis.Scopes.First(s => s.DeclaredSymbols.Any(sym => sym.Name == "mode")).Kind);
    }

    [Fact]
    public void NestedDeconstruction_DeclaresDeeplyNestedComponent()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DeconClass : UdonSharpBehaviour {
    public Action _cb;
    (int, (int, int)) GetTriple() => (1, (2, 3));
    void Start() {
        var (a, (b, c)) = GetTriple();
        _cb = () => { int y = b; };
    }
}", "DeconClass", out var emitter);

        var analysis = emitter.CaptureScope;
        // All three components declare, but only 'b' — two levels deep in the nested tuple pattern — is captured.
        var declaringScope = analysis.Scopes.Single(s => s.DeclaredSymbols.Any(sym => sym.Name == "a"));
        Assert.Contains(declaringScope.DeclaredSymbols, sym => sym.Name == "b");
        Assert.Contains(declaringScope.DeclaredSymbols, sym => sym.Name == "c");
        Assert.True(IsCaptured(analysis, "b"));
        Assert.False(IsCaptured(analysis, "a"));
        Assert.False(IsCaptured(analysis, "c"));
        Assert.Single(declaringScope.OwnedCaptures);
    }

    // ── §1.2 / INV-1: for/foreach/while/do at multiple nesting depths ──

    [Fact]
    public void ForLoop_InitVarAndBodyVar_OwnedByDifferentScopes()
    {
        // The capturing lambda is invoked WITHIN its own iteration (a local Action, not stored to a
        // field) — storing a per-iteration-fresh capture to a field that outlives the iteration trips
        // Stage 1's EXISTING per-iteration escape guard (unrelated to this analysis; see the wave-9 [W1]/[W2]
        // gotcha in Library~/fcd_stage1_handoff.md — "declared inside the loop, invoked within the
        // iteration" is the legal shape).
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ForClass : UdonSharpBehaviour {
    void Start() {
        for (int i = 0; i < 3; i++) {
            int v = i;
            Action cb = () => { int y = v + i; };
            cb();
        }
    }
}", "ForClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "i"));
        Assert.True(IsCaptured(analysis, "v"));
        var forScope = OwnerScope(analysis, "i");
        var iterScope = OwnerScope(analysis, "v");
        Assert.Equal(CaptureScopeKind.ForInit, forScope.Kind);
        Assert.Equal(CaptureScopeKind.Iteration, iterScope.Kind);
        Assert.NotSame(forScope, iterScope);
        // The iteration scope's raw parent IS the for-init scope (one level, no intervening block).
        Assert.Same(forScope, iterScope.Parent);
        Assert.Same(forScope, analysis.EffectiveParent(iterScope));
        Assert.Equal(1, analysis.HopDistance(iterScope, forScope));
        Assert.Equal(0, analysis.HopDistance(iterScope, iterScope));
    }

    [Fact]
    public void NestedForLoops_FourDistinctScopesCorrectlyChained()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class NestedForClass : UdonSharpBehaviour {
    void Start() {
        for (int i = 0; i < 3; i++) {
            int mid = i * 10;
            for (int j = 0; j < i + 1; j++) {
                Action cb = () => { int y = i + j + mid; };
                cb();
            }
        }
    }
}", "NestedForClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "i"));
        Assert.True(IsCaptured(analysis, "j"));
        Assert.True(IsCaptured(analysis, "mid"));
        var outerForScope = OwnerScope(analysis, "i");
        var outerIterScope = OwnerScope(analysis, "mid");
        var innerForScope = OwnerScope(analysis, "j");
        Assert.Equal(CaptureScopeKind.ForInit, outerForScope.Kind);
        Assert.Equal(CaptureScopeKind.Iteration, outerIterScope.Kind);
        Assert.Equal(CaptureScopeKind.ForInit, innerForScope.Kind);
        Assert.NotSame(outerForScope, innerForScope);

        // Four raw scopes total on this chain: outer ForInit -> outer Iteration -> inner ForInit -> inner Iteration.
        Assert.Same(outerForScope, outerIterScope.Parent);
        Assert.Same(outerIterScope, innerForScope.Parent);
        // 'mid' makes the outer Iteration scope capture-bearing too, so BOTH raw hops now carry an env —
        // a genuine two-hop chain (inner ForInit -> outer Iteration -> outer ForInit), not a skip-through.
        Assert.Same(outerIterScope, analysis.EffectiveParent(innerForScope));
        Assert.Equal(2, analysis.HopDistance(innerForScope, outerForScope));
        Assert.Equal(1, analysis.HopDistance(innerForScope, outerIterScope));
    }

    [Fact]
    public void Foreach_ControlVariable_FreshIterationScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ForeachClass : UdonSharpBehaviour {
    int[] GetArr() => new int[] { 1, 2, 3 };
    void Start() {
        foreach (var item in GetArr()) {
            Action cb = () => { int y = item; };
            cb();
        }
    }
}", "ForeachClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "item"));
        Assert.Equal(CaptureScopeKind.Iteration, OwnerScope(analysis, "item").Kind);
    }

    [Fact]
    public void While_ConditionOutVar_And_BodyLocal_ShareIterationScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class WhileClass : UdonSharpBehaviour {
    int _n = 3;
    bool TryNext(out int v) { v = _n; _n--; return _n >= 0; }
    void Start() {
        while (TryNext(out int v)) {
            int doubled = v * 2;
            Action cb = () => { int y = v + doubled; };
            cb();
        }
    }
}", "WhileClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "v"));
        Assert.True(IsCaptured(analysis, "doubled"));
        // INV-3: the condition's out-var and the body-direct local share ONE per-iteration scope.
        Assert.Same(OwnerScope(analysis, "v"), OwnerScope(analysis, "doubled"));
        Assert.Equal(CaptureScopeKind.Iteration, OwnerScope(analysis, "v").Kind);
    }

    // ── local functions / nested lambdas: BindingScope + HopDistance chain shape ──

    [Fact]
    public void LocalFunction_CapturingEnclosingLocal_BindsToEnclosingScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LocalFuncClass : UdonSharpBehaviour {
    public Action _cb;
    void Start() {
        int a = 5;
        void Inner() {
            int b = a + 1;
            _cb = () => { int y = b; };
        }
        Inner();
    }
}", "LocalFuncClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "a"));
        Assert.True(IsCaptured(analysis, "b"));

        var startScope = OwnerScope(analysis, "a");
        var innerScope = ClosureScopeNamed(analysis, "Inner");
        var lambdaScope = LambdaScopesInEncounterOrder(analysis).Single();

        // Inner itself declares 'b', captured by the nested lambda — Inner's OWN body scope is capture-bearing.
        Assert.Same(innerScope, OwnerScope(analysis, "b"));
        // Inner binds its env chain to Start's scope (the nearest capture-bearing ancestor of its declaration site).
        Assert.Same(startScope, innerScope.BindingScope);
        // The lambda (declared directly inside Inner) binds to Inner's own scope — zero hops to reach 'b'.
        Assert.Same(innerScope, lambdaScope.BindingScope);
        Assert.Equal(0, analysis.HopDistance(lambdaScope.BindingScope, innerScope));
        // ...but one hop from the lambda's binding scope up to where 'a' lives.
        Assert.Equal(1, analysis.HopDistance(lambdaScope.BindingScope, startScope));
    }

    [Fact]
    public void NestedLambdas_ChainShape_InnerBindsToOuterLambdaScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class NestedLambdaClass : UdonSharpBehaviour {
    public Action _outer;
    void Start() {
        int a = 1;
        _outer = () => {
            int b = 2;
            Action inner = () => { int c = a + b; };
            inner();
        };
    }
}", "NestedLambdaClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "a"));
        Assert.True(IsCaptured(analysis, "b"));
        Assert.False(IsCaptured(analysis, "c")); // nothing captures c — declared but not owned

        var startScope = OwnerScope(analysis, "a");
        var lambdas = LambdaScopesInEncounterOrder(analysis);
        Assert.Equal(2, lambdas.Length);
        var outerScope = lambdas[0];
        var innerScope = lambdas[1];

        Assert.Same(outerScope, OwnerScope(analysis, "b"));
        Assert.Same(startScope, outerScope.BindingScope);
        Assert.Same(outerScope, innerScope.BindingScope);
        Assert.Equal(0, analysis.HopDistance(innerScope.BindingScope, outerScope));
        Assert.Equal(1, analysis.HopDistance(innerScope.BindingScope, startScope));
    }

    [Fact]
    public void CapturesAtDifferentDepths_ShareOneOwningScope()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class SharedCaptureClass : UdonSharpBehaviour {
    public Action _a;
    public Action _b;
    void Start() {
        int shared = 10;
        _a = () => { int y = shared + 1; };
        _b = () => { int y = shared + 2; };
    }
}", "SharedCaptureClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "shared"));
        // Two distinct lambdas both reading 'shared' resolve to the SAME owning scope and slot — one cell.
        var lambdas = LambdaScopesInEncounterOrder(analysis);
        Assert.Equal(2, lambdas.Length);
        Assert.Same(lambdas[0].BindingScope, lambdas[1].BindingScope);
        Assert.Equal(1, SlotOf(analysis, "shared"));
    }

    // ── generics: OriginalDefinition keying (design §2 rule 2) ──

    [Fact]
    public void GenericMethod_CapturedParam_KeyedByOriginalDefinitionParameter()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class GenericCaptureClass : UdonSharpBehaviour {
    public Action _cb;
    void Start() { Echo(5); }
    T Echo<T>(T val) {
        _cb = () => { T y = val; };
        return val;
    }
}", "GenericCaptureClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.True(IsCaptured(analysis, "val"));

        var echoDef = emitter.ClassSymbol.GetMembers("Echo").OfType<IMethodSymbol>().Single();
        Assert.True(echoDef.IsDefinition);
        var capturedParamKey = analysis.CapturedSlots.Keys.Single(s => s.Name == "val");
        Assert.True(SymbolEqualityComparer.Default.Equals(capturedParamKey, echoDef.Parameters[0]));
        // The owning scope is Echo's own MethodEntry scope (params/body-direct locals share one scope).
        Assert.Equal(CaptureScopeKind.MethodEntry, OwnerScope(analysis, "val").Kind);
    }

    // ── 'this' capture: always excluded (design §1.2) ──

    [Fact]
    public void ThisFieldAccess_InsideLambda_NeverCaptured()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class ThisOnlyClass : UdonSharpBehaviour {
    public Action _cb;
    public int val;
    void Start() { _cb = () => { val = val + 1; }; }
}", "ThisOnlyClass", out var emitter);

        var analysis = emitter.CaptureScope;
        Assert.Empty(analysis.CapturedSlots);
        Assert.All(analysis.Scopes, s => Assert.False(s.IsCaptureBearing));
        var lambdaScope = LambdaScopesInEncounterOrder(analysis).Single();
        Assert.Null(lambdaScope.BindingScope);
    }

    // ── determinism (design §11 M1 gate) ──

    [Fact]
    public void Build_IsDeterministic_AcrossRepeatedRuns()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DeterminismClass : UdonSharpBehaviour {
    void Start() {
        for (int i = 0; i < 3; i++) {
            int v = i;
            if (v is int w) {
                Action cb = () => { int y = v + w + i; };
                cb();
            }
        }
    }
}", "DeterminismClass", out var emitter);

        var a1 = CaptureScopeAnalysis.Build(emitter.Compilation, emitter.ClassSymbol);
        var a2 = CaptureScopeAnalysis.Build(emitter.Compilation, emitter.ClassSymbol);

        Assert.Equal(a1.Scopes.Count, a2.Scopes.Count);
        Assert.Equal(a1.Scopes.Select(s => s.Kind), a2.Scopes.Select(s => s.Kind));
        Assert.Equal(
            a1.CapturedSlots.OrderBy(kv => kv.Key.Name).Select(kv => (kv.Key.Name, kv.Value.Slot)),
            a2.CapturedSlots.OrderBy(kv => kv.Key.Name).Select(kv => (kv.Key.Name, kv.Value.Slot)));
    }
}
