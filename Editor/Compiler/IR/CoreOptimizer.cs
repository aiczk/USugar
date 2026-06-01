using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// HIR constant folding optimizer.
/// Replaces pure extern calls on constant arguments with computed constant results,
/// and simplifies CSelect with constant boolean conditions.
/// </summary>
public static class CoreOptimizer
{
    static readonly Dictionary<string, Func<List<CConst>, object>> FoldableExterns = BuildFoldTable();

    public static void ConstantFold(CModule module)
    {
        foreach (var func in module.Functions)
            FoldBlock(func.Body);
    }

    static void FoldBlock(CBlock block)
    {
        for (int i = 0; i < block.Stmts.Count; i++)
            block.Stmts[i] = FoldStmt(block.Stmts[i]);
    }

    static CStmt FoldStmt(CStmt stmt)
    {
        switch (stmt)
        {
            case CAssign a:
                return new CAssign(a.DestSlot, FoldExpr(a.Value));

            case CStoreField sf:
                return new CStoreField(sf.FieldName, FoldExpr(sf.Value));

            case CIf hif:
            {
                var cond = FoldExpr(hif.Cond);
                if (cond is CConst { Value: bool b })
                {
                    // Dead branch elimination: replace CIf with the live branch
                    var live = b ? hif.Then : hif.Else;
                    FoldBlock(live);
                    return live.Stmts.Count == 1 ? live.Stmts[0] : new CBlock(live.Stmts);
                }
                FoldBlock(hif.Then);
                FoldBlock(hif.Else);
                return new CIf(cond, hif.Then, hif.Else);
            }

            case CWhile hw:
                FoldBlock(hw.CondBlock);
                FoldBlock(hw.Body);
                return new CWhile(FoldExpr(hw.Cond), hw.Body, hw.IsDoWhile, hw.CondBlock);

            case CFor hf:
                FoldBlock(hf.Init);
                FoldBlock(hf.CondBlock);
                FoldBlock(hf.Update);
                FoldBlock(hf.Body);
                return new CFor(hf.Init, hf.Cond != null ? FoldExpr(hf.Cond) : null, hf.Update, hf.Body, hf.CondBlock);

            case CReturn hr:
                return hr.Value != null ? new CReturn(FoldExpr(hr.Value)) : hr;

            case CExprStmt es:
                return new CExprStmt(FoldExpr(es.Expr));

            case CBlock blk:
                FoldBlock(blk);
                return blk;

            // CBreak, CContinue, CGoto, CLabel — no expressions to fold
            default:
                return stmt;
        }
    }

    /// <summary>
    /// Bottom-up expression folding. Folds children first, then attempts
    /// to evaluate the current node if all inputs are constant.
    /// Visible internally for testing.
    /// </summary>
    internal static CValue FoldExpr(CValue expr)
    {
        switch (expr)
        {
            case CExternCall call:
            {
                var foldedArgs = call.Args.Select(FoldExpr).ToList();
                if (foldedArgs.All(a => a is CConst) && TryEval(call.Sig, foldedArgs, out var result))
                    return result;
                return new CExternCall(call.Sig, foldedArgs, call.Type);
            }

            case CSelect sel:
            {
                var cond = FoldExpr(sel.Cond);
                if (cond is CConst { Value: bool b })
                    return b ? FoldExpr(sel.TrueVal) : FoldExpr(sel.FalseVal);
                return new CSelect(cond, FoldExpr(sel.TrueVal), FoldExpr(sel.FalseVal), sel.Type);
            }

            case CInternalCall ic:
            {
                var foldedArgs = ic.Args.Select(FoldExpr).ToList();
                return new CInternalCall(ic.FuncName, foldedArgs, ic.Type);
            }

            case CCrossCall cb:
            {
                var foldedInstance = FoldExpr(cb.Instance);
                var foldedParams = cb.Params.Select(p => (p.ParamName, FoldExpr(p.Value))).ToList();
                return new CCrossCall(foldedInstance, cb.EventName, foldedParams, cb.Returns, cb.Type);
            }

            // CConst, CSlotRef, CFieldRef, CFieldRef, CFuncRef — leaves, nothing to fold
            default:
                return expr;
        }
    }

