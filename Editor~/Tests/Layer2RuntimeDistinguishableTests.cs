using Xunit;

namespace USugar.Tests;

/// <summary>
/// Layer-2 structural fix — the runtime-type-tag-collapse unsoundness class closed BY CONSTRUCTION.
///
/// ExternResolver.GetUdonTypeName is non-injective: it folds many distinct CLR types onto one Udon
/// runtime tag (every delegate/user-struct/tuple/array-of-those + object[] → SystemObjectArray;
/// UdonSharpBehaviour + every derived type + UdonBehaviour + every user interface → IUdonEventReceiver;
/// arrays of those → ComponentArray; a user enum → its underlying int; Nullable&lt;T&gt; → a box). A runtime
/// type test (SystemType.__IsInstanceOfType) against such a type CANNOT discriminate it — it matches ANY
/// same-tag value and silently takes the wrong branch. ExternResolver.IsRuntimeDistinguishable is the
/// single predicate; both runtime-type-test sites (OperatorHandler.EmitTypeCheck for type patterns AND
/// VisitIsType for a bare `x is T`) route through it, so no reachable path emits IsInstanceOfType without
/// the guard.
///
/// ACCEPT boundary: a type whose Udon tag uniquely identifies it (int/string/GameObject/Vector3/SDK
/// enums/int[]/…) plus bare `object` (its test is universally answerable) still compiles. REJECT
/// boundary: every folded type is a loud NotSupportedException — the accepted over-rejection is that
/// `o is DerivedBehaviour`, `o is SomeStruct`, `o is object[]`, `o is MyEnum`, `o is IMyInterface`,
/// `o is (int,int)`, `o is Func&lt;...&gt;` are now loud rather than silently-wrong.
/// </summary>
public class Layer2RuntimeDistinguishableTests
{
    // ── ACCEPT: uniquely-tagged targets still compile (no over-rejection) ──

    [Fact]
    public void IsInt_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L2Int : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = seed; result = o is int ? 1 : 0; }
}", "L2Int");

    [Fact]
    public void IsString_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L2Str : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = ""x""; result = o is string ? 1 : 0; }
}", "L2Str");

    [Fact]
    public void IsGameObject_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class L2Go : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = gameObject; result = o is GameObject ? 1 : 0; }
}", "L2Go");

    [Fact]
    public void IsVector3_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class L2Vec : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = new Vector3(1, 2, 3); result = o is Vector3 ? 1 : 0; }
}", "L2Vec");

    [Fact]
    public void IsObject_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L2Obj : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = seed; result = o is object ? 1 : 0; }
}", "L2Obj");

    [Fact]
    public void IsIntArray_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L2IntArr : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = new int[] { seed }; result = o is int[] ? 1 : 0; }
}", "L2IntArr");

    [Fact]
    public void IsSdkEnum_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class L2SdkEnum : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { object o = KeyCode.A; result = o is KeyCode ? 1 : 0; }
}", "L2SdkEnum");

    [Fact]
    public void SameSignatureDelegate_NoIsTest_Compiles()
        => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class L2Dlg : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { Func<int> f = () => seed; result = f(); }
}", "L2Dlg");

    // ── REJECT: folded targets are loud (accepted over-rejection) ──

    [Fact]
    public void IsFuncDelegate_Throws()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class L2RFunc : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { Func<int> f = () => seed; boxed = f; result = boxed is Func<int> ? 1 : 0; }
}", "L2RFunc"));
        Assert.Contains("cannot tell delegate signatures apart", ex.Message);
    }

    [Fact]
    public void IsUserStruct_Throws()
    {
        // WaveJoint R2 [D10]: boxing the struct itself now rejects at the erasure choke, so the box
        // here is an int — the pin isolates the runtime-type-test choke on the aggregate TARGET.
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct L2S { public int v; }
public class L2RStruct : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { boxed = seed; result = boxed is L2S ? 1 : 0; }
}", "L2RStruct"));
        Assert.Contains("Runtime type test against", ex.Message);
    }

    [Fact]
    public void IsTuple_Throws()
    {
        // WaveJoint R2 [D10]: boxing the tuple itself now rejects at the erasure choke, so the box
        // here is an int — the pin isolates the runtime-type-test choke on the aggregate TARGET.
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class L2RTuple : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { boxed = seed; result = boxed is ValueTuple<int, int> ? 1 : 0; }
}", "L2RTuple"));
        Assert.Contains("Runtime type test against", ex.Message);
    }

    [Fact]
    public void IsUserEnum_Throws()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum L2E { A, B }
public class L2REnum : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { boxed = L2E.A; result = boxed is L2E ? 1 : 0; }
}", "L2REnum"));
        Assert.Contains("Runtime type test against", ex.Message);
    }

    [Fact]
    public void IsDerivedBehaviour_Throws()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L2Derived : UdonSharpBehaviour { }
public class L2RDerived : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { result = boxed is L2Derived ? 1 : 0; }
}", "L2RDerived"));
        Assert.Contains("Runtime type test against", ex.Message);
    }

    [Fact]
    public void IsObjectArray_Throws()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class L2RObjArr : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { boxed = new object[] { seed }; result = boxed is object[] ? 1 : 0; }
}", "L2RObjArr"));
        Assert.Contains("Runtime type test against", ex.Message);
    }

    [Fact]
    public void IsUserInterface_Throws()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IL2 { void M(); }
public class L2RIface : UdonSharpBehaviour {
    public int seed; public int result;
    object boxed;
    void Start() { result = boxed is IL2 ? 1 : 0; }
}", "L2RIface"));
        Assert.Contains("Runtime type test against", ex.Message);
    }
}
