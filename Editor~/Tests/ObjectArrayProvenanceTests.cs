using System;
using Xunit;

namespace USugar.Tests;

public class ObjectArrayProvenanceTests
{
    [Fact]
    public void GenericArrayCarryingProgramLocalClass_ErasureToObjectArray_Rejects()
    {
        // B85 ledger: T[] can instantiate as Foo[], and Foo is represented by a program-local
        // object[] bundle. Erasing that static fact to object[] would launder the payload.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class Foo { public int v; }
public class ProgramLocalArrayErasureHost : UdonSharpBehaviour {
    object[] Erase<T>(T[] xs) where T : class { object[] o = xs; return o; }
    void Start() { var xs = new Foo[1]; Erase(xs); }
}", "ProgramLocalArrayErasureHost"));
        Assert.Contains("Erasing the v1 user class", ex.Message);
    }

    [Fact]
    public void GenericStructCarryingProgramLocalClass_ErasureToObject_Rejects()
    {
        // B85 ledger: aggregate payload classification must look through generic fields.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class Foo { public int v; }
public struct Box<T> { public T value; }
public class ProgramLocalStructErasureHost : UdonSharpBehaviour {
    object Erase<T>(Box<T> box) { object o = box; return o; }
    void Start() { Box<Foo> box = default; Erase(box); }
}", "ProgramLocalStructErasureHost"));
        // WaveJoint R2 [D10]: the aggregate erasure arm now owns this shape (the source IS an
        // aggregate) — it fires before the class-payload walk, and names the immediate source type.
        Assert.Contains("Erasing the value type 'Box<", ex.Message);
    }
}
