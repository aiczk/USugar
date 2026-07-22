using Xunit;

namespace USugar.Tests;

/// <summary>
/// Core code-generator tests. Ported from the LirToUasm tests when LIR was absorbed into the unified
/// Core IR. Targeted codegen checks that complement the end-to-end snapshot oracle.
/// </summary>
public class CoreToUasmTests
{
    [Fact]
    public void CoreToUasm_UnusedSlot_NotDeclaredInUasm()
    {
        // Slot that is never referenced should not appear as a UASM variable.
        var module = new CModule { ClassName = "Test" };
        var func = new CFunction("test", "_test") { Shape = Shape.Flat };
        module.Functions.Add(func);

        // slot0 is used, slot1 is unused (coalesced away)
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Frame));
        func.Slots.Add(new SlotDecl(1, "SystemString", SlotClass.Scratch));

        var block = func.NewBlock();
        // Only reference slot0
        block.Stmts.Add(new CAssign(0, new CConst(42, "SystemInt32")));
        block.Terminator = new CRet(new CSlotRef(0, "SystemInt32"));
        func.ReturnSlots.Add(new ReturnSlot("__retval", "SystemInt32"));
        func.ReturnType = "SystemInt32";

        var result = CoreToUasm.Generate(module);
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
        var builder = new CoreBuilder(new CModule { ClassName = "ConstCollision" });
        var nullValue = builder.Const(null, "SystemObject");
        var nullString = builder.Const("null", "SystemObject");
        var boxedOne = builder.Const(1, "SystemObject");
        var stringOne = builder.Const("1", "SystemObject");

        Assert.NotSame(nullValue, nullString);
        Assert.NotSame(boxedOne, stringOne);
    }
}
