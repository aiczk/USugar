using System;
using System.Collections.Generic;

/// <summary>
/// Lowers structured HIR (HModule) into flat LIR (LModule) with basic blocks.
/// Converts structured control flow (if/while/for) into branches and jumps.
/// </summary>
public static class HirToLir
{
    public static LModule Lower(HModule hirModule)
    {
        var lmod = new LModule { ClassName = hirModule.ClassName };
        foreach (var field in hirModule.Fields)
            lmod.Fields.Add(field);

        foreach (var hfunc in hirModule.Functions)
            lmod.Functions.Add(LowerFunction(hfunc));

        return lmod;
    }

    static LFunction LowerFunction(HFunction hfunc)
    {
        var ctx = new LowerCtx(hfunc);
        ctx.Current = ctx.LFunc.NewBlock();

        // Pre-scan for labels to handle forward references.
        PreScanLabels(hfunc.Body, ctx);

        LowerBlock(hfunc.Body, ctx);

        // If the last block has no terminator, add a void return.
        if (ctx.Current.Term == null)
            ctx.Current.Term = new LReturn();

        return ctx.LFunc;
    }

    /// <summary>Pre-scan all statements for HLabelStmt and pre-allocate blocks.</summary>
    static void PreScanLabels(HBlock block, LowerCtx ctx)
    {
        foreach (var stmt in block.Stmts)
            PreScanLabelsStmt(stmt, ctx);
    }

    static void PreScanLabelsStmt(HStmt stmt, LowerCtx ctx)
    {
        switch (stmt)
        {
            case HLabelStmt lbl:
                if (!ctx.LabelBlocks.ContainsKey(lbl.Label))
                {
                    var labelBlock = ctx.LFunc.NewBlock();
                    labelBlock.Hint = $"__goto_{lbl.Label}";
                    ctx.LabelBlocks[lbl.Label] = labelBlock;
                }
                break;
            case HBlock blk:
                PreScanLabels(blk, ctx);
                break;
            case HIf hif:
                PreScanLabels(hif.Then, ctx);
                PreScanLabels(hif.Else, ctx);
                break;
            case HWhile hw:
                PreScanLabels(hw.CondBlock, ctx);
                PreScanLabels(hw.Body, ctx);
                break;
            case HFor hf:
                PreScanLabels(hf.Init, ctx);
                PreScanLabels(hf.CondBlock, ctx);
                PreScanLabels(hf.Update, ctx);
                PreScanLabels(hf.Body, ctx);
                break;
        }
    }

    // ── Statement lowering ──────────────────────────────────────────────

    static void LowerBlock(HBlock block, LowerCtx ctx)
    {
        foreach (var stmt in block.Stmts)
        {
            // If current block already has a terminator (e.g. from break/continue/return/goto),
            // skip unreachable statements — UNLESS the next statement is a label (valid jump target).
            if (ctx.Current.Term != null)
            {
                if (stmt is HLabelStmt)
                {
                    // Label starts a new reachable block; fall through to LowerStmt.
                }
                else
                {
                    continue;
                }
            }

            LowerStmt(stmt, ctx);
        }
    }

    static void LowerStmt(HStmt stmt, LowerCtx ctx)
    {
        switch (stmt)
        {
            case HBlock blk:
                LowerBlock(blk, ctx);
                break;

            case HAssign assign:
                LowerAssign(assign, ctx);
                break;

            case HStoreField store:
                LowerStoreField(store, ctx);
                break;

            case HExprStmt exprStmt:
                LowerExpr(exprStmt.Expr, ctx); // side effects happen, result discarded
                break;

            case HReturn ret:
                LowerReturn(ret, ctx);
                break;

            case HIf hif:
                LowerIf(hif, ctx);
                break;

            case HWhile hw:
                LowerWhile(hw, ctx);
                break;

            case HFor hf:
                LowerFor(hf, ctx);
                break;

            case HBreak _:
                LowerBreak(ctx);
                break;

            case HContinue _:
                LowerContinue(ctx);
                break;

            case HGoto hgoto:
                LowerGoto(hgoto, ctx);
                break;

            case HLabelStmt lbl:
                LowerLabelStmt(lbl, ctx);
                break;

            default:
                throw new InvalidOperationException($"Unknown HStmt type: {stmt.GetType().Name}");
        }
    }

