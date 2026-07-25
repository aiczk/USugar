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
    public void CompilationSessionSeedsSdkReferenceFactsForExternOperands(
        string expected, string actual)
    {
        var signature = $"Example.__Accept__{expected}__SystemVoid";
        var sdkFacts = new UdonTypeFactRegistry();
        sdkFacts.RecordForTest(expected, isEnum: false, isValueType: false);
        var catalog = new UdonAbiCatalog(new[]
        {
            new UdonExternPrototype(signature, new[]
            {
                Param("value", expected, UdonAbiParameterMode.In),
            }),
        }, sdkFacts.Snapshot());
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

    static UdonAbiCatalog Catalog(string signature, params UdonAbiParameter[] parameters)
        => new(new[]
        {
            new UdonExternPrototype(signature, parameters),
        });

    static UdonAbiParameter Param(string name, string type, UdonAbiParameterMode mode)
        => new(name, UdonAbiType.Exact(type), mode);
}