    static bool TryEval(string sig, List<CValue> args, out CConst result)
    {
        result = null;
        if (!FoldableExterns.TryGetValue(sig, out var eval))
            return false;

        var consts = args.Cast<CConst>().ToList();
        try
        {
            var value = eval(consts);
            var retType = sig.Substring(sig.LastIndexOf("__") + 2);
            result = new CConst(value, retType);
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

    public static void DeadCodeElimination(CModule module)
    {
        foreach (var func in module.Functions)
            EliminateDeadCode(func.Body);
    }

    static void EliminateDeadCode(CBlock block)
    {
        // Recursively process nested structures first (bottom-up)
        for (int i = 0; i < block.Stmts.Count; i++)
        {
            switch (block.Stmts[i])
            {
                case CIf hif:
                    EliminateDeadCode(hif.Then);
                    EliminateDeadCode(hif.Else);
                    // Remove empty CIf where both branches are empty and condition is pure
                    if (hif.Then.Stmts.Count == 0 && hif.Else.Stmts.Count == 0 && IsPureExpr(hif.Cond))
                    {
                        block.Stmts.RemoveAt(i);
                        i--;
                    }
                    break;

                case CWhile hw:
                    EliminateDeadCode(hw.CondBlock);
                    EliminateDeadCode(hw.Body);
                    // Remove empty loop with pure condition (not do-while, since body runs at least once)
                    if (!hw.IsDoWhile && hw.Body.Stmts.Count == 0 && hw.CondBlock.Stmts.Count == 0 && IsPureExpr(hw.Cond))
                    {
                        block.Stmts.RemoveAt(i);
                        i--;
                    }
                    break;

                case CFor hf:
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

                case CBlock nested:
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
            if (block.Stmts[i] is CReturn or CBreak or CContinue or CGoto)
            {
                int j = i + 1;
                while (j < block.Stmts.Count && block.Stmts[j] is not CLabel)
                    block.Stmts.RemoveAt(j);
                // After hitting a label (or end of block), reachability is restored.
                // Continue scanning for the next terminator from the label onward.
            }
        }
    }

    static bool IsPureExpr(CValue expr)
    {
        return expr switch
        {
            CConst => true,
            CSlotRef => true,
            CFieldRef => true,
            CFuncRef => true,
            CExternCall => false,
            CInternalCall => false,
            CCrossCall => false,
            CSelect sel => IsPureExpr(sel.Cond) && IsPureExpr(sel.TrueVal) && IsPureExpr(sel.FalseVal),
            _ => false,
        };
    }

    // ========================================================================
    // Copy Propagation
    // ========================================================================

    /// <summary>
    /// Propagates constant values stored to single-write compiler-generated temp fields.
    /// Only propagates CConst values to avoid correctness issues with intervening writes.
    /// </summary>
    public static void CopyPropagation(CModule module)
    {
        foreach (var func in module.Functions)
            PropagateInFunction(func);
    }

    static void PropagateInFunction(CFunction func)
    {
        // Phase 1: Count writes per field across the entire function
        var writeCounts = new Dictionary<string, int>();
        CountWrites(func.Body, writeCounts);

        // Phase 2: Collect candidates — single-write temp fields with constant values
        var candidates = new Dictionary<string, CConst>();
        CollectCandidates(func.Body, writeCounts, candidates);

        if (candidates.Count == 0) return;

        // Phase 3: Replace CFieldRef references with the constant value
        ReplaceInBlock(func.Body, candidates);

        // Phase 4: Slot-based copy propagation
        // If CAssign(slotId, CConst) and the slot is written exactly once,
        // replace all CSlotRef(slotId) with the constant.
        var slotWriteCounts = new Dictionary<int, int>();
        CountSlotWrites(func.Body, slotWriteCounts);

        var slotCandidates = new Dictionary<int, CConst>();
        CollectSlotCandidates(func.Body, slotWriteCounts, slotCandidates);

        if (slotCandidates.Count > 0)
            ReplaceSlotRefs(func.Body, slotCandidates);
    }

    static bool IsTempField(string fieldName) =>
        fieldName.StartsWith("__lcl_tmp_") ||
        fieldName.StartsWith("__lcl_sc_") ||
        fieldName.StartsWith("__lcl_ternary_");

    static void CountWrites(CBlock block, Dictionary<string, int> writeCounts)
    {
        foreach (var stmt in block.Stmts)
            CountWritesStmt(stmt, writeCounts);
    }

    static void CountWritesStmt(CStmt stmt, Dictionary<string, int> writeCounts)
    {
        switch (stmt)
        {
            case CStoreField sf:
                writeCounts[sf.FieldName] = writeCounts.TryGetValue(sf.FieldName, out var c) ? c + 1 : 1;
                break;
            case CIf hif:
                CountWrites(hif.Then, writeCounts);
                CountWrites(hif.Else, writeCounts);
                break;
            case CWhile hw:
                CountWrites(hw.CondBlock, writeCounts);
                CountWrites(hw.Body, writeCounts);
                break;
            case CFor hf:
                CountWrites(hf.Init, writeCounts);
                CountWrites(hf.CondBlock, writeCounts);
                CountWrites(hf.Update, writeCounts);
                CountWrites(hf.Body, writeCounts);
                break;
            case CBlock blk:
                CountWrites(blk, writeCounts);
                break;
        }
    }

    static void CollectCandidates(CBlock block, Dictionary<string, int> writeCounts, Dictionary<string, CConst> candidates)
    {
        foreach (var stmt in block.Stmts)
            CollectCandidatesStmt(stmt, writeCounts, candidates);
    }

    static void CollectCandidatesStmt(CStmt stmt, Dictionary<string, int> writeCounts, Dictionary<string, CConst> candidates)
    {
        switch (stmt)
        {
            case CStoreField sf:
                if (writeCounts.TryGetValue(sf.FieldName, out var wc) && wc == 1
                    && IsTempField(sf.FieldName) && sf.Value is CConst constVal)
                {
                    candidates[sf.FieldName] = constVal;
                }
                break;
            case CIf hif:
                CollectCandidates(hif.Then, writeCounts, candidates);
                CollectCandidates(hif.Else, writeCounts, candidates);
                break;
            case CWhile hw:
                CollectCandidates(hw.CondBlock, writeCounts, candidates);
                CollectCandidates(hw.Body, writeCounts, candidates);
                break;
            case CFor hf:
                CollectCandidates(hf.Init, writeCounts, candidates);
                CollectCandidates(hf.CondBlock, writeCounts, candidates);
                CollectCandidates(hf.Update, writeCounts, candidates);
                CollectCandidates(hf.Body, writeCounts, candidates);
                break;
            case CBlock blk:
                CollectCandidates(blk, writeCounts, candidates);
                break;
        }
    }

    static void ReplaceInBlock(CBlock block, Dictionary<string, CConst> candidates)
    {
        for (int i = 0; i < block.Stmts.Count; i++)
            block.Stmts[i] = ReplaceInStmt(block.Stmts[i], candidates);
    }

    static CStmt ReplaceInStmt(CStmt stmt, Dictionary<string, CConst> candidates)
    {
        switch (stmt)
        {
            case CAssign a:
                return new CAssign(a.DestSlot, ReplaceInExpr(a.Value, candidates));

            case CStoreField sf:
                return new CStoreField(sf.FieldName, ReplaceInExpr(sf.Value, candidates));

            case CIf hif:
                ReplaceInBlock(hif.Then, candidates);
                ReplaceInBlock(hif.Else, candidates);
                return new CIf(ReplaceInExpr(hif.Cond, candidates), hif.Then, hif.Else);

            case CWhile hw:
                ReplaceInBlock(hw.CondBlock, candidates);
                ReplaceInBlock(hw.Body, candidates);
                return new CWhile(ReplaceInExpr(hw.Cond, candidates), hw.Body, hw.IsDoWhile, hw.CondBlock);

            case CFor hf:
                ReplaceInBlock(hf.Init, candidates);
                ReplaceInBlock(hf.CondBlock, candidates);
                ReplaceInBlock(hf.Update, candidates);
                ReplaceInBlock(hf.Body, candidates);
                return new CFor(hf.Init, hf.Cond != null ? ReplaceInExpr(hf.Cond, candidates) : null, hf.Update, hf.Body, hf.CondBlock);

            case CReturn hr:
                return hr.Value != null ? new CReturn(ReplaceInExpr(hr.Value, candidates)) : hr;

            case CExprStmt es:
                return new CExprStmt(ReplaceInExpr(es.Expr, candidates));

            case CBlock blk:
                ReplaceInBlock(blk, candidates);
                return blk;

            default:
                return stmt;
        }
    }

    static CValue ReplaceInExpr(CValue expr, Dictionary<string, CConst> candidates)
    {
        switch (expr)
        {
            case CFieldRef lf when lf.Mode == CFieldMode.Load:
                return candidates.TryGetValue(lf.FieldName, out var replacement) ? replacement : expr;

            case CExternCall call:
                return new CExternCall(call.Sig, call.Args.Select(a => ReplaceInExpr(a, candidates)).ToList(), call.Type);

            case CSelect sel:
                return new CSelect(ReplaceInExpr(sel.Cond, candidates), ReplaceInExpr(sel.TrueVal, candidates), ReplaceInExpr(sel.FalseVal, candidates), sel.Type);

            case CInternalCall ic:
                return new CInternalCall(ic.FuncName, ic.Args.Select(a => ReplaceInExpr(a, candidates)).ToList(), ic.Type);

            case CCrossCall cb:
                return new CCrossCall(
                    ReplaceInExpr(cb.Instance, candidates), cb.EventName,
                    cb.Params.Select(p => (p.ParamName, ReplaceInExpr(p.Value, candidates))).ToList(),
                    cb.Returns, cb.Type);

            // CConst, CSlotRef, CFieldRef, CFuncRef — no replacement needed
            // Note: CFieldRef is intentionally NOT replaced — it represents an address for out/ref,
            // not a value load. Replacing it with a constant would be semantically wrong.
            default:
                return expr;
        }
    }

    // ========================================================================
    // Slot-based Copy Propagation
    // ========================================================================

    static void CountSlotWrites(CBlock block, Dictionary<int, int> writeCounts)
    {
        foreach (var stmt in block.Stmts)
            CountSlotWritesStmt(stmt, writeCounts);
    }

    static void CountSlotWritesStmt(CStmt stmt, Dictionary<int, int> writeCounts)
    {
        switch (stmt)
        {
            case CAssign a:
                writeCounts[a.DestSlot] = writeCounts.TryGetValue(a.DestSlot, out var c) ? c + 1 : 1;
                break;
            case CIf hif:
                CountSlotWrites(hif.Then, writeCounts);
                CountSlotWrites(hif.Else, writeCounts);
                break;
            case CWhile hw:
                CountSlotWrites(hw.CondBlock, writeCounts);
                CountSlotWrites(hw.Body, writeCounts);
                break;
            case CFor hf:
                CountSlotWrites(hf.Init, writeCounts);
                CountSlotWrites(hf.CondBlock, writeCounts);
                CountSlotWrites(hf.Update, writeCounts);
                CountSlotWrites(hf.Body, writeCounts);
                break;
            case CBlock blk:
                CountSlotWrites(blk, writeCounts);
                break;
        }
    }

    static void CollectSlotCandidates(CBlock block, Dictionary<int, int> writeCounts, Dictionary<int, CConst> candidates)
    {
        foreach (var stmt in block.Stmts)
            CollectSlotCandidatesStmt(stmt, writeCounts, candidates);
    }

    static void CollectSlotCandidatesStmt(CStmt stmt, Dictionary<int, int> writeCounts, Dictionary<int, CConst> candidates)
    {
        switch (stmt)
        {
            case CAssign a:
                if (writeCounts.TryGetValue(a.DestSlot, out var wc) && wc == 1 && a.Value is CConst constVal)
                    candidates[a.DestSlot] = constVal;
                break;
            case CIf hif:
                CollectSlotCandidates(hif.Then, writeCounts, candidates);
                CollectSlotCandidates(hif.Else, writeCounts, candidates);
                break;
            case CWhile hw:
                CollectSlotCandidates(hw.CondBlock, writeCounts, candidates);
                CollectSlotCandidates(hw.Body, writeCounts, candidates);
                break;
            case CFor hf:
                CollectSlotCandidates(hf.Init, writeCounts, candidates);
                CollectSlotCandidates(hf.CondBlock, writeCounts, candidates);
                CollectSlotCandidates(hf.Update, writeCounts, candidates);
                CollectSlotCandidates(hf.Body, writeCounts, candidates);
                break;
            case CBlock blk:
                CollectSlotCandidates(blk, writeCounts, candidates);
                break;
        }
    }

    static void ReplaceSlotRefs(CBlock block, Dictionary<int, CConst> candidates)
    {
        for (int i = 0; i < block.Stmts.Count; i++)
            block.Stmts[i] = ReplaceSlotRefsStmt(block.Stmts[i], candidates);
    }

    static CStmt ReplaceSlotRefsStmt(CStmt stmt, Dictionary<int, CConst> candidates)
    {
        switch (stmt)
        {
            case CAssign a:
                return new CAssign(a.DestSlot, ReplaceSlotRefsExpr(a.Value, candidates));

            case CStoreField sf:
                return new CStoreField(sf.FieldName, ReplaceSlotRefsExpr(sf.Value, candidates));

            case CIf hif:
                ReplaceSlotRefs(hif.Then, candidates);
                ReplaceSlotRefs(hif.Else, candidates);
                return new CIf(ReplaceSlotRefsExpr(hif.Cond, candidates), hif.Then, hif.Else);

            case CWhile hw:
                ReplaceSlotRefs(hw.CondBlock, candidates);
                ReplaceSlotRefs(hw.Body, candidates);
                return new CWhile(ReplaceSlotRefsExpr(hw.Cond, candidates), hw.Body, hw.IsDoWhile, hw.CondBlock);

            case CFor hf:
                ReplaceSlotRefs(hf.Init, candidates);
                ReplaceSlotRefs(hf.CondBlock, candidates);
                ReplaceSlotRefs(hf.Update, candidates);
                ReplaceSlotRefs(hf.Body, candidates);
                return new CFor(hf.Init, hf.Cond != null ? ReplaceSlotRefsExpr(hf.Cond, candidates) : null, hf.Update, hf.Body, hf.CondBlock);

            case CReturn hr:
                return hr.Value != null ? new CReturn(ReplaceSlotRefsExpr(hr.Value, candidates)) : hr;

            case CExprStmt es:
                return new CExprStmt(ReplaceSlotRefsExpr(es.Expr, candidates));

            case CBlock blk:
                ReplaceSlotRefs(blk, candidates);
                return blk;

            default:
                return stmt;
        }
    }

    static CValue ReplaceSlotRefsExpr(CValue expr, Dictionary<int, CConst> candidates)
    {
        switch (expr)
        {
            case CSlotRef sr:
                return candidates.TryGetValue(sr.SlotId, out var replacement) ? replacement : expr;

            case CExternCall call:
                return new CExternCall(call.Sig, call.Args.Select(a => ReplaceSlotRefsExpr(a, candidates)).ToList(), call.Type);

            case CSelect sel:
                return new CSelect(ReplaceSlotRefsExpr(sel.Cond, candidates), ReplaceSlotRefsExpr(sel.TrueVal, candidates), ReplaceSlotRefsExpr(sel.FalseVal, candidates), sel.Type);

            case CInternalCall ic:
                return new CInternalCall(ic.FuncName, ic.Args.Select(a => ReplaceSlotRefsExpr(a, candidates)).ToList(), ic.Type);

            case CCrossCall cb:
                return new CCrossCall(
                    ReplaceSlotRefsExpr(cb.Instance, candidates), cb.EventName,
                    cb.Params.Select(p => (p.ParamName, ReplaceSlotRefsExpr(p.Value, candidates))).ToList(),
                    cb.Returns, cb.Type);

            default:
                return expr;
        }
    }

    // ========================================================================
    // Fold Table
    // ========================================================================

    static Dictionary<string, Func<List<CConst>, object>> BuildFoldTable()
    {
        var t = new Dictionary<string, Func<List<CConst>, object>>();

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
