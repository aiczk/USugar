using System;
using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class CfgBuilderTests
{
    [Fact]
    public void FieldLoadWritesItsAllocatedSlotDirectly()
    {
        var builder = Begin(out var module, out var function);
        module.Fields.Add(new FieldDecl("value", StorageTypes.Int32));

        var value = builder.LoadField("value", StorageTypes.Int32);
        builder.EmitReturn();

        var load = Assert.IsType<CLoadField>(
            Assert.Single(function.Entry.Instructions));
        Assert.Equal(value.SlotId, load.DestSlot);
    }

    [Fact]
    public void ExternCallWritesItsAllocatedSlotAndPreservesMetadata()
    {
        var builder = Begin(out _, out var function);
        var destination = builder.AllocScratch(StorageTypes.Int32);

        const string signature =
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32";
        builder.EmitAssign(destination, new CExternCall(
            TestHelper.RegistryFacts.Require(
                TestHelper.AbiKey(signature)),
            new List<CLeaf>
            {
                builder.Const(1, StorageTypes.Int32),
                builder.Const(2, StorageTypes.Int32),
            },
            StorageTypes.Int32, reentrant: true));

        var statement = Assert.IsType<CExprStmt>(
            Assert.Single(function.Entry.Instructions));
        var call = Assert.IsType<CExternCall>(statement.Expr);
        Assert.Equal(destination, call.DestSlot);
        Assert.True(call.Reentrant);
        Assert.Equal(1, function.ReentrantSiteCount);
    }

    [Fact]
    public void SelectWritesBothArmsIntoOneAllocatedSlot()
    {
        var builder = Begin(out _, out var function);

        var value = builder.Select(
            builder.Const(true, StorageTypes.Boolean),
            builder.Const(1, StorageTypes.Int32),
            builder.Const(2, StorageTypes.Int32),
            StorageTypes.Int32);
        builder.EmitReturn();
        builder.Complete();

        Assert.Equal(2, CountAssignmentsTo(function, value.SlotId));
        FlatVerify.Verify(function);
    }

    [Fact]
    public void CrossCallMaterializesTypedTransportImmediately()
    {
        var builder = Begin(out _, out var function);
        var receiver = builder.Const(null, StorageTypes.UdonEventReceiver);
        var name = builder.Const("value", StorageTypes.String);

        var value = builder.LoadProgramVariable(
            receiver, name, StorageTypes.Int32);
        builder.EmitProgramVariableStore(
            receiver, name, StorageTypes.Int32, value);

        var calls = Calls(function);
        Assert.Equal(2, calls.Count);
        Assert.Equal(
            ExternResolver.EventReceiverGetProgramVariable,
            calls[0].Sig.Key);
        Assert.Equal(value.SlotId, calls[0].DestSlot);
        Assert.Equal(
            ExternResolver.EventReceiverSetProgramVariable,
            calls[1].Sig.Key);
        Assert.Equal(StorageTypes.Int32, calls[1].Args[2].Type);
    }

    [Fact]
    public void TupleCrossCallReadsEveryDeclaredReturnType()
    {
        var builder = Begin(out _, out var function);

        builder.CrossCall(
            builder.Const(null, StorageTypes.UdonEventReceiver),
            new CrossCallTransportPlan(
                builder.Const("Call", StorageTypes.String),
                Array.Empty<CrossCallParameter>(),
                new[]
                {
                    new ReturnSlot("number", StorageTypes.Int32),
                    new ReturnSlot("text", StorageTypes.String),
                },
                StorageTypes.Void));

        var resultTypes = Calls(function)
            .FindAll(call =>
                call.Sig.Key
                == ExternResolver.EventReceiverGetProgramVariable)
            .ConvertAll(call => call.Type);
        Assert.Equal(
            new[] { StorageTypes.Int32, StorageTypes.String },
            resultTypes);
    }

    [Fact]
    public void DynamicCrossCallEventNameRemainsASlotOperand()
    {
        var builder = Begin(out _, out var function);
        var eventNameSlot = builder.AllocFrame(StorageTypes.String);

        builder.CrossCall(
            builder.Const(null, StorageTypes.UdonEventReceiver),
            new CrossCallTransportPlan(
                builder.SlotRef(eventNameSlot),
                Array.Empty<CrossCallParameter>(),
                Array.Empty<ReturnSlot>(),
                StorageTypes.Void));

        var dispatch = Assert.Single(
            Calls(function),
            call => call.Sig.Key
                == ExternResolver.EventReceiverSendCustomEvent);
        Assert.Equal(
            eventNameSlot,
            Assert.IsType<CSlotRef>(dispatch.Args[1]).SlotId);
    }

    [Fact]
    public void ControlFlowBuildsBasicBlocksWithoutAnIntermediateTree()
    {
        var builder = Begin(out _, out var function);
        var result = builder.AllocFrame(StorageTypes.Int32);

        builder.EmitIf(
            builder.Const(true, StorageTypes.Boolean),
            _ => builder.EmitAssign(
                result, builder.Const(1, StorageTypes.Int32)),
            _ => builder.EmitAssign(
                result, builder.Const(2, StorageTypes.Int32)));
        builder.EmitReturn();
        builder.Complete();

        Assert.Equal(4, function.Blocks.Count);
        Assert.IsType<CBranch>(function.Entry.Terminator);
        FlatVerify.Verify(function);
    }

    [Fact]
    public void DeadCodeIsStillValidatedButNeverMaterialized()
    {
        var builder = Begin(out _, out var function);
        var slot = builder.AllocFrame(StorageTypes.Int32);
        builder.EmitReturn();

        var error = Assert.Throws<VerificationException>(
            () => builder.EmitAssign(
                slot, builder.Const("bad", StorageTypes.String)));

        Assert.Contains("CAssign", error.Message);
        Assert.Empty(function.Entry.Instructions);
    }

    [Fact]
    public void ForwardGotoReservesAndDefinesOneTargetBlock()
    {
        var builder = Begin(out _, out var function);

        builder.EmitGoto("done");
        builder.EmitLabel("done");
        builder.EmitReturn();
        builder.Complete();

        Assert.Equal(2, function.Blocks.Count);
        FlatVerify.Verify(function);
    }

    [Fact]
    public void UndefinedGotoFailsAtCfgCompletion()
    {
        var builder = Begin(out _, out _);
        builder.EmitGoto("missing");

        var error = Assert.Throws<VerificationException>(
            builder.Complete);

        Assert.Contains("undefined label 'missing'", error.Message);
    }

    [Fact]
    public void BreakOutsideLoopFailsAtConstruction()
    {
        var builder = Begin(out _, out _);

        Assert.Throws<VerificationException>(builder.EmitBreak);
    }

    static CoreBuilder Begin(
        out FlatModule module, out FlatFunction function)
    {
        module = new FlatModule(
            abiCatalog: TestHelper.RegistryFacts);
        var builder = new CoreBuilder(module);
        function = builder.BeginFunction("test");
        function.ReturnType = StorageTypes.Void;
        return builder;
    }

    static List<CExternCall> Calls(FlatFunction function)
    {
        var calls = new List<CExternCall>();
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
            if (instruction is CExprStmt
                { Expr: CExternCall call })
                calls.Add(call);
        return calls;
    }

    static int CountAssignmentsTo(
        FlatFunction function, int slot)
    {
        var count = 0;
        foreach (var block in function.Blocks)
        foreach (var instruction in block.Instructions)
            if (instruction is CAssign assign
                && assign.DestSlot == slot)
                count++;
        return count;
    }
}
