using System.Collections.Generic;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Structured (pre-flatten) Core IR optimizer tests: ConstantFold, DeadCodeElimination and
/// CopyPropagation over CStmt/CValue. Ported from HirOptimizerTests when HIR was absorbed into the
/// unified Core IR. The snapshot oracle covers the pipeline end-to-end; these localize regressions
/// in the structured optimizer to a specific pass and statement shape.
/// </summary>
public class CoreOptimizerTests
{
    // ── Helpers ──

    static (CModule module, CFunction func) MakeFunc()
    {
        var module = new CModule();
        var func = module.AddFunction("test");
        return (module, func);
    }

    static CExternCall IntAdd(CValue left, CValue right) =>
        new("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { left, right }, "SystemInt32");

    static CExternCall IntMul(CValue left, CValue right) =>
        new("SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { left, right }, "SystemInt32");

    static CExternCall IntDiv(CValue left, CValue right) =>
        new("SystemInt32.__op_Division__SystemInt32_SystemInt32__SystemInt32",
            new List<CValue> { left, right }, "SystemInt32");

    static CExternCall BoolNeg(CValue operand) =>
        new("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            new List<CValue> { operand }, "SystemBoolean");

    static CConst IntConst(int v) => new(v, "SystemInt32");
    static CConst BoolConst(bool v) => new(v, "SystemBoolean");

    // ========================================================================
    // ConstantFold
    // ========================================================================

    [Fact]
    public void ConstantFold_IntAddition_FoldsToConst()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new CStoreField("x", IntAdd(IntConst(1), IntConst(2))));

        CoreOptimizer.ConstantFold(module);

        var store = Assert.IsType<CStoreField>(func.Body.Stmts[0]);
        var value = Assert.IsType<CConst>(store.Value);
        Assert.Equal(3, value.Value);
    }

    [Fact]
    public void ConstantFold_BoolNegation_FoldsToConst()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new CStoreField("x", BoolNeg(BoolConst(true))));

        CoreOptimizer.ConstantFold(module);

