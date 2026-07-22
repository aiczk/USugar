using Xunit;
using System.Text.RegularExpressions;

namespace USugar.Tests;

public class StaticOwnerAbiTests
{
    [Fact]
    public void StaticHelperMutableField_ReadWriteAndIncrement_Compile()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class CounterOwner {
    public static int Value = 3;
    public static int Next() { return ++Value; }
}
public class StaticOwnerHost : UdonSharpBehaviour {
    public int Result;
    void Start() { CounterOwner.Value = 5; Result = CounterOwner.Next() + CounterOwner.Value; }
}
", "StaticOwnerHost");

        Assert.Contains("__static_", uasm);
    }

    [Fact]
    public void UserClassStaticField_HasOwnerScopedStorage()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ClassWithStatic {
    public static int Count;
    public ClassWithStatic() { Count++; }
}
public class ClassStaticHost : UdonSharpBehaviour {
    public int Result;
    void Start() { new ClassWithStatic(); new ClassWithStatic(); Result = ClassWithStatic.Count; }
}
", "ClassStaticHost");

        Assert.Contains("__static_", uasm);
    }

    [Fact]
    public void DifferentOwnersWithSameFieldName_DoNotCollide()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class StaticA { public static int Value; }
public static class StaticB { public static int Value; }
public class StaticCollisionHost : UdonSharpBehaviour {
    public int Result;
    void Start() { StaticA.Value = 1; StaticB.Value = 2; Result = StaticA.Value * 10 + StaticB.Value; }
}
", "StaticCollisionHost");

        Assert.True(Regex.Matches(uasm, @"__static_[A-Za-z0-9_]+_Value").Count >= 2);
    }

    [Fact]
    public void ClosedGenericOwner_DirectFieldAccess_UsesPerSpecStorage()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class StaticBox<T> { public static int Value; }
public class GenericStaticOwnerHost : UdonSharpBehaviour {
    public int Result;
    void Start() { StaticBox<int>.Value = 1; StaticBox<string>.Value = 2;
        Result = StaticBox<int>.Value * 10 + StaticBox<string>.Value; }
}
", "GenericStaticOwnerHost");

        Assert.True(Regex.Matches(uasm, @"__static_[A-Za-z0-9_]+_Value").Count >= 2);
    }

    [Fact]
    public void StaticComputedProperty_CallsSourceGetter()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class StaticPropertyOwner {
    public static int Value = 4;
    public static int Doubled { get { return Value * 2; } }
}
public class StaticPropertyHost : UdonSharpBehaviour {
    public int Result;
    void Start() { Result = StaticPropertyOwner.Doubled; }
}
", "StaticPropertyHost");

        Assert.Contains("get_Doubled", uasm);
        Assert.Contains("__static_", uasm);
    }

    [Fact]
    public void ClosedGenericOwner_MethodBodyUsesPerSpecStaticField()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class GenericCounter<T> {
    static int Value;
    public static int Next() { return ++Value; }
}
public class GenericCounterHost : UdonSharpBehaviour {
    public int Result;
    void Start() { Result = GenericCounter<int>.Next() * 10 + GenericCounter<string>.Next(); }
}
", "GenericCounterHost");

        Assert.True(Regex.Matches(uasm, @"__static_[A-Za-z0-9_]+_Value").Count >= 2);
    }

    [Fact]
    public void StaticComputedProperty_SetterCallsSourceBody()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class StaticPropertySetOwner {
    static int Backing;
    public static int Value { get { return Backing; } set { Backing = value + 1; } }
}
public class StaticPropertySetHost : UdonSharpBehaviour {
    public int Result;
    void Start() { StaticPropertySetOwner.Value = 4; Result = StaticPropertySetOwner.Value; }
}
", "StaticPropertySetHost");

        Assert.Contains("set_Value", uasm);
        Assert.Contains("get_Value", uasm);
    }

    [Fact]
    public void StaticAutoProperty_UsesOwnerScopedStorage()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class StaticAutoOwner {
    public static int Value { get; set; } = 3;
}
public class StaticAutoHost : UdonSharpBehaviour {
    public int Result;
    void Start() { StaticAutoOwner.Value += 2; Result = StaticAutoOwner.Value; }
}
", "StaticAutoHost");

        Assert.Contains("_prop_Value", uasm);
    }

    [Fact]
    public void StaticDelegateField_UsesOwnerScopedStorage()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public static class StaticCallbacks {
    public static Action Callback;
}
public class StaticDelegateHost : UdonSharpBehaviour {
    public int Result;
    void Mark() { Result++; }
    void Start() { StaticCallbacks.Callback = Mark; StaticCallbacks.Callback(); }
}
", "StaticDelegateHost");

        Assert.Contains("__static_", uasm);
        Assert.Contains("_Callback", uasm);
    }

    [Fact]
    public void StaticInitializers_PreserveDeclarationOrder()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticOrderHost : UdonSharpBehaviour {
    static int First = Make(1);
    static int Second = Make(2);
    static int Make(int value) { return value; }
}
", "StaticOrderHost", out var emitter);

        Assert.Collection(emitter.DebugStaticInitializerOrder,
            name => Assert.EndsWith("_First", name),
            name => Assert.EndsWith("_Second", name));
    }

    [Fact]
    public void StaticInitializerCycle_ThrowsWithDependencyPath()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticCycleHost : UdonSharpBehaviour {
    static int A = B + Read();
    static int B = A + Read();
    static int Read() { return 1; }
}
", "StaticCycleHost"));

        Assert.Contains("Static initializer cycle", ex.Message);
        Assert.Contains("StaticCycleHost.A", ex.Message);
        Assert.Contains("StaticCycleHost.B", ex.Message);
    }

    [Fact]
    public void StaticInitializerCycleThroughMethods_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticMethodCycleHost : UdonSharpBehaviour {
    static int A = ReadB();
    static int B = ReadA();
    static int ReadA() { return A; }
    static int ReadB() { return B; }
}
", "StaticMethodCycleHost"));

        Assert.Contains("Static initializer cycle", ex.Message);
        Assert.Contains("StaticMethodCycleHost.A", ex.Message);
        Assert.Contains("StaticMethodCycleHost.B", ex.Message);
    }

    [Fact]
    public void StaticInitializerCycleThroughComputedProperties_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticPropertyCycleHost : UdonSharpBehaviour {
    static int A = ReadB;
    static int B = ReadA;
    static int ReadA { get { return A; } }
    static int ReadB { get { return B; } }
}
", "StaticPropertyCycleHost"));

        Assert.Contains("Static initializer cycle", ex.Message);
    }
}
