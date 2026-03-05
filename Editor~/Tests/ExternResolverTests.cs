using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class ExternResolverTests : System.IDisposable
{
    public void Dispose() => ExternResolver.OnTypeFallback = null;
    [Theory]
    [InlineData("System.Int32", "SystemInt32")]
    [InlineData("System.String", "SystemString")]
    [InlineData("System.Boolean", "SystemBoolean")]
    [InlineData("System.Single", "SystemSingle")]
    [InlineData("System.Object", "SystemObject")]
    [InlineData("System.Void", "SystemVoid")]
    [InlineData("UnityEngine.Vector3", "UnityEngineVector3")]
    [InlineData("UnityEngine.Transform", "UnityEngineTransform")]
    [InlineData("UnityEngine.GameObject", "UnityEngineGameObject")]
    public void SanitizeTypeName_Basic(string input, string expected)
    {
        Assert.Equal(expected, ExternResolver.SanitizeTypeName(input));
    }

    [Theory]
    [InlineData("System.Int32[]", "SystemInt32Array")]
    [InlineData("UnityEngine.GameObject[]", "UnityEngineGameObjectArray")]
    public void SanitizeTypeName_Array(string input, string expected)
    {
        Assert.Equal(expected, ExternResolver.SanitizeTypeName(input));
    }

    [Fact]
    public void BuildMethodSignature_IntAddition()
    {
        var sig = ExternResolver.BuildMethodSignature(
            "System.Int32", "__op_Addition", new[] { "System.Int32", "System.Int32" }, "System.Int32");
        Assert.Equal("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32", sig);
    }

    [Fact]
    public void BuildMethodSignature_SetActive()
    {
        var sig = ExternResolver.BuildMethodSignature(
            "UnityEngine.GameObject", "__SetActive", new[] { "System.Boolean" }, "System.Void");
        Assert.Equal("UnityEngineGameObject.__SetActive__SystemBoolean__SystemVoid", sig);
    }

    [Fact]
    public void BuildPropertySignature_Getter()
    {
        var sig = ExternResolver.BuildPropertyGetSignature(
            "UnityEngine.Transform", "position", "UnityEngine.Vector3");
        Assert.Equal("UnityEngineTransform.__get_position__UnityEngineVector3", sig);
    }

    // ── Task 22: Type remapping ──

    [Fact]
    public void TypeRemap_UdonSharpBehaviour()
    {
        var remapped = ExternResolver.RemapUdonType("UdonSharpUdonSharpBehaviour");
        Assert.Equal("VRCUdonCommonInterfacesIUdonEventReceiver", remapped);
    }

    [Fact]
    public void TypeRemap_UnknownType_PassesThrough()
    {
        var result = ExternResolver.RemapUdonType("SystemInt32");
        Assert.Equal("SystemInt32", result);
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<string, int>", "SystemCollectionsGenericDictionarystringint")]
    [InlineData("System.Nullable<int>", "SystemNullableint")]
    public void SanitizeTypeName_GenericBrackets(string input, string expected)
    {
        Assert.Equal(expected, ExternResolver.SanitizeTypeName(input));
    }

    [Fact]
    public void OnTypeFallback_FiresForDisplayStringTypes()
    {
        var captured = new System.Collections.Concurrent.ConcurrentBag<(ITypeSymbol type, string sanitized)>();
        ExternResolver.OnTypeFallback = (t, s) => captured.Add((t, s));

        // Vector3 field triggers the display-string fallback path
        TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class FallbackTest : UdonSharpBehaviour {
    UnityEngine.Vector3 _pos;
    void Start() { UnityEngine.Vector3 v = _pos; }
}
");
        Assert.Contains(captured.ToArray(), c => c.sanitized == "UnityEngineVector3");
    }

    [Fact]
    public void BuildOperatorName_Addition()
    {
        Assert.Equal("__op_Addition", ExternResolver.GetOperatorExternName("op_Addition"));
    }

    [Fact]
    public void BuildOperatorName_Equality()
    {
        Assert.Equal("__op_Equality", ExternResolver.GetOperatorExternName("op_Equality"));
    }

    // ── Generic type name resolution ──

    [Fact]
    public void GetUdonTypeName_GenericListInt_ReturnsCorrectName()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
using System.Collections.Generic;
public class C { public List<int> field; }");
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var type = comp.GetTypeByMetadataName("C");
        var field = type.GetMembers("field")[0] as IFieldSymbol;
        Assert.Equal("SystemCollectionsGenericListSystemInt32", ExternResolver.GetUdonTypeName(field.Type));
    }

    [Fact]
    public void GetUdonTypeName_GenericDictStringInt_ReturnsCorrectName()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
using System.Collections.Generic;
public class C { public Dictionary<string, int> field; }");
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.Dictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var type = comp.GetTypeByMetadataName("C");
        var field = type.GetMembers("field")[0] as IFieldSymbol;
        Assert.Equal("SystemCollectionsGenericDictionarySystemStringSystemInt32",
            ExternResolver.GetUdonTypeName(field.Type));
    }

    [Fact]
    public void GetUdonTypeName_WithExplicitMap_ResolvesTypeParameter()
    {
        var source = @"
namespace UdonSharp { public class UdonSharpBehaviour : UnityEngine.MonoBehaviour {} }
namespace UnityEngine { public class Object {} public class Component : Object {} public class MonoBehaviour : Component {} }
public class G<T> { public T value; }
public class C : UdonSharp.UdonSharpBehaviour { G<int> g; }
";
        var compilation = CSharpCompilation.Create("Test",
            new[] { CSharpSyntaxTree.ParseText(source) },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var classG = compilation.GetTypeByMetadataName("G`1");
        var tp = classG.TypeParameters[0]; // T
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default)
        {
            [tp] = intType
        };
        var result = ExternResolver.GetUdonTypeName(tp, map);
        Assert.Equal("SystemInt32", result);
    }

    [Fact]
    public void IsUdonSharpBehaviour_WithExplicitMap_ResolvesTypeParameter()
    {
        var source = @"
namespace UdonSharp { public class UdonSharpBehaviour : UnityEngine.MonoBehaviour {} }
namespace UnityEngine { public class Object {} public class Component : Object {} public class MonoBehaviour : Component {} }
public class MyUsb : UdonSharp.UdonSharpBehaviour {}
public class G<T> { public T value; }
";
        var compilation = CSharpCompilation.Create("Test",
            new[] { CSharpSyntaxTree.ParseText(source) },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var classG = compilation.GetTypeByMetadataName("G`1");
        var tp = classG.TypeParameters[0];
        var usbType = compilation.GetTypeByMetadataName("MyUsb");
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default)
        {
            [tp] = usbType
        };
        Assert.True(ExternResolver.IsUdonSharpBehaviour(tp, map));
        Assert.False(ExternResolver.IsUdonSharpBehaviour(tp, null));
    }

    [Fact]
    public void GetUdonTypeName_GenericListArray_ReturnsCorrectName()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
using System.Collections.Generic;
public class C { public List<int>[] field; }");
        var refs = new[] {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            MetadataReference.CreateFromFile(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                + "System.Runtime.dll"),
        };
        var comp = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var type = comp.GetTypeByMetadataName("C");
        var field = type.GetMembers("field")[0] as IFieldSymbol;
        Assert.Equal("SystemCollectionsGenericListSystemInt32Array",
            ExternResolver.GetUdonTypeName(field.Type));
    }
}
