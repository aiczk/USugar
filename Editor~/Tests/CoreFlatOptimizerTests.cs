using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Flat (post-flatten) Core IR optimizer tests: CoalesceSlots over flat CBlocks (the only retained flat
/// pass). A flat value-producing call is a <see cref="CExprStmt"/> wrapping a <see cref="CExternCall"/>
/// whose DestSlot is set; everything else mirrors the former LIR shape.
/// </summary>
public class CoreFlatOptimizerTests
{
    // ── Helpers ──

    static CModule MakeModule(CFunction func)
    {
        var module = new CModule { ClassName = "Test" };
        module.Functions.Add(func);
        return module;
    }

    static CFunction MakeFunc(string name = "test") => new(name) { Shape = Shape.Flat };

    static CExprStmt Call(int? dest, string sig, List<CLeaf> args, string retType) =>
        new(new CExternCall(
            TestHelper.BindExtern(sig), args, new StorageType(retType), dest));

    // ========================================================================
    // Slot Coalescing
    // ========================================================================

    [Fact]
    public void Coalesce_NonOverlapping_Merged()
    {
        // slot0 (Scratch, Int32): def at inst 0, used at inst 1
        // slot1 (Scratch, Int32): def at inst 2, used at inst 3
        // Non-overlapping → merged to same ID
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));            // pos 0: def slot0
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, StorageTypes.Int32)));    // pos 1: use slot0 (last use)
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));            // pos 2: def slot1
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, StorageTypes.Int32)));    // pos 3: use slot1 (last use)
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot1 should be remapped to slot0 (non-overlapping, same type, same class)
        // Check that the third instruction writes to slot0
        var move2 = Assert.IsType<CAssign>(bb0.Stmts[2]);
        Assert.Equal(0, move2.DestSlot);

        // And the fourth instruction reads slot0
        var store2 = Assert.IsType<CStoreField>(bb0.Stmts[3]);
        var sr = Assert.IsType<CSlotRef>(store2.Value);
        Assert.Equal(0, sr.SlotId);

        // Slot list retains both entries (positional indexing), but slot1 is unused
        Assert.Equal(2, func.Slots.Count);
    }

    [Fact]
    public void Coalesce_RepresentationCopyReadPreservesInterference()
    {
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, StorageTypes.String, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));
        bb0.Stmts.Add(new CRepresentationCopy(
            2,
            new CSlotRef(0, StorageTypes.Int32),
            StorageTypes.String,
            RepresentationCastKind.ClosedGenericObjectCast));
        bb0.Stmts.Add(new CStoreField("number", new CSlotRef(1, StorageTypes.Int32)));
        bb0.Terminator = new CRet();

        CoreFlatOptimizer.CoalesceSlots(MakeModule(func));

        Assert.NotEqual(
            ((CAssign)bb0.Stmts[0]).DestSlot,
            ((CAssign)bb0.Stmts[1]).DestSlot);
    }

    [Fact]
    public void Coalesce_RepresentationCopyRemapsSourceSlot()
    {
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, StorageTypes.String, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("first", new CSlotRef(0, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));
        bb0.Stmts.Add(new CRepresentationCopy(
            2,
            new CSlotRef(1, StorageTypes.Int32),
            StorageTypes.String,
            RepresentationCastKind.ClosedGenericObjectCast));
        bb0.Terminator = new CRet();

        CoreFlatOptimizer.CoalesceSlots(MakeModule(func));

        var copy = Assert.IsType<CRepresentationCopy>(bb0.Stmts[3]);
        Assert.Equal(0, Assert.IsType<CSlotRef>(copy.Source).SlotId);
    }

    [Fact]
    public void Coalesce_Overlapping_Kept()
    {
        // slot0 and slot1 are both live at the same time → must not merge
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));            // pos 0: def slot0
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));            // pos 1: def slot1 (slot0 still live)
        bb0.Stmts.Add(Call(null, "Foo__SystemVoid",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32), new CSlotRef(1, StorageTypes.Int32) },
            "SystemVoid"));                                                       // pos 2: use both
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // Both slots must remain (overlapping lifetimes)
        Assert.Equal(2, func.Slots.Count);

        // Instruction operands should still reference different slots
        var es = Assert.IsType<CExprStmt>(bb0.Stmts[2]);
        var call = Assert.IsType<CExternCall>(es.Expr);
        var ids = call.Args.OfType<CSlotRef>().Select(s => s.SlotId).Distinct().ToList();
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Coalesce_DifferentTypes_NotMerged()
    {
        // slot0 (Int32) and slot1 (Boolean): non-overlapping but different types → separate
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Boolean, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(42, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(true, StorageTypes.Boolean)));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, StorageTypes.Boolean)));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // Both slots must remain (different types)
        Assert.Equal(2, func.Slots.Count);
    }

    [Fact]
    public void Coalesce_Pinned_NeverMerged()
    {
        // Two Pinned slots with non-overlapping lifetimes → never coalesced
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Pinned, "__param_x"));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Pinned, "__param_y"));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, StorageTypes.Int32)));
        bb0.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // Both Pinned slots preserved
        Assert.Equal(2, func.Slots.Count);
        Assert.All(func.Slots, s => Assert.Equal(SlotClass.Pinned, s.Class));

        // Operands unchanged
        var move1 = Assert.IsType<CAssign>(bb0.Stmts[0]);
        Assert.Equal(0, move1.DestSlot);
        var move2 = Assert.IsType<CAssign>(bb0.Stmts[2]);
        Assert.Equal(1, move2.DestSlot);
    }

    [Fact]
    public void Coalesce_LoopHeaderRedefinedSlot_MergesAcrossDeadBody()
    {
        // slot0 is loaded at the TOP of the header and read only by the header's branch; the body reads
        // and writes only slot1. The back-edge re-enters the header, which RE-DEFINES slot0 before any
        // read — so slot0's value from iteration N is never observed in the body or in iteration N+1. Its
        // true live range is [header load, header branch] only; it is DEAD across the body. Exact-liveness
        // coalescing therefore MERGES slot0 and slot1 (non-overlapping lifetimes), and VerifyNoInterference
        // (which shares that liveness) agrees — the merge is runtime-safe.
        //
        // (This test previously asserted NON-merge on the false premise that a back-edge makes slot0 "live
        // throughout the body". That premise is wrong: a slot the header re-defines each iteration is not
        // loop-carried. The genuinely-loop-carried hazard is pinned separately by
        // Coalesce_LoopCarriedSlot_LiveAcrossBody_DoesNotMerge below.)
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));

        var header = func.NewBlock(); // bb0
        var body = func.NewBlock();   // bb1
        var exit = func.NewBlock();   // bb2

        // header: slot0 = condition, branch on slot0
        header.Stmts.Add(new CLoadField(0, "cond", StorageTypes.Int32));
        header.Terminator = new CBranch(new CSlotRef(0, StorageTypes.Int32), body.Id, exit.Id);

        // body: slot1 = 42, use slot1, jump back to header
        body.Stmts.Add(new CAssign(1, new CConst(42, StorageTypes.Int32)));
        body.Stmts.Add(new CStoreField("result", new CSlotRef(1, StorageTypes.Int32)));
        body.Terminator = new CJump(header.Id); // back-edge

        // exit: return
        exit.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot0 dead across the body → slot1 coalesces onto slot0's physical slot.
        var bodyMove = Assert.IsType<CAssign>(body.Stmts[0]);
        Assert.Equal(0, bodyMove.DestSlot);
    }

    [Fact]
    public void Coalesce_LoopCarriedSlot_LiveAcrossBody_DoesNotMerge()
    {
        // True-positive boundary (the other side of Coalesce_LoopHeaderRedefinedSlot_MergesAcrossDeadBody):
        // slot0 is defined ONCE in the preheader and read every iteration inside the body — it is genuinely
        // loop-carried and live across the whole loop, INCLUDING the point where slot1's lifetime begins
        // (slot1 = 42, before slot0 is read at "b"). slot0 and slot1 are therefore simultaneously live and
        // must NOT be coalesced onto one physical slot — merging would clobber the carried value.
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, StorageTypes.Boolean, SlotClass.Scratch));

        var pre = func.NewBlock();    // bb0 preheader
        var header = func.NewBlock(); // bb1
        var body = func.NewBlock();   // bb2
        var exit = func.NewBlock();   // bb3

        // preheader: slot0 = base (defined once, never redefined)
        pre.Stmts.Add(new CLoadField(0, "base", StorageTypes.Int32));
        pre.Terminator = new CJump(header.Id);

        // header: slot2 = cond; branch
        header.Stmts.Add(new CLoadField(2, "cond", StorageTypes.Boolean));
        header.Terminator = new CBranch(new CSlotRef(2, StorageTypes.Boolean), body.Id, exit.Id);

        // body: slot1 = 42; use slot1; then READ slot0 (carried) → both live at once; back-edge
        body.Stmts.Add(new CAssign(1, new CConst(42, StorageTypes.Int32)));
        body.Stmts.Add(new CStoreField("a", new CSlotRef(1, StorageTypes.Int32)));
        body.Stmts.Add(new CStoreField("b", new CSlotRef(0, StorageTypes.Int32)));
        body.Terminator = new CJump(header.Id); // back-edge → slot0 live across body

        exit.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot0 live across the body while slot1 is live → distinct physical slots.
        var bodyMove = Assert.IsType<CAssign>(body.Stmts[0]);      // slot1 def
        var carriedRead = Assert.IsType<CStoreField>(body.Stmts[2]); // slot0 read
        var carriedRef = Assert.IsType<CSlotRef>(carriedRead.Value);
        Assert.NotEqual(carriedRef.SlotId, bodyMove.DestSlot);
    }

    [Fact]
    public void Coalesce_LoopWithNonOverlapping_StillMerges()
    {
        // slot0 defined and used only in body (not in header)
        // slot1 defined and used only after loop
        // These should still merge even with a loop present
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, StorageTypes.Boolean, SlotClass.Scratch));

        var header = func.NewBlock();
        var body = func.NewBlock();
        var exit = func.NewBlock();

        header.Stmts.Add(new CLoadField(2, "flag", StorageTypes.Boolean));
        header.Terminator = new CBranch(new CSlotRef(2, StorageTypes.Boolean), body.Id, exit.Id);

        // body: use slot0 entirely within body
        body.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));
        body.Stmts.Add(new CStoreField("x", new CSlotRef(0, StorageTypes.Int32)));
        body.Terminator = new CJump(header.Id);

        // exit: use slot1 entirely after loop
        exit.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));
        exit.Stmts.Add(new CStoreField("y", new CSlotRef(1, StorageTypes.Int32)));
        exit.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot0 and slot1 CAN be merged (non-overlapping, one in body one after)
        // RPO visits exit before body, so slot1 (exit) gets the lower def position
        // and becomes the representative. slot0 (body) merges into slot1.
        var bodyMove = Assert.IsType<CAssign>(body.Stmts[0]);
        var exitMove = Assert.IsType<CAssign>(exit.Stmts[0]);
        Assert.Equal(exitMove.DestSlot, bodyMove.DestSlot); // merged to same slot
    }

    [Fact]
    public void Coalesce_RewritesOperands()
    {
        // Verify all instruction types get operands remapped after coalescing
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, StorageTypes.Boolean, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        // slot0: def and last use in first two instructions
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));            // def slot0
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, StorageTypes.Int32)));    // last use slot0

        // slot1: def after slot0 is dead → should coalesce to slot0
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));            // def slot1
        bb0.Stmts.Add(Call(null, "Bar__SystemVoid",
            new List<CLeaf> { new CSlotRef(1, StorageTypes.Int32) },
            "SystemVoid"));                                                       // use slot1 as arg

        // slot2 (Boolean): used in branch
        bb0.Stmts.Add(new CAssign(2, new CConst(true, StorageTypes.Boolean)));
        bb0.Terminator = new CBranch(new CSlotRef(2, StorageTypes.Boolean), bb1.Id, bb1.Id);

        // slot1 also used in return value in bb1
        bb1.Terminator = new CRet(new CSlotRef(1, StorageTypes.Int32));

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot1 should be remapped to slot0 (non-overlapping Int32 Scratch)
        // Verify CAssign dest rewritten
        var move2 = Assert.IsType<CAssign>(bb0.Stmts[2]);
        Assert.Equal(0, move2.DestSlot);

        // Verify extern-call arg rewritten
        var es = Assert.IsType<CExprStmt>(bb0.Stmts[3]);
        var call = Assert.IsType<CExternCall>(es.Expr);
        var argRef = Assert.IsType<CSlotRef>(call.Args[0]);
        Assert.Equal(0, argRef.SlotId);

        // Verify CRet value rewritten
        var ret = Assert.IsType<CRet>(bb1.Terminator);
        var retRef = Assert.IsType<CSlotRef>(ret.Value);
        Assert.Equal(0, retRef.SlotId);

        // Verify CBranch condition NOT rewritten (slot2 is Boolean, different type)
        var br = Assert.IsType<CBranch>(bb0.Terminator);
        var condRef = Assert.IsType<CSlotRef>(br.Cond);
        Assert.Equal(2, condRef.SlotId);
    }

    // ========================================================================
    // CoalesceSlots interference self-check (fast approximate allocator + sound independent checker)
    // ========================================================================
    // CoreFlatOptimizer.VerifyNoInterference is internal (not private) specifically so it can be
    // driven directly here with a hand-built BAD merge map — the real interval algorithm in
    // CoalesceSlotsFunc is not expected to ever produce one, so exercising the checker through the
    // public CoalesceSlots(module) entry point can't isolate it from "does the real algorithm merge
    // correctly" (already covered by the Coalesce_* tests above).

    [Fact]
    public void VerifyNoInterference_SimultaneouslyLiveSlotsMappedToSamePhysical_Throws()
    {
        // slot0 and slot1 are both read by the same call — genuinely, simultaneously live. A merge map
        // that (incorrectly) coalesces slot1 into slot0 must be rejected.
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));
        bb0.Stmts.Add(Call(null, "Foo__SystemVoid",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32), new CSlotRef(1, StorageTypes.Int32) },
            "SystemVoid"));
        bb0.Terminator = new CRet();

        var badMapping = new Dictionary<int, int> { { 1, 0 } };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CoreFlatOptimizer.VerifyNoInterference(func, badMapping));
        Assert.Contains("interference", ex.Message);
    }

    [Fact]
    public void VerifyNoInterference_NonOverlappingSlotsMappedToSamePhysical_Passes()
    {
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, StorageTypes.Int32)));
        bb0.Terminator = new CRet();

        var mapping = new Dictionary<int, int> { { 1, 0 } };
        CoreFlatOptimizer.VerifyNoInterference(func, mapping); // no throw
    }

    [Fact]
    public void VerifyNoInterference_EmptyMapping_NoOp()
    {
        // No merges proposed — must not even walk the function (also exercises the early return with
        // an otherwise-invalid func, i.e. no blocks).
        var func = MakeFunc();
        CoreFlatOptimizer.VerifyNoInterference(func, new Dictionary<int, int>()); // no throw
    }

    [Fact]
    public void Coalesce_NestedLoop_DualLevelCarriedAccumulators_NoInterferenceAndMergeCorrect()
    {
        // Outer loop carries `outerAcc` (slot0) across outer back-edges; inner loop carries `innerAcc`
        // (slot2) across inner back-edges and is reset every outer pass. A temp used only within a
        // single inner-loop iteration (slot4) must be free to coalesce; outerAcc/innerAcc themselves
        // (live across their own loop's back-edge) must never be merged with anything overlapping.
        //
        //   bb0 (preheader): outerAcc(0) = 0; i(1) = 0                         -> bb1
        //   bb1 (outer header): branch i < N                                   -> T:bb2 F:bb6
        //   bb2 (inner preheader): innerAcc(2) = 0; j(3) = 0                   -> bb3
        //   bb3 (inner header): branch j < M                                   -> T:bb4 F:bb5
        //   bb4 (inner body): tmp(4) = i + j; innerAcc = innerAcc + tmp; j++    -> bb3 (back-edge)
        //   bb5 (inner exit): outerAcc = outerAcc + innerAcc; i++              -> bb1 (back-edge)
        //   bb6 (exit): return outerAcc
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch)); // outerAcc
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch)); // i
        func.Slots.Add(new SlotDecl(2, StorageTypes.Int32, SlotClass.Scratch)); // innerAcc
        func.Slots.Add(new SlotDecl(3, StorageTypes.Int32, SlotClass.Scratch)); // j
        func.Slots.Add(new SlotDecl(4, StorageTypes.Int32, SlotClass.Scratch)); // per-iteration temp

        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        var bb2 = func.NewBlock();
        var bb3 = func.NewBlock();
        var bb4 = func.NewBlock();
        var bb5 = func.NewBlock();
        var bb6 = func.NewBlock();

        bb0.Stmts.Add(new CAssign(0, new CConst(0, StorageTypes.Int32)));
        bb0.Stmts.Add(new CAssign(1, new CConst(0, StorageTypes.Int32)));
        bb0.Terminator = new CJump(bb1.Id);

        bb1.Stmts.Add(Call(5, "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<CLeaf> { new CSlotRef(1, StorageTypes.Int32), new CConst(4, StorageTypes.Int32) }, "SystemBoolean"));
        bb1.Terminator = new CBranch(new CSlotRef(5, StorageTypes.Boolean), bb2.Id, bb6.Id);

        bb2.Stmts.Add(new CAssign(2, new CConst(0, StorageTypes.Int32)));
        bb2.Stmts.Add(new CAssign(3, new CConst(0, StorageTypes.Int32)));
        bb2.Terminator = new CJump(bb3.Id);

        bb3.Stmts.Add(Call(6, "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<CLeaf> { new CSlotRef(3, StorageTypes.Int32), new CConst(3, StorageTypes.Int32) }, "SystemBoolean"));
        bb3.Terminator = new CBranch(new CSlotRef(6, StorageTypes.Boolean), bb4.Id, bb5.Id);

        bb4.Stmts.Add(Call(4, "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { new CSlotRef(1, StorageTypes.Int32), new CSlotRef(3, StorageTypes.Int32) }, "SystemInt32"));
        bb4.Stmts.Add(Call(7, "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { new CSlotRef(2, StorageTypes.Int32), new CSlotRef(4, StorageTypes.Int32) }, "SystemInt32"));
        bb4.Stmts.Add(new CAssign(2, new CSlotRef(7, StorageTypes.Int32)));
        bb4.Stmts.Add(Call(8, "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { new CSlotRef(3, StorageTypes.Int32), new CConst(1, StorageTypes.Int32) }, "SystemInt32"));
        bb4.Stmts.Add(new CAssign(3, new CSlotRef(8, StorageTypes.Int32)));
        bb4.Terminator = new CJump(bb3.Id); // inner back-edge

        bb5.Stmts.Add(Call(9, "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32), new CSlotRef(2, StorageTypes.Int32) }, "SystemInt32"));
        bb5.Stmts.Add(new CAssign(0, new CSlotRef(9, StorageTypes.Int32)));
        bb5.Stmts.Add(Call(10, "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { new CSlotRef(1, StorageTypes.Int32), new CConst(1, StorageTypes.Int32) }, "SystemInt32"));
        bb5.Stmts.Add(new CAssign(1, new CSlotRef(10, StorageTypes.Int32)));
        bb5.Terminator = new CJump(bb1.Id); // outer back-edge

        bb6.Terminator = new CRet(new CSlotRef(0, StorageTypes.Int32));

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module); // must not throw (interference self-check runs inside)

        // outerAcc (slot0) and innerAcc (slot2) are each live across their own loop's back-edge for the
        // entire run — they must never end up mapped to the same physical slot as each other, nor as
        // the loop counters i (slot1) / j (slot3) that are simultaneously live alongside them. Since
        // CoalesceSlotsFunc rewrites operands in place, their post-coalesce identity is whatever id
        // their own defining CAssign (bb0/bb2's preheader inits) now carries.
        var outerAccDest = ((CAssign)bb0.Stmts[0]).DestSlot;
        var iDest = ((CAssign)bb0.Stmts[1]).DestSlot;
        var innerAccDest = ((CAssign)bb2.Stmts[0]).DestSlot;
        var jDest = ((CAssign)bb2.Stmts[1]).DestSlot;
        var ids = new HashSet<int> { outerAccDest, iDest, innerAccDest, jDest };
        Assert.Equal(4, ids.Count); // all four remain distinct physical slots
    }

    [Fact]
    public void Coalesce_DoWhile_BackEdgeKeepsCarriedValueDistinctFromBodyTemp()
    {
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch)); // carried accumulator
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch)); // body temp
        func.Slots.Add(new SlotDecl(2, StorageTypes.Boolean, SlotClass.Scratch)); // loop condition
        func.Slots.Add(new SlotDecl(3, StorageTypes.Int32, SlotClass.Scratch)); // addition result

        var entry = func.NewBlock();
        var body = func.NewBlock();
        var exit = func.NewBlock();

        entry.Stmts.Add(new CAssign(0, new CConst(0, StorageTypes.Int32)));
        entry.Terminator = new CJump(body.Id);

        body.Stmts.Add(new CAssign(1, new CConst(1, StorageTypes.Int32)));
        body.Stmts.Add(Call(3, "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32), new CSlotRef(1, StorageTypes.Int32) },
            "SystemInt32"));
        body.Stmts.Add(new CAssign(0, new CSlotRef(3, StorageTypes.Int32)));
        body.Stmts.Add(Call(2, "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32), new CConst(3, StorageTypes.Int32) },
            "SystemBoolean"));
        body.Terminator = new CBranch(new CSlotRef(2, StorageTypes.Boolean), body.Id, exit.Id);

        exit.Terminator = new CRet(new CSlotRef(0, StorageTypes.Int32));

        CoreFlatOptimizer.CoalesceSlots(MakeModule(func));

        var accumulator = ((CAssign)entry.Stmts[0]).DestSlot;
        var temp = ((CAssign)body.Stmts[0]).DestSlot;
        Assert.NotEqual(accumulator, temp);
    }

    [Fact]
    public void Coalesce_UsedBeforeDef_NotMergedWithEarlierNaivelyNonOverlappingSlot()
    {
        // Regression for the real bug VerifyNoInterference caught on Compat_GetComponentTest's
        // ExecuteTests (a GetComponent<UserDefinedType>() reflection-loop "not found" fallthrough that
        // never assigns its result slot, relying on the Udon VM's implicit zero/null heap default).
        // Minimal reproduction: slot0 is assigned on ONE branch arm only; the other arm falls straight
        // through to slot0's read without ever assigning it. slot1 is a DIFFERENT slot whose own
        // def+use window closes entirely before slot0's naive (first-CAssign) RPO-linear position — so
        // the interval algorithm's uncorrected heuristic would consider them non-overlapping and merge
        // them, even though slot0's TRUE live-in (via the bypass arm) reaches back to before slot1's
        // window and they are genuinely, simultaneously live.
        //
        //   bb0 (entry): cond(2)=true; slot1(1)=5; use slot1                -> bb1
        //   bb1 (branch header): branch cond                                 -> T:bb2 F:bb3
        //   bb2 (found): slot0(0) = 42                                       -> bb4
        //   bb3 (not found — never assigns slot0)                            -> bb4
        //   bb4 (merge): use slot0
        //
        // DFS visits bb2 (and its entire successor subtree, here just bb4) to completion before ever
        // visiting bb3, so bb3 precedes bb2 in RPO — exactly the ordering that let the real bug's
        // "found" block's write look later than a use it doesn't dominate.
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, StorageTypes.Int32, SlotClass.Scratch));  // used-before-def
        func.Slots.Add(new SlotDecl(1, StorageTypes.Int32, SlotClass.Scratch)); // earlier, naively non-overlapping
        func.Slots.Add(new SlotDecl(2, StorageTypes.Boolean, SlotClass.Scratch)); // branch condition

        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();
        var bb2 = func.NewBlock();
        var bb3 = func.NewBlock();
        var bb4 = func.NewBlock();

        bb0.Stmts.Add(new CAssign(2, new CConst(true, StorageTypes.Boolean)));
        bb0.Stmts.Add(new CAssign(1, new CConst(5, StorageTypes.Int32)));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(1, StorageTypes.Int32))); // slot1 dead right here
        bb0.Terminator = new CJump(bb1.Id);

        bb1.Terminator = new CBranch(new CSlotRef(2, StorageTypes.Boolean), bb2.Id, bb3.Id);

        bb2.Stmts.Add(new CAssign(0, new CConst(42, StorageTypes.Int32)));
        bb2.Terminator = new CJump(bb4.Id);

        bb3.Terminator = new CJump(bb4.Id); // never assigns slot0

        bb4.Stmts.Add(new CStoreField("result", new CSlotRef(0, StorageTypes.Int32)));
        bb4.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module); // must not throw

        // slot0 and slot1 are genuinely simultaneously live (slot0's true live-in reaches bb0, where
        // slot1 is defined and used) and must never end up sharing a physical slot.
        var slot0Dest = ((CAssign)bb2.Stmts[0]).DestSlot;
        var slot1Dest = ((CAssign)bb0.Stmts[1]).DestSlot;
        Assert.NotEqual(slot0Dest, slot1Dest);
    }
}
