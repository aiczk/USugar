using Xunit;

namespace USugar.Tests;

public class UdonAbiBinderTests
{
    [Fact]
    public void IndexerGetterBindsMetadataNameAndDeclaredParameters()
    {
        const string signature =
            "SystemTextStringBuilder.__get_Chars__SystemInt32__SystemChar";
        var binder = new UdonAbiBinder(new UdonAbiCatalog(new[] { signature }));

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
        var binder = new UdonAbiBinder(new UdonAbiCatalog(new[] { signature }));

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
        var binder = new UdonAbiBinder(new UdonAbiCatalog(new[]
        {
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
        }));

        var error = Assert.Throws<System.NotSupportedException>(() =>
            binder.BindExact("SystemInt32.__op_Division__SystemInt32_SystemInt32__SystemInt32"));

        Assert.Contains("not registered", error.Message);
    }
}
