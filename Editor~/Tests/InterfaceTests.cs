using Xunit;

namespace USugar.Tests;

public class InterfaceTests
{
    [Fact]
    public void Interface_BasicImplementation_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class HelloGreeter : UdonSharpBehaviour, IToggleable {
    bool _on;
    public void Toggle() { _on = !_on; }
}", "HelloGreeter");
        Assert.Contains(".data_start", uasm);
        Assert.Contains(".code_start", uasm);
        Assert.Contains(".export Toggle", uasm);
    }

    [Fact]
    public void Interface_NoBridge_WhenLayoutMatches()
    {
        // Toggle() is parameterless → raw name "Toggle" in both interface and class
        // No bridge needed
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class SimpleImpl : UdonSharpBehaviour, IToggleable {
    public void Toggle() { }
}", "SimpleImpl");
        Assert.Contains(".export Toggle", uasm);
        // Should not have a duplicate bridge export
        var lines = uasm.Split('\n');
        var toggleExports = System.Linq.Enumerable.Count(lines, l => l.Trim() == ".export Toggle");
        Assert.Equal(1, toggleExports);
    }

    [Fact]
    public void Interface_Bridge_WhenParamIdsDisagree()
    {
        // Interface DoIt(int x) → param __0_x__param
        // Class: Extra(int x) consumes x__param counter → DoIt gets __1_x__param
        // ExportName matches (__0_DoIt) but ParamIds differ → bridge needed.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IDoer {
    void DoIt(int x);
}
public class MyDoer : UdonSharpBehaviour, IDoer {
    public void Extra(int x) { }
    public void DoIt(int x) { }
}", "MyDoer");
        Assert.Contains(".export __0_DoIt", uasm);   // the class method
        Assert.Contains(".export __0_Extra", uasm);
        var lines = uasm.Split('\n');
        // The class method exports __0_DoIt EXACTLY ONCE; the bridge takes a unique interface-qualified
        // export (it must NOT collide with the class method's export — the collision this regression guards,
        // which used to fail real assembly with "Entry point already exported").
        var doItExports = System.Linq.Enumerable.Count(lines, l => l.Trim() == ".export __0_DoIt");
        Assert.Equal(1, doItExports);
        Assert.Contains(lines, l => l.Trim().StartsWith(".export __iface_") && l.Contains("DoIt"));
        // Every exported entry-point name is unique (no duplicate .export — the assembler rejects those).
        var exportNames = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(
            System.Linq.Enumerable.Where(lines, l => l.Trim().StartsWith(".export ")), l => l.Trim()));
        Assert.Equal(exportNames.Count, System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(exportNames)));
    }

    [Fact]
    public void Interface_BridgeExport_WhenOverloadCausesCounterMismatch()
    {
        // Class has two public overloads of Process:
        // Process(string) → __0_Process (first allocation)
        // Process(int)    → __1_Process (second allocation)
        // Interface: Process(int) → __0_Process
        // Bridge needed: __0_Process → __1_Process
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IProcessor {
    void Process(int x);
}
public class MyProcessor : UdonSharpBehaviour, IProcessor {
    public void Process(string s) { }
    public void Process(int x) { }
}", "MyProcessor");
        // Class layout
        Assert.Contains(".export __0_Process", uasm);  // Process(string) - first overload
        Assert.Contains(".export __1_Process", uasm);  // Process(int) - second overload
        // Interface bridge: __0_Process (interface name) maps to __1_Process (class body)
        // Both __0_Process and __1_Process should be exported
    }

    [Fact]
    public void Interface_BridgeExport_SharedParams()
    {
        // With per-name counters, interface and class params for the same param name
        // get the same variable name (__0_input__param). No separate copy needed.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IWorker {
    void Work(int input);
}
public class MyWorker : UdonSharpBehaviour, IWorker {
    public void Work(string s) { }
    public void Work(int input) { }
}", "MyWorker");
        // Both interface and class use __0_input__param (same counter key)
        Assert.Contains("__0_input__param:", uasm);
        // Bridge export should exist (export names differ)
        Assert.Contains(".export __0_Work", uasm);  // interface or first overload
        Assert.Contains(".export __1_Work", uasm);  // second overload
    }

    [Fact]
    public void Interface_BridgeExport_WithReturnValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface ICalc {
    int Compute(int x);
}
public class MyCalc : UdonSharpBehaviour, ICalc {
    public int Compute(string s) { return 0; }
    public int Compute(int x) { return x + 1; }
}", "MyCalc");
        // Class: Compute(string) → __0_Compute, Compute(int) → __1_Compute
        // Interface: Compute(int) → __0_Compute
        // Bridge: __0_Compute → __1_Compute body
        Assert.Contains(".export __0_Compute", uasm);
        Assert.Contains(".export __1_Compute", uasm);
    }

    [Fact]
    public void Interface_CallerUsesInterfaceLayout()
    {
        // Verify that calling through an interface uses the interface's export name
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class Caller : UdonSharpBehaviour {
    UnityEngine.Component _target;
    void Start() {
        ((IToggleable)_target).Toggle();
    }
}", "Caller");
        // The caller should emit SendCustomEvent with interface export name
        Assert.Contains("SendCustomEvent", uasm);
    }

    [Fact]
    public void Interface_CallerWithReturnValue_UsesInterfaceLayout()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class ScoreCaller : UdonSharpBehaviour {
    UnityEngine.Component _target;
    int _result;
    void Start() {
        _result = ((IScored)_target).GetScore();
    }
}", "ScoreCaller");
        Assert.Contains("SendCustomEvent", uasm);
        Assert.Contains("GetProgramVariable", uasm);
    }

    // ── Round-7 follow-up [Q1]: default interface members (DIM) ──
    // A DIM with no class-level implementation is emitted inside the implementing program and reached
    // through the same canonical interface bridge as an explicit class implementation.

    [Fact]
    public void Interface_DefaultMethod_NoClassImpl_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IDimM { int F() { return 3; } }
