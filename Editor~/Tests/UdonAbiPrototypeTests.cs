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

    [Fact]
    public void ExternInputRejectsUnrelatedReferenceTypes()
    {
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("ExpectedReference", isEnum: false, isValueType: false);
        facts.RecordForTest("UnrelatedReference", isEnum: false, isValueType: false);

        var reason = ExternOperandCompatibility.WhyIncompatible(
            "ExpectedReference", "UnrelatedReference",
            UdonAbiParameterMode.In, facts);

        Assert.Contains(
            "no directed assignability fact from 'UnrelatedReference' to 'ExpectedReference'",
            reason);
        Assert.Null(RawCopyCompatibility.WhyIncompatible(
            "ExpectedReference", "UnrelatedReference", facts));
    }

    [Fact]
    public void ExternObjectWildcardIsDirectional()
    {
        var facts = new UdonTypeFactRegistry();

        Assert.Null(ExternOperandCompatibility.WhyIncompatible(
            "SystemObject", "SystemString", UdonAbiParameterMode.In, facts));
        Assert.NotNull(ExternOperandCompatibility.WhyIncompatible(
            "SystemString", "SystemObject", UdonAbiParameterMode.In, facts));
        Assert.Null(ExternOperandCompatibility.WhyIncompatible(
            "SystemString", "SystemObject", UdonAbiParameterMode.Out, facts));
        Assert.NotNull(ExternOperandCompatibility.WhyIncompatible(
            "SystemObject", "SystemString", UdonAbiParameterMode.Out, facts));
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

        UdonAbiVerifier.VerifyInvocation(
            call, new UdonTypeFactRegistry(), "component_transform");
    }

    [Fact]
    public void ProgramVariableResultIsTheOnlyTypedObjectOutput()
    {
        const string signature =
            "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject";
        var bound = Catalog(signature,
            Param("instance", "VRCUdonCommonInterfacesIUdonEventReceiver",
                UdonAbiParameterMode.In),
            Param("name", "SystemString", UdonAbiParameterMode.In),
            Param("result", "SystemObject", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var call = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.UdonEventReceiver),
            new CConst("score", StorageTypes.String),
        }, StorageTypes.Int32);

        UdonAbiVerifier.VerifyInvocation(
            call, new UdonTypeFactRegistry(), "typed_program_variable");

        const string unrelated = "Example.__GetObject__SystemObject";
        var unrelatedCall = new CExternCall(
            Catalog(unrelated,
                    Param("result", "SystemObject", UdonAbiParameterMode.Out))
                .Require(TestHelper.AbiKey(unrelated)),
            new List<CLeaf>(),
            StorageTypes.Int32);
        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(
                unrelatedCall, new UdonTypeFactRegistry(), "unrelated_object"));
        Assert.Contains(
            "'SystemObject' does not prove a value readable as 'SystemInt32'",
            error.Message);
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

    static UdonAbiCatalog Catalog(string signature, params UdonAbiParameter[] parameters)
        => new(new[]
        {
            new UdonExternPrototype(signature, parameters),
        });

    static UdonAbiParameter Param(string name, string type, UdonAbiParameterMode mode)
        => new(name, UdonAbiType.Exact(type), mode);
}
