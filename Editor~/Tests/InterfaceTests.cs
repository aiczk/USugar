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
        Assert.Contains(".export __0_DoIt", uasm);
        Assert.Contains(".export __0_Extra", uasm);
        // Bridge generated: interface __0_x__param → class __1_x__param
        var lines = uasm.Split('\n');
        var doItExports = System.Linq.Enumerable.Count(lines, l => l.Trim() == ".export __0_DoIt");
        Assert.Equal(2, doItExports); // method export + bridge export
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
}
