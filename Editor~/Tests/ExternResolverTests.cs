using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class ExternResolverTests
{
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
    public void AbiKey_IntAddition()
    {
        var sig = UdonAbiKey.Method(
            "System.Int32", "op_Addition",
            new[] { "System.Int32", "System.Int32" }, "System.Int32");
        Assert.Equal("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            sig.ToString());
    }

    [Fact]
    public void AbiKey_SetActive()
    {
        var sig = UdonAbiKey.Method(
            "UnityEngine.GameObject", "SetActive",
            new[] { "System.Boolean" }, "System.Void");
        Assert.Equal("UnityEngineGameObject.__SetActive__SystemBoolean__SystemVoid",
            sig.ToString());
    }

    // The signature builders route the receiver through the single RemapUdonType table (the former
    // private 1-arm RemapExternType twin carried only this arm — pin that the fold kept its semantics).
    [Fact]
    public void AbiKey_UdonBehaviourReceiver_RemapsToEventReceiver()
    {
        var sig = UdonAbiKey.Method(
            "VRC.Udon.UdonBehaviour", "SendCustomEvent",
            new[] { "System.String" }, "System.Void");
        Assert.Equal(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            sig.ToString());
    }

    // Hand-enumeration audit Tier-2: BinaryOperatorNames' miss used to silently mint a bogus
    // "{type}.__{kind}__…" extern from ToString(); the default is now a loud throw naming the kind.
    // Every kind outside the table is unreachable from the handler layer (ConditionalAnd/Or are
    // lowered to short-circuit control flow before extern resolution; the rest are VB-only kinds).
    [Theory]
    [InlineData(BinaryOperatorKind.Power)]
    [InlineData(BinaryOperatorKind.IntegerDivide)]
    [InlineData(BinaryOperatorKind.ObjectValueEquals)]
    [InlineData(BinaryOperatorKind.ObjectValueNotEquals)]
    [InlineData(BinaryOperatorKind.ConditionalAnd)]
    [InlineData(BinaryOperatorKind.ConditionalOr)]
    public void ResolveBuiltInBinaryExtern_UnmappedOperatorKind_ThrowsNamingTheKind(BinaryOperatorKind kind)
    {
        var comp = CSharpCompilation.Create("OpKindCensus", references: TestHelper.StandardRefs);
        var int32 = comp.GetSpecialType(SpecialType.System_Int32);
        var types = new CompilationSession(
            comp, TestHelper.RegistryFacts).Types;
        var ex = Assert.Throws<System.NotSupportedException>(
            () => ExternResolver.ResolveBuiltInBinaryExtern(
                kind, int32, int32, int32,
                type => types.GetUdonTypeName(type)));
        Assert.Contains(kind.ToString(), ex.Message);
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
    public void DisplayStringFallback_ResolvesNonSpecialTypeName()
    {
        // Vector3 has no dedicated GetUdonTypeName branch (not an array/delegate/behaviour/interface/
        // nullable/aggregate/enum/generic special case) — it resolves through the terminal
        // display-string sanitization branch. Assert the real resolution behavior directly instead
        // of observing it via the (now-removed) OnTypeFallback instrumentation hook.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class FallbackTest : UdonSharpBehaviour {
    UnityEngine.Vector3 _pos;
    void Start() { UnityEngine.Vector3 v = _pos; }
}
");
        Assert.Contains("UnityEngineVector3", uasm);
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
        Assert.Equal("SystemCollectionsGenericListSystemInt32",
            UdonTypeIdentity.FromStorage(field.Type).Name);
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
            UdonTypeIdentity.FromStorage(field.Type).Name);
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
        var result = new CompilationSession(
            compilation, TestHelper.RegistryFacts)
            .Types.GetUdonTypeName(tp, map);
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
            UdonTypeIdentity.FromStorage(field.Type).Name);
    }
}
