using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public sealed class ObjectArrayBehaviourAliasTests
{
    const string Source = @"
using UdonSharp;

public class ObjectMarker : UdonSharpBehaviour { }

public static class ObjectMarkerExtensions
{
    public static T ForceCast<T>(this object[] array)
    {
        return (T)(object)array;
    }

    public static object[] UnPack(this ObjectMarker value)
    {
        return (object[])(object)value;
    }
}

public class FauxObject : ObjectMarker
{
    public static FauxObject Build()
    {
        object[] result = new object[] { 42 };
        int beforeReturn = result.ForceCast<FauxObject>().Read();
        return result.ForceCast<FauxObject>();
    }
}

public static class FauxObjectExtensions
{
    public static int Read(this FauxObject value)
    {
        return (int)value.UnPack()[0];
    }
}

public class AliasHost : UdonSharpBehaviour
{
    FauxObject value;

    void Start()
    {
        value = FauxObject.Build();
        int answer = value.Read();
    }
}";

    [Fact]
    public void AliasType_UsesObjectArrayAcrossFieldParametersAndReturns()
    {
        TestHelper.CompileToUasm(Source, "AliasHost", out var emitter);
        var alias = emitter.Compilation.GetTypeByMetadataName("FauxObject");
        var marker = emitter.Compilation.GetTypeByMetadataName("ObjectMarker");

        Assert.Equal(StorageTypes.ObjectArray,
            emitter.Planner.Session.Types.GetStorageType(alias));
        Assert.Equal(StorageTypes.ObjectArray,
            emitter.Planner.Session.Types.GetStorageType(marker));
        Assert.Equal(StorageTypes.ObjectArray,
            emitter.Planner.Session.Types.GetStorageType(
                emitter.Compilation.CreateArrayTypeSymbol(alias)));
        Assert.Contains(emitter.Module.Fields,
            field => field.Name == "value" && field.Type == StorageTypes.ObjectArray);

        var forceCast = emitter.Module.Functions
            .Where(function => function.Name.Contains("ForceCast", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(forceCast);
        Assert.All(forceCast,
            function => Assert.Equal(StorageTypes.ObjectArray, function.ReturnType));
    }

    [Fact]
    public void AliasCast_IsStorageIdentity_NotRepresentationCopy()
    {
        TestHelper.CompileToUasm(Source, "AliasHost", out var emitter);

        Assert.DoesNotContain(
            emitter.Module.Functions.SelectMany(function => function.FlatBlocks)
                .SelectMany(block => block.Stmts),
            statement => statement is CRepresentationCopy
            {
                Kind: RepresentationCastKind.ClosedGenericObjectCast,
            });
    }

    [Fact]
    public void AliasType_CannotAlsoBeAGetComponentTarget()
    {
        var source = Source.Replace(
            "int answer = value.Read();",
            "int answer = value.Read(); FauxObject component = GetComponent<FauxObject>();");

        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(source, "AliasHost"));
        Assert.Contains("legacy object[] nominal alias", error.Message);
    }
}
