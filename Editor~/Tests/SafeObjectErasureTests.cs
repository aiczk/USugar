using System;
using Xunit;

namespace USugar.Tests;

public class SafeObjectErasureTests
{
    [Fact]
    public void StableLocal_ClassToObjectAndBack_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ErasedNode { public int Value; }
public class SafeErasureHost : UdonSharpBehaviour {
    public int Result;
    void Start() {
        var node = new ErasedNode { Value = 7 };
        object erased = node;
        if (erased is ErasedNode) Result = ((ErasedNode)erased).Value;
    }
}
", "SafeErasureHost");

        Assert.Contains("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean", uasm);
    }

    [Fact]
    public void StableLocal_AsCast_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ErasedAsNode { public int Value; }
public class SafeAsErasureHost : UdonSharpBehaviour {
    void Start() {
        object erased = new ErasedAsNode();
        ErasedAsNode restored = erased as ErasedAsNode;
    }
}
", "SafeAsErasureHost");

        Assert.NotNull(uasm);
    }

    [Fact]
    public void LocalErasure_ExternUse_RejectsWholeErasure()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class UnsafeErasedNode { }
public class UnsafeErasureHost : UdonSharpBehaviour {
    void Start() { object erased = new UnsafeErasedNode(); Debug.Log(erased); }
}
", "UnsafeErasureHost"));

        Assert.Contains("Erasing the v1 user class", ex.Message);
    }

    [Fact]
    public void LocalErasure_ObjectCopy_RejectsWholeErasure()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CopiedErasedNode { }
public class CopiedErasureHost : UdonSharpBehaviour {
    void Start() { object erased = new CopiedErasedNode(); object copy = erased; }
}
", "CopiedErasureHost"));

        Assert.Contains("Erasing the v1 user class", ex.Message);
    }
}