    static void LowerAssign(HAssign assign, LowerCtx ctx)
    {
        var src = LowerExpr(assign.Value, ctx);
        var slotType = ctx.LFunc.Slots[assign.DestSlot].Type;
        ctx.Current.Insts.Add(new LMove(assign.DestSlot, src, slotType));
    }

    static void LowerStoreField(HStoreField store, LowerCtx ctx)
    {
        var src = LowerExpr(store.Value, ctx);
        ctx.Current.Insts.Add(new LStoreField(store.FieldName, src));
    }

    static void LowerReturn(HReturn ret, LowerCtx ctx)
    {
        LOperand val = null;
        if (ret.Value != null)
            val = LowerExpr(ret.Value, ctx);
        ctx.Current.Term = new LReturn(val);
    }

    // ── Control flow lowering ───────────────────────────────────────────

    static void LowerIf(HIf hif, LowerCtx ctx)
    {
        var cond = LowerExpr(hif.Cond, ctx);
        var thenBlock = ctx.LFunc.NewBlock();
        var elseBlock = ctx.LFunc.NewBlock();
        var mergeBlock = ctx.LFunc.NewBlock();

        ctx.Current.Term = new LBranch(cond, thenBlock.Id, elseBlock.Id);

        // Then branch
        ctx.Current = thenBlock;
        LowerBlock(hif.Then, ctx);
        if (ctx.Current.Term == null)
            ctx.Current.Term = new LJump(mergeBlock.Id);

        // Else branch
        ctx.Current = elseBlock;
        LowerBlock(hif.Else, ctx);
        if (ctx.Current.Term == null)
            ctx.Current.Term = new LJump(mergeBlock.Id);

        ctx.Current = mergeBlock;
    }

    static void LowerWhile(HWhile hw, LowerCtx ctx)
    {
        var headerBlock = ctx.LFunc.NewBlock();
        var bodyBlock = ctx.LFunc.NewBlock();
        var exitBlock = ctx.LFunc.NewBlock();

        if (hw.IsDoWhile)
        {
            // do-while: jump to body first, condition at bottom
            ctx.Current.Term = new LJump(bodyBlock.Id);

            // Body
            ctx.LoopStack.Push((exitBlock, headerBlock)); // continue → header (condition check)
            ctx.Current = bodyBlock;
            LowerBlock(hw.Body, ctx);
            if (ctx.Current.Term == null)
                ctx.Current.Term = new LJump(headerBlock.Id);
            ctx.LoopStack.Pop();

            // Header: run CondBlock (short-circuit setup) then evaluate Cond
            ctx.Current = headerBlock;
            if (hw.CondBlock.Stmts.Count > 0)
                LowerBlock(hw.CondBlock, ctx);
            var cond = LowerExpr(hw.Cond, ctx);
            ctx.Current.Term = new LBranch(cond, bodyBlock.Id, exitBlock.Id);
        }
        else
        {
            // while: condition at top
            ctx.Current.Term = new LJump(headerBlock.Id);

            // Header: run CondBlock (short-circuit setup) then evaluate Cond
            ctx.Current = headerBlock;
            if (hw.CondBlock.Stmts.Count > 0)
                LowerBlock(hw.CondBlock, ctx);
            var cond = LowerExpr(hw.Cond, ctx);
            ctx.Current.Term = new LBranch(cond, bodyBlock.Id, exitBlock.Id);

            // Body
            ctx.LoopStack.Push((exitBlock, headerBlock)); // continue → header
            ctx.Current = bodyBlock;
            LowerBlock(hw.Body, ctx);
            if (ctx.Current.Term == null)
                ctx.Current.Term = new LJump(headerBlock.Id);
            ctx.LoopStack.Pop();
        }

        ctx.Current = exitBlock;
    }

