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
        new(new CExternCall(sig, args, retType, dest));

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
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));            // pos 0: def slot0
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));    // pos 1: use slot0 (last use)
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));            // pos 2: def slot1
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, "SystemInt32")));    // pos 3: use slot1 (last use)
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
    public void Coalesce_Overlapping_Kept()
    {
        // slot0 and slot1 are both live at the same time → must not merge
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));            // pos 0: def slot0
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));            // pos 1: def slot1 (slot0 still live)
        bb0.Stmts.Add(Call(null, "Foo__SystemVoid",
            new List<CLeaf> { new CSlotRef(0, "SystemInt32"), new CSlotRef(1, "SystemInt32") },
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
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemBoolean", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(42, "SystemInt32")));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));
        bb0.Stmts.Add(new CAssign(1, new CConst(true, "SystemBoolean")));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, "SystemBoolean")));
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
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Pinned, "__param_x"));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Pinned, "__param_y"));

        var bb0 = func.NewBlock();
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));
        bb0.Stmts.Add(new CStoreField("f2", new CSlotRef(1, "SystemInt32")));
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
    public void Coalesce_LoopBackEdge_PreventsInvalidMerge()
    {
        // slot0 used in loop header, slot1 used in loop body
        // Back-edge from body to header → slot0 is live throughout body
        // Must NOT merge slot0 and slot1
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));

        var header = func.NewBlock(); // bb0
        var body = func.NewBlock();   // bb1
        var exit = func.NewBlock();   // bb2

        // header: slot0 = condition, branch on slot0
        header.Stmts.Add(new CLoadField(0, "cond", "SystemInt32"));
        header.Terminator = new CBranch(new CSlotRef(0, "SystemInt32"), body.Id, exit.Id);

        // body: slot1 = 42, use slot1, jump back to header
        body.Stmts.Add(new CAssign(1, new CConst(42, "SystemInt32")));
        body.Stmts.Add(new CStoreField("result", new CSlotRef(1, "SystemInt32")));
        body.Terminator = new CJump(header.Id); // back-edge

        // exit: return
        exit.Terminator = new CRet();

        var module = MakeModule(func);
        CoreFlatOptimizer.CoalesceSlots(module);

        // slot0 and slot1 must NOT be merged (slot0 alive through body via back-edge)
        var bodyMove = Assert.IsType<CAssign>(body.Stmts[0]);
        Assert.NotEqual(0, bodyMove.DestSlot); // slot1 must keep its own ID
    }

    [Fact]
    public void Coalesce_LoopWithNonOverlapping_StillMerges()
    {
        // slot0 defined and used only in body (not in header)
        // slot1 defined and used only after loop
        // These should still merge even with a loop present
        var func = MakeFunc();
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, "SystemBoolean", SlotClass.Scratch));

        var header = func.NewBlock();
        var body = func.NewBlock();
        var exit = func.NewBlock();

        header.Stmts.Add(new CLoadField(2, "flag", "SystemBoolean"));
        header.Terminator = new CBranch(new CSlotRef(2, "SystemBoolean"), body.Id, exit.Id);

        // body: use slot0 entirely within body
        body.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));
        body.Stmts.Add(new CStoreField("x", new CSlotRef(0, "SystemInt32")));
        body.Terminator = new CJump(header.Id);

        // exit: use slot1 entirely after loop
        exit.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));
        exit.Stmts.Add(new CStoreField("y", new CSlotRef(1, "SystemInt32")));
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
        func.Slots.Add(new SlotDecl(0, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(1, "SystemInt32", SlotClass.Scratch));
        func.Slots.Add(new SlotDecl(2, "SystemBoolean", SlotClass.Scratch));

        var bb0 = func.NewBlock();
        var bb1 = func.NewBlock();

        // slot0: def and last use in first two instructions
        bb0.Stmts.Add(new CAssign(0, new CConst(10, "SystemInt32")));            // def slot0
        bb0.Stmts.Add(new CStoreField("f1", new CSlotRef(0, "SystemInt32")));    // last use slot0

        // slot1: def after slot0 is dead → should coalesce to slot0
        bb0.Stmts.Add(new CAssign(1, new CConst(20, "SystemInt32")));            // def slot1
        bb0.Stmts.Add(Call(null, "Bar__SystemVoid",
            new List<CLeaf> { new CSlotRef(1, "SystemInt32") },
            "SystemVoid"));                                                       // use slot1 as arg

        // slot2 (Boolean): used in branch
        bb0.Stmts.Add(new CAssign(2, new CConst(true, "SystemBoolean")));
        bb0.Terminator = new CBranch(new CSlotRef(2, "SystemBoolean"), bb1.Id, bb1.Id);

        // slot1 also used in return value in bb1
        bb1.Terminator = new CRet(new CSlotRef(1, "SystemInt32"));

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
}
