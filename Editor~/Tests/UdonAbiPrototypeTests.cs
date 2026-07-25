using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class UdonAbiPrototypeTests
{
    [Fact]
    public void FlatRegistryFixtureIgnoresNonExternGraphNodes()
    {
        const string externName =
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32";
        var catalog = UdonAbiCatalog.FromNamesForTests(new[]
        {
            "Block",
            "Event_Start",
            "Const_SystemInt32",
            externName,
        });

        Assert.Single(catalog.ExternNames);
        Assert.Contains(externName, catalog.ExternNames);
    }

    [Fact]
    public void CoreVerifierAcceptsTypedSdkPrototype()
    {
        const string signature = "ExampleMath.__Add__SystemInt32_SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("left", "SystemInt32", UdonAbiParameterMode.In),
            Param("right", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var destination = builder.AllocFrame(StorageTypes.Int32);
        builder.EmitAssign(destination, new CExternCall(bound, new List<CLeaf>
        {
            builder.Const(1, StorageTypes.Int32),
            builder.Const(2, StorageTypes.Int32),
        }, StorageTypes.Int32));

        CoreVerify.Verify(module);
    }

    [Fact]
    public void CoreVerifierRejectsSdkPrototypeAritySkew()
    {
        const string signature = "ExampleMath.__Add__SystemInt32_SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("left", "SystemInt32", UdonAbiParameterMode.In),
            Param("right", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var destination = builder.AllocFrame(StorageTypes.Int32);
        builder.EmitAssign(destination, new CExternCall(bound, new List<CLeaf>
        {
            builder.Const(1, StorageTypes.Int32),
        }, StorageTypes.Int32));

        var error = Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
        Assert.Contains("consumes 3 SDK stack operands", error.Message);
    }

    [Fact]
    public void CoreVerifierRejectsSdkPrototypeTypeSkew()
    {
        const string signature = "ExampleMath.__Abs__SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("value", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var destination = builder.AllocFrame(StorageTypes.Int32);
        builder.EmitAssign(destination, new CExternCall(bound, new List<CLeaf>
        {
            builder.Const("bad", StorageTypes.String),
        }, StorageTypes.Int32));

        var error = Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
        Assert.Contains("expects ABI type 'SystemInt32'", error.Message);
    }

    [Fact]
    public void GenericSdkPrototypeUnifiesArrayElementAndReturn()
    {
        const string signature = "ExampleArray.__First__TArray__T";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            new UdonAbiParameter("values", UdonAbiType.Array(UdonAbiType.Generic("T")),
                UdonAbiParameterMode.In),
            new UdonAbiParameter("result", UdonAbiType.Generic("T"),
                UdonAbiParameterMode.Out),
        });
        var bound = new UdonAbiCatalog(new[] { prototype })
            .Require(TestHelper.AbiKey(signature));
        var facts = new UdonTypeFactRegistry();
        var good = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, new StorageType("SystemInt32Array")),
        }, StorageTypes.Int32);
        UdonAbiVerifier.VerifyInvocation(good, facts, "good");

        var bad = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, new StorageType("SystemInt32Array")),
        }, StorageTypes.String);
        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(bad, facts, "bad"));
        Assert.Contains("already bound to 'SystemInt32'", error.Message);
    }

    [Theory]
    [InlineData("UnityEngineComponent", "UnityEngineUIImage")]
    [InlineData("UnityEngineComponent", "UnityEngineTransform")]
    [InlineData("UnityEngineObject", "VRCUdonCommonInterfacesIUdonEventReceiver")]
    public void CompilationSessionSeedsSdkDirectedAssignabilityForExternOperands(
        string expected, string actual)
    {
        var signature = $"Example.__Accept__{expected}__SystemVoid";
        var sdkFacts = new UdonTypeFactRegistry();
        sdkFacts.RecordForTest(expected, isEnum: false, isValueType: false);
        sdkFacts.RecordAssignableForTest(actual, expected);
        var catalog = new UdonAbiCatalog(new[]
        {
            new UdonExternPrototype(signature, new[]
            {
                Param("value", expected, UdonAbiParameterMode.In),
            }),
        }, sdkFacts.Snapshot(), sdkFacts.AssignabilitySnapshot());
        var compilation = CSharpCompilation.Create(
            "SdkFactSeed",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var session = new CompilationSession(compilation, catalog);
        session.TypeFacts.RecordForTest(actual, isEnum: false, isValueType: false);
        var call = new CExternCall(
            catalog.Require(UdonAbiKey.Method(
                "Example", "Accept", new[] { expected }, "SystemVoid")),
            new List<CLeaf> { new CConst(null, new StorageType(actual)) },
            StorageTypes.Void);

        Assert.True(session.TypeFacts.IsReferenceFact(expected));
        UdonAbiVerifier.VerifyInvocation(call, session.TypeFacts, "reference_operand");
    }

    /// <summary>UdonHeap.SetHeapVariable re-boxes its destination and GetHeapVariable falls back to
    /// an `is T` test on the stored value, so a declared operand type is not a necessary condition in
    /// either direction. These four shapes all appear in real projects; requiring declared-type
    /// assignability rejected every one of them.</summary>
    [Fact]
    public void OperandCheckDoesNotRequireDeclaredTypeAssignability()
    {
        var facts = new UdonTypeFactRegistry();
        foreach (var name in new[]
                 {
                     "UnityEngineObject", "UnityEngineComponent", "UnityEngineTransform",
                     "UnityEngineCanvasGroup",
                 })
            facts.RecordForTest(name, isEnum: false, isValueType: false);

        AcceptsOperands(facts,
            "UnityEngineComponent.__GetComponent__T",
            new[] { ("instance", "UnityEngineComponent"), ("type", "SystemType") },
            "UnityEngineObject",
            new[] { StorageTypes.Transform, StorageTypes.Type },
            new StorageType("UnityEngineCanvasGroup"));

        AcceptsOperands(facts,
            "UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent",
            new[] { ("instance", "UnityEngineComponentArray"), ("index", "SystemInt32") },
            "UnityEngineComponent",
            new[] { StorageTypes.ComponentArray, StorageTypes.Int32 },
            StorageTypes.UdonEventReceiver);

        AcceptsOperands(facts,
            "SystemObjectArray.__Get__SystemInt32__SystemObject",
            new[] { ("instance", "SystemObjectArray"), ("index", "SystemInt32") },
            "SystemObject",
            new[] { StorageTypes.ObjectArray, StorageTypes.Int32 },
            new StorageType("SystemInt32Array"));

        AcceptsOperands(facts,
            "SystemString.__op_Inequality__SystemString_SystemString__SystemBoolean",
            new[] { ("left", "SystemString"), ("right", "SystemString") },
            "SystemBoolean",
            new[] { StorageTypes.Object, StorageTypes.String },
            StorageTypes.Boolean);
    }

    static void AcceptsOperands(UdonTypeFactRegistry facts, string signature,
        (string Name, string Type)[] inputs, string resultType,
        StorageType[] actualArgs, StorageType actualResult)
    {
        var parameters = new List<UdonAbiParameter>();
        foreach (var (name, type) in inputs)
            parameters.Add(Param(name, type, UdonAbiParameterMode.In));
        parameters.Add(Param("result", resultType, UdonAbiParameterMode.Out));
        var bound = new UdonAbiCatalog(new[]
            {
                new UdonExternPrototype(signature, parameters),
            })
            .Require(TestHelper.AbiKey(signature));

        var args = new List<CLeaf>();
        foreach (var arg in actualArgs)
            args.Add(new CConst(null, arg));
        UdonAbiVerifier.VerifyInvocation(
            new CExternCall(bound, args, actualResult), facts, signature);
    }

    [Fact]
    public void GenericPlaceholderUnificationDoesNotUseReferenceAssignability()
    {
        const string signature = "Example.__Pair__T_T__SystemVoid";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            new UdonAbiParameter(
                "first", UdonAbiType.Generic("T"), UdonAbiParameterMode.In),
            new UdonAbiParameter(
                "second", UdonAbiType.Generic("T"), UdonAbiParameterMode.In),
        });
        var call = new CExternCall(
            new UdonAbiCatalog(new[] { prototype })
                .Require(TestHelper.AbiKey(signature)),
            new List<CLeaf>
            {
                new CConst(null, new StorageType("DerivedReference")),
                new CConst(null, new StorageType("BaseReference")),
            },
            StorageTypes.Void);
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("DerivedReference", isEnum: false, isValueType: false);
        facts.RecordForTest("BaseReference", isEnum: false, isValueType: false);
        facts.RecordAssignableForTest("DerivedReference", "BaseReference");

        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(call, facts, "generic_pair"));
        Assert.Contains(
            "placeholder 'T' was already bound to 'DerivedReference', but received 'BaseReference'",
            error.Message);
    }

    [Fact]
    public void GenericComponentQueryRequiresTransformStrongbox()
    {
        const string signature = "UnityEngineComponent.__GetComponent__T";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            Param("instance", "UnityEngineComponent", UdonAbiParameterMode.In),
            Param("type", "SystemType", UdonAbiParameterMode.In),
            new UdonAbiParameter(
                "result", UdonAbiType.Generic("T"), UdonAbiParameterMode.Out),
        });
        var bound = new UdonAbiCatalog(new[] { prototype })
            .Require(TestHelper.AbiKey(signature));
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("UnityEngineComponent", isEnum: false, isValueType: false);
        facts.RecordForTest("UnityEngineTransform", isEnum: false, isValueType: false);
        facts.RecordAssignableForTest("UnityEngineTransform", "UnityEngineComponent");

        var unsafeCall = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.UdonEventReceiver),
            new CConst(null, StorageTypes.Type),
        }, new StorageType("UnityEngineRigidbody"));
        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(unsafeCall, facts, "unsafe_query"));
        Assert.Contains("must be backed by a 'UnityEngineTransform' strongbox", error.Message);

        var safeCall = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.Transform),
            new CConst(null, StorageTypes.Type),
        }, new StorageType("UnityEngineRigidbody"));
        UdonAbiVerifier.VerifyInvocation(safeCall, facts, "safe_query");
    }

    [Theory]
    [InlineData("UnityEngineRenderer")]
    [InlineData("UnityEngineTransform")]
    [InlineData("UnityEngineGameObject")]
    public void StrongboxContractIsIndependentOfTheGetterOwner(string owner)
        => AssertStrongboxContract(owner, "GetComponent", "T");

    [Fact]
    public void EveryRegisteredGenericGetterMemberRequiresTransformStrongbox()
    {
        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in ExternRegistry.All)
        {
            var ownerEnd = name.IndexOf(".__", StringComparison.Ordinal);
            if (ownerEnd <= 0) continue;
            var rest = name.Substring(ownerEnd + 3);
            string result = null;
            if (rest.EndsWith("__TArray", StringComparison.Ordinal)) result = "TArray";
            else if (rest.EndsWith("__T", StringComparison.Ordinal)) result = "T";
            if (result == null) continue;
            var head = rest.Substring(0, rest.Length - result.Length - 2);
            var paramStart = head.IndexOf("__", StringComparison.Ordinal);
            members.Add(paramStart < 0 ? head : head.Substring(0, paramStart));
        }

        Assert.Equal(
            new[]
            {
                "GetComponent", "GetComponentInChildren", "GetComponentInParent",
                "GetComponents", "GetComponentsInChildren", "GetComponentsInParent",
            },
            members);

        foreach (var member in members)
            AssertStrongboxContract("UnityEngineComponent", member,
                member.StartsWith("GetComponents", StringComparison.Ordinal) ? "TArray" : "T");
    }

    static void AssertStrongboxContract(string owner, string member, string resultType)
    {
        var signature = $"{owner}.__{member}__{resultType}";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            Param("instance", owner, UdonAbiParameterMode.In),
            Param("type", "SystemType", UdonAbiParameterMode.In),
            new UdonAbiParameter("result",
                resultType == "TArray"
                    ? UdonAbiType.Array(UdonAbiType.Generic("T"))
                    : UdonAbiType.Generic("T"),
                UdonAbiParameterMode.Out),
        });
        var bound = new UdonAbiCatalog(new[] { prototype })
            .Require(TestHelper.AbiKey(signature));
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest(owner, isEnum: false, isValueType: false);
        facts.RecordForTest("UnityEngineTransform", isEnum: false, isValueType: false);

        var unsafeCall = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.UdonEventReceiver),
            new CConst(null, StorageTypes.Type),
        }, resultType == "TArray"
            ? new StorageType("UnityEngineRigidbodyArray")
            : new StorageType("UnityEngineRigidbody"));
        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(unsafeCall, facts, $"unsafe_{member}"));
        Assert.Contains("must be backed by a 'UnityEngineTransform' strongbox", error.Message);
    }

    [Fact]
    public void OrdinaryComponentGetterAcceptsUdonBehaviourValue()
    {
        const string signature =
            "UnityEngineComponent.__get_transform__UnityEngineTransform";
        var bound = Catalog(signature,
            Param("instance", "UnityEngineComponent", UdonAbiParameterMode.In),
            Param("result", "UnityEngineTransform", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var call = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.UdonEventReceiver),
        }, StorageTypes.Transform);
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("UnityEngineComponent", isEnum: false, isValueType: false);
        facts.RecordForTest("UnityEngineTransform", isEnum: false, isValueType: false);

        UdonAbiVerifier.VerifyInvocation(call, facts, "component_transform");
    }

    [Fact]
    public void FoldedUdonBehaviourUsesOnlyItsRepresentationHierarchy()
    {
        var facts = new UdonTypeFactRegistry();

        Assert.True(facts.IsAssignableFact(
            "VRCUdonUdonBehaviour",
            "VRCUdonCommonInterfacesIUdonEventReceiver"));
        Assert.True(facts.IsAssignableFact(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            "VRCUdonCommonInterfacesIUdonProgramVariableAccessTarget"));
        Assert.True(facts.IsAssignableFact(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            "UnityEngineComponent"));
        Assert.Null(facts.IsAssignableFact(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            "UnrelatedSourceInterface"));
    }

    [Fact]
    public void ClrSdkFactsPreserveReferenceValueAndEnumCategories()
    {
        var facts = new UdonTypeFactRegistry();

        facts.Record("SdkReference", typeof(System.IDisposable));
        facts.Record("SdkValue", typeof(System.DateTime));
        facts.Record("SdkEnum", typeof(System.DayOfWeek));

        Assert.True(facts.IsReferenceFact("SdkReference"));
        Assert.False(facts.IsReferenceFact("SdkValue"));
        Assert.True(facts.IsEnumFact("SdkEnum"));
    }

    [Fact]
    public void ClrSdkFactsPreserveDirectedInheritance()
    {
        var facts = new UdonTypeFactRegistry();
        facts.Record("SystemIOFileStream", typeof(System.IO.FileStream));
        facts.Record("SystemIOStream", typeof(System.IO.Stream));

        Assert.True(facts.IsAssignableFact("SystemIOFileStream", "SystemIOStream"));
        Assert.False(facts.IsAssignableFact("SystemIOStream", "SystemIOFileStream"));
    }

    [Fact]
    public void FoldedSourceTypeDoesNotLeakItsSourceInterfaces()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
