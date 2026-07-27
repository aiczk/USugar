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
    // definition to a single instantiation — reject continues (design §3, widened [X6]/[Y2] tier). This
    // is the ONLY surviving closure-pin tier after B45 M2.
    //
    // B45 M2 history: wave-14 briefly ALSO pinned on ANY capture in a struct member
    // (ClosurePin.StructMemberCapturing) because struct-hosted closures fell back to the naive
    // shared-field mechanism (CaptureScopeAnalysis did not walk struct methods as roots, so no
    // per-activation env). B45 M1 joined struct-hosted closures to the Stage-2 env records; B45 M2 then
    // retired that capture tier — a NON-T-dependent capturing closure is legal across instantiations
    // again (its captures live in per-activation env records, not a shared field). Only T-dependence
    // pins now, exactly as for an ordinary generic method. ──

    [Fact]
    public void GenericStructMethod_TDependentClosure_SecondInstantiation_Compiles()
    {
        // FLIPPED 2026-07-10 (per-spec closure root fix): each struct spec's member carries its own
        // closure copy — VM oracle: harness PerSpec B70_GenericStruct_TDependentClosure.
        TestHelper.CompileToUasm(@"
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
}", "GR1User");
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
    public void GenericStructMethod_NonTDependentClosure_TwoInstantiations_Compiles()
    {
        // B45 M2 unlock: a NON-type-param-dependent capturing closure in a generic struct member, shared
        // across two DISTINCT instantiations, is now LEGAL. The wave-14 reject
        // (ClosurePin.StructMemberCapturing) existed only because CaptureScopeAnalysis did not walk struct
        // member bodies, so the closure fell back to the naive shared-field mechanism and the first
        // instantiation's captured contribution silently vanished (90 instead of the CLR's 110). B45 M1
        // joined struct-hosted closures to the Stage-2 per-activation env records, so the T-free shared
        // hoist is sound across instantiations exactly like an ordinary generic method. Structure-only
        // here (tracked suite is headless); the 90→110 VALUE flip is real-VM DiffFuzz-verified in the
        // harness (Wave14GenStructMemberRegressionTests / B45 M2 value pins).
        var uasm = TestHelper.CompileToUasm(@"
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
}", "GR1NonDep");
        Assert.NotNull(uasm);
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
    public void GenericStructIsType_UsesClosedBundleIdentity()
    {
        // WaveJoint R2 [D10]: boxing the struct itself now rejects at the erasure choke, so the box
        // here is an int — the pin isolates the runtime-type-test choke on the generic-struct TARGET.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Box<T> { public T value; }
public class GR2User : UdonSharpBehaviour {
    public int seed;
    void Start() {
        object box = seed;
        bool b = box is Box<int>;
    }
}", "GR2User");
        Assert.Contains(
            "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
            uasm);
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
