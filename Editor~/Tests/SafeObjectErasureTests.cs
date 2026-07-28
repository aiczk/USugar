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
        if (erased is ErasedNode restored) Result = restored.Value;
    }
}
", "SafeErasureHost");
        Assert.Contains(
            "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
            uasm);
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
    public void LocalErasure_ExternUse_RejectsAtBoundary()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class UnsafeErasedNode { }
public class UnsafeErasureHost : UdonSharpBehaviour {
    void Start() { object erased = new UnsafeErasedNode(); Debug.Log(erased); }
}
", "UnsafeErasureHost"));
        Assert.Contains("extern", ex.Message);
        Assert.Contains("compiler bundle", ex.Message);
    }

    [Fact]
    public void LocalErasure_ObjectCopy_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CopiedErasedNode { }
public class CopiedErasureHost : UdonSharpBehaviour {
    void Start() { object erased = new CopiedErasedNode(); object copy = erased; }
}
", "CopiedErasureHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void MutableObjectCarrier_RejectsAtExternBoundary()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class MutableNode { }
public class MutableCarrierHost : UdonSharpBehaviour {
    void Start() {
        object value = null;
        value = new MutableNode();
        Debug.Log(value);
    }
}", "MutableCarrierHost"));
        Assert.Contains("may carry a compiler bundle", ex.Message);
    }

    [Fact]
    public void ObjectParameter_RejectsAtExternBoundary()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ParameterNode { }
public class ParameterCarrierHost : UdonSharpBehaviour {
    void Log(object value) { Debug.Log(value); }
    void Start() { Log(new ParameterNode()); }
}", "ParameterCarrierHost"));
        Assert.Contains("may carry a compiler bundle", ex.Message);
    }

    [Fact]
    public void ProvenNativeObjectLocal_CanCrossExternBoundary()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NativeCarrierHost : UdonSharpBehaviour {
    public int seed;
    void Start() { object value = seed; Debug.Log(value); }
}", "NativeCarrierHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void DynamicObjectEquals_DispatchesCompilerBundlesByTypeIdentity()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DynamicEqualsHost : UdonSharpBehaviour {
    public object left;
    public object right;
    public bool result;
    void Start() { result = object.Equals(left, right); }
}", "DynamicEqualsHost");
        Assert.Contains(
            "SystemString.__StartsWith__SystemString__SystemBoolean",
            uasm);
        Assert.Contains(
            "SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
            uasm);
    }

    [Fact]
    public void DynamicGetType_RejectsCompilerBundleCarrier()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public class DynamicGetTypeNode { }
public class DynamicGetTypeHost : UdonSharpBehaviour {
    object Read(object value) { return value.GetType(); }
    void Start() { Read(new DynamicGetTypeNode()); }
}", "DynamicGetTypeHost"));
        Assert.Contains(
            "GetType() is not supported for a value that may contain a compiler bundle",
            ex.Message);
    }

    [Fact]
    public void SwitchExpressionCarrier_RejectsAtExternBoundary()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SwitchArmNode { }
public class SwitchArmCarrierHost : UdonSharpBehaviour {
    public bool flag;
    void Start() {
        var node = new SwitchArmNode();
        Debug.Log(flag switch { true => (object)node, _ => null });
    }
}", "SwitchArmCarrierHost"));
        Assert.Contains("may carry a compiler bundle", ex.Message);
    }

    [Fact]
    public void ConditionalAccessCarrier_RejectsAtExternBoundary()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class AccessArmNode { public object Payload; }
public class AccessArmCarrierHost : UdonSharpBehaviour {
    void Start() {
        var node = new AccessArmNode { Payload = new AccessArmNode() };
        Debug.Log(node?.Payload);
    }
}", "AccessArmCarrierHost"));
        Assert.Contains("may carry a compiler bundle", ex.Message);
    }

    [Fact]
    public void UserClassGetHashCode_UsesBundleReferenceHash()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class HashNode { }
public class HashHost : UdonSharpBehaviour {
    public int result;
    void Start() { result = new HashNode().GetHashCode(); }
}", "HashHost");
        Assert.Contains(
            "SystemObject.__GetHashCode__SystemInt32",
            uasm);
    }

    [Fact]
    public void DelegateReturn_RejectsAtExternBoundary()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
using UnityEngine;
public class DelegateReturnNode { }
public class DelegateReturnHost : UdonSharpBehaviour {
    void Start() { Func<object> make = () => new DelegateReturnNode(); Debug.Log(make()); }
}", "DelegateReturnHost"));
        Assert.Contains("may carry a compiler bundle", ex.Message);
    }
}
