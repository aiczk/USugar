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

    // ConstFormat is the single source for constant-pool keys (hand-enumeration audit Tier-2: it
    // replaced three per-site copies in CoreBuilder.Const and CoreToUasm.GetConstVar). The key
    // decides pool partitioning and therefore the deterministic __const_{type}_{n} data-section
    // names — pin the format per value family so a drift shows up here before it reshuffles goldens.
    [Theory]
    [InlineData(null, "SystemObject", "SystemObject_null")]
    [InlineData(1.5f, "SystemSingle", "SystemSingle_1.5")]
    [InlineData(2.25, "SystemDouble", "SystemDouble_2.25")]
    [InlineData(true, "SystemBoolean", "SystemBoolean_True")]
    [InlineData("hi", "SystemString", "SystemString_hi")]
    [InlineData(-42, "SystemInt32", "SystemInt32_-42")]
    [InlineData(200, "SystemByte", "SystemByte_200")]
    [InlineData('A', "SystemChar", "SystemChar_A")]
    [InlineData(ulong.MaxValue, "SystemUInt64", "SystemUInt64_18446744073709551615")]
    public void ConstFormat_Key_PinsThePoolPartitioningFormat(object value, string type, string expected)
    {
        Assert.Equal(expected, ConstFormat.Key(type, value));
    }
}
