using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// User reference-type boundary coverage. Class ABI v1 source classes lower to object[] bundles,
/// while foreign/SDK classes must bind their constructors through the mandatory Udon ABI catalog.
/// Records and unsupported inheritance remain loud rejects because the v1 layout cannot represent
/// their semantics.
/// </summary>
public class UserReferenceTypeRejectTests
{
    // CA-M1 FLIP: `new` on a v1 supported user class now MINTS an object[1+F] bundle (reference semantics)
    // instead of rejecting. VM value parity is pinned in the harness (ClassAbiM1VmTests).
    [Fact]
    public void PlainClass_NewInstance_Compiles()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainFoo { public int Value; }
public class PlainFooCtorUser : UdonSharpBehaviour {
    void Start() { var f = new PlainFoo(); }
}", "PlainFooCtorUser");
    }

    [Fact]
    public void PlainClass_NewInstanceWithArgs_Compiles()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class PlainQux { public int Value; public PlainQux(int v) { Value = v; } }
public class PlainQuxCtorUser : UdonSharpBehaviour {
    void Start() { var f = new PlainQux(1); }
}", "PlainQuxCtorUser");
    }

    [Fact]
    public void RecordClass_NewInstance_ThrowsNotSupported()
    {
        // Distinct from RecordClass_ThrowsNotSupported (FeatureCoverageTests): there the record itself
        // is the compile TARGET, structurally unreachable in real usage (never a UdonSharpBehaviour).
        // Here a plain behaviour merely USES a record class as a value — the reachable hole.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public record class PlainRec(int Value);
public class PlainRecUser : UdonSharpBehaviour {
    void Start() { var r = new PlainRec(1); }
}", "PlainRecUser"));
        Assert.Contains("User-defined reference types", ex.Message);
    }

    // ── Accept-boundary controls: must still compile ──

    [Fact]
    public void UdonSharpBehaviourField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class OtherBehaviour : UdonSharpBehaviour { }
public class UsbFieldUser : UdonSharpBehaviour {
    OtherBehaviour other;
    void Start() { }
}", "UsbFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void UnityReferenceTypeField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class UnityFieldUser : UdonSharpBehaviour {
    GameObject go;
    Transform t;
    void Start() { }
}", "UnityFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void StringField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class StringFieldUser : UdonSharpBehaviour {
    string s;
    void Start() { s = ""hi""; }
}", "StringFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void ArrayField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ArrayFieldUser : UdonSharpBehaviour {
    int[] nums;
    void Start() { nums = new int[3]; }
}", "ArrayFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void UserStructField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct PlainStruct { public int Value; }
public class StructFieldUser : UdonSharpBehaviour {
    PlainStruct s;
    void Start() { }
}", "StructFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void DelegateField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DelegateFieldUser : UdonSharpBehaviour {
    Action _callback;
    void Start() { _callback = () => { }; }
}", "DelegateFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void InterfaceTypedField_StillCompiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IPlainService { int GetValue(); }
public class InterfaceFieldUser : UdonSharpBehaviour {
    IPlainService svc;
    void Start() { }
}", "InterfaceFieldUser");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void ForeignClassWithRegisteredExtern_NewInstance_StillCompiles()
    {
        // TestStubs.DisposableResource: same TypeKind.Class/non-SDK-namespace/non-USB shape as PlainFoo,
        // but its ctor/Dispose/accessors ARE registered externs (fixture registry) — models a genuine
        // foreign/SDK reference type (e.g. VRCUrl, DataList). Must NOT reject; this is the exact case that
        // sank the type-lowering-level version of this guard.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class ForeignCtorUser : UdonSharpBehaviour {
    void Start() { using (var r = new DisposableResource()) { r.Value = 1; } }
}", "ForeignCtorUser");
        Assert.Contains("TestStubsDisposableResource.__ctor__", uasm);
    }
}
