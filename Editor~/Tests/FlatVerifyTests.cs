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
/// The positive case (well-formed CoreFlatten output passes) has no dedicated unit-test file —
/// it is exercised by every compiling test, since IrPipeline runs FlatVerify on each compile.
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
        var f = Flat(Block(0, new List<CStmt> { new CAssign(0, new CConst(1, StorageTypes.Int32)) }, new CRet()));
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
            new List<CStmt> { new CAssign(0, new CFieldAddr("y", StorageTypes.Int32)) },
            new CRet()));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void StructuredStatementInFlatBlock_Throws()
    {
        var leaked = new CIf(new CSlotRef(0, StorageTypes.Boolean), new CBlock(), new CBlock());
        var f = Flat(Block(0, new List<CStmt> { leaked }, new CRet()));
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void NonFlatShape_Throws()
    {
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(new CFunction("f"))); // Shape defaults to Structured
    }

    // ── Reentrant-flag conservation (design §4.3) ──
    // CoreFlatten and CoalesceSlots/RemapInst REBUILD call instructions; a rebuild that drops the
    // Reentrant flag silently loses the dispatch-site recursion spill. FlatVerify must catch the
    // imbalance structurally: flat flag count must equal CFunction.ReentrantSiteCount.

    [Fact]
    public void ReentrantFlagLost_Throws()
    {
        // The function claims one Reentrant dispatch arm, but the flat stream carries none —
        // the signature of a rebuild pass that forgot to copy the flag.
        var f = Flat(Block(0,
            new List<CStmt> { new CExprStmt(new CInternalCall("__indirect",
                new List<CLeaf> { new CSlotRef(0, StorageTypes.UInt32) }, StorageTypes.Void)) },
            new CRet()));
        f.ReentrantSiteCount = 1;
        var ex = Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
        Assert.Contains("conservation", ex.Message);
    }

    [Fact]
    public void ReentrantFlagDuplicated_Throws()
    {
        // The inverse imbalance (more flags than registered sites) is equally a verifier error.
        var f = Flat(Block(0,
            new List<CStmt> { new CExprStmt(new CInternalCall("__indirect",
                new List<CLeaf> { new CSlotRef(0, StorageTypes.UInt32) }, StorageTypes.Void, null, reentrant: true)) },
            new CRet()));
        f.ReentrantSiteCount = 0;
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void ReentrantFlagBalanced_Passes()
    {
        // Balanced internal-call (self arm) + extern-call (cross arm) flags verify clean.
        var f = Flat(Block(0,
            new List<CStmt>
            {
                new CExprStmt(new CInternalCall("__indirect",
                    new List<CLeaf> { new CSlotRef(0, StorageTypes.UInt32) }, StorageTypes.Void, null, reentrant: true)),
                new CExprStmt(new CExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
                    new List<CLeaf> { new CSlotRef(1, StorageTypes.Object), new CSlotRef(2, StorageTypes.String) },
                    StorageTypes.Void, null, reentrant: true)),
            },
            new CRet()));
        f.ReentrantSiteCount = 2;
        FlatVerify.Verify(f); // no throw
    }

    // ── PreSpillStmts positional contract (wave-12 r2 [V1], CExternCall.PreSpillStmts) ──
    // A Reentrant SendCustomEvent's PreSpillStmts=N claims the N immediately-preceding statements are
    // its own void SetProgramVariable copy-ins (same flat block by construction), so
    // InsertRecursionSpillsFunc can pull them inside the spill window. Nothing confirmed that shape
    // before; these pin the checker against the exact violations a rebuild-pass regression would cause.

    static CExprStmt SetVar(int recvSlot, int strSlot) => new(new CExternCall(
        "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
        new List<CLeaf> { new CSlotRef(recvSlot, StorageTypes.Object), new CConst("p", StorageTypes.String), new CSlotRef(strSlot, StorageTypes.Object) },
        StorageTypes.Void));

    static CExprStmt SendEvent(int recvSlot, int nameSlot, int preSpillStmts) => new(new CExternCall(
        "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
        new List<CLeaf> { new CSlotRef(recvSlot, StorageTypes.Object), new CSlotRef(nameSlot, StorageTypes.String) },
        StorageTypes.Void, null, reentrant: true, preSpillStmts: preSpillStmts));

    [Fact]
    public void PreSpillStmts_ExceedsAvailablePrecedingStatements_Throws()
    {
        // PreSpillStmts=1 claims one preceding copy-in, but this is the first instruction in the block.
        var f = Flat(Block(0, new List<CStmt> { SendEvent(0, 1, preSpillStmts: 1) }, new CRet()));
        f.ReentrantSiteCount = 1;
        var ex = Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
        Assert.Contains("PreSpillStmts", ex.Message);
    }

    [Fact]
    public void PreSpillStmts_PrecedingStatementIsNotACopyIn_Throws()
    {
        // The statement right before the flagged dispatch is an ordinary CAssign, not a void
        // SetProgramVariable copy-in — exactly what a future edit inserting a statement between a
        // hand-emitted copy-in/dispatch pair (HandlerBase's interface-setter dispatch) would produce.
        var f = Flat(Block(0,
            new List<CStmt>
            {
                new CAssign(2, new CConst(1, StorageTypes.Int32)),
                SendEvent(0, 1, preSpillStmts: 1),
            },
            new CRet()));
        f.ReentrantSiteCount = 1;
        var ex = Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
        Assert.Contains("PreSpillStmts", ex.Message);
    }

    [Fact]
    public void PreSpillStmts_CopyInHasDestSlot_Throws()
    {
        // A copy-in-shaped call that (incorrectly) binds a result slot is not a void self-effecting
        // SetProgramVariable — the spill window's "no value survives this call" assumption would be wrong.
        var f = Flat(Block(0,
            new List<CStmt>
            {
                new CExprStmt(new CExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                    new List<CLeaf> { new CSlotRef(0, StorageTypes.Object), new CConst("p", StorageTypes.String), new CSlotRef(1, StorageTypes.Object) },
                    StorageTypes.Void, destSlot: 5)),
                SendEvent(0, 1, preSpillStmts: 1),
            },
            new CRet()));
        f.ReentrantSiteCount = 1;
        Assert.ThrowsAny<System.Exception>(() => FlatVerify.Verify(f));
    }

    [Fact]
    public void PreSpillStmts_CorrectShape_Passes()
    {
        var f = Flat(Block(0,
            new List<CStmt> { SetVar(0, 1), SendEvent(0, 2, preSpillStmts: 1) },
            new CRet()));
        f.ReentrantSiteCount = 1;
        FlatVerify.Verify(f); // no throw
    }

    [Fact]
    public void PreSpillStmts_MultiParamCorrectShape_Passes()
    {
        // Mirrors LowerCrossCall: N SetProgramVariable copy-ins back-to-back, then the flagged dispatch.
        var f = Flat(Block(0,
            new List<CStmt> { SetVar(0, 1), SetVar(0, 2), SetVar(0, 3), SendEvent(0, 4, preSpillStmts: 3) },
            new CRet()));
        f.ReentrantSiteCount = 1;
        FlatVerify.Verify(f); // no throw
    }
}
