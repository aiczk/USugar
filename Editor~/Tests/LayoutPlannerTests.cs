using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace USugar.Tests;

public class LayoutPlannerTests
{
    static readonly string StubSource = @"
namespace UdonSharp
{
    public class UdonSyncedAttribute : System.Attribute { }
    public class UdonSharpBehaviour : UnityEngine.MonoBehaviour { }
}
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
}
";

    static (Compilation compilation, INamedTypeSymbol symbol) CompileAndGetSymbol(
        string source, string className)
    {
        var tree = CSharpSyntaxTree.ParseText(StubSource + source);
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var symbol = comp.GetTypeByMetadataName(className)
            ?? throw new System.Exception($"Type {className} not found");
        return (comp, symbol);
    }

    [Fact]
    public void Plan_PublicParameterlessMethod_RawExportName()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class Simple : UdonSharp.UdonSharpBehaviour {
    public void DoThing() { }
}", "Simple");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("DoThing")[0] as IMethodSymbol;
        Assert.True(layout.Methods.TryGetValue(method, out var ml));
        Assert.Equal("DoThing", ml.ExportName);
        Assert.Null(ml.ReturnId);
        Assert.Empty(ml.ParamIds);
    }

    [Fact]
    public void Plan_PublicMethod_WithParams_IndexedExportName()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithParams : UdonSharp.UdonSharpBehaviour {
    public void Send(int type, int arg) { }
}", "WithParams");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("Send")[0] as IMethodSymbol;
        Assert.True(layout.Methods.TryGetValue(method, out var ml));
        Assert.Equal("__0_Send", ml.ExportName);
        Assert.Equal(2, ml.ParamIds.Count);
        Assert.Equal("__0_type__param", ml.ParamIds[0]);
        Assert.Equal("__0_arg__param", ml.ParamIds[1]);
    }

    [Fact]
    public void Plan_PublicMethod_WithReturn_HasReturnId()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithReturn : UdonSharp.UdonSharpBehaviour {
    public int GetValue() { return 0; }
}", "WithReturn");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("GetValue")[0] as IMethodSymbol;
        Assert.True(layout.Methods.TryGetValue(method, out var ml));
        Assert.Equal("GetValue", ml.ExportName);
        Assert.Equal("__0_GetValue__ret", ml.ReturnId);
    }

    [Fact]
    public void Plan_UdonEvent_UsesEventName()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithStart : UdonSharp.UdonSharpBehaviour {
    void Start() { }
}", "WithStart");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("Start")[0] as IMethodSymbol;
        Assert.True(layout.Methods.TryGetValue(method, out var ml));
        Assert.Equal("_start", ml.ExportName);
    }

    [Fact]
    public void Plan_PrivateMethod_HasProperExportName()
    {
        // All methods get export names (matching pure compiler).
        // Parameterless methods get raw name, methods with params get __N_name.
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithPrivate : UdonSharp.UdonSharpBehaviour {
    void DoSecret() { }
}", "WithPrivate");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("DoSecret")[0] as IMethodSymbol;
        Assert.True(layout.Methods.ContainsKey(method));
        Assert.Equal("DoSecret", layout.Methods[method].ExportName);
    }

    [Fact]
    public void Plan_IsCached()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class Cached : UdonSharp.UdonSharpBehaviour {
    public void Foo() { }
}", "Cached");
        var planner = new LayoutPlanner(comp);
        var layout1 = planner.Plan(sym);
        var layout2 = planner.Plan(sym);
        Assert.Same(layout1, layout2);
    }

    [Fact]
    public void Plan_MethodCounters_PerNameIndependent()
    {
        // Per-name counters: each distinct name gets counter starting at 0.
        // Different names don't interfere with each other.
        var (comp, sym) = CompileAndGetSymbol(@"
public class Multi : UdonSharp.UdonSharpBehaviour {
    public void First(int x) { }
    public void Second(int x) { }
    public void Third(int x) { }
}", "Multi");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);

        var first = sym.GetMembers("First")[0] as IMethodSymbol;
        var second = sym.GetMembers("Second")[0] as IMethodSymbol;
        var third = sym.GetMembers("Third")[0] as IMethodSymbol;

        // Each name has its own independent counter, all starting at 0
        Assert.Equal("__0_First", layout.Methods[first].ExportName);
        Assert.Equal("__0_Second", layout.Methods[second].ExportName);
        Assert.Equal("__0_Third", layout.Methods[third].ExportName);
    }

    [Fact]
    public void Plan_Fields_DetectsExportAndSync()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithFields : UdonSharp.UdonSharpBehaviour {
    public int score;
    [UdonSharp.UdonSynced] int syncedVal;
}", "WithFields");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);

        var scoreField = sym.GetMembers("score")[0] as IFieldSymbol;
        var syncedField = sym.GetMembers("syncedVal")[0] as IFieldSymbol;

        Assert.True(layout.Fields.ContainsKey(scoreField));
        Assert.True(layout.Fields[scoreField].Flags.HasFlag(VarFlags.Export));

        Assert.True(layout.Fields.ContainsKey(syncedField));
        Assert.True(layout.Fields[syncedField].Flags.HasFlag(VarFlags.Sync));
    }

    [Fact]
    public void Plan_DerivedType_InheritsParentCounters()
    {
        // Child class should inherit parent's NameAllocator counters so
        // counter values don't collide with parent's already-used names.
        var (comp, sym) = CompileAndGetSymbol(@"
public class Base1 : UdonSharp.UdonSharpBehaviour {
    public void Send(int x) { }
}
public class Derived1 : Base1 {
    public void Recv(int x) { }
}", "Derived1");
        var planner = new LayoutPlanner(comp);
        var baseSym = comp.GetTypeByMetadataName("Base1");

        var baseLayout = planner.Plan(baseSym);
        var baseMethod = baseSym.GetMembers("Send")[0] as IMethodSymbol;
        // Base: x__param counter starts at 0
        Assert.Equal("__0_x__param", baseLayout.Methods[baseMethod].ParamIds[0]);

        var derivedLayout = planner.Plan(sym);
        var derivedMethod = sym.GetMembers("Recv")[0] as IMethodSymbol;
        // Derived: x__param counter inherits from parent → starts at 1
        Assert.Equal("__1_x__param", derivedLayout.Methods[derivedMethod].ParamIds[0]);
    }

    [Fact]
    public void Plan_PrivateNonVoidMethod_HasReturnId()
    {
        // All non-void methods get ReturnId, regardless of accessibility.
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithPrivateReturn : UdonSharp.UdonSharpBehaviour {
    int GetSecret() { return 42; }
}", "WithPrivateReturn");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("GetSecret")[0] as IMethodSymbol;
        Assert.NotNull(layout.Methods[method].ReturnId);
        Assert.Equal("__0_GetSecret__ret", layout.Methods[method].ReturnId);
    }

    [Fact]
    public void Plan_UdonEvent_FixedParamNames()
    {
        // Udon event parameters should use fixed names from UdonEventParamNames,
        // not go through NameAllocator.
        var (comp, sym) = CompileAndGetSymbol(@"
namespace VRC.SDKBase { public class VRCPlayerApi { } }
public class WithEvent : UdonSharp.UdonSharpBehaviour {
    public override void OnPlayerJoined(VRC.SDKBase.VRCPlayerApi player) { }
}", "WithEvent");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        var method = sym.GetMembers("OnPlayerJoined")[0] as IMethodSymbol;
        Assert.True(layout.Methods.ContainsKey(method));
        Assert.Equal("_onPlayerJoined", layout.Methods[method].ExportName);
        Assert.Single(layout.Methods[method].ParamIds);
        Assert.Equal("onPlayerJoinedPlayer", layout.Methods[method].ParamIds[0]);
    }

    [Fact]
    public void Plan_UdonEvent_ParamsDoNotConsumeCounter()
    {
        // Event parameter names are fixed and should NOT consume the NameAllocator counter.
        // A subsequent method with the same param name should get counter 0, not 1.
        var (comp, sym) = CompileAndGetSymbol(@"
namespace VRC.SDKBase { public class VRCPlayerApi { } }
public class EventAndMethod : UdonSharp.UdonSharpBehaviour {
    public override void OnPlayerJoined(VRC.SDKBase.VRCPlayerApi player) { }
    public void DoStuff(int player) { }
}", "EventAndMethod");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);

        var doStuff = sym.GetMembers("DoStuff")[0] as IMethodSymbol;
        // player__param counter should start at 0 because event params don't consume it
        Assert.Equal("__0_player__param", layout.Methods[doStuff].ParamIds[0]);
    }

    [Fact]
    public void Plan_TypeLayout_HasSymbolCounters()
    {
        var (comp, sym) = CompileAndGetSymbol(@"
public class WithCounters : UdonSharp.UdonSharpBehaviour {
    public void Send(int x) { }
}", "WithCounters");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);
        Assert.NotNull(layout.SymbolCounters);
        Assert.True(layout.SymbolCounters.ContainsKey("x__param"));
    }

    // --- Parity tests: LayoutPlanner must produce the same names as existing UasmEmitter ---

    [Theory]
    [InlineData("public class PT1 : UdonSharp.UdonSharpBehaviour { public void Foo() {} public int Bar(int x) { return x; } }",
        "PT1")]
    [InlineData(@"public class PT2 : UdonSharp.UdonSharpBehaviour {
        public int seatIndex;
        public void SendAction(int type, int arg) { }
        public bool ConsumeAction() { return false; }
        public void ResetSeq() { }
        public int GetActionType() { return 0; }
    }", "PT2")]
    public void Plan_MatchesExistingUasmOutput(string source, string className)
    {
        // Get UASM from existing system (uses full TestHelper stubs)
        var fullSource = "using UdonSharp;\n" + source;
        var uasm = TestHelper.CompileToUasm(fullSource, className);

        // Get layout from LayoutPlanner (uses lightweight stubs)
        var tree = CSharpSyntaxTree.ParseText(StubSource + source);
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var sym = comp.GetTypeByMetadataName(className);
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);

        // Verify each method's export name appears in the UASM
        foreach (var (method, ml) in layout.Methods)
        {
            // Only public methods and Udon events are exported
            if (method.DeclaredAccessibility == Accessibility.Public
                || uasm.Contains($".export {ml.ExportName}"))
            {
                Assert.Contains($".export {ml.ExportName}", uasm);
            }

            // Verify param IDs exist as variable declarations
            foreach (var pid in ml.ParamIds)
                Assert.Contains($"{pid}:", uasm);

            // Verify return ID exists
            if (ml.ReturnId != null)
                Assert.Contains($"{ml.ReturnId}:", uasm);
        }
    }

    [Fact]
    public void Plan_CrossClassCall_ProducesSameNamesAsEmitter()
    {
        // This test compiles two classes and verifies that
        // LayoutPlanner produces the same cross-class names that
        // the existing emitter uses for SetProgramVariable/SendCustomEvent
        var source = @"
using UdonSharp;
public class Target : UdonSharpBehaviour {
    public void SendAction(int type, int arg) { }
    public int GetScore() { return 0; }
}";
        var uasm = TestHelper.CompileToUasm(source, "Target");

        // Extract export names from UASM
        var exports = Regex.Matches(uasm, @"\.export (\S+)")
            .Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();

        // Get LayoutPlanner layout
        var tree = CSharpSyntaxTree.ParseText(StubSource + @"
public class Target : UdonSharp.UdonSharpBehaviour {
    public void SendAction(int type, int arg) { }
    public int GetScore() { return 0; }
}");
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sym = comp.GetTypeByMetadataName("Target");
        var planner = new LayoutPlanner(comp);
        var layout = planner.Plan(sym);

        // SendAction has params → indexed name
        var sendAction = sym.GetMembers("SendAction")[0] as IMethodSymbol;
        Assert.Contains(layout.Methods[sendAction].ExportName, exports);

        // GetScore is public parameterless → raw name
        var getScore = sym.GetMembers("GetScore")[0] as IMethodSymbol;
        Assert.Contains(layout.Methods[getScore].ExportName, exports);
    }

    [Fact]
    public void Freeze_BlocksNewPlanning()
    {
        var source = @"
public class A : UdonSharp.UdonSharpBehaviour { public void Foo() {} }
public class B : UdonSharp.UdonSharpBehaviour { public void Bar() {} }
";
        var tree = CSharpSyntaxTree.ParseText(StubSource + source);
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location),
                    "System.Runtime.dll")),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var planner = new LayoutPlanner(comp);
        var typeA = comp.GetTypeByMetadataName("A");
        var typeB = comp.GetTypeByMetadataName("B");
        planner.Plan(typeA);
        planner.Freeze();
        // A is cached, should work
        var layoutA = planner.Plan(typeA);
        Assert.NotNull(layoutA);
        // B was not planned, should throw
        Assert.Throws<System.InvalidOperationException>(() => planner.Plan(typeB));
    }

    [Fact]
    public void Freeze_InterfacesPrePlanned_AllowsComputeBridges()
    {
        var source = @"
public interface IDoable { void Do(); }
public class Impl : UdonSharp.UdonSharpBehaviour, IDoable { public void Do() {} }
";
        var tree = CSharpSyntaxTree.ParseText(StubSource + source);
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location),
                    "System.Runtime.dll")),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var planner = new LayoutPlanner(comp);
        var implType = comp.GetTypeByMetadataName("Impl");
        // Pre-plan class AND its interfaces (matching USugarCompiler Phase 1)
        planner.Plan(implType);
        foreach (var iface in implType.AllInterfaces)
            planner.Plan(iface);
        planner.Freeze();
        // ComputeBridges should work without throwing
        var bridges = planner.ComputeBridges(implType);
        Assert.NotNull(bridges);
    }
}