        var store = Assert.IsType<CStoreField>(func.Body.Stmts[0]);
        var value = Assert.IsType<CConst>(store.Value);
        Assert.Equal(false, value.Value);
    }

    [Fact]
    public void ConstantFold_NestedExpr_FoldsRecursively()
    {
        var (module, func) = MakeFunc();
        // (1 + 2) * 3
        func.Body.Stmts.Add(new CStoreField("x",
            IntMul(IntAdd(IntConst(1), IntConst(2)), IntConst(3))));

        CoreOptimizer.ConstantFold(module);

        var store = Assert.IsType<CStoreField>(func.Body.Stmts[0]);
        var value = Assert.IsType<CConst>(store.Value);
        Assert.Equal(9, value.Value);
    }

    [Fact]
    public void ConstantFold_NonConstArgs_NotFolded()
    {
        var (module, func) = MakeFunc();
        var slot = func.NewSlot("SystemInt32", SlotClass.Frame);
        func.Body.Stmts.Add(new CStoreField("x",
            IntAdd(new CSlotRef(slot, "SystemInt32"), IntConst(1))));

        CoreOptimizer.ConstantFold(module);

        var store = Assert.IsType<CStoreField>(func.Body.Stmts[0]);
        Assert.IsType<CExternCall>(store.Value);
    }

    [Fact]
    public void ConstantFold_IfConstTrue_EliminatesBranch()
    {
        var (module, func) = MakeFunc();
        var thenBlock = new CBlock();
        thenBlock.Stmts.Add(new CStoreField("x", IntConst(1)));
        var elseBlock = new CBlock();
        elseBlock.Stmts.Add(new CStoreField("x", IntConst(2)));

        func.Body.Stmts.Add(new CIf(BoolConst(true), thenBlock, elseBlock));

        CoreOptimizer.ConstantFold(module);

        // The CIf should be replaced; the then-branch body survives
        Assert.Single(func.Body.Stmts);
        var remaining = func.Body.Stmts[0];
        // Single-stmt branch collapses to the stmt itself
        var store = Assert.IsType<CStoreField>(remaining);
        Assert.Equal("x", store.FieldName);
        var val = Assert.IsType<CConst>(store.Value);
        Assert.Equal(1, val.Value);
    }

    [Fact]
    public void ConstantFold_SelectConstCond_FoldsToValue()
    {
        // select(true, X, Y) → X
        var expr = new CSelect(BoolConst(true), IntConst(10), IntConst(20), "SystemInt32");

        var result = CoreOptimizer.FoldExpr(expr);

        var c = Assert.IsType<CConst>(result);
        Assert.Equal(10, c.Value);
    }

    [Fact]
    public void ConstantFold_DivByZero_NotFolded()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new CStoreField("x", IntDiv(IntConst(1), IntConst(0))));

        CoreOptimizer.ConstantFold(module);

        var store = Assert.IsType<CStoreField>(func.Body.Stmts[0]);
        // Should remain an extern call — not folded, not crashed
        Assert.IsType<CExternCall>(store.Value);
    }

    // ========================================================================
    // DeadCodeElimination
    // ========================================================================

    [Fact]
    public void DCE_AfterReturn_RemovesDeadCode()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new CReturn());
        func.Body.Stmts.Add(new CStoreField("x", IntConst(1)));

        CoreOptimizer.DeadCodeElimination(module);

        Assert.Single(func.Body.Stmts);
        Assert.IsType<CReturn>(func.Body.Stmts[0]);
    }

    [Fact]
    public void DCE_AfterReturn_PreservesLabel()
    {
        var (module, func) = MakeFunc();
        func.Body.Stmts.Add(new CReturn());
        func.Body.Stmts.Add(new CLabel("target"));
        func.Body.Stmts.Add(new CStoreField("x", IntConst(1)));

        CoreOptimizer.DeadCodeElimination(module);

        // return + label + store all survive (label restores reachability)
        Assert.Equal(3, func.Body.Stmts.Count);
        Assert.IsType<CReturn>(func.Body.Stmts[0]);
        Assert.IsType<CLabel>(func.Body.Stmts[1]);
        Assert.IsType<CStoreField>(func.Body.Stmts[2]);
    }

    [Fact]
    public void DCE_EmptyIf_PureCond_Removed()
    {
        var (module, func) = MakeFunc();
        var slot = func.NewSlot("SystemBoolean", SlotClass.Frame);
        // if (slotRef) {} else {} — pure condition, empty branches
        func.Body.Stmts.Add(new CIf(
            new CSlotRef(slot, "SystemBoolean"),
            new CBlock(),
            new CBlock()));

        CoreOptimizer.DeadCodeElimination(module);

        Assert.Empty(func.Body.Stmts);
    }

    [Fact]
    public void DCE_EmptyIf_ImpureCond_Kept()
    {
        var (module, func) = MakeFunc();
        // if (externCall()) {} else {} — impure condition, must keep
        func.Body.Stmts.Add(new CIf(
            new CExternCall("SomeType.__SomeMethod__SystemVoid__SystemBoolean",
                new List<CValue>(), "SystemBoolean"),
            new CBlock(),
            new CBlock()));

        CoreOptimizer.DeadCodeElimination(module);

        Assert.Single(func.Body.Stmts);
        Assert.IsType<CIf>(func.Body.Stmts[0]);
    }

    // ========================================================================
    // CopyPropagation
    // ========================================================================

    [Fact]
    public void CopyProp_TempConst_Propagated()
    {
        var (module, func) = MakeFunc();
        // store [__lcl_tmp_0] = const(42)
        func.Body.Stmts.Add(new CStoreField("__lcl_tmp_0", IntConst(42)));
        // store [result] = load [__lcl_tmp_0]
        func.Body.Stmts.Add(new CStoreField("result",
            new CFieldRef("__lcl_tmp_0", "SystemInt32", CFieldMode.Load)));

        CoreOptimizer.CopyPropagation(module);

        // The load should be replaced with the constant
        var store = Assert.IsType<CStoreField>(func.Body.Stmts[1]);
        Assert.Equal("result", store.FieldName);
        var val = Assert.IsType<CConst>(store.Value);
        Assert.Equal(42, val.Value);
    }

    [Fact]
    public void CopyProp_NonTemp_NotPropagated()
    {
        var (module, func) = MakeFunc();
        // store [userField] = const(42) — not a temp field
        func.Body.Stmts.Add(new CStoreField("userField", IntConst(42)));
        // store [result] = load [userField]
        func.Body.Stmts.Add(new CStoreField("result",
            new CFieldRef("userField", "SystemInt32", CFieldMode.Load)));

        CoreOptimizer.CopyPropagation(module);

        // Load should remain unchanged
        var store = Assert.IsType<CStoreField>(func.Body.Stmts[1]);
        Assert.IsType<CFieldRef>(store.Value);
    }

    [Fact]
    public void CopyProp_MultipleWrites_NotPropagated()
    {
        var (module, func) = MakeFunc();
        // write temp twice
        func.Body.Stmts.Add(new CStoreField("__lcl_tmp_0", IntConst(42)));
        func.Body.Stmts.Add(new CStoreField("__lcl_tmp_0", IntConst(99)));
        // load
        func.Body.Stmts.Add(new CStoreField("result",
            new CFieldRef("__lcl_tmp_0", "SystemInt32", CFieldMode.Load)));

        CoreOptimizer.CopyPropagation(module);

        // Load should remain — multiple writes disqualify the candidate
        var store = Assert.IsType<CStoreField>(func.Body.Stmts[2]);
        Assert.IsType<CFieldRef>(store.Value);
    }

    [Fact]
    public void CopyProp_NonConst_NotPropagated()
    {
        var (module, func) = MakeFunc();
        // store [__lcl_tmp_0] = load [y] — value is not CConst
        func.Body.Stmts.Add(new CStoreField("__lcl_tmp_0",
            new CFieldRef("y", "SystemInt32", CFieldMode.Load)));
        // load [__lcl_tmp_0]
        func.Body.Stmts.Add(new CStoreField("result",
            new CFieldRef("__lcl_tmp_0", "SystemInt32", CFieldMode.Load)));

        CoreOptimizer.CopyPropagation(module);

        // Load should remain — only CConst values are propagated
        var store = Assert.IsType<CStoreField>(func.Body.Stmts[1]);
        Assert.IsType<CFieldRef>(store.Value);
    }
}
