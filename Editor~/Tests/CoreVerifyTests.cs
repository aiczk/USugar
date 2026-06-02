using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Structured Core IR verifier tests: CoreVerify must reject malformed IR (undeclared slots, type
/// mismatches, non-boolean conditions, break outside a loop, return-type mismatches) and accept
/// valid IR. Ported from the HirVerifier tests when HIR was absorbed into the unified Core IR. These
/// cover the verifier's negative paths, which the snapshot oracle (valid programs only) cannot.
/// </summary>
public class CoreVerifyTests
{
    [Fact]
    public void Verifier_ValidFunction_Passes()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        var func = builder.BeginFunction("test");
        func.ReturnType = "SystemInt32";

        var slot = builder.AllocFrame("SystemInt32");
        builder.EmitAssign(slot, builder.Const(42, "SystemInt32"));
        builder.EmitReturn(builder.SlotRef(slot));

        CoreVerify.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_UndeclaredSlot_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.Body.Stmts.Add(new CAssign(99, new CConst(0, "SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_TypeMismatch_Throws()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame("SystemSingle");
        // Assign a string to a float slot — genuinely incompatible types
        builder.EmitAssign(slot, builder.Const("hello", "SystemString"));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_IfCondNotBoolean_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.NewSlot("SystemInt32", SlotClass.Frame);
        // if (intValue) — condition must be boolean
        func.Body.Stmts.Add(new CIf(
            new CSlotRef(0, "SystemInt32"),
            new CBlock(),
            new CBlock()));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_BreakOutsideLoop_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.Body.Stmts.Add(new CBreak());

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_BreakInsideLoop_Passes()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.NewSlot("SystemBoolean", SlotClass.Scratch);

        var body = new CBlock();
        body.Stmts.Add(new CBreak());
        func.Body.Stmts.Add(new CWhile(new CSlotRef(0, "SystemBoolean"), body));

        CoreVerify.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_ReturnTypeMismatch_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.ReturnType = "SystemSingle";
        func.Body.Stmts.Add(new CReturn(new CConst("hello", "SystemString")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_Int32ToEnumSlot_Passes()
    {
        // Enum types use Int32 underlying type — unknown types treated as potential enums
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame("MyCustomEnum");
        builder.EmitAssign(slot, builder.Const(1, "SystemInt32"));

        CoreVerify.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_Int32ToSingle_Throws()
    {
        // Single ← Int32 is not valid (was previously allowed by blanket Int32 check)
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame("SystemSingle");
        builder.EmitAssign(slot, builder.Const(1, "SystemInt32"));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_Int32ToDouble_Throws()
    {
        // Double ← Int32 is not valid
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame("SystemDouble");
        builder.EmitAssign(slot, builder.Const(1, "SystemInt32"));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    // ── Address-leaf placement (CFieldAddr is a CLeaf the TYPE allows anywhere, but it is only valid as
    //    an extern/internal out/ref argument; CoreVerify must reject it in any value position). ──

    [Fact]
    public void Verifier_FieldAddrAsStoreValue_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        // field = &otherField  — storing a heap address as a value is invalid
        func.Body.Stmts.Add(new CStoreField("x", new CFieldAddr("y", "SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldAddrAsReturnValue_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.ReturnType = "SystemInt32";
        func.Body.Stmts.Add(new CReturn(new CFieldAddr("y", "SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldAddrInSelectArm_Throws()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var cond = builder.AllocFrame("SystemBoolean");
        var dst = builder.AllocFrame("SystemInt32");
        // dst = cond ? &field : 0  — an address in a value-producing select arm is invalid
        builder.EmitAssign(dst, new CSelect(
            builder.SlotRef(cond), new CFieldAddr("y", "SystemInt32"),
            builder.Const(0, "SystemInt32"), "SystemInt32"));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldAddrAsExternArg_Passes()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        // SomeType.TryGet(out y) — a CFieldAddr IS valid as an out/ref extern argument
        func.Body.Stmts.Add(new CExprStmt(new CExternCall(
            "SomeType.__TryGet__SystemInt32Ref__SystemVoid",
            new List<CLeaf> { new CFieldAddr("y", "SystemInt32") }, "SystemVoid")));

        CoreVerify.Verify(module); // should not throw
    }
}
