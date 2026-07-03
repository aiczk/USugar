using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace USugar.Tests;

/// <summary>
/// Wave-12 fix round 5 — tracked pins for the three findings (real-VM value pins live in the local
/// harness corpora _w12_r5_min.json / fcd_wave12_envcross_r5.json):
///
/// [W1]/[W2] A variant delegate value laundered through a NON-delegate-typed box (object or
///      System.Delegate, explicit cast or `as`) bypassed the whole [V2]/round-4 gate family: the
///      conversion node's source side is not a delegate type, so the sig-part-divergence check
///      never ran, and the bundle flowed into channels keyed by the DESTINATION signature
///      (VM-proven silent loss: covariant Func ref=2 vs -1, contravariant Action ref=7 vs 5).
///      VisitConversion now rejects a non-delegate→delegate cast unless the operand, after
///      stripping conversions on THIS expression, is DIRECTLY a same-signature delegate (the
///      trivially-safe box-and-unbox roundtrip) or null/default. Everything whose boxed delegate
///      is not statically visible in the expression rejects loudly — a conservative, bounded
///      replacement for the former unbounded producer-walking evidence check (rounds 5-9 traced
///      33 producer AST shapes and never saturated). Design §8-3: loud over-rejection of the rare
///      cross-statement box roundtrip, never a silent wrong value; the per-shape reject pins are
///      consolidated here since the check no longer inspects how the box was produced.
///
/// [W3] A method group bound off a BASE-typed variable receiver resolved to the derived class's
///      `new`-hidden method instead of the statically bound base method (VM-proven 162 where C#
///      gives 2): cross-program dispatch is name-keyed via Plan(Base)'s plain export, but the
///      derived program's plain export was owned by the NEW method — the planner collision-renamed
///      the INHERITED member. LayoutPlanner now mangles the SHADOWING `new` declaration instead
///      (parameterless non-event non-[NetworkCallable] methods; parameterized ones were already
///      consistent through counter inheritance), so the statically bound chain keeps its plain
///      export name in every descendant program.
/// </summary>
public class Wave12Round5RegressionTests
{
    // ── [W1]/[W2] object-boxed variant delegate casts reject loudly (bounded, shape-agnostic) ──
    // The former per-producer-shape walker (rounds 5-9) is gone; the reject no longer inspects HOW
    // the box was produced, only that the cast's operand is not a statically visible same-sig
    // delegate. These pin the covariant Func, `as`-cast, and contravariant Action channels; the
    // controls below pin the flows the bounded check still accepts.

