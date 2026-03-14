using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// HIR constant folding optimizer.
/// Replaces pure extern calls on constant arguments with computed constant results,
/// and simplifies HSelect with constant boolean conditions.
/// </summary>
public static class HirOptimizer
{
    static readonly Dictionary<string, Func<List<HConst>, object>> FoldableExterns = BuildFoldTable();

    public static void ConstantFold(HModule module)
    {
        foreach (var func in module.Functions)
            FoldBlock(func.Body);
    }

    static void FoldBlock(HBlock block)
    {
        for (int i = 0; i < block.Stmts.Count; i++)
            block.Stmts[i] = FoldStmt(block.Stmts[i]);
    }

    static HStmt FoldStmt(HStmt stmt)
    {
        switch (stmt)
        {
            case HAssign a:
                return new HAssign(a.DestSlot, FoldExpr(a.Value));

            case HStoreField sf:
                return new HStoreField(sf.FieldName, FoldExpr(sf.Value));

            case HIf hif:
            {
                var cond = FoldExpr(hif.Cond);
                if (cond is HConst { Value: bool b })
                {
                    // Dead branch elimination: replace HIf with the live branch
                    var live = b ? hif.Then : hif.Else;
                    FoldBlock(live);
                    return live.Stmts.Count == 1 ? live.Stmts[0] : new HBlock(live.Stmts);
                }
                FoldBlock(hif.Then);
                FoldBlock(hif.Else);
                return new HIf(cond, hif.Then, hif.Else);
            }

            case HWhile hw:
                FoldBlock(hw.CondBlock);
                FoldBlock(hw.Body);
                return new HWhile(FoldExpr(hw.Cond), hw.Body, hw.IsDoWhile, hw.CondBlock);

            case HFor hf:
                FoldBlock(hf.Init);
                FoldBlock(hf.CondBlock);
                FoldBlock(hf.Update);
                FoldBlock(hf.Body);
                return new HFor(hf.Init, hf.Cond != null ? FoldExpr(hf.Cond) : null, hf.Update, hf.Body, hf.CondBlock);

            case HReturn hr:
                return hr.Value != null ? new HReturn(FoldExpr(hr.Value)) : hr;

            case HExprStmt es:
                return new HExprStmt(FoldExpr(es.Expr));

            case HBlock blk:
                FoldBlock(blk);
                return blk;

            // HBreak, HContinue, HGoto, HLabelStmt — no expressions to fold
            default:
                return stmt;
        }
    }

    /// <summary>
    /// Bottom-up expression folding. Folds children first, then attempts
    /// to evaluate the current node if all inputs are constant.
    /// Visible internally for testing.
    /// </summary>
    internal static HExpr FoldExpr(HExpr expr)
    {
        switch (expr)
        {
            case HExternCall call:
            {
                var foldedArgs = call.Args.Select(FoldExpr).ToList();
                if (foldedArgs.All(a => a is HConst) && TryEval(call.Sig, foldedArgs, out var result))
                    return result;
                return new HExternCall(call.Sig, foldedArgs, call.Type);
            }

            case HSelect sel:
            {
                var cond = FoldExpr(sel.Cond);
                if (cond is HConst { Value: bool b })
                    return b ? FoldExpr(sel.TrueVal) : FoldExpr(sel.FalseVal);
                return new HSelect(cond, FoldExpr(sel.TrueVal), FoldExpr(sel.FalseVal), sel.Type);
            }

            case HInternalCall ic:
            {
                var foldedArgs = ic.Args.Select(FoldExpr).ToList();
                return new HInternalCall(ic.FuncName, foldedArgs, ic.Type);
            }

            case HCrossBehaviourCall cb:
            {
                var foldedInstance = FoldExpr(cb.Instance);
                var foldedParams = cb.Params.Select(p => (p.ParamName, FoldExpr(p.Value))).ToList();
                return new HCrossBehaviourCall(foldedInstance, cb.EventName, foldedParams, cb.ReturnVarName, cb.Type);
            }

            // HConst, HSlotRef, HLoadField, HFieldAddr, HFuncRef — leaves, nothing to fold
            default:
                return expr;
        }
    }

