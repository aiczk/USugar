using System;
using System.Reflection;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Core code-generator tests. Ported from the LirToUasm tests when LIR was absorbed into the unified
/// Core IR. Targeted codegen checks that complement the end-to-end snapshot oracle.
/// </summary>
public class CoreToUasmTests
{
    [Fact]
    public void CoreToUasm_AcceptsOnlyVerifiedFlatModules()
    {
        var generate = Assert.Single(typeof(CoreToUasm).GetMethods(
            BindingFlags.Public | BindingFlags.Static));
        Assert.Equal(nameof(CoreToUasm.Generate), generate.Name);
        Assert.Equal(
            typeof(VerifiedFlatModule),
            Assert.Single(generate.GetParameters()).ParameterType);
    }

    [Fact]
    public void VerifiedFlatModule_FreezesTheCfgInPlace()
    {
        var module = new FlatModule(className: "Freeze");
        var field = new FieldDecl("value", StorageTypes.Int32);
        module.Fields.Add(field);
        var function = module.AddFunction("freeze");
        function.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        var entry = function.NewBlock();
        entry.Instructions.Add(new CAssign(
            0, new CConst(7, StorageTypes.Int32)));
        var exit = function.NewBlock();
        entry.Terminator = new CJump(exit.Id);
        exit.Terminator = new CRet();

        var verified = VerifiedFlatModule.VerifyAndFreeze(module);

        var frozenFunction = Assert.Single(verified.Functions);
        Assert.Same(function, frozenFunction);
        Assert.Equal(2, frozenFunction.Blocks.Count);
        Assert.Single(frozenFunction.Entry.Instructions);
        Assert.Equal(exit.Id, Assert.IsType<CJump>(
            frozenFunction.Entry.Terminator).TargetBlockId);
        Assert.Throws<NotSupportedException>(
            () => entry.Instructions.Clear());
        Assert.Throws<InvalidOperationException>(
            () => entry.Terminator = new CJump(entry.Id));
        Assert.Throws<NotSupportedException>(
            () => function.Blocks.Clear());
        Assert.Throws<NotSupportedException>(
            () => module.Functions.Clear());
        Assert.Throws<InvalidOperationException>(
            () => function.NewSlot(
                StorageTypes.String, SlotClass.Scratch));
        Assert.Throws<InvalidOperationException>(
            () => field.Flags = FieldFlags.Export);
        Assert.Null(typeof(VerifiedFlatModule).Assembly.GetType(
            "VerifiedFlatFunction"));
        Assert.Null(typeof(VerifiedFlatModule).Assembly.GetType(
            "VerifiedFlatBlock"));
        Assert.Null(typeof(VerifiedFlatModule).Assembly.GetType(
            "VerifiedFieldDecl"));
    }

    [Fact]
    public void CoreToUasm_UnusedSlot_NotDeclaredInUasm()
    {
        // Slot that is never referenced should not appear as a UASM variable.
        var module = new FlatModule(className: "Test");
        var func = new FlatFunction("test", "_test");
        module.Functions.Add(func);

        // slot0 is used, slot1 is unused (coalesced away)
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Frame));
        func.Slots.Add(new SlotDecl(1, StorageTypes.String, SlotClass.Scratch));

        var block = func.NewBlock();
        // Only reference slot0
        block.Instructions.Add(new CAssign(0, new CConst(42, StorageTypes.Int32)));
        block.Terminator = new CRet(new CSlotRef(0, StorageTypes.Int32));
        func.ReturnSlots.Add(new ReturnSlot("__retval", StorageTypes.Int32));
        func.ReturnType = StorageTypes.Int32;

        var result = CoreToUasm.Generate(
            VerifiedFlatModule.VerifyAndFreeze(module));
        var uasm = result.Uasm;

        // slot0 should be declared (it's referenced)
        Assert.Contains("SystemInt32", uasm);
        // slot1 (SystemString Scratch) should NOT be declared — never referenced
        Assert.DoesNotContain("SystemString", uasm);
    }

    [Fact]
    public void ConstKey_DistinguishesClrKindsCollapsedToSystemObject()
    {
        Assert.NotEqual(ConstFormat.Key("SystemObject", null), ConstFormat.Key("SystemObject", "null"));
        Assert.NotEqual(ConstFormat.Key("SystemObject", 1), ConstFormat.Key("SystemObject", "1"));
        Assert.NotEqual(ConstFormat.Key("SystemSingle", 0f), ConstFormat.Key("SystemSingle", -0f));
        Assert.Equal(ConstFormat.Key("SystemObject", "same"), ConstFormat.Key("SystemObject", "same"));
    }

    [Fact]
    public void ConstKey_ReferenceConstantsUseExactIdentity()
    {
        var first = new object();
        var second = new object();

        Assert.Equal(ConstFormat.Key("SystemObject", first), ConstFormat.Key("SystemObject", first));
        Assert.NotEqual(ConstFormat.Key("SystemObject", first), ConstFormat.Key("SystemObject", second));
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData(1.5f, "1.5")]
    [InlineData(2.25, "2.25")]
    [InlineData(true, "True")]
    [InlineData("hi", "hi")]
    [InlineData(-42, "-42")]
    [InlineData(200, "200")]
    [InlineData('A', "A")]
    [InlineData(ulong.MaxValue, "18446744073709551615")]
    public void ConstFormat_Value_PinsUasmSerializationFormat(object value, string expected)
    {
        Assert.Equal(expected, ConstFormat.Value(value));
    }

    [Fact]
    public void CoreBuilder_DoesNotDeduplicateCollidingObjectRenderings()
    {
        var builder = new CoreBuilder(
            new FlatModule(className: "ConstCollision"));
        var nullValue = builder.Const(null, StorageTypes.Object);
        var nullString = builder.Const("null", StorageTypes.Object);
        var boxedOne = builder.Const(1, StorageTypes.Object);
        var stringOne = builder.Const("1", StorageTypes.Object);

        Assert.NotSame(nullValue, nullString);
        Assert.NotSame(boxedOne, stringOne);
    }
}
