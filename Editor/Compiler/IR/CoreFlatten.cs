using System;
using System.Collections.Generic;

// ============================================================================
// CoreFlatten — the one structured->flat transform of the unified Core IR (Phase 2 of
// "Core IR by absorption"). A line-for-line port of HirToLir operating on CNode/CValue,
// producing flat CBlocks (Terminator + Id) and appending scratch slots in the SAME order
// as HirToLir. Mutates the CFunction in place: sets FlatBlocks, appends scratch to Slots, sets
// Shape=Flat. Semantics (CondBlock re-eval, select dual-arm, cross-call expansion) are preserved
// verbatim — no lowering is moved relative to HirToLir.
// ============================================================================

public static class CoreFlatten
{
    public static void Lower(CFunction f)
    {
        var ctx = new Ctx(f);
        ctx.Current = ctx.NewBlock();

        PreScanLabels(f.Body, ctx);
        LowerBlock(f.Body, ctx);

        if (ctx.Current.Terminator == null)
            ctx.Current.Terminator = new CRet();

        f.Shape = Shape.Flat;
    }

    // ── Label pre-scan ──

    static void PreScanLabels(CBlock block, Ctx ctx)
    {
        foreach (var stmt in block.Stmts)
            PreScanLabelsStmt(stmt, ctx);
    }

    static void PreScanLabelsStmt(CStmt stmt, Ctx ctx)
    {
        switch (stmt)
        {
            case CLabel lbl:
                if (!ctx.LabelBlocks.ContainsKey(lbl.Label))
                {
                    var labelBlock = ctx.NewBlock();
                    labelBlock.Hint = $"__goto_{ctx.Func.Name}_{lbl.Label}";
                    ctx.LabelBlocks[lbl.Label] = labelBlock;
                }
                break;
            case CBlock blk: PreScanLabels(blk, ctx); break;
            case CIf cif: PreScanLabels(cif.Then, ctx); PreScanLabels(cif.Else, ctx); break;
            case CWhile cw: PreScanLabels(cw.CondBlock, ctx); PreScanLabels(cw.Body, ctx); break;
            case CFor cf:
                PreScanLabels(cf.Init, ctx); PreScanLabels(cf.CondBlock, ctx);
                PreScanLabels(cf.Update, ctx); PreScanLabels(cf.Body, ctx);
                break;
        }
    }

    // ── Statement lowering ──

    static void LowerBlock(CBlock block, Ctx ctx)
    {
        foreach (var stmt in block.Stmts)
        {
            if (ctx.Current.Terminator != null)
            {
                if (stmt is CLabel) { /* label starts a new reachable block */ }
                else continue;
            }
            LowerStmt(stmt, ctx);
        }
    }

    static void LowerStmt(CStmt stmt, Ctx ctx)
    {
        switch (stmt)
        {
            case CBlock blk: LowerBlock(blk, ctx); break;
            case CAssign a: LowerAssign(a, ctx); break;
            case CStoreField sf: LowerStoreField(sf, ctx); break;
            case CExprStmt es: LowerExpr(es.Expr, ctx); break;
            case CReturn r: LowerReturn(r, ctx); break;
            case CIf cif: LowerIf(cif, ctx); break;
            case CWhile cw: LowerWhile(cw, ctx); break;
            case CFor cf: LowerFor(cf, ctx); break;
            case CBreak _: LowerBreak(ctx); break;
            case CContinue _: LowerContinue(ctx); break;
            case CGoto g: LowerGoto(g, ctx); break;
            case CLabel l: LowerLabel(l, ctx); break;
            default: throw new InvalidOperationException($"Unknown CStmt: {stmt.GetType().Name}");
        }
    }

    static void LowerAssign(CAssign a, Ctx ctx)
    {
        var src = LowerExpr(a.Value, ctx);
        ctx.Current.Stmts.Add(new CAssign(a.DestSlot, src));
    }

    static void LowerStoreField(CStoreField sf, Ctx ctx)
    {
        var src = LowerExpr(sf.Value, ctx);
        ctx.Current.Stmts.Add(new CStoreField(sf.FieldName, src));
    }

    static void LowerReturn(CReturn r, Ctx ctx)
    {
        CValue val = r.Value == null ? null : LowerExpr(r.Value, ctx);
        ctx.Current.Terminator = new CRet(val);
    }

    static void LowerIf(CIf cif, Ctx ctx)
    {
        var cond = LowerExpr(cif.Cond, ctx);
        var thenBlock = ctx.NewBlock();
        var elseBlock = ctx.NewBlock();
        var mergeBlock = ctx.NewBlock();

        ctx.Current.Terminator = new CBranch(cond, thenBlock.Id, elseBlock.Id);

        ctx.Current = thenBlock;
        LowerBlock(cif.Then, ctx);
        if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = elseBlock;
        LowerBlock(cif.Else, ctx);
        if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = mergeBlock;
    }

