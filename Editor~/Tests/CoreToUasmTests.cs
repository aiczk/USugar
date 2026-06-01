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
}
