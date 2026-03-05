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

    [Fact(Skip = "Udon VM missing: long % op_Remainder")]
    public void Compat_ArithmeticTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "ArithmeticTest.cs"), "ArithmeticTest");

    [Fact(Skip = "TODO: validator push count mismatch")]
    public void Compat_ArrayTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "ArrayTest.cs"), "ArrayTest");

    [Fact(Skip = "Udon VM missing: string indexer get_Chars/get_Item")]
    public void Compat_MethodCallsTest()
        => TestHelper.CompileToUasm(ReadTestFile("Core", "MethodCallsTest.cs"), "MethodCallsTest");

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
        => TestHelper.CompileToUasm(ReadTestFile("Canny", "DefaultHeapValueTest.cs"), "DefaultHeapValueTest");

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

    [Fact(Skip = "VM: MemberInfo.Name extern missing in Udon VM")]
    public void Compat_AccessViaAlternateInvocee()
        => TestHelper.CompileToUasm(ReadTestFile("BugTests", "AccessViaAlternateInvocee", "AccessViaAlternateInvocee.cs"), "AccessViaAlternateInvocee");

    [Fact]
    public void Compat_TestTernary()
        => TestHelper.CompileToUasm(ReadTestFile("BugTests", "TernaryCOWBugs", "TestTernary.cs"), "TestTernary");

    [Fact]
    public void Compat_MethodArgCorruption()
        => TestHelper.CompileToUasm(
            new[] {
                ReadTestFile("BugTests", "MethodArgumentCorruption", "ArgCorruption3.cs"),
                ReadTestFile("BugTests", "MethodArgumentCorruption", "ArgCorruption2.cs"),
                ReadTestFile("BugTests", "MethodArgumentCorruption", "MethodArgCorruptionMain.cs"),
            },
            "MethodArgCorruptionMain");

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
