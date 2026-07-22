using Xunit;

namespace USugar.Tests;

public class UserClassInterfaceTests
{
    [Fact]
    public void CrossProgramInterfaceField_UsesClassWireTypeIdForDispatch()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IWireRead { int Read(); }
public class WireRead : IWireRead { public int value; public int Read() => value; }
public class WireReadB : IWireRead { public int value; public int Read() => value + 1; }
public class InterfaceProvider : UdonSharpBehaviour { public IWireRead Value; }
public class InterfaceConsumer : UdonSharpBehaviour {
  public InterfaceProvider provider;
  public bool choose;
  public int result;
  void Start() {
    provider.Value = choose ? (IWireRead)new WireRead { value = 17 } : new WireReadB { value = 16 };
    result = provider.Value.Read();
  }
}
", "InterfaceConsumer");

        Assert.Contains("__typeobj_WireRead", uasm);
        Assert.Contains("SystemString.__op_Equality__SystemString_SystemString__SystemBoolean", uasm);
    }

    [Fact]
    public void LocalInterfaceVariable_DispatchesToUserClass()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public interface ILocalRead { int Read(int add); }
public class LocalReadA : ILocalRead {
  public int value;
  public int Read(int add) => value + add;
}
public class LocalInterfaceUse : UdonSharpBehaviour {
  public int result;
  void Start() {
    ILocalRead item = new LocalReadA { value = 4 };
    result = item.Read(3);
  }
}
", "LocalInterfaceUse");

    [Fact]
    public void LocalInterfaceVariable_DispatchesAcrossImplementations()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public interface ILocalKind { int Kind(); }
public class LocalKindA : ILocalKind { public int Kind() => 1; }
public class LocalKindB : ILocalKind { public int Kind() => 2; }
public class LocalInterfaceMany : UdonSharpBehaviour {
  public bool choose;
  public int result;
  void Start() {
    ILocalKind item = choose ? (ILocalKind)new LocalKindA() : new LocalKindB();
    result = item.Kind();
  }
}
", "LocalInterfaceMany");

    [Fact]
    public void LocalInterfaceProperty_DispatchesImplicitAutoImplementation()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public interface ILocalValue { int Value { get; set; } }
public class LocalValue : ILocalValue { public int Value { get; set; } }
public class LocalInterfaceProperty : UdonSharpBehaviour {
  public int result;
  void Start() {
    ILocalValue item = new LocalValue();
    item.Value = 12;
    result = item.Value;
  }
}
", "LocalInterfaceProperty");

    [Fact]
    public void LocalInterfaceProperty_DispatchesExplicitAutoImplementation()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public interface ILocalExplicit { int Value { get; set; } }
public class LocalExplicit : ILocalExplicit { int ILocalExplicit.Value { get; set; } }
public class LocalExplicitProperty : UdonSharpBehaviour {
  public int result;
  void Start() {
    ILocalExplicit item = new LocalExplicit();
    item.Value = 14;
    result = item.Value;
  }
}
", "LocalExplicitProperty");

    [Fact]
    public void LocalInterfaceEvent_DispatchesCustomAccessors()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public interface ILocalEvents { event Action Changed; void Fire(); }
public class LocalEvents : ILocalEvents {
  Action handlers;
  public event Action Changed { add { handlers += value; } remove { handlers -= value; } }
  public void Fire() { handlers?.Invoke(); }
}
public class LocalInterfaceEvent : UdonSharpBehaviour {
  public int result;
  void Handler() { result++; }
  void Start() {
    ILocalEvents source = new LocalEvents();
    source.Changed += Handler;
    source.Fire();
    source.Changed -= Handler;
  }
}
", "LocalInterfaceEvent");

    [Fact]
    public void LocalInterfaceMethodGroup_CapturesBundleReceiver()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public interface ILocalFunction { int Apply(int value); }
public class LocalFunction : ILocalFunction {
  public int offset;
  public int Apply(int value) => offset + value;
}
public class LocalInterfaceMethodGroup : UdonSharpBehaviour {
  public int result;
  void Start() {
    ILocalFunction source = new LocalFunction { offset = 6 };
    Func<int, int> apply = source.Apply;
    result = apply(5);
  }
}
", "LocalInterfaceMethodGroup");
}
