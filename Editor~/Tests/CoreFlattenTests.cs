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
            builder.Const(null, StorageTypes.UdonEventReceiver),
            new CrossCallTransportPlan(
                builder.Const("Call", StorageTypes.String),
                System.Array.Empty<CrossCallParameter>(),
                new[] { new ReturnSlot("result", StorageTypes.Int32) },
                StorageTypes.Int32));
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

    [Fact]
    public void TupleCrossCall_LowersEachReturnAtItsDeclaredType()
    {
        var builder = Begin(out var function);
        builder.CrossCall(
            builder.Const(null, StorageTypes.UdonEventReceiver),
            new CrossCallTransportPlan(
                builder.Const("Call", StorageTypes.String),
                System.Array.Empty<CrossCallParameter>(),
                new[] {
                    new ReturnSlot("number", StorageTypes.Int32),
                    new ReturnSlot("text", StorageTypes.String)
                },
                StorageTypes.Void));

        CoreFlatten.Lower(function);

        var resultTypes = new List<StorageType>();
        foreach (var block in function.FlatBlocks)
        foreach (var statement in block.Stmts)
            if (statement is CExprStmt expr && expr.Expr is CExternCall call
                && call.Sig == ExternResolver.EventReceiverGetProgramVariable)
                resultTypes.Add(call.Type);
        Assert.Equal(new[] { StorageTypes.Int32, StorageTypes.String }, resultTypes);
    }

    [Fact]
    public void ProgramVariableLoadAndStore_LowerThroughTypedTransport()
    {
        var builder = Begin(out var function);
        var receiver = builder.Const(null, StorageTypes.UdonEventReceiver);
        var name = builder.Const("value", StorageTypes.String);
        var value = builder.LoadProgramVariable(receiver, name, StorageTypes.Int32);
        builder.EmitProgramVariableStore(receiver, name, StorageTypes.Int32, value);
        var slotCount = function.Slots.Count;

        CoreFlatten.Lower(function);

        Assert.Equal(slotCount, function.Slots.Count);
        var calls = new List<CExternCall>();
        foreach (var block in function.FlatBlocks)
        foreach (var statement in block.Stmts)
            if (statement is CExprStmt expression && expression.Expr is CExternCall call)
                calls.Add(call);
        Assert.Equal(2, calls.Count);
        Assert.Equal(ExternResolver.EventReceiverGetProgramVariable, calls[0].Sig);
        Assert.Equal(StorageTypes.Int32, calls[0].Type);
        Assert.Equal(ExternResolver.EventReceiverSetProgramVariable, calls[1].Sig);
        Assert.Equal(StorageTypes.Int32, calls[1].Args[2].Type);
    }

    [Fact]
    public void CrossCall_PreservesDynamicEventName()
    {
        var builder = Begin(out var function);
        var eventNameSlot = builder.AllocFrame(StorageTypes.String);
        builder.CrossCall(
            builder.Const(null, StorageTypes.UdonEventReceiver),
            new CrossCallTransportPlan(
                builder.SlotRef(eventNameSlot),
                System.Array.Empty<CrossCallParameter>(),
                System.Array.Empty<ReturnSlot>(),
                StorageTypes.Void));

        CoreFlatten.Lower(function);

        CExternCall dispatch = null;
        foreach (var block in function.FlatBlocks)
        foreach (var statement in block.Stmts)
            if (statement is CExprStmt expression && expression.Expr is CExternCall call
                && call.Sig == ExternResolver.EventReceiverSendCustomEvent)
                dispatch = call;
        Assert.NotNull(dispatch);
        var eventName = Assert.IsType<CSlotRef>(dispatch.Args[1]);
        Assert.Equal(eventNameSlot, eventName.SlotId);
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
