using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

public class HirOptimizerTests
{
    // ── Helpers ──

    static (HModule module, HFunction func) MakeFunc()
    {
        var module = new HModule();
        var func = module.AddFunction("test");
        return (module, func);
    }

    static HExternCall IntAdd(HExpr left, HExpr right) =>
        new("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { left, right }, "SystemInt32");

    static HExternCall IntMul(HExpr left, HExpr right) =>
        new("SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { left, right }, "SystemInt32");

    static HExternCall IntDiv(HExpr left, HExpr right) =>
        new("SystemInt32.__op_Division__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { left, right }, "SystemInt32");

    static HExternCall BoolNeg(HExpr operand) =>
        new("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            new List<HExpr> { operand }, "SystemBoolean");

    static HConst IntConst(int v) => new(v, "SystemInt32");
    static HConst BoolConst(bool v) => new(v, "SystemBoolean");

    // ========================================================================
    // ConstantFold
    // ========================================================================

    [Fact]
    public void ConstantFold_IntAddition_FoldsToConst()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new HStoreField("x", IntAdd(IntConst(1), IntConst(2))));

        HirOptimizer.ConstantFold(module);

        var store = Assert.IsType<HStoreField>(func.Body.Stmts[0]);
        var value = Assert.IsType<HConst>(store.Value);
        Assert.Equal(3, value.Value);
    }

    [Fact]
    public void ConstantFold_BoolNegation_FoldsToConst()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new HStoreField("x", BoolNeg(BoolConst(true))));

        HirOptimizer.ConstantFold(module);

        var store = Assert.IsType<HStoreField>(func.Body.Stmts[0]);
        var value = Assert.IsType<HConst>(store.Value);
        Assert.Equal(false, value.Value);
    }

    [Fact]
    public void ConstantFold_NestedExpr_FoldsRecursively()
    {
        var (module, func) = MakeFunc();
        // (1 + 2) * 3
        func.Body.Stmts.Add(new HStoreField("x",
            IntMul(IntAdd(IntConst(1), IntConst(2)), IntConst(3))));

        HirOptimizer.ConstantFold(module);

        var store = Assert.IsType<HStoreField>(func.Body.Stmts[0]);
        var value = Assert.IsType<HConst>(store.Value);
        Assert.Equal(9, value.Value);
    }

    [Fact]
    public void ConstantFold_NonConstArgs_NotFolded()
    {
        var (module, func) = MakeFunc();
        var slot = func.NewSlot("SystemInt32", SlotClass.Frame);
        func.Body.Stmts.Add(new HStoreField("x",
            IntAdd(new HSlotRef(slot, "SystemInt32"), IntConst(1))));

        HirOptimizer.ConstantFold(module);

        var store = Assert.IsType<HStoreField>(func.Body.Stmts[0]);
        Assert.IsType<HExternCall>(store.Value);
    }

    [Fact]
    public void ConstantFold_IfConstTrue_EliminatesBranch()
    {
        var (module, func) = MakeFunc();
        var thenBlock = new HBlock();
        thenBlock.Stmts.Add(new HStoreField("x", IntConst(1)));
        var elseBlock = new HBlock();
        elseBlock.Stmts.Add(new HStoreField("x", IntConst(2)));

        func.Body.Stmts.Add(new HIf(BoolConst(true), thenBlock, elseBlock));

        HirOptimizer.ConstantFold(module);

        // The HIf should be replaced; the then-branch body survives
        Assert.Single(func.Body.Stmts);
        var remaining = func.Body.Stmts[0];
        // Single-stmt branch collapses to the stmt itself
        var store = Assert.IsType<HStoreField>(remaining);
        Assert.Equal("x", store.FieldName);
        var val = Assert.IsType<HConst>(store.Value);
        Assert.Equal(1, val.Value);
    }

    [Fact]
    public void ConstantFold_SelectConstCond_FoldsToValue()
    {
        // select(true, X, Y) → X
        var expr = new HSelect(BoolConst(true), IntConst(10), IntConst(20), "SystemInt32");

        var result = HirOptimizer.FoldExpr(expr);

        var c = Assert.IsType<HConst>(result);
        Assert.Equal(10, c.Value);
    }