    static bool TryEval(string sig, List<HExpr> args, out HConst result)
    {
        result = null;
        if (!FoldableExterns.TryGetValue(sig, out var eval))
            return false;

        var consts = args.Cast<HConst>().ToList();
        try
        {
            var value = eval(consts);
            var retType = sig.Substring(sig.LastIndexOf("__") + 2);
            result = new HConst(value, retType);
            return true;
        }
        catch (DivideByZeroException)
        {
            return false; // don't fold division by zero — let runtime raise it
        }
    }

    // ========================================================================
    // Dead Code Elimination
    // ========================================================================

    public static void DeadCodeElimination(HModule module)
    {
        foreach (var func in module.Functions)
            EliminateDeadCode(func.Body);
    }

    static void EliminateDeadCode(HBlock block)
    {
        // Recursively process nested structures first (bottom-up)
        for (int i = 0; i < block.Stmts.Count; i++)
        {
            switch (block.Stmts[i])
            {
                case HIf hif:
                    EliminateDeadCode(hif.Then);
                    EliminateDeadCode(hif.Else);
                    // Remove empty HIf where both branches are empty and condition is pure
                    if (hif.Then.Stmts.Count == 0 && hif.Else.Stmts.Count == 0 && IsPureExpr(hif.Cond))
                    {
                        block.Stmts.RemoveAt(i);
                        i--;
                    }
                    break;

                case HWhile hw:
                    EliminateDeadCode(hw.CondBlock);
                    EliminateDeadCode(hw.Body);
                    // Remove empty loop with pure condition (not do-while, since body runs at least once)
                    if (!hw.IsDoWhile && hw.Body.Stmts.Count == 0 && hw.CondBlock.Stmts.Count == 0 && IsPureExpr(hw.Cond))
                    {
                        block.Stmts.RemoveAt(i);
                        i--;
                    }
                    break;

                case HFor hf:
                    EliminateDeadCode(hf.Init);
                    EliminateDeadCode(hf.CondBlock);
                    EliminateDeadCode(hf.Update);
                    EliminateDeadCode(hf.Body);
                    // Remove empty for loop with pure condition and no init/update side effects
                    if (hf.Body.Stmts.Count == 0 && hf.Init.Stmts.Count == 0
                        && hf.Update.Stmts.Count == 0 && hf.CondBlock.Stmts.Count == 0
                        && (hf.Cond == null || IsPureExpr(hf.Cond)))
                    {
                        block.Stmts.RemoveAt(i);
                        i--;
                    }
                    break;

                case HBlock nested:
                    EliminateDeadCode(nested);
                    // Remove empty nested blocks
                    if (nested.Stmts.Count == 0)
                    {
                        block.Stmts.RemoveAt(i);
                        i--;
                    }
                    break;
            }
        }

        // Remove unreachable statements after terminators.
        // A label restores reachability (it is a jump target), so only remove
        // non-label statements between a terminator and the next label.
        for (int i = 0; i < block.Stmts.Count; i++)
        {
            if (block.Stmts[i] is HReturn or HBreak or HContinue or HGoto)
            {
                int j = i + 1;
                while (j < block.Stmts.Count && block.Stmts[j] is not HLabelStmt)
                    block.Stmts.RemoveAt(j);
                // After hitting a label (or end of block), reachability is restored.
                // Continue scanning for the next terminator from the label onward.
            }
        }
    }

    static bool IsPureExpr(HExpr expr)
    {
        return expr switch
        {
            HConst => true,
            HSlotRef => true,
            HLoadField => true,
            HFieldAddr => true,
            HFuncRef => true,
            HExternCall => false,
            HInternalCall => false,
            HCrossBehaviourCall => false,
            HSelect sel => IsPureExpr(sel.Cond) && IsPureExpr(sel.TrueVal) && IsPureExpr(sel.FalseVal),
            _ => false,
        };
    }

    // ========================================================================
    // Copy Propagation
    // ========================================================================

    /// <summary>
    /// Propagates constant values stored to single-write compiler-generated temp fields.
    /// Only propagates HConst values to avoid correctness issues with intervening writes.
    /// </summary>
    public static void CopyPropagation(HModule module)
    {
        foreach (var func in module.Functions)
            PropagateInFunction(func);
    }