    static void LowerWhile(CWhile cw, Ctx ctx)
    {
        var headerBlock = ctx.NewBlock();
        var bodyBlock = ctx.NewBlock();
        var exitBlock = ctx.NewBlock();

        if (cw.IsDoWhile)
        {
            ctx.Current.Terminator = new CJump(bodyBlock.Id);

            ctx.LoopStack.Push((exitBlock, headerBlock));
            ctx.Current = bodyBlock;
            LowerBlock(cw.Body, ctx);
            if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(headerBlock.Id);
            ctx.LoopStack.Pop();

            ctx.Current = headerBlock;
            if (cw.CondBlock.Stmts.Count > 0) LowerBlock(cw.CondBlock, ctx);
            var cond = LowerExpr(cw.Cond, ctx);
            ctx.Current.Terminator = new CBranch(cond, bodyBlock.Id, exitBlock.Id);
        }
        else
        {
            ctx.Current.Terminator = new CJump(headerBlock.Id);

            ctx.Current = headerBlock;
            if (cw.CondBlock.Stmts.Count > 0) LowerBlock(cw.CondBlock, ctx);
            var cond = LowerExpr(cw.Cond, ctx);
            ctx.Current.Terminator = new CBranch(cond, bodyBlock.Id, exitBlock.Id);

            ctx.LoopStack.Push((exitBlock, headerBlock));
            ctx.Current = bodyBlock;
            LowerBlock(cw.Body, ctx);
            if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(headerBlock.Id);
            ctx.LoopStack.Pop();
        }

        ctx.Current = exitBlock;
    }

    static void LowerFor(CFor cf, Ctx ctx)
    {
        LowerBlock(cf.Init, ctx);

        var headerBlock = ctx.NewBlock();
        var bodyBlock = ctx.NewBlock();
        var continueBlock = ctx.NewBlock();
        var exitBlock = ctx.NewBlock();

        ctx.Current.Terminator = new CJump(headerBlock.Id);

        ctx.Current = headerBlock;
        if (cf.CondBlock.Stmts.Count > 0) LowerBlock(cf.CondBlock, ctx);
        if (cf.Cond != null)
        {
            var cond = LowerExpr(cf.Cond, ctx);
            ctx.Current.Terminator = new CBranch(cond, bodyBlock.Id, exitBlock.Id);
        }
        else
        {
            ctx.Current.Terminator = new CJump(bodyBlock.Id);
        }

        ctx.LoopStack.Push((exitBlock, continueBlock));
        ctx.Current = bodyBlock;
        LowerBlock(cf.Body, ctx);
        if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(continueBlock.Id);
        ctx.LoopStack.Pop();

        ctx.Current = continueBlock;
        LowerBlock(cf.Update, ctx);
        if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(headerBlock.Id);

        ctx.Current = exitBlock;
    }

    static void LowerBreak(Ctx ctx)
    {
        if (ctx.LoopStack.Count == 0) throw new InvalidOperationException("break outside of loop");
        var (exitBlock, _) = ctx.LoopStack.Peek();
        ctx.Current.Terminator = new CJump(exitBlock.Id);
    }

    static void LowerContinue(Ctx ctx)
    {
        if (ctx.LoopStack.Count == 0) throw new InvalidOperationException("continue outside of loop");
        var (_, continueBlock) = ctx.LoopStack.Peek();
        ctx.Current.Terminator = new CJump(continueBlock.Id);
    }

    static void LowerGoto(CGoto g, Ctx ctx)
    {
        if (!ctx.LabelBlocks.TryGetValue(g.Label, out var target))
            throw new InvalidOperationException($"Unknown label: {g.Label}");
        ctx.Current.Terminator = new CJump(target.Id);
    }

    static void LowerLabel(CLabel lbl, Ctx ctx)
    {
        var labelBlock = ctx.LabelBlocks[lbl.Label];
        if (ctx.Current.Terminator == null) ctx.Current.Terminator = new CJump(labelBlock.Id);
        ctx.Current = labelBlock;
    }

    // ── Expression lowering (produces a flat leaf CValue; emits instructions for the rest) ──