interface IFolded { }
struct Folded : IFolded { }");
        var compilation = CSharpCompilation.Create(
            "FoldedFacts",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var folded = compilation.GetTypeByMetadataName("Folded");
        var foldedInterface = compilation.GetTypeByMetadataName("IFolded");
        var foldedTag = ExternResolver.GetUdonTypeName(folded);
        var interfaceTag = ExternResolver.GetUdonTypeName(foldedInterface);
        var facts = new UdonTypeFactRegistry();

        facts.Record(foldedTag, folded);

        Assert.Equal("SystemObjectArray", foldedTag);
        Assert.Equal("VRCUdonCommonInterfacesIUdonEventReceiver", interfaceTag);
        Assert.False(facts.IsAssignableFact(foldedTag, interfaceTag));
        Assert.True(facts.IsAssignableFact(foldedTag, "SystemArray"));
        Assert.DoesNotContain(
            facts.AssignabilitySnapshot(),
            relation => relation.From == foldedTag && relation.To == interfaceTag);
    }

    static UdonAbiCatalog Catalog(string signature, params UdonAbiParameter[] parameters)
        => new(new[]
        {
            new UdonExternPrototype(signature, parameters),
        });

    static UdonAbiParameter Param(string name, string type, UdonAbiParameterMode mode)
        => new(name, UdonAbiType.Exact(type), mode);
}