    static void PropagateInFunction(HFunction func)
    {
        // Phase 1: Count writes per field across the entire function
        var writeCounts = new Dictionary<string, int>();
        CountWrites(func.Body, writeCounts);

        // Phase 2: Collect candidates — single-write temp fields with constant values
        var candidates = new Dictionary<string, HConst>();
        CollectCandidates(func.Body, writeCounts, candidates);

        if (candidates.Count == 0) return;

        // Phase 3: Replace HLoadField references with the constant value
        ReplaceInBlock(func.Body, candidates);

        // Phase 4: Slot-based copy propagation
        // If HAssign(slotId, HConst) and the slot is written exactly once,
        // replace all HSlotRef(slotId) with the constant.
        var slotWriteCounts = new Dictionary<int, int>();
        CountSlotWrites(func.Body, slotWriteCounts);

        var slotCandidates = new Dictionary<int, HConst>();
        CollectSlotCandidates(func.Body, slotWriteCounts, slotCandidates);

        if (slotCandidates.Count > 0)
            ReplaceSlotRefs(func.Body, slotCandidates);
    }

    static bool IsTempField(string fieldName) =>
        fieldName.StartsWith("__lcl_tmp_") ||
        fieldName.StartsWith("__lcl_sc_") ||
        fieldName.StartsWith("__lcl_ternary_");

    static void CountWrites(HBlock block, Dictionary<string, int> writeCounts)
    {
        foreach (var stmt in block.Stmts)
            CountWritesStmt(stmt, writeCounts);
    }

    static void CountWritesStmt(HStmt stmt, Dictionary<string, int> writeCounts)
    {
        switch (stmt)
        {
            case HStoreField sf:
                writeCounts[sf.FieldName] = writeCounts.TryGetValue(sf.FieldName, out var c) ? c + 1 : 1;
                break;
            case HIf hif:
                CountWrites(hif.Then, writeCounts);
                CountWrites(hif.Else, writeCounts);
                break;
            case HWhile hw:
                CountWrites(hw.CondBlock, writeCounts);
                CountWrites(hw.Body, writeCounts);
                break;
            case HFor hf:
                CountWrites(hf.Init, writeCounts);
                CountWrites(hf.CondBlock, writeCounts);
                CountWrites(hf.Update, writeCounts);
                CountWrites(hf.Body, writeCounts);
                break;
            case HBlock blk:
                CountWrites(blk, writeCounts);
                break;
        }
    }

    static void CollectCandidates(HBlock block, Dictionary<string, int> writeCounts, Dictionary<string, HConst> candidates)
    {
        foreach (var stmt in block.Stmts)
            CollectCandidatesStmt(stmt, writeCounts, candidates);
    }

    static void CollectCandidatesStmt(HStmt stmt, Dictionary<string, int> writeCounts, Dictionary<string, HConst> candidates)
    {
        switch (stmt)
        {
            case HStoreField sf:
                if (writeCounts.TryGetValue(sf.FieldName, out var wc) && wc == 1
                    && IsTempField(sf.FieldName) && sf.Value is HConst constVal)
                {
                    candidates[sf.FieldName] = constVal;
                }
                break;
            case HIf hif:
                CollectCandidates(hif.Then, writeCounts, candidates);
                CollectCandidates(hif.Else, writeCounts, candidates);
                break;
            case HWhile hw:
                CollectCandidates(hw.CondBlock, writeCounts, candidates);
                CollectCandidates(hw.Body, writeCounts, candidates);
                break;
            case HFor hf:
                CollectCandidates(hf.Init, writeCounts, candidates);
                CollectCandidates(hf.CondBlock, writeCounts, candidates);
                CollectCandidates(hf.Update, writeCounts, candidates);
                CollectCandidates(hf.Body, writeCounts, candidates);
                break;
            case HBlock blk:
                CollectCandidates(blk, writeCounts, candidates);
                break;
        }
    }

    static void ReplaceInBlock(HBlock block, Dictionary<string, HConst> candidates)
    {
        for (int i = 0; i < block.Stmts.Count; i++)
            block.Stmts[i] = ReplaceInStmt(block.Stmts[i], candidates);
    }

