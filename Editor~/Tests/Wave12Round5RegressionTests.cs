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
///      (VM-proven silent loss: covariant Func ref=2 vs -1, contravariant Action ref=7 vs 5, and
///      the same channel through a setter's `value` param ref=3 vs -1). VisitConversion now runs
///      an EVIDENCE-BASED check on delegate-destination casts with non-delegate operands: it
///      rejects loudly iff a statically visible producer of the operand (nested conversions,
///      ternary/coalesce arms, the operand local's writes transitively through other locals, or a
///      parameter's class-family-visible call-site arguments / property-write values) carries a
///      delegate whose sig part diverges. Same-signature roundtrips (the pinned accepted
///      boundary) and evidence-free casts keep the reference passthrough.
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
    // ── [W1] object-boxed covariant Func laundering ──

    [Fact]
    public void ObjectBoxedCastCovariantDelegate_Throws()
    {
        // Verbatim minimized repro (VM-proven ref=2, usugar -1: return silently dropped).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
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
}", "W12R5ObjCo"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
        Assert.Contains("Func<string>", ex.Message);
        Assert.Contains("Func<object>", ex.Message);
    }

    [Fact]
    public void ObjectBoxedAsCastCovariantDelegate_Throws()
    {
        // `as` flavor of the same channel (corpus W12EC5R05, VM-proven ref=2 vs -1).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
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
}", "W12R5ObjAs"));
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [W2] object-boxed contravariant Action laundering ──

    [Fact]
    public void ObjectBoxedCastContravariantAction_Throws()
    {
        // Verbatim minimized repro (VM-proven ref=7, usugar 5: argument silently dropped).
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
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
}", "W12R5ObjContra"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("Action<object>", ex.Message);
        Assert.Contains("Action<string>", ex.Message);
    }

    // ── [W1] parameter-evidence flavor: the box is a setter's `value` param ──

    [Fact]
    public void SetterValueObjectCastVariantDelegate_Throws()
    {
        // Corpus W12EC5R16 (VM-proven ref=3 vs -1): the cast sits inside the setter body, so the
        // operand is the object-typed `value` PARAMETER — evidence comes from the class-visible
        // property write's RHS (Func<string>), not from a local.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R5SetVal : UdonSharpBehaviour {
    public int seed; public int result;
    Func<object> _store;
    string MakeTag() { return ""bb"" + seed; }
    public object Boxed { set { _store = (Func<object>)value; } }
    void Start() {
        Func<string> narrow = MakeTag;
        Boxed = narrow;
        object o = _store();
        result = o == null ? -1 : o.ToString().Length;
    }
}", "W12R5SetVal"));
        Assert.Contains("Variant delegate conversion", ex.Message);
        Assert.Contains("laundered through 'object'", ex.Message);
    }

    // ── [W1]/[W2] controls: the pinned accepted boundary keeps flowing ──

    [Fact]
    public void ObjectRoundtripSameTypeThroughParam_Compiles()
    {
        // w9w30-shaped control: all class-visible call sites feed the SAME sig — the parameter
        // evidence agrees with the destination, so the reference passthrough stays.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R5ParamRt : UdonSharpBehaviour {
    public int k; public int r1;
    void Start() { r1 = Run((Func<int, int>)Quad, k); }
    int Run(object boxed, int v) {
        Func<int, int> g = (Func<int, int>)boxed;
        return g(v);
    }
    public int Quad(int v) { return v * 4; }
}", "W12R5ParamRt");
        Assert.Contains("__dlg", uasm);
    }

    [Fact]
    public void ObjectCastEvidenceFree_Compiles()
    {
        // w12ec4r19-shaped control: the operand local's only write is a CALL RESULT — no delegate
        // evidence is visible, so the cast keeps flowing (the documented unprovable residual).
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W12R5EvFree : UdonSharpBehaviour {
    public int seed; public int result;
    string MakeTag() { return ""z"" + seed; }
    public T Echo<T>(T x) { return x; }
    void Start() {
        Func<string> d = MakeTag;
        object boxed = d;
        object echoed = Echo<object>(boxed);
        Func<string> rebound = (Func<string>)echoed;
        result = rebound().Length;
    }
}", "W12R5EvFree");
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
