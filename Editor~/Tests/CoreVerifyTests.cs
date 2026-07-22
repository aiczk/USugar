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
        func.ReturnType = new StorageType("SystemInt32");

        var slot = builder.AllocFrame(new StorageType("SystemInt32"));
        builder.EmitAssign(slot, builder.Const(42, new StorageType("SystemInt32")));
        builder.EmitReturn(builder.SlotRef(slot));

        CoreVerify.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_UndeclaredSlot_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.Body.Stmts.Add(new CAssign(99, new CConst(0, new StorageType("SystemInt32"))));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_TypeMismatch_Throws()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame(new StorageType("SystemSingle"));
        // Assign a string to a float slot — genuinely incompatible types
        builder.EmitAssign(slot, builder.Const("hello", new StorageType("SystemString")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_IfCondNotBoolean_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.NewSlot(new StorageType("SystemInt32"), SlotClass.Frame);
        // if (intValue) — condition must be boolean
        func.Body.Stmts.Add(new CIf(
            new CSlotRef(0, new StorageType("SystemInt32")),
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
        func.NewSlot(new StorageType("SystemBoolean"), SlotClass.Scratch);

        var body = new CBlock();
        body.Stmts.Add(new CBreak());
        func.Body.Stmts.Add(new CWhile(new CSlotRef(0, new StorageType("SystemBoolean")), body));

        CoreVerify.Verify(module); // should not throw
    }

    // ── Shape guard (mirrors FlatVerify.Verify's `f.Shape != Flat` check) ──
    // CoreVerify.VerifyFunction walks the STRUCTURED func.Body; CoreFlatten leaves that tree stale
    // once it sets Shape=Flat and populates FlatBlocks instead. Without a guard, re-running CoreVerify
    // on an already-flattened function silently "passes" by re-validating the frozen pre-flatten tree.

    [Fact]
    public void Verifier_FlattenedFunction_Throws()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        var func = builder.BeginFunction("test");
        func.ReturnType = new StorageType("SystemInt32");
        var slot = builder.AllocFrame(new StorageType("SystemInt32"));
        builder.EmitAssign(slot, builder.Const(42, new StorageType("SystemInt32")));
        builder.EmitReturn(builder.SlotRef(slot));

        CoreFlatten.Lower(func); // sets Shape = Flat; func.Body is now stale

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_ReturnTypeMismatch_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.ReturnType = new StorageType("SystemSingle");
        func.Body.Stmts.Add(new CReturn(new CConst("hello", new StorageType("SystemString"))));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_NonVoidReturnWithoutValue_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.ReturnType = new StorageType("SystemInt32");
        func.Body.Stmts.Add(new CReturn());

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_VoidReturnWithValue_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.ReturnType = new StorageType("SystemVoid");
        func.Body.Stmts.Add(new CReturn(new CConst(1, new StorageType("SystemInt32"))));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_UndeclaredFieldLoad_Throws()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame(new StorageType("SystemInt32"));
        builder.EmitAssign(slot, builder.LoadField("missing", new StorageType("SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldStoreTypeMismatch_Throws()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("value", new StorageType("SystemSingle")));
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        builder.EmitStoreField("value", builder.Const("bad", new StorageType("SystemString")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_DuplicateLabel_Throws()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        func.Body.Stmts.Add(new CLabel("same"));
        func.Body.Stmts.Add(new CLabel("same"));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_Int32ToEnumSlot_Passes()
    {
        // Enum slots interop with Int32 (Udon stores enums as their underlying type). Fact-backed like
        // every production slot type: only SDK enums keep their tag past the GetUdonTypeName minting
        // choke (user enums fold to the underlying type), and the choke records them as enum facts.
        var module = new CModule();
        module.TypeFacts.RecordForTest("CvFakeSdkEnum", isEnum: true, isValueType: true);
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame(new StorageType("CvFakeSdkEnum"));
        builder.EmitAssign(slot, builder.Const(1, new StorageType("SystemInt32")));

        CoreVerify.Verify(module); // should not throw
    }

    [Fact]
    public void Verifier_TypeFactsDoNotLeakBetweenModules()
    {
        var first = new CModule();
        first.TypeFacts.RecordForTest("CvCompilationLocalEnum", isEnum: true, isValueType: true);
        var firstBuilder = new CoreBuilder(first);
        firstBuilder.BeginFunction("first");
        firstBuilder.EmitAssign(firstBuilder.AllocFrame(new StorageType("CvCompilationLocalEnum")),
            firstBuilder.Const(1, new StorageType("SystemInt32")));
        CoreVerify.Verify(first);

        var second = new CModule();
        var secondBuilder = new CoreBuilder(second);
        secondBuilder.BeginFunction("second");
        secondBuilder.EmitAssign(secondBuilder.AllocFrame(new StorageType("CvCompilationLocalEnum")),
            secondBuilder.Const(1, new StorageType("SystemInt32")));

        var ex = Assert.Throws<VerificationException>(() => CoreVerify.Verify(second));
        Assert.Contains("no fact recorded for 'CvCompilationLocalEnum'", ex.Message);
    }

    [Fact]
    public void Verifier_Int32ToSingle_Throws()
    {
        // Single ← Int32 is not valid (was previously allowed by blanket Int32 check)
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame(new StorageType("SystemSingle"));
        builder.EmitAssign(slot, builder.Const(1, new StorageType("SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_Int32ToDouble_Throws()
    {
        // Double ← Int32 is not valid
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var slot = builder.AllocFrame(new StorageType("SystemDouble"));
        builder.EmitAssign(slot, builder.Const(1, new StorageType("SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    // ── Address-leaf placement (CFieldAddr is a CLeaf the TYPE allows anywhere, but it is only valid as
    //    an extern/internal out/ref argument; CoreVerify must reject it in any value position). ──

    [Fact]
    public void Verifier_FieldAddrAsStoreValue_Throws()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("x", new StorageType("SystemInt32")));
        module.Fields.Add(new FieldDecl("y", new StorageType("SystemInt32")));
        var func = module.AddFunction("test");
        // field = &otherField  — storing a heap address as a value is invalid
        func.Body.Stmts.Add(new CStoreField("x", new CFieldAddr("y", new StorageType("SystemInt32"))));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldAddrAsReturnValue_Throws()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("y", new StorageType("SystemInt32")));
        var func = module.AddFunction("test");
        func.ReturnType = new StorageType("SystemInt32");
        func.Body.Stmts.Add(new CReturn(new CFieldAddr("y", new StorageType("SystemInt32"))));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldAddrInSelectArm_Throws()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("y", new StorageType("SystemInt32")));
        var builder = new CoreBuilder(module);
        builder.BeginFunction("test");
        var cond = builder.AllocFrame(new StorageType("SystemBoolean"));
        var dst = builder.AllocFrame(new StorageType("SystemInt32"));
        // dst = cond ? &field : 0  — an address in a value-producing select arm is invalid
        builder.EmitAssign(dst, new CSelect(
            builder.SlotRef(cond), new CFieldAddr("y", new StorageType("SystemInt32")),
            builder.Const(0, new StorageType("SystemInt32")), new StorageType("SystemInt32")));

        Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
    }

    [Fact]
    public void Verifier_FieldAddrAsExternArg_Passes()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("y", new StorageType("SystemInt32")));
        var func = module.AddFunction("test");
        // SomeType.TryGet(out y) — a CFieldAddr IS valid as an out/ref extern argument
        func.Body.Stmts.Add(new CExprStmt(new CExternCall(
            "SomeType.__TryGet__SystemInt32Ref__SystemVoid",
            new List<CLeaf> { new CFieldAddr("y", new StorageType("SystemInt32")) }, new StorageType("SystemVoid"))));

        CoreVerify.Verify(module); // should not throw
    }
}