    static HStmt ReplaceInStmt(HStmt stmt, Dictionary<string, HConst> candidates)
    {
        switch (stmt)
        {
            case HAssign a:
                return new HAssign(a.DestSlot, ReplaceInExpr(a.Value, candidates));

            case HStoreField sf:
                return new HStoreField(sf.FieldName, ReplaceInExpr(sf.Value, candidates));

            case HIf hif:
                ReplaceInBlock(hif.Then, candidates);
                ReplaceInBlock(hif.Else, candidates);
                return new HIf(ReplaceInExpr(hif.Cond, candidates), hif.Then, hif.Else);

            case HWhile hw:
                ReplaceInBlock(hw.CondBlock, candidates);
                ReplaceInBlock(hw.Body, candidates);
                return new HWhile(ReplaceInExpr(hw.Cond, candidates), hw.Body, hw.IsDoWhile, hw.CondBlock);

            case HFor hf:
                ReplaceInBlock(hf.Init, candidates);
                ReplaceInBlock(hf.CondBlock, candidates);
                ReplaceInBlock(hf.Update, candidates);
                ReplaceInBlock(hf.Body, candidates);
                return new HFor(hf.Init, hf.Cond != null ? ReplaceInExpr(hf.Cond, candidates) : null, hf.Update, hf.Body, hf.CondBlock);

            case HReturn hr:
                return hr.Value != null ? new HReturn(ReplaceInExpr(hr.Value, candidates)) : hr;

            case HExprStmt es:
                return new HExprStmt(ReplaceInExpr(es.Expr, candidates));

            case HBlock blk:
                ReplaceInBlock(blk, candidates);
                return blk;

            default:
                return stmt;
        }
    }

    static HExpr ReplaceInExpr(HExpr expr, Dictionary<string, HConst> candidates)
    {
        switch (expr)
        {
            case HLoadField lf:
                return candidates.TryGetValue(lf.FieldName, out var replacement) ? replacement : expr;

            case HExternCall call:
                return new HExternCall(call.Sig, call.Args.Select(a => ReplaceInExpr(a, candidates)).ToList(), call.Type);

            case HSelect sel:
                return new HSelect(ReplaceInExpr(sel.Cond, candidates), ReplaceInExpr(sel.TrueVal, candidates), ReplaceInExpr(sel.FalseVal, candidates), sel.Type);

            case HInternalCall ic:
                return new HInternalCall(ic.FuncName, ic.Args.Select(a => ReplaceInExpr(a, candidates)).ToList(), ic.Type);

            case HCrossBehaviourCall cb:
                return new HCrossBehaviourCall(
                    ReplaceInExpr(cb.Instance, candidates), cb.EventName,
                    cb.Params.Select(p => (p.ParamName, ReplaceInExpr(p.Value, candidates))).ToList(),
                    cb.ReturnVarName, cb.Type);

            // HConst, HSlotRef, HFieldAddr, HFuncRef — no replacement needed
            // Note: HFieldAddr is intentionally NOT replaced — it represents an address for out/ref,
            // not a value load. Replacing it with a constant would be semantically wrong.
            default:
                return expr;
        }
    }

    // ========================================================================
    // Slot-based Copy Propagation
    // ========================================================================

    static void CountSlotWrites(HBlock block, Dictionary<int, int> writeCounts)
    {
        foreach (var stmt in block.Stmts)
            CountSlotWritesStmt(stmt, writeCounts);
    }

    static void CountSlotWritesStmt(HStmt stmt, Dictionary<int, int> writeCounts)
    {
        switch (stmt)
        {
            case HAssign a:
                writeCounts[a.DestSlot] = writeCounts.TryGetValue(a.DestSlot, out var c) ? c + 1 : 1;
                break;
            case HIf hif:
                CountSlotWrites(hif.Then, writeCounts);
                CountSlotWrites(hif.Else, writeCounts);
                break;
            case HWhile hw:
                CountSlotWrites(hw.CondBlock, writeCounts);
                CountSlotWrites(hw.Body, writeCounts);
                break;
            case HFor hf:
                CountSlotWrites(hf.Init, writeCounts);
                CountSlotWrites(hf.CondBlock, writeCounts);
                CountSlotWrites(hf.Update, writeCounts);
                CountSlotWrites(hf.Body, writeCounts);
                break;
            case HBlock blk:
                CountSlotWrites(blk, writeCounts);
                break;
        }
    }

    static void CollectSlotCandidates(HBlock block, Dictionary<int, int> writeCounts, Dictionary<int, HConst> candidates)
    {
        foreach (var stmt in block.Stmts)
            CollectSlotCandidatesStmt(stmt, writeCounts, candidates);
    }

