using System;
using System.Collections.Generic;

// ============================================================================
// CoreFlatten is the one structured-to-flat transform of Core IR. It returns a distinct
// FlatModule/FlatFunction graph and allocates flat-only scratch slots on the output.
// The structured input remains valid and unchanged.
// ============================================================================

public static class CoreFlatten
{
    public static FlatModule Lower(StructuredModule module)
    {
        if (module == null) throw new ArgumentNullException(nameof(module));
        var abiCatalog = module.AbiCatalog
            ?? throw new InvalidOperationException("Core flattening requires a Udon ABI catalog.");
        var result = new FlatModule(module);
        foreach (var function in module.Functions)
            result.Functions.Add(Lower(function, abiCatalog));
        return result;
    }

    public static FlatFunction Lower(StructuredFunction f, UdonAbiCatalog abiCatalog)
    {
        if (f == null) throw new ArgumentNullException(nameof(f));
        if (abiCatalog == null) throw new ArgumentNullException(nameof(abiCatalog));
        var output = new FlatFunction(f);
        var ctx = new Ctx(output, abiCatalog);
        ctx.Current = ctx.NewBlock();

        PreScanLabels(f.Body, ctx);
        LowerBlock(f.Body, ctx);

        if (ctx.Current.Terminator == null)
            ctx.Current.Terminator = new CRet();

        return output;
    }

    // ── Label pre-scan ──

