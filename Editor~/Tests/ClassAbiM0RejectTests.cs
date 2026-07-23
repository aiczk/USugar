using System;
using Xunit;

namespace USugar.Tests;

// CA-M0 pre-armor (B79): four faces closing pre-existing silent paths around plain user classes ahead of the
// class-ABI v1 feature. See docs/superpowers/specs/2026-07-05-class-abi-design.md §1. Faces:
//   1. is/as/switch on a plain user class → loud reject (was: unresolvable "Foo" SystemType token baked).
//   2. class-typed field/local/param/return → loud reject (was: fabricated "Foo" heap type, no validator).
//   3. a record used as a member type → the RECORD message, not the class message.
//   4. a source class whose base is anything but System.Object → loud reject (user AND native bases).
// A foreign type modeled as a source stub WITH registered externs (TestStubs.DisposableResource) must stay
// legal — the accept-controls pin that the reject is extern-validity-gated, not a bare shape check (the exact
// distinction a prior shape-only GetUdonTypeName guard got reverted over).
public class ClassAbiM0RejectTests
{
    // ── Face 1: is / as / switch on a plain user class ──

    [Fact]
    public void Face1_IsTest_PlainClass_Compiles()  // FLIPPED 2026-07-11 (CA-v2b-1 is/cast)
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F1Is : UdonSharpBehaviour { public object o; public bool r; void Start(){ r = o is PlainFoo79; } }", "F1Is");

    [Fact]
    public void Face1_AsCast_PlainClass_Compiles()  // FLIPPED 2026-07-11 (CA-v2b-1 is/cast)
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F1As : UdonSharpBehaviour { public object o; public int r; void Start(){ PlainFoo79 f = o as PlainFoo79; r = (f == null) ? 0 : 1; } }", "F1As");

    [Fact]
    public void Face1_SwitchPattern_PlainClass_Compiles()  // FLIPPED 2026-07-11 (CA-v2b-1 is/cast)
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F1Sw : UdonSharpBehaviour { public object o; public int r; void Start(){ switch(o){ case PlainFoo79 p: r = 1; break; default: r = 0; break; } } }", "F1Sw");

    // ── Face 2: class-typed heap-var declarations (field / local / param / return). CA-M1 FLIP: a v1
    //    supported class now declares as a SystemObjectArray heap var (object[1+F] bundle) rather than
    //    rejecting — the B79 fabricated-name silent path is closed by acceptance, not rejection. ──

    [Fact]
    public void Face2_Field_PlainClass_Compiles()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F2Fld : UdonSharpBehaviour { PlainFoo79 f; void Start(){ } }", "F2Fld");
    }

    [Fact]
    public void Face2_Local_PlainClass_Compiles()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F2Loc : UdonSharpBehaviour { void Start(){ PlainFoo79 f = null; } }", "F2Loc");
    }

    [Fact]
    public void Face2_Param_PlainClass_Compiles()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F2Par : UdonSharpBehaviour { void M(PlainFoo79 f){ } void Start(){ } }", "F2Par");
    }

    [Fact]
    public void Face2_Return_PlainClass_Compiles()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo79 { public int V; }
public class F2Ret : UdonSharpBehaviour { PlainFoo79 M() => null; void Start(){ } }", "F2Ret");
    }

    // ── Face 3: a record used as a member type gets the RECORD message ──

    [Fact]
    public void Face3_RecordMemberType_RejectsWithRecordMessage()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public record PlainRec79(int V);
public class F3Rec : UdonSharpBehaviour { PlainRec79 r; void Start(){ } }", "F3Rec"));
        Assert.Contains("ecord", ex.Message); // "record" / "Record"
    }

    // ── Face 4: a NATIVE base still rejects; a USER-CLASS base is now supported (CA-v2 M1). ──

    [Fact]
    public void Face4_UserBase_Compiles()  // FLIPPED 2026-07-11 (CA-v2 M1 inheritance)
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class Base79 { public int A; }
public class Derived79 : Base79 { public int B; }
public class F4Usr : UdonSharpBehaviour { Derived79 d; void Start(){ d = new Derived79(); d.A = 1; d.B = 2; } }", "F4Usr");

    [Fact]
    public void Face4_NativeBase_RejectsWithBaseMessage()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NativeBased79 : MonoBehaviour { public int A; }
public class F4Nat : UdonSharpBehaviour { NativeBased79 n; void Start(){ } }", "F4Nat"));
        Assert.Contains("base", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Accept-boundary controls: a foreign type with real externs stays legal (extern-validity gate, NOT a
    //    bare shape check) — the exact case that sank the reverted shape-only guard. ──

    [Fact]
    public void Accept_ForeignExternType_IsTest_StillCompiles()
    {
        // DisposableResource has a real Udon type tag, so `is` against it is a genuine, resolvable test.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.TestStubs;
public class AccIs : UdonSharpBehaviour { public object o; public bool r; void Start(){ r = o is DisposableResource; } }", "AccIs");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void Accept_ForeignExternType_Field_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.TestStubs;
public class AccFld : UdonSharpBehaviour { DisposableResource r; void Start(){ } }", "AccFld");
        Assert.NotNull(uasm);
    }
}
