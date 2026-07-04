using System;
using System.Collections.Generic;

// ============================================================================
// CoreFlatten — the one structured->flat transform of the Core IR. Lowers the structured CStmt tree
// into flat CBlocks (each with a Terminator and Id), allocating scratch slots, and mutates the
// CFunction in place: sets FlatBlocks, appends scratch to Slots, and sets Shape=Flat. Control-flow
// semantics (CondBlock re-evaluation, select dual-arm, cross-call expansion) are realized here.
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
                else
                {
                    // Dead-code drop: account for any Reentrant-flagged calls inside the dropped
                    // statement so FlatVerify's conservation check stays balanced (the drop is a
                    // deliberate, accounted removal — not the silent flag loss the check hunts).
                    ctx.Func.ReentrantSiteCount -= CountReentrant(stmt);
                    continue;
                }
            }
            LowerStmt(stmt, ctx);
        }
    }

    /// <summary>Count Reentrant-flagged calls in a STRUCTURED statement subtree (dead-code accounting).</summary>
    static int CountReentrant(CStmt stmt)
    {
        switch (stmt)
        {
            case CExprStmt es: return CountReentrantValue(es.Expr);
            case CAssign a: return CountReentrantValue(a.Value);
            case CBlock b:
            {
                int n = 0;
                foreach (var s in b.Stmts) n += CountReentrant(s);
                return n;
            }
            case CIf cif: return CountReentrant(cif.Then) + CountReentrant(cif.Else);
            case CWhile cw: return CountReentrant(cw.CondBlock) + CountReentrant(cw.Body);
            case CFor cf:
                return CountReentrant(cf.Init) + CountReentrant(cf.CondBlock)
                     + CountReentrant(cf.Update) + CountReentrant(cf.Body);
            default: return 0;
        }
    }

    static int CountReentrantValue(CValue v) => v switch
    {
        CExternCall ec when ec.Reentrant => 1,
        CInternalCall ic when ic.Reentrant => 1,
        // Wave-12 r2 [V1]: a reentrant CCrossCall lowers to ONE flagged SendCustomEvent — dropping
        // the structured statement must decrement the creation-counted flag exactly once.
        CCrossCall cc when cc.Reentrant => 1,
        _ => 0,
    };

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
        // sf.Value is a CLeaf (operand-leaf under ANF) — already a flat leaf, no lowering needed.
        ctx.Current.Stmts.Add(new CStoreField(sf.FieldName, sf.Value));
    }

    static void LowerReturn(CReturn r, Ctx ctx)
    {
        // r.Value is a CLeaf or null — already flat.
        ctx.Current.Terminator = new CRet(r.Value);
    }

    static void LowerIf(CIf cif, Ctx ctx)
    {
        var cond = cif.Cond; // CLeaf operand — already flat
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
            ctx.Current.Terminator = new CBranch(cw.Cond, bodyBlock.Id, exitBlock.Id);
        }
        else
        {
            ctx.Current.Terminator = new CJump(headerBlock.Id);

            ctx.Current = headerBlock;
            if (cw.CondBlock.Stmts.Count > 0) LowerBlock(cw.CondBlock, ctx);
            ctx.Current.Terminator = new CBranch(cw.Cond, bodyBlock.Id, exitBlock.Id);

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
            ctx.Current.Terminator = new CBranch(cf.Cond, bodyBlock.Id, exitBlock.Id);
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

    static CLeaf LowerExpr(CValue expr, Ctx ctx)
    {
        switch (expr)
        {
            case CLeaf leaf: // CConst / CSlotRef / CFuncRef / CFieldAddr — already a flat leaf
                return leaf;

            case CFieldLoad fl:
            {
                var dest = ctx.AllocScratch(fl.Type);
                ctx.Current.Stmts.Add(new CLoadField(dest, fl.FieldName, fl.Type));
                return new CSlotRef(dest, fl.Type);
            }

            case CExternCall ec:
            {
                // ec.Args are CLeaf operands (ANF) — already flat leaves, no per-arg lowering needed.
                // Reentrant MUST be copied: this rebuild is one of the two sites (with RemapInst) where
                // object-identity marking would silently die (design §4.3) — FlatVerify checks conservation.
                int? dest = ec.Type != "SystemVoid" ? ctx.AllocScratch(ec.Type) : (int?)null;
                ctx.Current.Stmts.Add(new CExprStmt(ec.With(new List<CLeaf>(ec.Args), dest)));
                return dest.HasValue ? new CSlotRef(dest.Value, ec.Type) : new CConst(null, "SystemVoid");
            }

            case CInternalCall ic:
            {
                // Reentrant AND TailSpared MUST be copied (see the CExternCall note above; TailSpared
                // is the round-9 [Y3] per-site spill exemption).
                int? dest = ic.Type != "SystemVoid" ? ctx.AllocScratch(ic.Type) : (int?)null;
                ctx.Current.Stmts.Add(new CExprStmt(ic.With(new List<CLeaf>(ic.Args), dest)));
                return dest.HasValue ? new CSlotRef(dest.Value, ic.Type) : new CConst(null, "SystemVoid");
            }

            case CCrossCall cc: return LowerCrossCall(cc, ctx);
            case CSelect sel: return LowerSelect(sel, ctx);

            default:
                throw new InvalidOperationException($"Unknown CValue: {expr.GetType().Name}");
        }
    }

    static CLeaf LowerCrossCall(CCrossCall cc, Ctx ctx)
    {
        // Instance, param values, and the string-constant operands are all CLeaf — already flat.
        var inst = cc.Instance;

        foreach (var (paramName, value) in cc.Params)
        {
            ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<CLeaf> { inst, new CConst(paramName, "SystemString"), value }, "SystemVoid", null)));
        }

        // Wave-12 r2 [V1]: a reentrant cross dispatch flags its SendCustomEvent as the §4.3 spill
        // site, with every param copy-in inside the spill window (PreSpillStmts — a same-program
        // reentrant callee shares the caller's param heap vars, so a copy-in that preceded the save
        // would be captured post-clobber). The copy-ins above are emitted back-to-back into the same
        // flat block, so the count is exact by construction.
        ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<CLeaf> { inst, new CConst(cc.EventName, "SystemString") }, "SystemVoid", null,
            cc.Reentrant, cc.Reentrant ? cc.Params.Count : 0)));

        if (cc.Returns.Count == 1)
        {
            var ret = cc.Returns[0];
            var dest = ctx.AllocScratch(cc.Type);
            ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<CLeaf> { inst, new CConst(ret.Id, "SystemString") }, cc.Type, dest)));
            return new CSlotRef(dest, cc.Type);
        }

        if (cc.Returns.Count > 1)
        {
            foreach (var ret in cc.Returns)
            {
                var dest = ctx.AllocScratch("SystemObject");
                ctx.Current.Stmts.Add(new CExprStmt(new CExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<CLeaf> { inst, new CConst(ret.Id, "SystemString") }, "SystemObject", dest)));
            }
        }

        return new CConst(null, "SystemVoid");
    }

    static CLeaf LowerSelect(CSelect sel, Ctx ctx)
    {
        var resultSlot = ctx.AllocScratch(sel.Type);
        // Cond/TrueVal/FalseVal are CLeaf operands (ANF) bound before the select — already flat. CSelect is
        // used only for PURE branches, so eagerly assigning the pre-bound branch leaf in each arm is correct.
        var trueBlock = ctx.NewBlock();
        var falseBlock = ctx.NewBlock();
        var mergeBlock = ctx.NewBlock();

        ctx.Current.Terminator = new CBranch(sel.Cond, trueBlock.Id, falseBlock.Id);

        ctx.Current = trueBlock;
        ctx.Current.Stmts.Add(new CAssign(resultSlot, sel.TrueVal));
        ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = falseBlock;
        ctx.Current.Stmts.Add(new CAssign(resultSlot, sel.FalseVal));
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
