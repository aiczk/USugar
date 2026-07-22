using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class CoreFlattenTests
{
    [Fact]
    public void BoundFieldLoad_WritesItsExistingSlotDirectly()
    {
        var builder = Begin(out var function);
        var value = builder.LoadField("value", StorageTypes.Int32);
        builder.EmitReturn(value);
        var slotCount = function.Slots.Count;

        CoreFlatten.Lower(function);

        Assert.Equal(slotCount, function.Slots.Count);
        var load = Assert.IsType<CLoadField>(Assert.Single(function.Entry.Stmts));
        Assert.Equal(value.SlotId, load.DestSlot);
    }

    [Fact]
    public void BoundExternCall_WritesItsExistingSlotAndPreservesSiteMetadata()
    {
        var builder = Begin(out var function);
        var destination = builder.AllocScratch(StorageTypes.Int32);
        function.ReentrantSiteCount = 1;
        builder.EmitAssign(destination, new CExternCall(
            new ExternSignature("SystemInt32.__op_Increment__SystemInt32__SystemInt32"),
            new List<CLeaf> { builder.Const(1, StorageTypes.Int32) },
            StorageTypes.Int32, reentrant: true, preSpillStmts: 2));
        var slotCount = function.Slots.Count;

        CoreFlatten.Lower(function);

        Assert.Equal(slotCount, function.Slots.Count);
        var stmt = Assert.IsType<CExprStmt>(Assert.Single(function.Entry.Stmts));
        var call = Assert.IsType<CExternCall>(stmt.Expr);
        Assert.Equal(destination, call.DestSlot);
        Assert.True(call.Reentrant);
        Assert.Equal(2, call.PreSpillStmts);
    }

    [Fact]
    public void BoundInternalCall_WritesItsExistingSlotAndPreservesSiteMetadata()
    {
        var builder = Begin(out var function);
        var destination = builder.AllocScratch(StorageTypes.Int32);
        function.ReentrantSiteCount = 1;
        builder.EmitAssign(destination, new CInternalCall(
            "callee", new List<CLeaf>(), StorageTypes.Int32,
            reentrant: true, tailSpared: true));
        var slotCount = function.Slots.Count;

        CoreFlatten.Lower(function);

        Assert.Equal(slotCount, function.Slots.Count);
        var stmt = Assert.IsType<CExprStmt>(Assert.Single(function.Entry.Stmts));
        var call = Assert.IsType<CInternalCall>(stmt.Expr);
        Assert.Equal(destination, call.DestSlot);
        Assert.True(call.Reentrant);
        Assert.True(call.TailSpared);
    }

    [Fact]
    public void BoundSelect_WritesBothArmsIntoItsExistingSlot()
    {
        var builder = Begin(out var function);
        var value = builder.Select(
            builder.Const(true, StorageTypes.Boolean),
            builder.Const(1, StorageTypes.Int32),
            builder.Const(2, StorageTypes.Int32),
            StorageTypes.Int32);
        var slotCount = function.Slots.Count;

        CoreFlatten.Lower(function);

        Assert.Equal(slotCount, function.Slots.Count);
        Assert.Equal(2, CountAssignmentsTo(function, value.SlotId));
    }

    [Fact]
    public void BoundCrossCall_WritesGetProgramVariableIntoItsExistingSlot()
    {
        var builder = Begin(out var function);
        var value = builder.CrossCall(
            builder.Const(null, StorageTypes.UdonEventReceiver), "Call",
            new List<(string, CLeaf)>(),
            new[] { new ReturnSlot("result", StorageTypes.Int32) },
            StorageTypes.Int32);
        var slotCount = function.Slots.Count;

        CoreFlatten.Lower(function);

        Assert.Equal(slotCount, function.Slots.Count);
        CExternCall resultCall = null;
        foreach (var block in function.FlatBlocks)
        foreach (var statement in block.Stmts)
            if (statement is CExprStmt expr && expr.Expr is CExternCall call
                && call.Sig == ExternResolver.EventReceiverGetProgramVariable)
                resultCall = call;
        Assert.NotNull(resultCall);
        Assert.Equal(value.SlotId, resultCall.DestSlot);
    }

    static int CountAssignmentsTo(CFunction function, int slot)
    {
        var count = 0;
        foreach (var block in function.FlatBlocks)
        foreach (var statement in block.Stmts)
            if (statement is CAssign assign && assign.DestSlot == slot)
                count++;
        return count;
    }

    static CoreBuilder Begin(out CFunction function)
    {
        var builder = new CoreBuilder(new CModule());
        function = builder.BeginFunction("test");
        function.ReturnType = StorageTypes.Void;
        return builder;
    }
}
