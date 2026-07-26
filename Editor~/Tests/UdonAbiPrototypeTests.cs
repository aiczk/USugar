using System;
using System.Collections.Generic;
using System.Linq;
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
    public void CfgBuilderAcceptsTypedSdkPrototype()
    {
        const string signature = "ExampleMath.__Add__SystemInt32_SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("left", "SystemInt32", UdonAbiParameterMode.In),
            Param("right", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var module = new FlatModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var destination = builder.AllocFrame(StorageTypes.Int32);
        builder.EmitAssign(destination, new CExternCall(bound, new List<CLeaf>
        {
            builder.Const(1, StorageTypes.Int32),
            builder.Const(2, StorageTypes.Int32),
        }, StorageTypes.Int32));

        builder.Complete();
        FlatVerify.Verify(module);
    }

    [Fact]
    public void CfgBuilderRejectsSdkPrototypeAritySkew()
    {
        const string signature = "ExampleMath.__Add__SystemInt32_SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("left", "SystemInt32", UdonAbiParameterMode.In),
            Param("right", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var module = new FlatModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var destination = builder.AllocFrame(StorageTypes.Int32);
        var error = Assert.Throws<VerificationException>(() =>
            builder.EmitAssign(destination, new CExternCall(
                bound, new List<CLeaf>
                {
                    builder.Const(1, StorageTypes.Int32),
                },
                StorageTypes.Int32)));
        Assert.Contains("consumes 3 SDK stack operands", error.Message);
    }

    [Fact]
    public void CfgBuilderRejectsSdkPrototypeTypeSkew()
    {
        const string signature = "ExampleMath.__Abs__SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("value", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out))
            .Require(TestHelper.AbiKey(signature));
        var module = new FlatModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var destination = builder.AllocFrame(StorageTypes.Int32);
        var error = Assert.Throws<VerificationException>(() =>
            builder.EmitAssign(destination, new CExternCall(
                bound, new List<CLeaf>
                {
                    builder.Const("bad", StorageTypes.String),
                },
                StorageTypes.Int32)));
        Assert.Contains("expects ABI type 'SystemInt32'", error.Message);
    }

    [Fact]
    public void ExternOperandPolicyIsIndependentFromRawCopyPolicy()
    {
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest(
            "FixtureReferenceA", isEnum: false, isValueType: false);
        facts.RecordForTest(
            "FixtureReferenceB", isEnum: false, isValueType: false);

        Assert.Null(ExternOperandCompatibility.WhyIncompatible(
            "FixtureReferenceA",
            "FixtureReferenceB",
            UdonAbiParameterMode.In,
            facts));
        Assert.Null(RawCopyCompatibility.WhyIncompatible(
            "FixtureReferenceA",
            "FixtureReferenceB",
            facts));

        var externReason = ExternOperandCompatibility.WhyIncompatible(
            "SystemInt32",
            "SystemString",
            UdonAbiParameterMode.Out,
            facts);
        var copyReason = RawCopyCompatibility.WhyIncompatible(
            "SystemInt32",
            "SystemString",
            facts);

        Assert.NotNull(externReason);
        Assert.NotNull(copyReason);
        Assert.Contains("extern Out operand", externReason);
        Assert.DoesNotContain("extern Out operand", copyReason);
    }

    [Fact]
    public void NameOnlyPrototypeCannotBypassOperandVerification()
    {
        const string signature = "Example.__Touch__SystemInt32__SystemVoid";
        var bound = UdonAbiCatalog.FromNamesForTests(new[] { signature })
            .Require(TestHelper.AbiKey(signature));
        var call = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.Int32),
        }, StorageTypes.Void);

        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(
                call, new UdonTypeFactRegistry(), "name_only"));
        Assert.Contains("name-only ABI fixture", error.Message);
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
            args.Add(new CConst(
                arg == StorageTypes.Type ? actualResult.Name : null,
                arg));
        UdonAbiVerifier.VerifyInvocation(
            new CExternCall(bound, args, actualResult), facts, signature);
    }

    [Fact]
    public void GenericComponentQueryRequiresTransformStrongbox()
    {
        const string signature = "UnityEngineComponent.__GetComponent__T";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            Param("instance", "UnityEngineComponent", UdonAbiParameterMode.In),
            Param("type", "SystemType", UdonAbiParameterMode.In),
            Param("result", "UnityEngineObject", UdonAbiParameterMode.Out),
        });
        var bound = new UdonAbiCatalog(new[] { prototype })
            .Require(TestHelper.AbiKey(signature));
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("UnityEngineComponent", isEnum: false, isValueType: false);
        facts.RecordForTest("UnityEngineTransform", isEnum: false, isValueType: false);

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
            new CConst("UnityEngineObject", StorageTypes.Type),
        }, new StorageType("UnityEngineObject"));
        UdonAbiVerifier.VerifyInvocation(safeCall, facts, "safe_query");
    }

    [Fact]
    public void GenericComponentResultMustMatchItsTypeToken()
    {
        const string signature = "UnityEngineComponent.__GetComponent__T";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            Param("instance", "UnityEngineComponent", UdonAbiParameterMode.In),
            Param("type", "SystemType", UdonAbiParameterMode.In),
            Param("result", "UnityEngineObject", UdonAbiParameterMode.Out),
        });
        var bound = new UdonAbiCatalog(new[] { prototype })
            .Require(TestHelper.AbiKey(signature));
        var facts = new UdonTypeFactRegistry();
        foreach (var name in new[]
                 {
                     "UnityEngineComponent", "UnityEngineTransform",
                     "UnityEngineRigidbody", "UnityEngineCanvasGroup",
                 })
            facts.RecordForTest(name, isEnum: false, isValueType: false);

        var call = new CExternCall(bound, new List<CLeaf>
        {
            new CConst(null, StorageTypes.Transform),
            new CConst("UnityEngineRigidbody", StorageTypes.Type),
        }, new StorageType("UnityEngineCanvasGroup"));

        var error = Assert.Throws<VerificationException>(
            () => UdonAbiVerifier.VerifyInvocation(call, facts, "mismatched_query"));
        Assert.Contains(
            "binds generic result 'T' to 'UnityEngineRigidbody'", error.Message);
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

    [Fact]
    public void EveryRegisteredGenericListComponentQueryRequiresTransformStrongbox()
    {
        var signatures = ExternRegistry.All
            .Where(name => name.StartsWith(
                "UnityEngineComponent.__GetComponents", StringComparison.Ordinal)
                && name.Contains("ListT"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "UnityEngineComponent.__GetComponentsInChildren__ListT__SystemVoid",
            "UnityEngineComponent.__GetComponentsInChildren__SystemBoolean_ListT__SystemVoid",
            "UnityEngineComponent.__GetComponentsInParent__SystemBoolean_ListT__SystemVoid",
            "UnityEngineComponent.__GetComponents__ListT__SystemVoid",
        }, signatures);
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("UnityEngineComponent", isEnum: false, isValueType: false);
        facts.RecordForTest("UnityEngineTransform", isEnum: false, isValueType: false);

        foreach (var signature in signatures)
        {
            var bound = TestHelper.RegistryFacts.Require(TestHelper.AbiKey(signature));
            var args = Enumerable.Range(0, bound.Prototype.Parameters.Count)
                .Select(index => index == 0
                    ? (CLeaf)new CConst(null, StorageTypes.UdonEventReceiver)
                    : new CConst(null, StorageTypes.Object))
                .ToList();
            var unsafeCall = new CExternCall(bound, args, StorageTypes.Void);

            var error = Assert.Throws<VerificationException>(
                () => UdonAbiVerifier.VerifyInvocation(
                    unsafeCall, facts, "unsafe_list_query"));

            Assert.Contains(
                "must be backed by a 'UnityEngineTransform' strongbox",
                error.Message);
        }
    }

    static void AssertStrongboxContract(string owner, string member, string resultType)
    {
        var signature = $"{owner}.__{member}__{resultType}";
        var prototype = new UdonExternPrototype(signature, new[]
        {
            Param("instance", owner, UdonAbiParameterMode.In),
            Param("type", "SystemType", UdonAbiParameterMode.In),
            Param("result",
                resultType == "TArray"
                    ? "UnityEngineObjectArray"
                    : "UnityEngineObject",
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