public class DimNoImpl : UdonSharpBehaviour, IDimM {
    public int sum;
    void Start() { IDimM i = this; sum = i.F(); }
}", "DimNoImpl");
        Assert.Contains(".export __iface_IDimM_F", uasm);
    }

    [Fact]
    public void Interface_DefaultProperty_NoClassImpl_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IDimP { int Q => 4; }
public class DimPropNoImpl : UdonSharpBehaviour, IDimP {
    public int sum;
    void Start() { IDimP i = this; sum = i.Q; }
}", "DimPropNoImpl");
        Assert.Contains(".export __iface_IDimP_get_Q", uasm);
    }

    [Fact]
    public void UserClass_DefaultInterfaceMethod_RejectsReceiverAbi()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IClassDim { int F() { return 5; } }
public class ClassDimValue : IClassDim { }
public class ClassDimHost : UdonSharpBehaviour {
    public int sum;
    void Start() { IClassDim value = new ClassDimValue(); sum = value.F(); }
}", "ClassDimHost"));
        Assert.Contains("object[] receiver", ex.Message);
    }

    [Fact]
    public void Interface_DefaultMethod_WithClassImpl_ExportsBridge()
    {
        // A class-level implementation overrides the DIM: legal, bridge exported as usual.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IDimOk { int F() { return 3; } }
public class DimWithImpl : UdonSharpBehaviour, IDimOk {
    public int sum;
    public int F() { return 7; }
    void Start() { IDimOk i = this; sum = i.F(); }
}", "DimWithImpl");
        Assert.Contains(".export __iface_IDimOk_F", uasm);
    }

    // ── Round-8 [R3]: inherited EXPLICIT interface implementations ──

    [Fact]
    public void Interface_InheritedExplicitImpl_ExportsBridge()
    {
        // A BASE class's explicit implementation must be inherited into the derived layout so the
        // derived program emits and exports the __iface_* bridge — pre-fix the call site dispatched
        // a never-exported name (silent no-op + stale return; VM-proven, DiffFuzz ref=5 post-fix).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IExpInh { int F(); }
public class ExpInhBase : UdonSharpBehaviour, IExpInh { int IExpInh.F() { return 5; } }
public class ExpInhDerived : ExpInhBase {
    public int sum;
    void Start() { IExpInh i = this; sum = i.F(); }
}", "ExpInhDerived");
        Assert.Contains(".export __iface_IExpInh_F", uasm);
    }

    [Fact]
    public void Interface_OwnClassExplicitImpl_ExportsBridge()
    {
        // Control: the own-class explicit implementation keeps working (harness value pin = 5).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IExpOwn { int F(); }
public class ExpOwnImpl : UdonSharpBehaviour, IExpOwn {
    public int sum;
    int IExpOwn.F() { return 5; }
    void Start() { IExpOwn i = this; sum = i.F(); }
}", "ExpOwnImpl");
        Assert.Contains(".export __iface_IExpOwn_F", uasm);
    }
}
