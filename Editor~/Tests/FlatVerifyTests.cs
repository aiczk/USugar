using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 2: FlatVerify negative tests — confirms the post-condition verifier rejects the
/// structural violations it can still be handed at runtime. With operand positions retyped to
/// CLeaf (re-arming the guarantee LIR's separate LOperand type gave for free), a nested call in a
/// call argument and a CFieldLoad in a CStoreField value are now compile-time-prevented — they can
/// no longer be constructed, so their former negative tests are gone. The remaining violations
/// (missing/dangling terminators, a CFieldAddr in a CAssign value, a structured statement leaking
/// into a flat block, a non-flat shape) flow through CValue positions and stay runtime-checked.
/// The positive case (well-formed CoreFlatten output passes) is exercised inside CoreFlattenTests
/// across all control-flow shapes.
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

    // NestedCallArg and LoadFieldRefAsOperand violations are now compile-time-prevented: CExternCall.Args
    // is List<CLeaf> and CStoreField.Value is CLeaf, so neither a nested CExternCall nor a CFieldLoad can be
    // placed in those positions. The runtime guard in FlatVerify.RequireLeaf remains as a defensive backstop.

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