    static void CollectSlotCandidatesStmt(HStmt stmt, Dictionary<int, int> writeCounts, Dictionary<int, HConst> candidates)
    {
        switch (stmt)
        {
            case HAssign a:
                if (writeCounts.TryGetValue(a.DestSlot, out var wc) && wc == 1 && a.Value is HConst constVal)
                    candidates[a.DestSlot] = constVal;
                break;
            case HIf hif:
                CollectSlotCandidates(hif.Then, writeCounts, candidates);
                CollectSlotCandidates(hif.Else, writeCounts, candidates);
                break;
            case HWhile hw:
                CollectSlotCandidates(hw.CondBlock, writeCounts, candidates);
                CollectSlotCandidates(hw.Body, writeCounts, candidates);
                break;
            case HFor hf:
                CollectSlotCandidates(hf.Init, writeCounts, candidates);
                CollectSlotCandidates(hf.CondBlock, writeCounts, candidates);
                CollectSlotCandidates(hf.Update, writeCounts, candidates);
                CollectSlotCandidates(hf.Body, writeCounts, candidates);
                break;
            case HBlock blk:
                CollectSlotCandidates(blk, writeCounts, candidates);
                break;
        }
    }

    static void ReplaceSlotRefs(HBlock block, Dictionary<int, HConst> candidates)
    {
        for (int i = 0; i < block.Stmts.Count; i++)
            block.Stmts[i] = ReplaceSlotRefsStmt(block.Stmts[i], candidates);
    }

    static HStmt ReplaceSlotRefsStmt(HStmt stmt, Dictionary<int, HConst> candidates)
    {
        switch (stmt)
        {
            case HAssign a:
                return new HAssign(a.DestSlot, ReplaceSlotRefsExpr(a.Value, candidates));

            case HStoreField sf:
                return new HStoreField(sf.FieldName, ReplaceSlotRefsExpr(sf.Value, candidates));

            case HIf hif:
                ReplaceSlotRefs(hif.Then, candidates);
                ReplaceSlotRefs(hif.Else, candidates);
                return new HIf(ReplaceSlotRefsExpr(hif.Cond, candidates), hif.Then, hif.Else);

            case HWhile hw:
                ReplaceSlotRefs(hw.CondBlock, candidates);
                ReplaceSlotRefs(hw.Body, candidates);
                return new HWhile(ReplaceSlotRefsExpr(hw.Cond, candidates), hw.Body, hw.IsDoWhile, hw.CondBlock);

            case HFor hf:
                ReplaceSlotRefs(hf.Init, candidates);
                ReplaceSlotRefs(hf.CondBlock, candidates);
                ReplaceSlotRefs(hf.Update, candidates);
                ReplaceSlotRefs(hf.Body, candidates);
                return new HFor(hf.Init, hf.Cond != null ? ReplaceSlotRefsExpr(hf.Cond, candidates) : null, hf.Update, hf.Body, hf.CondBlock);

            case HReturn hr:
                return hr.Value != null ? new HReturn(ReplaceSlotRefsExpr(hr.Value, candidates)) : hr;

            case HExprStmt es:
                return new HExprStmt(ReplaceSlotRefsExpr(es.Expr, candidates));

            case HBlock blk:
                ReplaceSlotRefs(blk, candidates);
                return blk;

            default:
                return stmt;
        }
    }

    static HExpr ReplaceSlotRefsExpr(HExpr expr, Dictionary<int, HConst> candidates)
    {
        switch (expr)
        {
            case HSlotRef sr:
                return candidates.TryGetValue(sr.SlotId, out var replacement) ? replacement : expr;

            case HExternCall call:
                return new HExternCall(call.Sig, call.Args.Select(a => ReplaceSlotRefsExpr(a, candidates)).ToList(), call.Type);

            case HSelect sel:
                return new HSelect(ReplaceSlotRefsExpr(sel.Cond, candidates), ReplaceSlotRefsExpr(sel.TrueVal, candidates), ReplaceSlotRefsExpr(sel.FalseVal, candidates), sel.Type);

            case HInternalCall ic:
                return new HInternalCall(ic.FuncName, ic.Args.Select(a => ReplaceSlotRefsExpr(a, candidates)).ToList(), ic.Type);

            case HCrossBehaviourCall cb:
                return new HCrossBehaviourCall(
                    ReplaceSlotRefsExpr(cb.Instance, candidates), cb.EventName,
                    cb.Params.Select(p => (p.ParamName, ReplaceSlotRefsExpr(p.Value, candidates))).ToList(),
                    cb.ReturnVarName, cb.Type);

            default:
                return expr;
        }
    }