    static void LowerFor(HFor hf, LowerCtx ctx)
    {
        // Init in current block
        LowerBlock(hf.Init, ctx);

        var headerBlock = ctx.LFunc.NewBlock();
        var bodyBlock = ctx.LFunc.NewBlock();
        var continueBlock = ctx.LFunc.NewBlock(); // update goes here
        var exitBlock = ctx.LFunc.NewBlock();

        ctx.Current.Term = new LJump(headerBlock.Id);

        // Header: run CondBlock (short-circuit setup) then evaluate Cond
        ctx.Current = headerBlock;
        if (hf.CondBlock.Stmts.Count > 0)
            LowerBlock(hf.CondBlock, ctx);
        if (hf.Cond != null)
        {
            var cond = LowerExpr(hf.Cond, ctx);
            ctx.Current.Term = new LBranch(cond, bodyBlock.Id, exitBlock.Id);
        }
        else
        {
            // Infinite loop (no condition)
            ctx.Current.Term = new LJump(bodyBlock.Id);
        }

        // Body
        ctx.LoopStack.Push((exitBlock, continueBlock)); // continue → update block
        ctx.Current = bodyBlock;
        LowerBlock(hf.Body, ctx);
        if (ctx.Current.Term == null)
            ctx.Current.Term = new LJump(continueBlock.Id);
        ctx.LoopStack.Pop();

        // Continue (update)
        ctx.Current = continueBlock;
        LowerBlock(hf.Update, ctx);
        if (ctx.Current.Term == null)
            ctx.Current.Term = new LJump(headerBlock.Id);

        ctx.Current = exitBlock;
    }

    static void LowerBreak(LowerCtx ctx)
    {
        if (ctx.LoopStack.Count == 0)
            throw new InvalidOperationException("break outside of loop");
        var (exitBlock, _) = ctx.LoopStack.Peek();
        ctx.Current.Term = new LJump(exitBlock.Id);
    }

    static void LowerContinue(LowerCtx ctx)
    {
        if (ctx.LoopStack.Count == 0)
            throw new InvalidOperationException("continue outside of loop");
        var (_, continueBlock) = ctx.LoopStack.Peek();
        ctx.Current.Term = new LJump(continueBlock.Id);
    }

    static void LowerGoto(HGoto hgoto, LowerCtx ctx)
    {
        if (!ctx.LabelBlocks.TryGetValue(hgoto.Label, out var target))
            throw new InvalidOperationException($"Unknown label: {hgoto.Label}");
        ctx.Current.Term = new LJump(target.Id);
    }

    static void LowerLabelStmt(HLabelStmt lbl, LowerCtx ctx)
    {
        var labelBlock = ctx.LabelBlocks[lbl.Label];

        // End current block with jump to label block (fall-through)
        if (ctx.Current.Term == null)
            ctx.Current.Term = new LJump(labelBlock.Id);

        ctx.Current = labelBlock;
    }

    // ── Expression lowering ─────────────────────────────────────────────

    static LOperand LowerExpr(HExpr expr, LowerCtx ctx)
    {
        switch (expr)
        {
            case HConst c:
                return new LConst(c.Value, c.Type);

            case HSlotRef s:
                return new LSlotRef(s.SlotId, s.Type);

            case HFuncRef f:
                return new LFuncRef(f.FuncName);

            case HLoadField lf:
            {
                var dest = ctx.AllocScratch(lf.Type);
                ctx.Current.Insts.Add(new LLoadField(dest, lf.FieldName, lf.Type));
                return new LSlotRef(dest, lf.Type);
            }

            case HFieldAddr fa:
                return new LFieldRef(fa.FieldName, fa.Type);

            case HExternCall ec:
            {
                var args = new List<LOperand>(ec.Args.Count);
                foreach (var a in ec.Args)
                    args.Add(LowerExpr(a, ctx));

                int? dest = null;
                if (ec.Type != "SystemVoid")
                    dest = ctx.AllocScratch(ec.Type);

                ctx.Current.Insts.Add(new LCallExtern(dest, ec.Sig, args, ec.Type, ec.IsPure));
                return dest.HasValue ? new LSlotRef(dest.Value, ec.Type) : new LConst(null, "SystemVoid");
            }

            case HInternalCall ic:
            {
                var args = new List<LOperand>(ic.Args.Count);
                foreach (var a in ic.Args)
                    args.Add(LowerExpr(a, ctx));

                int? dest = null;
                if (ic.Type != "SystemVoid")
                    dest = ctx.AllocScratch(ic.Type);

                ctx.Current.Insts.Add(new LCallInternal(dest, ic.FuncName, args, ic.Type));
                return dest.HasValue ? new LSlotRef(dest.Value, ic.Type) : new LConst(null, "SystemVoid");
            }

            case HCrossBehaviourCall cc:
                return LowerCrossBehaviourCall(cc, ctx);

            case HSelect sel:
                return LowerSelect(sel, ctx);

            default:
                throw new InvalidOperationException($"Unknown HExpr type: {expr.GetType().Name}");
        }
    }