    static void AssertObjectDelegateCastReject(string src, string cls)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(src, cls));
        Assert.Contains("carries no statically visible signature", ex.Message);
    }

    [Fact]
    public void ObjectBoxedCastCovariantDelegate_Throws()
    {
        // Verbatim minimized repro (VM-proven ref=2, usugar -1: return silently dropped). The box
        // is a cross-statement object local — its runtime delegate signature is not visible at the
        // cast, so the bounded check rejects.
        AssertObjectDelegateCastReject(@"
using System;
using UdonSharp;
public class W12R5ObjCo : UdonSharpBehaviour {
    public int seed; public int result;
    W12R5ObjCo other;
    Func<object> bundle;
    string MakeTag() { return ""q"" + seed; }
    void Start() {
        other = this;
        Func<string> narrow = MakeTag;
        object boxed = narrow;
        Func<object> wide = (Func<object>)boxed;
        other.bundle = wide;
        object o = other.bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R5ObjCo");
    }

    [Fact]
    public void ObjectBoxedAsCastCovariantDelegate_Throws()
    {
        // `as` flavor of the same channel (corpus W12EC5R05, VM-proven ref=2 vs -1) — an `as`-cast
        // to a delegate type is the same IConversionOperation and rejects the same way.
        AssertObjectDelegateCastReject(@"
using System;
using UdonSharp;
public class W12R5ObjAs : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> bundle;
    string MakeTag() { return ""r"" + seed; }
    void Start() {
        Func<string> narrow = MakeTag;
        object boxed = narrow;
        bundle = boxed as Func<object>;
        object o = bundle();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R5ObjAs");
    }

    [Fact]
    public void ObjectBoxedCastContravariantAction_Throws()
    {
        // Verbatim minimized repro (VM-proven ref=7, usugar 5: argument silently dropped).
        AssertObjectDelegateCastReject(@"
using System;
using UdonSharp;
public class W12R5ObjContra : UdonSharpBehaviour {
    public int seed; public int result;
    W12R5ObjContra other;
    Action<string> bundle;
    void TakeObject(object o) { result = seed + (o == null ? 0 : o.ToString().Length); }
    void Start() {
        other = this;
        Action<object> wideAction = TakeObject;
        object boxed = wideAction;
        Action<string> narrowBundle = (Action<string>)boxed;
        other.bundle = narrowBundle;
        other.bundle(""ab"");
    }
}", "W12R5ObjContra");
    }

    // ── [W1]/[W2] controls: the flows the bounded check still accepts ──

    [Fact]
    public void SameSigDelegateRoundtripInOneExpression_Compiles()
    {
        // The trivially-safe roundtrip: a delegate boxed and unboxed to the SAME signature WITHIN
        // one expression — the stripped operand is directly a same-sig delegate value, so the
        // channels agree and the reference passthrough stays.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R5InExpr : UdonSharpBehaviour {
    public int seed; public int result;
    string MakeTag() { return ""x"" + seed; }
    void Start() {
        Func<string> d = MakeTag;
        Func<string> e = (Func<string>)(object)d;
        result = e().Length;
    }
}", "W12R5InExpr");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void NullCastToDelegate_Compiles()
    {
        // `(Func<...>)null` / `(Func<...>)(object)null` carry no delegate and no signature — the
        // invoke-time target-null guard handles them, never diverging a channel. Safe passthrough.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R5NullCast : UdonSharpBehaviour {
    public int result;
    Func<int> a;
    Func<int> b;
    void Start() {
        a = (Func<int>)null;
        b = (Func<int>)(object)null;
        result = (a == null && b == null) ? 1 : 0;
    }
}", "W12R5NullCast");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void DelegateToObjectBoxing_Compiles()
    {
        // The boxing DIRECTION (delegate → object) is untouched: the reject fires only on a
        // NON-delegate operand cast TO a delegate, so storing a delegate into an object stays legal.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R5Boxing : UdonSharpBehaviour {
    public int seed; public int result;
    string MakeTag() { return ""b"" + seed; }
    void Start() {
        Func<string> d = MakeTag;
        object o = d;
        result = o == null ? 0 : 1;
    }
}", "W12R5Boxing");
        Assert.Contains("__dlg", uasm);
    }

    // ── [W3] planner invariant: the `new` declaration is the mangled one ──

    static readonly string PlannerStubSource = @"
namespace UdonSharp
{
    public class UdonSharpBehaviour : UnityEngine.MonoBehaviour { }
}
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
}
";

    static (Compilation compilation, INamedTypeSymbol symbol) CompileAndGetSymbol(
        string source, string className)
    {
        var tree = CSharpSyntaxTree.ParseText(PlannerStubSource + source);
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var symbol = comp.GetTypeByMetadataName(className)
            ?? throw new Exception($"Type {className} not found");
        return (comp, symbol);
    }

    [Fact]
    public void NewShadowedMethod_BaseKeepsPlainExport_NewDeclarationMangled()
    {
        // Cross-program dispatch resolves the statically bound BASE method via Plan(Base)'s plain
        // export name, so the derived program must keep that name on the INHERITED member and
        // mangle the shadowing `new` declaration (pre-fix it was the other way around, and a
        // base-typed receiver's method group ran the `new` body: VM-proven 162 vs 2).
        var (comp, drv) = CompileAndGetSymbol(@"
public class ShadowBase : UdonSharp.UdonSharpBehaviour {
    public virtual int Get() { return 1; }
}
public class ShadowDrv : ShadowBase {
    public new int Get() { return 9; }
}", "ShadowDrv");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(drv);
        var baseGet = (IMethodSymbol)drv.BaseType.GetMembers("Get")[0];
        var newGet = (IMethodSymbol)drv.GetMembers("Get")[0];
        Assert.True(layout.Methods.TryGetValue(baseGet, out var baseMl));
        Assert.True(layout.Methods.TryGetValue(newGet, out var newMl));
        Assert.Equal("Get", baseMl.ExportName);
        Assert.Matches(@"^__\d+_Get$", newMl.ExportName);
    }

    [Fact]
    public void NonShadowedInheritedMethod_BothKeepPlainExports()
    {
        // Control: without hiding, nothing is mangled — the inherited member and the derived
        // class's own differently-named method both keep their plain exports.
        var (comp, drv) = CompileAndGetSymbol(@"
public class PlainBase : UdonSharp.UdonSharpBehaviour {
    public virtual int Get() { return 1; }
}
public class PlainDrv : PlainBase {
    public int Other() { return 2; }
}", "PlainDrv");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(drv);
        var baseGet = (IMethodSymbol)drv.BaseType.GetMembers("Get")[0];
        var own = (IMethodSymbol)drv.GetMembers("Other")[0];
        Assert.Equal("Get", layout.Methods[baseGet].ExportName);
        Assert.Equal("Other", layout.Methods[own].ExportName);
    }

    [Fact]
    public void NewShadowBaseReceiverMethodGroup_CompilesWithBothExports()
    {
        // Verbatim minimized repro compiles clean, with BOTH functions exported (one plain, one
        // mangled) and both delegate bridges present — same shape guard as the wave-9 W9T18 pin,
        // kept here against the round-5 planner change regressing the export pair.
        var uasm = TestHelper.CompileToUasm(new[] { @"
using System;
using UdonSharp;
public class W12R5ShadowBase : UdonSharpBehaviour {
    public virtual int Get() { return 1; }
}
public class W12R5Shadow : W12R5ShadowBase {
    public int seed; public int result;
    W12R5ShadowBase baseRef;
    Func<int> cb;
    public new int Get() { return seed * 9; }
    void Start() {
        baseRef = this;
        cb = baseRef.Get;
        result = cb() + cb();
    }
}" }, "W12R5Shadow");
        Assert.Single(Regex.Matches(uasm, @"^\s*\.export Get\s*$", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(uasm, @"^\s*\.export __\d+_Get\s*$", RegexOptions.Multiline));
        Assert.Contains("__dlg_Get", uasm);
    }
}