    static CValue LowerExpr(CValue expr, Ctx ctx)
    {
        switch (expr)
        {
            case CConst _:
            case CSlotRef _:
            case CFuncRef _:
                return expr;

            case CFieldRef fr when fr.Mode == CFieldMode.Load:
            {
                var dest = ctx.AllocScratch(fr.Type);
                ctx.Current.Stmts.Add(new CLoadField(dest, fr.FieldName, fr.Type));
                return new CSlotRef(dest, fr.Type);
            }

            case CFieldRef fr: // Addr — address operand passes through
                return fr;

            case CExternCall ec:
            {
                var args = new List<CValue>(ec.Args.Count);
                foreach (var a in ec.Args) args.Add(LowerExpr(a, ctx));
                int? dest = ec.Type != "SystemVoid" ? ctx.AllocScratch(ec.Type) : (int?)null;
                ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(ec.Sig, args, ec.Type, dest)));
                return dest.HasValue ? new CSlotRef(dest.Value, ec.Type) : new CConst(null, "SystemVoid");
            }

            case CInternalCall ic:
            {
                var args = new List<CValue>(ic.Args.Count);
                foreach (var a in ic.Args) args.Add(LowerExpr(a, ctx));
                int? dest = ic.Type != "SystemVoid" ? ctx.AllocScratch(ic.Type) : (int?)null;
                ctx.Current.Stmts.Add(new CExprStmt(new CInternalCall(ic.FuncName, args, ic.Type, dest)));
                return dest.HasValue ? new CSlotRef(dest.Value, ic.Type) : new CConst(null, "SystemVoid");
            }

            case CCrossCall cc: return LowerCrossCall(cc, ctx);
            case CSelect sel: return LowerSelect(sel, ctx);

            default:
                throw new InvalidOperationException($"Unknown CValue: {expr.GetType().Name}");
        }
    }

    static CValue LowerCrossCall(CCrossCall cc, Ctx ctx)
    {
        var inst = LowerExpr(cc.Instance, ctx);

        foreach (var (paramName, value) in cc.Params)
        {
            var paramVal = LowerExpr(value, ctx);
            var paramNameOp = LowerExpr(new CConst(paramName, "SystemString"), ctx);
            ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<CValue> { inst, paramNameOp, paramVal }, "SystemVoid", null)));
        }

        var eventNameOp = LowerExpr(new CConst(cc.EventName, "SystemString"), ctx);
        ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<CValue> { inst, eventNameOp }, "SystemVoid", null)));

        if (cc.Returns.Count == 1)
        {
            var ret = cc.Returns[0];
            var retNameOp = LowerExpr(new CConst(ret.Id, "SystemString"), ctx);
            var dest = ctx.AllocScratch(cc.Type);
            ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<CValue> { inst, retNameOp }, cc.Type, dest)));
            return new CSlotRef(dest, cc.Type);
        }

        if (cc.Returns.Count > 1)
        {
            foreach (var ret in cc.Returns)
            {
                var retNameOp = LowerExpr(new CConst(ret.Id, "SystemString"), ctx);
                var dest = ctx.AllocScratch("SystemObject");
                ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<CValue> { inst, retNameOp }, "SystemObject", dest)));
            }
        }

        return new CConst(null, "SystemVoid");
    }

    static CValue LowerSelect(CSelect sel, Ctx ctx)
    {
        var resultSlot = ctx.AllocScratch(sel.Type);
        var cond = LowerExpr(sel.Cond, ctx);

        var trueBlock = ctx.NewBlock();
        var falseBlock = ctx.NewBlock();
        var mergeBlock = ctx.NewBlock();

        ctx.Current.Terminator = new CBranch(cond, trueBlock.Id, falseBlock.Id);

        ctx.Current = trueBlock;
        var trueVal = LowerExpr(sel.TrueVal, ctx);
        ctx.Current.Stmts.Add(new CAssign(resultSlot, trueVal));
        ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = falseBlock;
        var falseVal = LowerExpr(sel.FalseVal, ctx);
        ctx.Current.Stmts.Add(new CAssign(resultSlot, falseVal));
        ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = mergeBlock;
        return new CSlotRef(resultSlot, sel.Type);
    }

    // ── Lowering context ──

    sealed class Ctx
    {
        public readonly CFunction Func;
        public readonly Stack<(CBlock Exit, CBlock Continue)> LoopStack = new Stack<(CBlock, CBlock)>();
        public readonly Dictionary<string, CBlock> LabelBlocks = new Dictionary<string, CBlock>();
        public CBlock Current;
        int _nextBlockId;

        public Ctx(CFunction f) => Func = f;

        public CBlock NewBlock()
        {
            var b = new CBlock { Id = _nextBlockId++ };
            Func.FlatBlocks.Add(b);
            return b;
        }

        public int AllocScratch(string type)
        {
            var id = Func.Slots.Count;
            Func.Slots.Add(new SlotDecl(id, type, SlotClass.Scratch));
            return id;
        }
    }
}