    // ========================================================================
    // Fold Table
    // ========================================================================

    static Dictionary<string, Func<List<HConst>, object>> BuildFoldTable()
    {
        var t = new Dictionary<string, Func<List<HConst>, object>>();

        // Int32 arithmetic
        t["SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value + (int)a[1].Value;
        t["SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value - (int)a[1].Value;
        t["SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value * (int)a[1].Value;
        t["SystemInt32.__op_Division__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value / (int)a[1].Value;
        t["SystemInt32.__op_Remainder__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value % (int)a[1].Value;

        // Int32 comparison
        t["SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean"] = a => (int)a[0].Value < (int)a[1].Value;
        t["SystemInt32.__op_GreaterThan__SystemInt32_SystemInt32__SystemBoolean"] = a => (int)a[0].Value > (int)a[1].Value;
        t["SystemInt32.__op_LessThanOrEqual__SystemInt32_SystemInt32__SystemBoolean"] = a => (int)a[0].Value <= (int)a[1].Value;
        t["SystemInt32.__op_GreaterThanOrEqual__SystemInt32_SystemInt32__SystemBoolean"] = a => (int)a[0].Value >= (int)a[1].Value;
        t["SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean"] = a => (int)a[0].Value == (int)a[1].Value;
        t["SystemInt32.__op_Inequality__SystemInt32_SystemInt32__SystemBoolean"] = a => (int)a[0].Value != (int)a[1].Value;

        // Int32 bitwise/shift
        t["SystemInt32.__op_LeftShift__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value << (int)a[1].Value;
        t["SystemInt32.__op_RightShift__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value >> (int)a[1].Value;
        t["SystemInt32.__op_LogicalAnd__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value & (int)a[1].Value;
        t["SystemInt32.__op_LogicalOr__SystemInt32_SystemInt32__SystemInt32"] = a => (int)a[0].Value | (int)a[1].Value;

        // Int32 unary
        t["SystemInt32.__op_UnaryMinus__SystemInt32__SystemInt32"] = a => -(int)a[0].Value;

        // Boolean
        t["SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean"] = a => !(bool)a[0].Value;
        t["SystemBoolean.__op_Equality__SystemBoolean_SystemBoolean__SystemBoolean"] = a => (bool)a[0].Value == (bool)a[1].Value;
        t["SystemBoolean.__op_Inequality__SystemBoolean_SystemBoolean__SystemBoolean"] = a => (bool)a[0].Value != (bool)a[1].Value;

        // Single arithmetic
        t["SystemSingle.__op_Addition__SystemSingle_SystemSingle__SystemSingle"] = a => (float)a[0].Value + (float)a[1].Value;
        t["SystemSingle.__op_Subtraction__SystemSingle_SystemSingle__SystemSingle"] = a => (float)a[0].Value - (float)a[1].Value;
        t["SystemSingle.__op_Multiplication__SystemSingle_SystemSingle__SystemSingle"] = a => (float)a[0].Value * (float)a[1].Value;
        t["SystemSingle.__op_Division__SystemSingle_SystemSingle__SystemSingle"] = a => (float)a[0].Value / (float)a[1].Value;

        // Single comparison
        t["SystemSingle.__op_LessThan__SystemSingle_SystemSingle__SystemBoolean"] = a => (float)a[0].Value < (float)a[1].Value;
        t["SystemSingle.__op_GreaterThan__SystemSingle_SystemSingle__SystemBoolean"] = a => (float)a[0].Value > (float)a[1].Value;

        // String comparison
        t["SystemString.__op_Equality__SystemString_SystemString__SystemBoolean"] = a => (string)a[0].Value == (string)a[1].Value;
        t["SystemString.__op_Inequality__SystemString_SystemString__SystemBoolean"] = a => (string)a[0].Value != (string)a[1].Value;

        // Conversions
        t["SystemConvert.__ToSingle__SystemInt32__SystemSingle"] = a => (float)(int)a[0].Value;
        t["SystemConvert.__ToInt32__SystemSingle__SystemInt32"] = a => (int)(float)a[0].Value;

        return t;
    }
}