    static void PreScanLabels(StructuredBlock block, Ctx ctx)
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
            case StructuredBlock blk: PreScanLabels(blk, ctx); break;
            case CIf cif: PreScanLabels(cif.Then, ctx); PreScanLabels(cif.Else, ctx); break;
            case CWhile cw: PreScanLabels(cw.CondBlock, ctx); PreScanLabels(cw.Body, ctx); break;
            case CFor cf:
                PreScanLabels(cf.Init, ctx); PreScanLabels(cf.CondBlock, ctx);
                PreScanLabels(cf.Update, ctx); PreScanLabels(cf.Body, ctx);
                break;
        }
    }

    // ── Statement lowering ──

    static void LowerBlock(StructuredBlock block, Ctx ctx)
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
            case StructuredBlock b:
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
            case StructuredBlock blk: LowerBlock(blk, ctx); break;
            case CAssign a: LowerAssign(a, ctx); break;
            case CStoreField sf: LowerStoreField(sf, ctx); break;
            case CProgramVariableStore store: LowerProgramVariableStore(store, ctx); break;
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
        // Bind already allocated the producer's logical result slot. Simple producers can write it
        // directly; allocating another scratch here only to COPY it back inflated the flat IR and
        // made correctness depend on the later coalescer removing that redundant pair.
        switch (a.Value)
        {
            case CRepresentationCast cast:
                ctx.Current.Instructions.Add(new CRepresentationCopy(
                    a.DestSlot, cast.Source, cast.Type, cast.Kind));
                return;
            case CFieldLoad fl:
                ctx.Current.Instructions.Add(new CLoadField(a.DestSlot, fl.FieldName, fl.Type));
                return;
            case CProgramVariableLoad load:
                LowerProgramVariableLoad(load, ctx, a.DestSlot);
                return;
            case CExternCall ec:
                ctx.Current.Instructions.Add(new CExprStmt(ec.With(new List<CLeaf>(ec.Args), a.DestSlot)));
                return;
            case CInternalCall ic:
                ctx.Current.Instructions.Add(new CExprStmt(ic.With(new List<CLeaf>(ic.Args), a.DestSlot)));
                return;
            case CCrossCall cc when cc.Returns.Count == 1:
                LowerCrossCall(cc, ctx, a.DestSlot);
                return;
            case CSelect sel:
                LowerSelect(sel, ctx, a.DestSlot);
                return;
        }

        var src = LowerExpr(a.Value, ctx);
        ctx.Current.Instructions.Add(new CAssign(a.DestSlot, src));
    }

    static void LowerStoreField(CStoreField sf, Ctx ctx)
    {
        // sf.Value is a CLeaf (operand-leaf under ANF) — already a flat leaf, no lowering needed.
        ctx.Current.Instructions.Add(new CStoreField(sf.FieldName, sf.Value));
    }

    static void LowerProgramVariableStore(CProgramVariableStore store, Ctx ctx)
    {
        ctx.Current.Instructions.Add(new CExprStmt(new CExternCall(
            ctx.Bind(ExternResolver.EventReceiverSetProgramVariable),
            new List<CLeaf> { store.Instance, store.VariableName, store.Value },
            StorageTypes.Void, null)));
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

            case CExternCall ec:
            {
                // ec.Args are CLeaf operands (ANF) — already flat leaves, no per-arg lowering needed.
                // Reentrant MUST be copied: this rebuild is one of the two sites (with RemapInst) where
                // object-identity marking would silently die (design §4.3) — FlatVerify checks conservation.
                int? dest = ec.Type != StorageTypes.Void ? ctx.AllocScratch(ec.Type) : (int?)null;
                ctx.Current.Instructions.Add(new CExprStmt(ec.With(new List<CLeaf>(ec.Args), dest)));
                return dest.HasValue ? new CSlotRef(dest.Value, ec.Type) : new CConst(null, StorageTypes.Void);
            }

            case CInternalCall ic:
            {
                // Reentrant AND TailSpared MUST be copied (see the CExternCall note above; TailSpared
                // is the round-9 [Y3] per-site spill exemption).
                int? dest = ic.Type != StorageTypes.Void ? ctx.AllocScratch(ic.Type) : (int?)null;
                ctx.Current.Instructions.Add(new CExprStmt(ic.With(new List<CLeaf>(ic.Args), dest)));
                return dest.HasValue ? new CSlotRef(dest.Value, ic.Type) : new CConst(null, StorageTypes.Void);
            }

            case CCrossCall cc: return LowerCrossCall(cc, ctx, null);

            default:
                throw new InvalidOperationException($"Unknown CValue: {expr.GetType().Name}");
        }
    }

    static CLeaf LowerCrossCall(CCrossCall cc, Ctx ctx, int? destination)
    {
        // Instance, param values, and the string-constant operands are all CLeaf — already flat.
        var inst = cc.Instance;

        foreach (var parameter in cc.Params)
        {
            ctx.Current.Instructions.Add(new CExprStmt(new CExternCall(
                ctx.Bind(ExternResolver.EventReceiverSetProgramVariable),
                new List<CLeaf> {
                    inst,
                    new CConst(parameter.Id, StorageTypes.String),
                    parameter.Value
                }, StorageTypes.Void, null)));
        }

        // Wave-12 r2 [V1]: a reentrant cross dispatch flags its SendCustomEvent as the §4.3 spill
        // site, with every param copy-in inside the spill window (PreSpillStmts — a same-program
        // reentrant callee shares the caller's param heap vars, so a copy-in that preceded the save
        // would be captured post-clobber). The copy-ins above are emitted back-to-back into the same
        // flat block, so the count is exact by construction.
        ctx.Current.Instructions.Add(new CExprStmt(new CExternCall(
            ctx.Bind(ExternResolver.EventReceiverSendCustomEvent),
            new List<CLeaf> { inst, cc.EventName }, StorageTypes.Void, null,
            cc.Reentrant, cc.Reentrant ? cc.Params.Count : 0)));

        if (cc.Returns.Count == 1)
        {
            var ret = cc.Returns[0];
            var dest = destination ?? ctx.AllocScratch(cc.Type);
            ctx.Current.Instructions.Add(new CExprStmt(new CExternCall(
                ctx.Bind(ExternResolver.EventReceiverGetProgramVariable),
                new List<CLeaf> { inst, new CConst(ret.Id, StorageTypes.String) }, cc.Type, dest)));
            return new CSlotRef(dest, cc.Type);
        }

        if (cc.Returns.Count > 1)
        {
            foreach (var ret in cc.Returns)
            {
                var dest = ctx.AllocScratch(ret.StorageType);
                ctx.Current.Instructions.Add(new CExprStmt(new CExternCall(
                    ctx.Bind(ExternResolver.EventReceiverGetProgramVariable),
                    new List<CLeaf> { inst, new CConst(ret.Id, StorageTypes.String) },
                    ret.StorageType, dest)));
            }
        }

        return new CConst(null, StorageTypes.Void);
    }

    static void LowerProgramVariableLoad(CProgramVariableLoad load, Ctx ctx, int destination)
        => ctx.Current.Instructions.Add(new CExprStmt(new CExternCall(
            ctx.Bind(ExternResolver.EventReceiverGetProgramVariable),
            new List<CLeaf> { load.Instance, load.VariableName }, load.Type, destination)));

    static void LowerSelect(CSelect sel, Ctx ctx, int destination)
    {
        // Cond/TrueVal/FalseVal are CLeaf operands (ANF) bound before the select — already flat. CSelect is
        // used only for PURE branches, so eagerly assigning the pre-bound branch leaf in each arm is correct.
        var trueBlock = ctx.NewBlock();
        var falseBlock = ctx.NewBlock();
        var mergeBlock = ctx.NewBlock();

        ctx.Current.Terminator = new CBranch(sel.Cond, trueBlock.Id, falseBlock.Id);

        ctx.Current = trueBlock;
        ctx.Current.Instructions.Add(new CAssign(destination, sel.TrueVal));
        ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = falseBlock;
        ctx.Current.Instructions.Add(new CAssign(destination, sel.FalseVal));
        ctx.Current.Terminator = new CJump(mergeBlock.Id);

        ctx.Current = mergeBlock;
    }

    // ── Lowering context ──

    sealed class Ctx
    {
        public readonly FlatFunction Func;
        public readonly UdonAbiCatalog AbiCatalog;
        public readonly Stack<(FlatBlock Exit, FlatBlock Continue)> LoopStack =
            new Stack<(FlatBlock, FlatBlock)>();
        public readonly Dictionary<string, FlatBlock> LabelBlocks =
            new Dictionary<string, FlatBlock>();
        public FlatBlock Current;

        public Ctx(FlatFunction f, UdonAbiCatalog abiCatalog)
        {
            Func = f;
            AbiCatalog = abiCatalog;
        }

        public BoundExtern Bind(UdonAbiKey signature) => AbiCatalog.Require(signature);

        public FlatBlock NewBlock() => Func.NewBlock();

        public int AllocScratch(StorageType type)
        {
            var id = Func.Slots.Count;
            Func.Slots.Add(new SlotDecl(id, type, SlotClass.Scratch));
            return id;
        }
    }
}
