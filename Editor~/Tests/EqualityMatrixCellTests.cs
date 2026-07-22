using Xunit;

namespace USugar.Tests;

/// <summary>
/// Tier-2 equality-matrix cells (hand-enumeration audit 2026-07-17): the two un-armed cells that
/// worked by generic-extern coincidence, VM-verified per shape and frozen here as contract
/// (real-VM value pins live in the local harness, EqMatrixCellsVmTests).
///
/// (a) STATIC object.Equals(a, b) with v1-class operands: the argument erasure is carved out at the
///     equality position (BoundaryChecker.IsProgramLocalEqualityPosition) and the call falls to the
///     registered null-safe static SystemObject extern, whose BCL semantics (ReferenceEquals, then
///     virtual Equals — reference equality for object[] bundles) match C# reference semantics for
///     unoverridden classes on same-ref / different-ref / null / mixed operand pairs (VM Match).
///
/// (b) enum instance .Equals: a USER enum's inherited Enum.Equals owner resolves through B59 to the
///     receiver's erased underlying-primitive extern (SystemInt32/SystemByte.__Equals__SystemObject),
///     whose box-type-and-value BCL semantics equal C# Enum.Equals under the erased tag (VM Match) —
///     frozen. An SDK enum's owner resolved to its own Udon type (UnityEngineKeyCode …), which has NO
///     registered __Equals extern, and ResolveExtern's Component-owner fallback silently adopted
///     UnityEngineComponent.__Equals__SystemObject__SystemBoolean — whose wrapper reads the receiver
///     as UnityEngine.Object, so the real VM throws HeapTypeMismatchException at runtime on legal C#
///     (confirmed by the runtime differential harness), laundered past the extern
///     census because the adopted extern IS registered. Fixed: an SDK-enum receiver's .Equals lowers
///     to the null-safe STATIC object.Equals extern — an SDK enum's box keeps its real type identity
///     on the VM heap, so static object.Equals IS C#'s Enum.Equals (same type AND same value) for
///     every argument shape, including cross-type. (EmitEnumToUnderlying + int equality would be
///     WRONG here: it erases the type check, answering true for equal-valued different SDK enums.)
/// </summary>
public class EqualityMatrixCellTests
{
    const string StaticObjectEqualsExtern = "SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean";

    // ── Cell (a): static object.Equals with v1-class operands — frozen lowering ──

    [Fact]
    public void StaticObjectEquals_ClassOperands_LowersToStaticObjectEqualsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class EqCellA { public int V; }
public class EqCellCls : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        EqCellA a = new EqCellA(); a.V = seed; EqCellA b = new EqCellA(); EqCellA n = null;
        bool e1 = object.Equals(a, b);
        bool e2 = object.Equals(a, n);
        result = (e1 ? 1 : 0) + (e2 ? 2 : 0);
    }
}", "EqCellCls");
        Assert.Contains(StaticObjectEqualsExtern, uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    [Fact]
    public void StaticObjectEquals_MixedClassNonClassOperands_LowersToStaticObjectEqualsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class EqCellM { public int V; }
public class EqCellMix : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        EqCellM a = new EqCellM(); a.V = seed;
        bool e1 = object.Equals(a, ""x"");
        bool e2 = object.Equals(a, seed);
        result = (e1 ? 1 : 0) + (e2 ? 2 : 0);
    }
}", "EqCellMix");
        Assert.Contains(StaticObjectEqualsExtern, uasm);
    }

    // ── Cell (b): user-enum instance .Equals — frozen underlying-primitive lowering ──

    [Fact]
    public void UserEnumInstanceEquals_IntUnderlying_LowersToUnderlyingPrimitiveEqualsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EqDirI { N, E, S, W }
public class EqCellUe : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        EqDirI a = (EqDirI)(seed & 3); EqDirI b = (EqDirI)((seed + 1) & 3);
        result = a.Equals(b) ? 1 : 0;
    }
}", "EqCellUe");
        Assert.Contains("SystemInt32.__Equals__SystemObject__SystemBoolean", uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    [Fact]
    public void UserEnumInstanceEquals_ByteUnderlying_LowersToUnderlyingPrimitiveEqualsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EqDirB : byte { A, B, C, D }
public class EqCellUb : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        EqDirB a = (EqDirB)(seed & 3); EqDirB b = (EqDirB)((seed + 1) & 3);
        result = a.Equals(b) ? 1 : 0;
    }
}", "EqCellUb");
        Assert.Contains("SystemByte.__Equals__SystemObject__SystemBoolean", uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    // ── Cell (b): SDK-enum instance .Equals — fixed lowering (was a runtime HeapTypeMismatch) ──

    [Fact]
    public void SdkEnumInstanceEquals_LowersToStaticObjectEquals_NotComponentFallback()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class EqCellSdk : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        KeyCode a = seed > 0 ? KeyCode.Space : KeyCode.A; KeyCode d = KeyCode.Return;
        result = a.Equals(d) ? 1 : 0;
    }
}", "EqCellSdk");
        Assert.Contains(StaticObjectEqualsExtern, uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    [Fact]
    public void SdkEnumInstanceEquals_ObjectBoxedArgument_LowersToStaticObjectEquals()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class EqCellSdkO : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() {
        KeyCode a = seed > 0 ? KeyCode.Space : KeyCode.A; object d = (object)KeyCode.Return;
        result = a.Equals(d) ? 1 : 0;
    }
}", "EqCellSdkO");
        Assert.Contains(StaticObjectEqualsExtern, uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }

    // Type-parameter receiver instantiated with an SDK enum reaches the same cell through the
    // monomorphization map (the [V3] concrete re-route would otherwise build the same invalid
    // UnityEngineKeyCode.__Equals signature and crash through the Component fallback).
    [Fact]
    public void TypeParamReceiver_SdkEnumEquals_LowersToStaticObjectEquals()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class EqCellSdkT : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { result = G<KeyCode>(seed > 0 ? KeyCode.Space : KeyCode.A); }
    int G<T>(T v) where T : struct {
        T dummy = v;
        return dummy.Equals(default(T)) ? 1 : 0;
    }
}", "EqCellSdkT");
        Assert.Contains(StaticObjectEqualsExtern, uasm);
        Assert.DoesNotContain("UnityEngineComponent.", uasm);
    }
}
