using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class UdonAbiSnapshotCodecTests
{
    [Fact]
    public void RoundTripPreservesExactTypesModesAndTypeFacts()
    {
        const string signature = "Fixture.__Map__SystemInt32_SystemInt32Array__SystemInt32Array";
        var facts = new[]
        {
            new KeyValuePair<string, UdonTypeFactRegistry.TypeFact>(
                "Fixture", new UdonTypeFactRegistry.TypeFact(
                    isEnum: false, isValueType: false)),
        };
        var catalog = new UdonAbiCatalog(new[]
        {
            new UdonExternPrototype(signature, new[]
            {
                new UdonAbiParameter(
                    "value", UdonAbiType.Exact("SystemInt32"), UdonAbiParameterMode.In),
                new UdonAbiParameter(
                    "buffer", UdonAbiType.Exact("SystemInt32Array"),
                    UdonAbiParameterMode.InOut),
                new UdonAbiParameter(
                    "result", UdonAbiType.Exact("SystemInt32Array"),
                    UdonAbiParameterMode.Out),
            }),
        }, facts, new[] { "Fixture", "SystemInt32", "SystemInt32Array" });

        var decoded = UdonAbiSnapshotCodec.Decode(
            UdonAbiSnapshotCodec.Encode(catalog));
        var prototype = decoded.Require(
            UdonAbiKey.Method("Fixture", "Map",
                new[] { "SystemInt32", "SystemInt32Array" },
                "SystemInt32Array")).Prototype;

        Assert.True(prototype.HasTypedParameters);
        Assert.Equal(3, prototype.Parameters.Count);
        Assert.Equal(UdonAbiParameterMode.InOut, prototype.Parameters[1].Mode);
        Assert.Equal("SystemInt32Array", prototype.Parameters[1].Type.ExactType.Name);
        var imported = new UdonTypeFactRegistry();
        decoded.SeedTypeFacts(imported);
        Assert.True(imported.IsReferenceFact("Fixture"));
        Assert.True(decoded.IsRegisteredType("Fixture"));
        Assert.True(decoded.IsRegisteredType("SystemInt32"));
        Assert.False(decoded.IsRegisteredType("Missing"));

        Assert.True(decoded.TryGetType(
            UdonTypeIdentity.FromCanonicalName("Fixture"), out var fixture));
        Assert.True(fixture.HasTypeNode);
        Assert.True(fixture.AppearsAsExternOwner);
        Assert.False(fixture.AppearsAsExternOperand);
        Assert.False(fixture.IsValueType);

        Assert.True(decoded.TryGetType(
            UdonTypeIdentity.FromCanonicalName("SystemInt32"), out var int32));
        Assert.True(int32.HasTypeNode);
        Assert.True(int32.AppearsAsExternOperand);
    }

    [Fact]
    public void EncodingRejectsNameOnlyPrototypes()
    {
        var catalog = UdonAbiCatalog.FromNamesForTests(new[]
        {
            "Fixture.__Read__SystemInt32",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => UdonAbiSnapshotCodec.Encode(catalog));

        Assert.Contains("untyped extern prototype", error.Message);
    }

    [Theory]
    [InlineData("X\tbad")]
    [InlineData("R\t")]
    [InlineData("T\tName\t2\t0")]
    [InlineData("E\tFixture.__Read__SystemInt32\t1\tvalue\tSideways\tE:SystemInt32")]
    [InlineData("E\tFixture.__Read__SystemInt32\t1\tvalue\tIn\tG:T")]
    [InlineData("E\tFixture.__Read__SystemInt32\t1\tvalue\tIn\tA:E:SystemInt32")]
    public void MalformedRowsFailLoudly(string row)
        => Assert.Throws<FormatException>(
            () => UdonAbiSnapshotCodec.Decode(new[] { row }));

    [Fact]
    public void SdkTypedSnapshotMatchesTheTrackedExternCensus()
    {
        var typed = ExternRegistry.LoadSdkTypedCatalog();
        var typedNames = typed.ExternNames.ToHashSet(StringComparer.Ordinal);
        var flatExterns = ExternRegistry.All
            .Where(UdonAbiCatalog.IsExternRegistryName)
            .ToHashSet(StringComparer.Ordinal);
        var flatTypes = ExternRegistry.RegisteredTypes
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(typedNames.Except(flatExterns));
        Assert.Equal(
            new[]
            {
                "VRCTestStubsDisposableResource.__Dispose__SystemVoid",
                "VRCTestStubsDisposableResource.__ctor____VRCTestStubsDisposableResource",
                "VRCTestStubsDisposableResource.__get_Value__SystemInt32",
                "VRCTestStubsDisposableResource.__set_Value__SystemInt32__SystemVoid",
            },
            flatExterns.Except(typedNames).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(typed.Prototypes,
            prototype => Assert.True(prototype.HasTypedParameters));
        Assert.Empty(typed.RegisteredTypes.Except(flatTypes));
        Assert.Empty(flatTypes.Except(typed.RegisteredTypes));

        var merged = ExternRegistry.LoadTypedCatalog();
        Assert.Empty(flatExterns.Except(merged.ExternNames));
        Assert.Empty(merged.ExternNames.Except(flatExterns));
        Assert.All(merged.Prototypes,
            prototype => Assert.True(prototype.HasTypedParameters));
    }

    [Fact]
    public void SdkTypeCapabilitiesDoNotConflateTypeNodesAndExternOperands()
    {
        var catalog = ExternRegistry.LoadSdkTypedCatalog();
        var operandOnly = catalog.Types
            .Where(type => type.AppearsAsExternOperand && !type.HasTypeNode)
            .Select(type => type.Id.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(operandOnly);
        Assert.All(operandOnly,
            name => Assert.False(catalog.IsRegisteredType(name)));
        Assert.Contains("SystemConvert", operandOnly);
    }
}
