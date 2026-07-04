using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Feature G (generic struct type monomorphization, 2026-07-04 design). A struct that declares its OWN
/// type parameter (struct Box&lt;T&gt;) used to have no monomorphization path — CollectStructMethodsInOperation
/// registered its instance methods once by OriginalDefinition regardless of the receiver's concrete T,
/// so every call site dispatched to one shared body and hit an SDK-assembler ICE (roadmap B36),
/// rejected loudly at the collector choke point. Feature G replaces the reject with real per-spec
/// monomorphization — the CONSTRUCTED symbol (Box&lt;int&gt;.Get(), not Box&lt;T&gt;.Get()) is registered and
/// emitted with its own body, mirroring RegisterGenericSpecialization's discipline for the
/// containing-type dimension. This class is the acceptance lattice per design §6: every previously
/// B36-rejected member kind now compiles, and the two-instantiation-coexistence cases are the
/// structural half of the gate (value correctness is real-VM DiffFuzz-verified separately in the local
/// harness, FeatureGGenericStructRegressionTests — B36's root cause was value cross-contamination, so
/// "compiles" alone is not the bar; these tests only pin that the STRUCTURE goes through).
///
/// Two reject pins remain (design §4): G-R1 (a closure referencing the struct's OR method's own type
/// parameter pins the definition to one instantiation — same [X6]/[Y2] mechanism as a plain generic
/// method, widened to the containing-type dimension) and G-R2 (a runtime type test against a generic
/// struct — layer-2 IsRuntimeDistinguishable, unrelated to this design, out of scope). G-R3 pins that a
/// generic struct in a static-readonly / field-initializer position (the B41-fixed S1 path) works.
/// </summary>
public class GenericStructMethodRejectTests
{
    // ── Accept lattice (design §6): every previously-B36-rejected member kind now compiles ──

