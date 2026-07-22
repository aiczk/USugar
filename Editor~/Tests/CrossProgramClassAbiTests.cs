using Xunit;

namespace USugar.Tests;

public class CrossProgramClassAbiTests
{
    const string Source = @"
using UdonSharp;
public class WireBase {
    public int Value;
    public virtual int Read() { return Value + 1; }
}
public class WireDerived : WireBase {
    public override int Read() { return Value + 2; }
}
public class WireReceiver : UdonSharpBehaviour {
    public WireBase Stored;
    public int Result;
    public void Accept(WireBase value) { Stored = value; Result = value.Read(); }
    public WireBase Echo(WireBase value) { return value; }
}
public class WireSender : UdonSharpBehaviour {
    public WireReceiver Receiver;
    void Start() {
        var value = new WireDerived { Value = 5 };
        Receiver.Stored = value;
        Receiver.Accept(value);
        WireBase copy = Receiver.Echo(value);
    }
}
";

    [Fact]
    public void PublicFieldAndCall_ClassBundle_CrossProgramCompile()
    {
        var uasm = TestHelper.CompileToUasm(Source, "WireSender");

        Assert.Contains("SetProgramVariable", uasm);
        Assert.Contains("SystemObjectArray", uasm);
    }

    [Fact]
    public void IncomingClass_VirtualDispatchUsesStableWireTypeId()
    {
        var uasm = TestHelper.CompileToUasm(Source, "WireReceiver");

        Assert.Contains("__typeobj_WireDerived", uasm);
        Assert.Contains("SystemString.__op_Equality__SystemString_SystemString__SystemBoolean", uasm);
        Assert.Contains("WireDerived", uasm);
    }

    [Fact]
    public void PublicClassField_IsExportedObjectArray()
    {
        var uasm = TestHelper.CompileToUasm(Source, "WireReceiver");

        Assert.Contains("Stored: %SystemObjectArray", uasm);
        Assert.Contains(".export Stored", uasm);
    }

    [Fact]
    public void ClosedGenericClass_IncomingWithoutLocalMint_KeepsTypeIdentity()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class WireBox<T> {
    public T Value;
    public virtual int Kind() { return 1; }
}
public class WireIntBox : WireBox<int> {
    public override int Kind() { return 2; }
}
public class WireGenericReceiver : UdonSharpBehaviour {
    public WireBox<int> Stored;
    public int Read(WireBox<int> value) { Stored = value; return value.Kind(); }
}
", "WireGenericReceiver");

        Assert.Contains("__typeobj_WireBox_Int32", uasm);
        Assert.Contains("SystemString.__op_Equality__SystemString_SystemString__SystemBoolean", uasm);
    }
}
