using System;
using Xunit;

namespace USugar.Tests;

public class ObjectArrayProvenanceTests
{
    [Fact]
    public void GenericArrayCarryingProgramLocalClass_ErasureToObjectArray_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public class Foo { public int v; }
public class ProgramLocalArrayErasureHost : UdonSharpBehaviour {
    object[] Erase<T>(T[] xs) where T : class { object[] o = xs; return o; }
    void Start() { var xs = new Foo[1]; Erase(xs); }
}", "ProgramLocalArrayErasureHost"));
        Assert.Contains("Erasing the v1 user class", ex.Message);
    }

    [Fact]
    public void GenericStructCarryingProgramLocalClass_LocalErasure_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Foo { public int v; }
public struct Box<T> { public T value; }
public class ProgramLocalStructErasureHost : UdonSharpBehaviour {
    object Erase<T>(Box<T> box) { object o = box; return o; }
    void Start() { Box<Foo> box = default; Erase(box); }
}", "ProgramLocalStructErasureHost");
        Assert.NotNull(uasm);
    }
}
