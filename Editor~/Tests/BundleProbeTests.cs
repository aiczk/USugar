using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class BundleProbeTests
{
    [Fact]
    public void EmptyOrMalformedObjectArray_HeaderReadsAreGuarded()
    {
        var module = new FlatModule(
            abiCatalog: TestHelper.RegistryFacts);
        var builder = new CoreBuilder(module);
        var function = builder.BeginFunction("probe");
        function.ReturnType = StorageTypes.Boolean;

        // The value is deliberately erased. At runtime it may be an empty object[], a short
        // delegate-shaped array, or an object[] whose first cell is not a string.
        var result = BundleProbe.IsTagged(
            builder,
            builder.Const(Array.Empty<object>(), StorageTypes.Object),
            DelegateAbi.KindTag,
            DelegateAbi.BundleSize);
        builder.EmitReturn(result);
        builder.Complete();
        FlatVerify.Verify(function);

        var carrierBranch = Assert.IsType<CBranch>(
            function.Entry.Terminator);
        AssertCall(
            function.Entry,
            "SystemType", "IsInstanceOfType");

        // Empty/short arrays branch away before the first slot read.
        var lengthGuard = Block(
            function, carrierBranch.TrueBlockId);
        AssertCall(
            lengthGuard,
            AggregateAbi.ArrayType, "get_Length");
        Assert.DoesNotContain(
            Calls(function.Entry),
            call => call.Sig.Key.Member == "Get");
        var lengthBranch = Assert.IsType<CBranch>(
            lengthGuard.Terminator);

        // A non-string header branches away before the typed string read and StartsWith.
        var headerGuard = Block(
            function, lengthBranch.TrueBlockId);
        Assert.Contains(
            Calls(headerGuard),
            call => call.Sig.Key.Owner == AggregateAbi.ArrayType
                    && call.Sig.Key.Member == "Get"
                    && call.Sig.Key.ResultType == StorageTypes.Object.Name);
        AssertCall(
            headerGuard,
            "SystemType", "IsInstanceOfType");
        var headerBranch = Assert.IsType<CBranch>(
            headerGuard.Terminator);

        var tagCheck = Block(
            function, headerBranch.TrueBlockId);
        Assert.Contains(
            Calls(tagCheck),
            call => call.Sig.Key.Owner == AggregateAbi.ArrayType
                    && call.Sig.Key.Member == "Get"
                    && call.Type == StorageTypes.String);
        AssertCall(
            tagCheck,
            "SystemString", "StartsWith");
    }

    static FlatBlock Block(
        FlatFunction function,
        int id)
        => function.Blocks.Single(block => block.Id == id);

    static CExternCall[] Calls(FlatBlock block)
        => block.Instructions
            .OfType<CExprStmt>()
            .Select(statement => statement.Expr)
            .OfType<CExternCall>()
            .ToArray();

    static void AssertCall(
        FlatBlock block,
        string owner,
        string member)
        => Assert.Contains(
            Calls(block),
            call => call.Sig.Key.Owner == owner
                    && call.Sig.Key.Member == member);
}
