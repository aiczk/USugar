using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class FlatTypeVerifyTests
{
    static FlatBlock Block(int id, List<IFlatInstruction> instructions, CTerminator terminator) =>
        new FlatBlock(id, instructions, terminator);

    static void MakeFlat(FlatFunction function, params FlatBlock[] blocks)
    {
        foreach (var block in blocks) function.Blocks.Add(block);
    }

    [Fact]
    public void ModuleVerifier_RejectsCopyIntoIncompatibleSlot()
    {
        var module = new FlatModule();
        var function = module.AddFunction("caller");
        function.NewSlot(StorageTypes.Int32, SlotClass.Scratch);
        MakeFlat(function, Block(0,
            new List<IFlatInstruction> { new CAssign(0, new CConst("bad", StorageTypes.String)) },
            new CRet()));

        var ex = Assert.Throws<VerificationException>(() => FlatVerify.Verify(module));
        Assert.Contains("CAssign", ex.Message);
    }

    [Fact]
    public void ModuleVerifier_RejectsInternalCallArgumentTypeMismatch()
    {
        var module = new FlatModule();
        module.Fields.Add(new FieldDecl("__callee_arg", StorageTypes.Int32));

        var callee = module.AddFunction("callee");
        callee.ParamFieldNames.Add("__callee_arg");
        callee.ReturnType = StorageTypes.Void;
        MakeFlat(callee, Block(0, new List<IFlatInstruction>(), new CRet()));

        var caller = module.AddFunction("caller");
        caller.ReturnType = StorageTypes.Void;
        MakeFlat(caller, Block(0,
            new List<IFlatInstruction>
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
    public void CfgBuilderRejectsSelectArmMismatchAtConstruction()
    {
        var module = new FlatModule(abiCatalog: TestHelper.RegistryFacts);
        var builder = new CoreBuilder(module);
        builder.BeginFunction("select");
        var condition = builder.AllocScratch(StorageTypes.Boolean);
        var ex = Assert.Throws<VerificationException>(() =>
            builder.Select(
                builder.SlotRef(condition),
                builder.Const("bad", StorageTypes.String),
                builder.Const(1, StorageTypes.Int32),
                StorageTypes.Int32));
        Assert.Contains("CAssign", ex.Message);
    }

    [Fact]
    public void ModuleVerifier_AllowsDeclaredObjectUnboxingCopy()
    {
        var module = new FlatModule();
        var function = module.AddFunction("spill_reload");
        function.NewSlot(StorageTypes.Int32, SlotClass.Scratch);
        MakeFlat(function, Block(0,
            new List<IFlatInstruction> { new CAssign(0, new CConst(null, StorageTypes.Object)) },
            new CRet()));

        FlatVerify.Verify(module);
    }

    [Fact]
    public void ModuleVerifier_RejectsUnknownFunctionReference()
    {
        var module = new FlatModule();
        var function = module.AddFunction("caller");
        function.NewSlot(StorageTypes.UInt32, SlotClass.Scratch);
        MakeFlat(function, Block(0,
            new List<IFlatInstruction>
            {
                new CAssign(0, new CFuncRef("missing")),
            },
            new CRet()));

        var ex = Assert.Throws<VerificationException>(
            () => FlatVerify.Verify(module));

        Assert.Contains("unknown function", ex.Message);
        Assert.Contains("missing", ex.Message);
    }
}
