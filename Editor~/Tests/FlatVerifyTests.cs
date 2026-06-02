using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 2: FlatVerify negative tests — confirms the post-condition verifier rejects the
/// structural violations that the unified CValue makes type-system-expressible (and which
/// LIR's separate LOperand type prevented for free). The positive case (well-formed CoreFlatten
/// output passes) is exercised inside CoreFlattenTests across all control-flow shapes.
/// </summary>
public class FlatVerifyTests
{
    static CFunction Flat(params CBlock[] blocks)
    {
        var f = new CFunction("f", "_f") { Shape = Shape.Flat };
        foreach (var b in blocks) f.FlatBlocks.Add(b);
        return f;
    }

    static CBlock Block(int id, List<CStmt> insts, CTerminator term)
        => new CBlock(insts) { Id = id, Terminator = term };

    [Fact]
    public void WellFormed_Passes()
    {
        var f = Flat(Block(0, new List<CStmt> { new CAssign(0, new CConst(1, "SystemInt32")) }, new CRet()));
        FlatVerify.Verify(f); // no throw
    }

    [Fact]
    public void MissingTerminator_Throws()
    {
        var b = new CBlock(new List<CStmt>()) { Id = 0 }; // Terminator left null
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(Flat(b)));
    }

    [Fact]
    public void DanglingJumpTarget_Throws()
    {
        var f = Flat(Block(0, new List<CStmt>(), new CJump(99)));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void NestedCallArg_Throws()
    {
        var call = new CExternCall("Outer",
            new List<CValue> { new CExternCall("Inner", new List<CValue>(), "SystemInt32") }, "SystemVoid", null);
        var f = Flat(Block(0, new List<CStmt> { new CExprStmt(call) }, new CRet()));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void LoadFieldRefAsOperand_Throws()
    {
        var f = Flat(Block(0,
            new List<CStmt> { new CStoreField("x", new CFieldLoad("y", "SystemInt32")) },
            new CRet()));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void AddrFieldRef_AsAssignValue_Throws_OnlyValidAsCallArg()
    {
        var f = Flat(Block(0,
            new List<CStmt> { new CAssign(0, new CFieldAddr("y", "SystemInt32")) },
            new CRet()));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void StructuredStatementInFlatBlock_Throws()
    {
        var leaked = new CIf(new CSlotRef(0, "SystemBoolean"), new CBlock(), new CBlock());
        var f = Flat(Block(0, new List<CStmt> { leaked }, new CRet()));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void NonFlatShape_Throws()
    {
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(new CFunction("f"))); // Shape defaults to Structured
    }
}
