using System;
using System.IO;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class UdonSharpCompatTests
{
    static string FindProjectRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "Packages"))
                && Directory.Exists(Path.Combine(dir, "Assets")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Exception("Unity project root not found");
    }

    static readonly string TestScriptsDir = Path.Combine(
        FindProjectRoot(),
        "Packages", "com.vrchat.worlds", "Integrations", "UdonSharp", "Tests~", "TestScripts");

    static string ReadTestFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(new[] { TestScriptsDir }.Concat(pathParts).ToArray()));

    // --- Core ---

    [Fact]
    public void Compat_ArithmeticTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "ArithmeticTest.cs"), "ArithmeticTest");

    [Fact]
    public void Compat_ArrayTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "ArrayTest.cs"), "ArrayTest");

    [Fact]
    public void Compat_MethodCallsTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "MethodCallsTest.cs"), "MethodCallsTest");

    [Fact]
    public void Utilities_IsValid_ResolvesToSystemObjectExtern()
    {
        // Utilities.IsValid takes a UnityEngine.Object in C#, but the only Udon node is __IsValid__SystemObject__.
        // USugar must gap-fill the reference-type param to SystemObject, not emit the nonexistent
        // __IsValid__UnityEngineObject__ extern (which fails to assemble in VRChat).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsValidExternTest : UdonSharpBehaviour {
    public Transform target;
    public bool ok;
    void Start() { ok = VRC.SDKBase.Utilities.IsValid(target); }
}", "IsValidExternTest");
        Assert.Contains("VRCSDKBaseUtilities.__IsValid__SystemObject__SystemBoolean", uasm);
        Assert.DoesNotContain("__IsValid__UnityEngineObject__", uasm);
    }

    [Fact]
    public void Compat_PropertyTest()
        => TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("Core", "PropertyTest.cs"),
                ReadTestFile("Core", "PropertyTestReferenceScript.cs"),
            },
            "PropertyTest");

    [Fact]
    public void Compat_GenericsTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "GenericsTest.cs"), "GenericsTest");

    [Fact]
    public void Compat_LocalFunctionTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "LocalFunctionTest.cs"), "LocalFunctionTest");

    [Fact]
    public void Compat_OrderOfOperations()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "OrderOfOperations.cs"), "OrderOfOperations");

    [Fact]
    public void Compat_ImplicitConversions()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "ImplicitConversions.cs"), "ImplicitConversions");

    [Fact]
    public void Compat_NameOf()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "NameOf.cs"), "NameOf");

    // --- FlowControl ---

    [Fact]
    public void Compat_ForLoopTest()
    {
        // ForLoopTest.cs contains foreach(Transform) which is intentionally unsupported
        var ex = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(ReadTestFile("FlowControl", "ForLoopTest.cs"), "ForLoopTest"));
        Assert.Contains("foreach over", ex.Message);
    }

    [Fact]
    public void Compat_SwitchTest()
        => TestHelper.CompileToUasm(ReadTestFile("FlowControl", "SwitchTest.cs"), "SwitchTest");

    [Fact]
    public void Compat_RecursionTest()
    {
        // RecursionTest.cs contains foreach(Transform) which is intentionally unsupported
        var ex = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(ReadTestFile("FlowControl", "RecursionTest.cs"), "RecursionTest"));
        Assert.Contains("foreach over", ex.Message);
    }

    // --- Core (Inheritance) ---

    [Fact]
    public void Compat_InheritanceTest()
        => TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("Core", "Inheritance", "TestInheritanceClassBase.cs"),
                ReadTestFile("Core", "Inheritance", "ClassA.cs"),
                ReadTestFile("Core", "Inheritance", "ClassB.cs"),
                ReadTestFile("Core", "Inheritance", "ClassC.cs"),
                ReadTestFile("Core", "Inheritance", "InheritanceRootTest.cs"),
            },
            "InheritanceRootTest");

    // --- Canny ---

    [Fact]
    public void Compat_StructMutatorTest()
        => TestHelper.CompileToUasm(ReadTestFile("Canny", "StructMutatorTest.cs"), "StructMutatorTest");

    [Fact]
    public void Compat_DefaultHeapValueTest()
    {
        // The upstream test observes GetType() on a jagged-array carrier.
        // USugar intentionally hides that object[] implementation detail.
        var ex = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(
                ReadTestFile("Canny", "DefaultHeapValueTest.cs"),
                "DefaultHeapValueTest"));
        Assert.Contains("extern-call ABI", ex.Message);
    }

    [Fact]
    public void Compat_JaggedArrayCOWTest()
        => TestHelper.CompileToUasm(ReadTestFile("RegressionTests", "JaggedArrayCOWTest.cs"), "JaggedArrayCOWTest");

    // --- Bugs ---

    [Fact]
    public void Compat_BracesInStringInterpolation()
        => TestHelper.CompileToUasm(ReadTestFile("BugTests", "BracesInStringInterpolation", "BracesInStringInterpolation.cs"), "BracesInStringInterpolation");

    // --- Regression ---

    [Fact]
    public void Compat_UserFieldTypeConversionTest()
        => TestHelper.CompileToUasm(ReadTestFile("RegressionTests", "UserFieldTypeConversionTest.cs"), "UserFieldTypeConversionTest");

    [Fact]
    public void Compat_LampOrderOfOpsTests()
        => TestHelper.CompileToUasm(ReadTestFile("RegressionTests", "LampOrderOfOpsTests.cs"), "LampOrderOfOpsTests");

    // --- Bugs (Layer 2) ---

    [Fact]
    public void Compat_TestTernary()
        => TestHelper.CompileToUasm(ReadTestFile("BugTests", "TernaryCOWBugs", "TestTernary.cs"), "TestTernary");

    [Fact]
    public void Compat_MethodArgCorruption()
    {
        var main = ReadTestFile(
            "BugTests", "MethodArgumentCorruption",
            "MethodArgCorruptionMain.cs");
        const string runtimeTypeAssertion =
            "obj2.GetType() == typeof(int) && (int)obj2 == 123";
        Assert.Contains(runtimeTypeAssertion, main);
        main = main.Replace(
            runtimeTypeAssertion,
            "(int)obj2 == 123");
        TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("BugTests", "MethodArgumentCorruption", "ArgCorruption3.cs"),
                ReadTestFile("BugTests", "MethodArgumentCorruption", "ArgCorruption2.cs"),
                main,
            },
            "MethodArgCorruptionMain");
    }

    // --- Canny (Layer 2) ---

    [Fact]
    public void Compat_MultiOutParamsTest()
        => TestHelper.CompileToUasm(ReadTestFile("Canny", "MultiOutParamsTest.cs"), "MultiOutParamsTest");

    // --- Canny (simple, Layer 3) ---

    [Fact]
    public void Compat_InstantiateTest()
        => TestHelper.CompileToUasm(ReadTestFile("Canny", "InstantiateTest.cs"), "InstantiateTest");

    [Fact]
    public void Compat_ObjectDestroyNullCheck()
        => TestHelper.CompileToUasm(ReadTestFile("Canny", "ObjectDestroyNullCheck.cs"), "ObjectDestroyNullCheck");

    [Fact]
    public void Compat_SByteSerialization()
        => TestHelper.CompileToUasm(ReadTestFile("Canny", "SByteSerialization.cs"), "SByteSerialization");

    // --- Core (SDK-heavy, Layer 3) ---

    [Fact]
    public void Compat_GetComponentTest()
        => TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("Core", "Inheritance", "TestInheritanceClassBase.cs"),
                ReadTestFile("Core", "Inheritance", "ClassA.cs"),
                ReadTestFile("Core", "Inheritance", "ClassB.cs"),
                ReadTestFile("Core", "Inheritance", "ClassC.cs"),
                ReadTestFile("Core", "NameOf.cs"),
                ReadTestFile("Core", "GetComponentTest.cs"),
            },
            "GetComponentTest");

    [Fact]
    public void Compat_SerializationTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "SerializationTest.cs"), "SerializationTest");

    // --- Canny (runtime-dependent, Layer 3) ---

    [Fact]
    public void Compat_DisabledObjectHeapTest()
        => TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("Canny", "InstantiatedObjectTesterScript.cs"),
                ReadTestFile("Canny", "DisabledObjectHeapTest.cs"),
            },
            "DisabledObjectHeapTest");

    [Fact]
    public void Compat_InstantiatedObjectHeapTest()
        => TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("Canny", "InstantiatedObjectTesterScript.cs"),
                ReadTestFile("Canny", "InstantiatedObjectHeapTest.cs"),
            },
            "InstantiatedObjectHeapTest");

    // --- Regression (complex, Layer 3) ---

    [Fact]
    public void Compat_DebugCarSystemWorking()
        => TestHelper.CompileToUasm(ReadTestFile("RegressionTests", "DebugCarSystemWorking.cs"), "DebugCarSystemWorking");
}