    [Fact]
    public void GenericStructInstanceMethod_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public T Get() { return value; }
}
public class BoxUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>();
        var v = box.Get();
    }
}", "BoxUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructParameterizedCtor_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public Box(T v) { value = v; }
}
public class BoxCtorUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>(5);
        var v = box.value;
    }
}", "BoxCtorUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructComputedProperty_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public T Doubled => value;
}
public class BoxPropUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>();
        box.value = 5;
        var v = box.Doubled;
    }
}", "BoxPropUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructIndexer_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T[] items;
    public T this[int i] { get { return items[i]; } set { items[i] = value; } }
}
public class BoxIdxUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>();
        box.items = new int[1];
        box[0] = 5;
        var v = box[0];
    }
}", "BoxIdxUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructOperator_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public int tag;
    public static Box<T> operator +(Box<T> a, Box<T> b) { Box<T> r = new Box<T>(); r.tag = a.tag + b.tag; return r; }
}
public class BoxOpUser : UdonSharpBehaviour {
    void Start() {
        Box<int> a = new Box<int>();
        Box<int> b = new Box<int>();
        var c = a + b;
    }
}", "BoxOpUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericMethodOnGenericStruct_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public U Map<U>(U u) { return u; }
}
public class BoxMapUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>();
        var a = box.Map(1);
        var b = box.Map(""hi"");
    }
}", "BoxMapUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void RecursiveGenericStructMethod_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public int CountDown(int n) { if (n <= 0) return 0; return 1 + CountDown(n - 1); }
}
public class BoxRecurUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>();
        var v = box.CountDown(5);
    }
}", "BoxRecurUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void TwoInstantiations_MethodCtorPropIndexerOperator_Coexist()
    {
        // Structural half of the mandatory gate (design §6) — Box<int> and Box<string> coexisting,
        // both calling methods/ctor/computed-prop/indexer/operator. Value correctness (the actual
        // acceptance bar — B36's root was shared-body cross-contamination) is real-VM DiffFuzz-verified
        // in the local harness (FeatureGGenericStructRegressionTests), not tracked here.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public T[] items;
    public Box(T v) { value = v; items = null; }
    public T Get() { return value; }
    public T Doubled => value;
    public T this[int i] { get { return items[i]; } set { items[i] = value; } }
    public static Box<T> operator +(Box<T> a, Box<T> b) { return a; }
}
public class TwoInstUser : UdonSharpBehaviour {
    void Start() {
        Box<int> a = new Box<int>(1);
        a.items = new int[1];
        a[0] = a.Get() + a.Doubled;
        var ac = a + a;

        Box<string> b = new Box<string>(""x"");
        b.items = new string[1];
        b[0] = b.Get();
        var bc = b + b;
    }
}", "TwoInstUser");
        Assert.NotNull(uasm);
    }

    // ── G-R1: a closure referencing the generic's type parameter (struct's OR method's own) pins the
    // definition to a single instantiation — reject continues (design §3, widened [X6]/[Y2] tier).
    //
    // B45 update (wave-14, corrected from the original note below): a struct-hosted closure ALWAYS
    // compiles via the naive shared-field fallback (CaptureScopeAnalysis never walks struct methods as
    // roots, so it never gets the multi-activation-safe envp/BindingScope treatment). The original note
    // here believed that fallback was safe for any single-activation/non-escaping/non-recursive shape
    // REGARDLESS of T-dependence — real-VM diff-fuzzing proved that false for a CAPTURING closure shared
    // across two DISTINCT struct instantiations (the shared field is rebound by whichever instantiation
    // registers last). The boundary is now: non-capturing closures stay legal to share (nothing to
    // alias); a capturing closure pins the instantiation exactly like the type-param-dependent case,
    // regardless of whether it references T. ──

    [Fact]
    public void GenericStructMethod_TDependentClosure_SecondInstantiation_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct Box<T> {
    public T value;
    public T RunWithClosure() {
        T local = value;
        Func<T> f = () => local;
        return f();
    }
}
public class GR1User : UdonSharpBehaviour {
    void Start() {
        Box<int> a = new Box<int>();
        var x = a.RunWithClosure();
        Box<string> b = new Box<string>();
        var y = b.RunWithClosure();
    }
}", "GR1User"));
        Assert.Contains("lambda or local function", ex.Message);
    }

    [Fact]
    public void GenericStructMethod_TDependentClosure_SingleInstantiation_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct Box<T> {
    public T value;
    public T RunWithClosure() {
        T local = value;
        Func<T> f = () => local;
        return f();
    }
}
public class GR1Single : UdonSharpBehaviour {
    void Start() {
        Box<int> a = new Box<int>();
        var x = a.RunWithClosure();
    }
}", "GR1Single");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructMethod_NonTDependentClosure_TwoInstantiations_ThrowsNotSupported()
    {
        // Corrected (wave-14 diff-fuzzing, DiffFuzz real-VM oracle): this used to assert
        // "StillCompiles" on the belief that a non-T-dependent closure is a safe shared hoist across
        // instantiations (design §3's G-R1 note). That belief was never runtime-value-checked here
        // (Assert.NotNull(uasm) — compile-success only) and was FALSE: a struct-hosted closure never
        // gets the Stage-2 per-activation env-record protection that makes the T-free case safe for an
        // ordinary generic METHOD (CaptureScopeAnalysis.AddRoots walks class+base roots only — B45), so
        // it always falls back to the naive shared-field LocalBindings mechanism — rebound by whichever
        // instantiation registers last while the ONE shared hoisted closure body was already emitted
        // against a single fixed binding. Real-VM proof: the first instantiation's captured contribution
        // silently vanished (90 instead of the CLR's 110 for `bi.Compute(a) + bs.Compute(b)`). Now loud
        // (ClosurePin.StructMemberCapturing) instead of silently wrong — same "loud over silent" doctrine
        // as the T-dependent case above.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct Box<T> {
    public T value;
    public int RunWithClosure(int extra) {
        int local = extra;
        Func<int> f = () => local + 1;
        return f();
    }
}
public class GR1NonDep : UdonSharpBehaviour {
    void Start() {
        Box<int> a = new Box<int>();
        var x = a.RunWithClosure(1);
        Box<string> b = new Box<string>();
        var y = b.RunWithClosure(2);
    }
}", "GR1NonDep"));
        Assert.Contains("captures an outer variable", ex.Message);
    }

    [Fact]
    public void GenericStructMethod_NonCapturingClosure_TwoInstantiations_StillCompiles()
    {
        // The safe half of the corrected boundary above: a struct-hosted closure that captures NOTHING
        // has no shared-field state to alias, so sharing one physical hoist across instantiations is
        // fine regardless of T-dependence.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public struct Box<T> {
    public T value;
    public int RunWithClosure(int extra) {
        Func<int, int> f = y => y + 1;
        return f(extra);
    }
}
public class GR1NonCapturing : UdonSharpBehaviour {
    void Start() {
        Box<int> a = new Box<int>();
        var x = a.RunWithClosure(1);
        Box<string> b = new Box<string>();
        var y = b.RunWithClosure(2);
    }
}", "GR1NonCapturing");
        Assert.NotNull(uasm);
    }

    // ── G-R2: runtime type test against a generic struct — layer-2 IsRuntimeDistinguishable, out of
    // scope for this design (type-tag ABI backlog). Unrelated to feature G's changes; pinned as a
    // regression guard only. ──

    [Fact]
    public void GenericStructIsType_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> { public T value; }
