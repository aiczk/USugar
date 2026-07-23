using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class UdonAbiPrototypeTests
{
    [Fact]
    public void CoreVerifierAcceptsTypedSdkPrototype()
    {
        const string signature = "ExampleMath.__Add__SystemInt32_SystemInt32__SystemInt32";
        var bound = Catalog(signature,
            Param("left", "SystemInt32", UdonAbiParameterMode.In),
            Param("right", "SystemInt32", UdonAbiParameterMode.In),
            Param("result", "SystemInt32", UdonAbiParameterMode.Out)).Require(signature);
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
            Param("result", "SystemInt32", UdonAbiParameterMode.Out)).Require(signature);
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
            Param("result", "SystemInt32", UdonAbiParameterMode.Out)).Require(signature);
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
        var prototype = new UdonExternPrototype(signature, "ExampleArray", "First", new[]
        {
            new UdonAbiParameter("values", UdonAbiType.Array(UdonAbiType.Generic("T")),
                UdonAbiParameterMode.In),
            new UdonAbiParameter("result", UdonAbiType.Generic("T"),
                UdonAbiParameterMode.Out),
        });
        var bound = new UdonAbiCatalog(new[] { prototype }).Require(signature);
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

    static UdonAbiCatalog Catalog(string signature, params UdonAbiParameter[] parameters)
        => new(new[]
        {
            new UdonExternPrototype(signature, ExternResolver.ExternTypePrefix(signature),
                "test", parameters),
        });

    static UdonAbiParameter Param(string name, string type, UdonAbiParameterMode mode)
        => new(name, UdonAbiType.Exact(type), mode);
}
