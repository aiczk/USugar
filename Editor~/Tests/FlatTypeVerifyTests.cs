using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class FlatTypeVerifyTests
{
    static CBlock Block(int id, List<CStmt> instructions, CTerminator terminator) =>
        new CBlock(instructions) { Id = id, Terminator = terminator };

    static void MakeFlat(CFunction function, params CBlock[] blocks)
    {
        function.Shape = Shape.Flat;
        foreach (var block in blocks) function.FlatBlocks.Add(block);
    }

    [Fact]
    public void ModuleVerifier_RejectsCopyIntoIncompatibleSlot()
    {
        var module = new CModule();
        var function = module.AddFunction("caller");
        function.NewSlot(StorageTypes.Int32, SlotClass.Scratch);
        MakeFlat(function, Block(0,
            new List<CStmt> { new CAssign(0, new CConst("bad", StorageTypes.String)) },
            new CRet()));

        var ex = Assert.Throws<VerificationException>(() => FlatVerify.Verify(module));
        Assert.Contains("CAssign", ex.Message);
    }

    [Fact]
    public void ModuleVerifier_RejectsInternalCallArgumentTypeMismatch()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("__callee_arg", StorageTypes.Int32));

        var callee = module.AddFunction("callee");
        callee.ParamFieldNames.Add("__callee_arg");
        callee.ReturnType = StorageTypes.Void;
        MakeFlat(callee, Block(0, new List<CStmt>(), new CRet()));

        var caller = module.AddFunction("caller");
        caller.ReturnType = StorageTypes.Void;
        MakeFlat(caller, Block(0,
            new List<CStmt>
            {
                new CExprStmt(new CInternalCall("callee",
                    new List<CLeaf> { new CConst("bad", StorageTypes.String) },
                    StorageTypes.Void)),
            },
            new CRet()));

        var ex = Assert.Throws<VerificationException>(() => FlatVerify.Verify(module));
        Assert.Contains("__callee_arg", ex.Message);
    }

    [Fact]
    public void ModuleVerifier_RejectsSelectArmMismatchAfterFlattening()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        var function = builder.BeginFunction("select");
        var condition = builder.AllocScratch(StorageTypes.Boolean);
        var result = builder.AllocScratch(StorageTypes.Int32);
        builder.EmitAssign(result, new CSelect(
            builder.SlotRef(condition),
            builder.Const("bad", StorageTypes.String),
            builder.Const(1, StorageTypes.Int32),
            StorageTypes.Int32));

        // Structured verification intentionally permits inheritance-compatible CSelect arms. Once
        // lowered to concrete COPY edges, the shared declared-relaxation rule can decide each arm.
        CoreVerify.Verify(module);
        CoreFlatten.Lower(function);

        var ex = Assert.Throws<VerificationException>(() => FlatVerify.Verify(module));
        Assert.Contains("CAssign", ex.Message);
    }

    [Fact]
    public void ModuleVerifier_AllowsDeclaredObjectUnboxingCopy()
    {
        var module = new CModule();
        var function = module.AddFunction("spill_reload");
        function.NewSlot(StorageTypes.Int32, SlotClass.Scratch);
        MakeFlat(function, Block(0,
            new List<CStmt> { new CAssign(0, new CConst(null, StorageTypes.Object)) },
            new CRet()));

        FlatVerify.Verify(module);
    }
}