    static LOperand LowerCrossBehaviourCall(HCrossBehaviourCall cc, LowerCtx ctx)
    {
        var inst = LowerExpr(cc.Instance, ctx);

        // SetProgramVariable for each param
        foreach (var (paramName, value) in cc.Params)
        {
            var paramVal = LowerExpr(value, ctx);
            var paramNameOp = LowerExpr(new HConst(paramName, "SystemString"), ctx);
            ctx.Current.Insts.Add(new LCallExtern(null,
                "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<LOperand> { inst, paramNameOp, paramVal }, "SystemVoid"));
        }

        // SendCustomEvent
        var eventNameOp = LowerExpr(new HConst(cc.EventName, "SystemString"), ctx);
        ctx.Current.Insts.Add(new LCallExtern(null,
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<LOperand> { inst, eventNameOp }, "SystemVoid"));

        // GetProgramVariable for return
        if (cc.ReturnVarName != null && cc.Type != "SystemVoid")
        {
            var retNameOp = LowerExpr(new HConst(cc.ReturnVarName, "SystemString"), ctx);
            var dest = ctx.AllocScratch(cc.Type);
            ctx.Current.Insts.Add(new LCallExtern(dest,
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<LOperand> { inst, retNameOp }, cc.Type));
            return new LSlotRef(dest, cc.Type);
        }

        return new LConst(null, "SystemVoid");
    }

    static LOperand LowerSelect(HSelect sel, LowerCtx ctx)
    {
        var resultSlot = ctx.AllocScratch(sel.Type);
        var cond = LowerExpr(sel.Cond, ctx);

        var trueBlock = ctx.LFunc.NewBlock();
        var falseBlock = ctx.LFunc.NewBlock();
        var mergeBlock = ctx.LFunc.NewBlock();

        ctx.Current.Term = new LBranch(cond, trueBlock.Id, falseBlock.Id);

        // True branch
        ctx.Current = trueBlock;
        var trueVal = LowerExpr(sel.TrueVal, ctx);
        ctx.Current.Insts.Add(new LMove(resultSlot, trueVal, sel.Type));
        ctx.Current.Term = new LJump(mergeBlock.Id);

        // False branch
        ctx.Current = falseBlock;
        var falseVal = LowerExpr(sel.FalseVal, ctx);
        ctx.Current.Insts.Add(new LMove(resultSlot, falseVal, sel.Type));
        ctx.Current.Term = new LJump(mergeBlock.Id);

        ctx.Current = mergeBlock;
        return new LSlotRef(resultSlot, sel.Type);
    }

    // ── Lowering context ────────────────────────────────────────────────

    sealed class LowerCtx
    {
        public readonly LFunction LFunc;
        /// <summary>Stack of (exitBlock, continueBlock) for nested loops.</summary>
        public readonly Stack<(LBlock Exit, LBlock Continue)> LoopStack = new();
        /// <summary>Named label → pre-allocated block.</summary>
        public readonly Dictionary<string, LBlock> LabelBlocks = new();

        public LBlock Current;

        public LowerCtx(HFunction hfunc)
        {
            LFunc = new LFunction(hfunc.Name, hfunc.ExportName)
            {
                ReturnType = hfunc.ReturnType,
                ReturnFieldName = hfunc.ReturnFieldName,
            };
            foreach (var p in hfunc.ParamFieldNames)
                LFunc.ParamFieldNames.Add(p);
            // Copy all existing slots.
            foreach (var slot in hfunc.Slots)
                LFunc.Slots.Add(slot);
        }

        public int AllocScratch(string type)
        {
            var id = LFunc.Slots.Count;
            LFunc.Slots.Add(new SlotDecl(id, type, SlotClass.Scratch));
            return id;
        }
    }
}
