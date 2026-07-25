using Xunit;

namespace USugar.Tests;

public class UdonAbiBinderTests
{
    [Fact]
    public void SemanticKeySerializesOnlyAtCatalogBoundary()
    {
        const string signature =
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32";
        var key = UdonAbiKey.Binary("System.Int32", "op_Addition",
            "System.Int32", "System.Int32", "System.Int32");
        var catalog = UdonAbiCatalog.FromNamesForTests(new[] { signature });

        var bound = catalog.Require(key);

        Assert.Equal(signature, bound.Text);
        Assert.Equal(key, bound.Key);
    }

    [Fact]
    public void OmittedResultAndVoidResultAreDifferentAbiShapes()
    {
        var omitted = UdonAbiKey.OmittedResult(
            "ExampleBuffer", "set_Item", "SystemInt32", "SystemString");
        var withVoid = UdonAbiKey.VoidMethod(
            "ExampleBuffer", "set_Item", "SystemInt32", "SystemString");

        Assert.Equal(
            "ExampleBuffer.__set_Item__SystemInt32_SystemString",
            omitted.ToString());
        Assert.Equal(
            "ExampleBuffer.__set_Item__SystemInt32_SystemString__SystemVoid",
            withVoid.ToString());
        Assert.NotEqual(omitted, withVoid);
    }

    [Fact]
    public void ParameterlessConstructorPreservesExplicitEmptyParameterList()
    {
        var constructor = UdonAbiKey.Constructor(
            "SystemObject", System.Array.Empty<string>(), "SystemObject");

        Assert.Equal("SystemObject.__ctor____SystemObject",
            constructor.ToString());
    }

    [Fact]
    public void IndexerGetterBindsMetadataNameAndDeclaredParameters()
    {
        const string signature =
            "SystemTextStringBuilder.__get_Chars__SystemInt32__SystemChar";
        var binder = new UdonAbiBinder(
            UdonAbiCatalog.FromNamesForTests(new[] { signature }));

        var bound = binder.BindIndexerGetter(
            "SystemTextStringBuilder",
            "Chars",
            new[] { "SystemInt32" },
            "SystemChar");

        Assert.Equal(signature, bound.Text);
    }

    [Fact]
    public void IndexerSetterSelectsRegisteredSuffixShape()
    {
        const string signature =
            "ExampleBuffer.__set_Item__SystemInt32_SystemString";
        var binder = new UdonAbiBinder(
            UdonAbiCatalog.FromNamesForTests(new[] { signature }));

        var bound = binder.BindIndexerSetter(
            "ExampleBuffer",
            "Item",
            new[] { "SystemInt32" },
            "SystemString");

        Assert.Equal(signature, bound.Text);
    }

    [Fact]
    public void ExactBindingRejectsUnregisteredCandidateImmediately()
    {
        var binder = new UdonAbiBinder(UdonAbiCatalog.FromNamesForTests(new[]
        {
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
        }));

        var error = Assert.Throws<System.NotSupportedException>(() =>
            binder.BindExact(UdonAbiKey.Binary("SystemInt32", "op_Division",
                "SystemInt32", "SystemInt32", "SystemInt32")));

        Assert.Contains("not registered", error.Message);
    }

    [Fact]
    public void InheritedObjectMethodUsesTheReceiverHierarchy()
    {
        const string source = @"
using UdonSharp;
using UnityEngine;
public class AbiOwnerHierarchy : UdonSharpBehaviour
{
    public Material material;
    public string result;
    void Start() { result = material.shader.ToString(); }
}";

        var uasm = TestHelper.CompileToUasm(
            source, "AbiOwnerHierarchy");

        Assert.Contains(
            "UnityEngineObject.__ToString__SystemString", uasm);
        Assert.DoesNotContain(
            "UnityEngineComponent.__ToString__SystemString", uasm);
    }
}