public class GR2User : UdonSharpBehaviour {
    void Start() {
        object box = new Box<int>();
        bool b = box is Box<int>;
    }
}", "GR2User"));
        Assert.Contains("Runtime type test", ex.Message);
    }

    // ── G-R3: a generic struct in a static-readonly field / field initializer (the B41-fixed S1 path)
    // works — accept, not reject. ──

    [Fact]
    public void GenericStructInStaticReadonlyField_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public Box(T v) { value = v; }
    public T Get() { return value; }
}
public class GR3User : UdonSharpBehaviour {
    static readonly Box<int> _s = new Box<int>(5);
    public int outv;
    void Start() { outv = _s.Get(); }
}", "GR3User");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructFieldInitializer_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
    public Box(T v) { value = v; }
}
public class GR3FieldInit : UdonSharpBehaviour {
    Box<int> _s = new Box<int>(7);
    void Start() { }
}", "GR3FieldInit");
        Assert.NotNull(uasm);
    }

    // ── Accept-boundary controls (predate feature G — must still compile) ──

    [Fact]
    public void NonGenericStructInstanceMethod_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct PlainBox {
    public int value;
    public int Get() { return value; }
}
public class PlainBoxUser : UdonSharpBehaviour {
    void Start() {
        PlainBox box = new PlainBox();
        var v = box.Get();
    }
}", "PlainBoxUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericMethodOnNonGenericStruct_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct IdBox {
    public T Id<T>(T x) { return x; }
}
public class IdBoxUser : UdonSharpBehaviour {
    void Start() {
        IdBox box = new IdBox();
        var a = box.Id(1);
        var b = box.Id(""hi"");
    }
}", "IdBoxUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericStructConstructionAndFieldAccess_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> {
    public T value;
}
public class BoxFieldUser : UdonSharpBehaviour {
    void Start() {
        Box<int> box = new Box<int>();
        box.value = 5;
        var v = box.value;
    }
}", "BoxFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void GenericMethodOnBehaviour_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GenMethodBehaviour : UdonSharpBehaviour {
    T Id<T>(T x) { return x; }
    void Start() {
        var a = Id(1);
        var b = Id(""hi"");
    }
}", "GenMethodBehaviour");
        Assert.NotNull(uasm);
    }
}
