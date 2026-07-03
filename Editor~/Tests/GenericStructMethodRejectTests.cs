using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Layer-3 diagnostic quick-win / roadmap B36 residue: an instance method on a struct that declares
/// its OWN type parameter (struct Box&lt;T&gt;) has no monomorphization path — CollectStructMethodsInOperation
/// registers the method once by OriginalDefinition regardless of the receiver's concrete T, so every
/// call site dispatches to one body and hits an SDK-assembler ICE. Closed with a loud
/// NotSupportedException at the collector choke point, before extern/assembler. Full generic-struct-type
/// monomorphization remains backlogged — only the call site is rejected.
/// </summary>
public class GenericStructMethodRejectTests
{
    [Fact]
    public void GenericStructInstanceMethod_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
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
}", "BoxUser"));
        Assert.Contains("Generic struct instance methods", ex.Message);
        Assert.Contains("Box", ex.Message);
    }

    // ── Accept-boundary controls: must still compile ──

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
        // Constructing a generic struct and touching its FIELD (no instance method call) has no
        // per-call-site monomorphization problem (fields are plain object[] slots) — only the
        // instance-method dispatch is rejected.
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