    [Fact]
    public void ConstantFold_DivByZero_NotFolded()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new HStoreField("x", IntDiv(IntConst(1), IntConst(0))));

        HirOptimizer.ConstantFold(module);

        var store = Assert.IsType<HStoreField>(func.Body.Stmts[0]);
        // Should remain an extern call — not folded, not crashed
        Assert.IsType<HExternCall>(store.Value);
    }

    // ========================================================================
    // DeadCodeElimination
    // ========================================================================

    [Fact]
    public void DCE_AfterReturn_RemovesDeadCode()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new HReturn());
        func.Body.Stmts.Add(new HStoreField("x", IntConst(1)));

        HirOptimizer.DeadCodeElimination(module);

        Assert.Single(func.Body.Stmts);
        Assert.IsType<HReturn>(func.Body.Stmts[0]);
    }

    [Fact]
    public void DCE_AfterReturn_PreservesLabel()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new HReturn());
        func.Body.Stmts.Add(new HLabelStmt("target"));
        func.Body.Stmts.Add(new HStoreField("x", IntConst(1)));

        HirOptimizer.DeadCodeElimination(module);

        // return + label + store all survive (label restores reachability)
        Assert.Equal(3, func.Body.Stmts.Count);
        Assert.IsType<HReturn>(func.Body.Stmts[0]);
        Assert.IsType<HLabelStmt>(func.Body.Stmts[1]);
        Assert.IsType<HStoreField>(func.Body.Stmts[2]);
    }

    [Fact]
    public void DCE_EmptyIf_PureCond_Removed()
    {
        var (module, func) = MakeFunc();
        var slot = func.NewSlot("SystemBoolean", SlotClass.Frame);
        // if (slotRef) {} else {} — pure condition, empty branches
        func.Body.Stmts.Add(new HIf(
            new HSlotRef(slot, "SystemBoolean"),
            new HBlock(),
            new HBlock()));

        HirOptimizer.DeadCodeElimination(module);

        Assert.Empty(func.Body.Stmts);
    }

    [Fact]
    public void DCE_EmptyIf_ImpureCond_Kept()
    {
        var (module, func) = MakeFunc();
        // if (externCall()) {} else {} — impure condition, must keep
        func.Body.Stmts.Add(new HIf(
            new HExternCall("SomeType.__SomeMethod__SystemVoid__SystemBoolean",
                new List<HExpr>(), "SystemBoolean"),
            new HBlock(),
            new HBlock()));

        HirOptimizer.DeadCodeElimination(module);

        Assert.Single(func.Body.Stmts);
        Assert.IsType<HIf>(func.Body.Stmts[0]);
    }

    // ========================================================================
    // CopyPropagation
    // ========================================================================

    [Fact]
    public void CopyProp_TempConst_Propagated()
    {
        var (module, func) = MakeFunc();
        // store [__lcl_tmp_0] = const(42)
        func.Body.Stmts.Add(new HStoreField("__lcl_tmp_0", IntConst(42)));
        // store [result] = load [__lcl_tmp_0]
        func.Body.Stmts.Add(new HStoreField("result",
            new HLoadField("__lcl_tmp_0", "SystemInt32")));

        HirOptimizer.CopyPropagation(module);

        // The load should be replaced with the constant
        var store = Assert.IsType<HStoreField>(func.Body.Stmts[1]);
        Assert.Equal("result", store.FieldName);
        var val = Assert.IsType<HConst>(store.Value);
        Assert.Equal(42, val.Value);
    }

    [Fact]
    public void CopyProp_NonTemp_NotPropagated()
    {
        var (module, func) = MakeFunc();
        // store [userField] = const(42) — not a temp field
        func.Body.Stmts.Add(new HStoreField("userField", IntConst(42)));
        // store [result] = load [userField]
        func.Body.Stmts.Add(new HStoreField("result",
            new HLoadField("userField", "SystemInt32")));

        HirOptimizer.CopyPropagation(module);

        // Load should remain unchanged
        var store = Assert.IsType<HStoreField>(func.Body.Stmts[1]);
        Assert.IsType<HLoadField>(store.Value);
    }

    [Fact]
    public void CopyProp_MultipleWrites_NotPropagated()
    {
        var (module, func) = MakeFunc();
        // write temp twice
        func.Body.Stmts.Add(new HStoreField("__lcl_tmp_0", IntConst(42)));
        func.Body.Stmts.Add(new HStoreField("__lcl_tmp_0", IntConst(99)));
        // load
        func.Body.Stmts.Add(new HStoreField("result",
            new HLoadField("__lcl_tmp_0", "SystemInt32")));

        HirOptimizer.CopyPropagation(module);

        // Load should remain — multiple writes disqualify the candidate
        var store = Assert.IsType<HStoreField>(func.Body.Stmts[2]);
        Assert.IsType<HLoadField>(store.Value);
    }

    [Fact]
    public void CopyProp_NonConst_NotPropagated()
    {
        var (module, func) = MakeFunc();
        // store [__lcl_tmp_0] = load [y] — value is not HConst
        func.Body.Stmts.Add(new HStoreField("__lcl_tmp_0",
            new HLoadField("y", "SystemInt32")));
        // load [__lcl_tmp_0]
        func.Body.Stmts.Add(new HStoreField("result",
            new HLoadField("__lcl_tmp_0", "SystemInt32")));

        HirOptimizer.CopyPropagation(module);

        // Load should remain — only HConst values are propagated
        var store = Assert.IsType<HStoreField>(func.Body.Stmts[1]);
        Assert.IsType<HLoadField>(store.Value);
    }
}
