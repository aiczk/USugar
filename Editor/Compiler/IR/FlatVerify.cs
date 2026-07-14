using System;
using System.Collections.Generic;

// ============================================================================
// FlatVerify — post-condition verifier for a flattened Core function. Re-arms, as a checked
// invariant, the guarantee that a separate flat-operand type would give for free (operands cannot
// nest calls) but which the unified CValue gives up.
// Asserts: (1) every block has exactly one terminator; (2) terminator targets exist;
// (3) flat-block instruction operands are leaves (no nested calls); (4) CFieldRef(Load) never
// appears as a flat operand (must be materialized via CLoadField); (5) CFieldRef(Addr) appears
// only as a direct extern/internal-call argument; (6) no structured statement leaked into a flat block.
// ============================================================================

public static class FlatVerify
{
    public static void Verify(CFunction f)
    {
        if (f.Shape != Shape.Flat)
            throw new InvalidOperationException($"FlatVerify requires Shape=Flat, got {f.Shape} for {f.Name}");

        var blockIds = new HashSet<int>();
        foreach (var b in f.FlatBlocks)
        {
            if (!blockIds.Add(b.Id))
                throw new InvalidOperationException($"Duplicate flat block id {b.Id} in {f.Name}");
        }

        foreach (var b in f.FlatBlocks)
        {
            if (b.Terminator == null)
                throw new InvalidOperationException($"Flat block {b.Id} in {f.Name} has no terminator");

            foreach (var inst in b.Stmts)
                VerifyInstruction(inst, f.Name);

            VerifyTerminator(b.Terminator, blockIds, f.Name);
        }

        VerifyReentrantConservation(f);
        VerifyPreSpillStmtsShape(f);
    }

    /// <summary>PreSpillStmts positional contract (wave-12 r2 [V1], see <see cref="CExternCall.PreSpillStmts"/>):
    /// a Reentrant SendCustomEvent's PreSpillStmts=N claims the N statements immediately preceding it in the
    /// same flat block are its own param copy-ins — void SetProgramVariable extern calls — so
    /// InsertRecursionSpillsFunc can pull them inside the spill window. Nothing else confirmed that shape
    /// before; a future edit inserting a statement between a hand-emitted copy-in/dispatch pair (e.g.
    /// HandlerBase's interface-setter dispatch) would silently desync the count from reality. Checked
    /// structurally rather than trusting the producer, mirroring VerifyReentrantConservation's stance on
    /// the sibling Reentrant flag.</summary>
    static void VerifyPreSpillStmtsShape(CFunction f)
    {
        foreach (var b in f.FlatBlocks)
        {
            for (int i = 0; i < b.Stmts.Count; i++)
            {
                if (b.Stmts[i] is not CExprStmt { Expr: CExternCall { PreSpillStmts: > 0 } ec }) continue;
                int n = ec.PreSpillStmts;
                if (n > i)
                    throw new InvalidOperationException(
                        $"{f.Name}: block {b.Id} instruction {i}: PreSpillStmts={n} exceeds the {i} statement(s) " +
                        "available before it (a rebuild pass moved or dropped the copy-ins)");

                for (int k = i - n; k < i; k++)
                {
                    if (!IsVoidSetProgramVariableCopyIn(b.Stmts[k]))
                        throw new InvalidOperationException(
                            $"{f.Name}: block {b.Id} instruction {i}: PreSpillStmts={n} expects a void " +
                            $"SetProgramVariable copy-in at index {k}, found {b.Stmts[k]?.GetType().Name} " +
                            "(the spill window would capture the wrong statements)");
                }
            }
        }
    }

    static bool IsVoidSetProgramVariableCopyIn(CStmt stmt)
        => stmt is CExprStmt { Expr: CExternCall { Type: "SystemVoid", DestSlot: null } call }
           && call.Sig == ExternResolver.EventReceiverSetProgramVariable;

    /// <summary>Reentrant-flag conservation (design §4.3): CoreFlatten and CoalesceSlots/RemapInst both
    /// REBUILD call instructions, so a rebuild that forgets to copy the flag silently loses the
    /// dispatch-site recursion spill — exactly the failure object-identity marking died of. The flat
    /// instruction stream must carry exactly CFunction.ReentrantSiteCount flags (creation-counted by
    /// CoreBuilder, dead-code-adjusted by CoreFlatten).</summary>
    static void VerifyReentrantConservation(CFunction f)
    {
        int flagged = 0;
        foreach (var b in f.FlatBlocks)
            foreach (var inst in b.Stmts)
                if (inst is CExprStmt es
                    && (es.Expr is CExternCall { Reentrant: true } || es.Expr is CInternalCall { Reentrant: true }))
                    flagged++;
        if (flagged != f.ReentrantSiteCount)
            throw new InvalidOperationException(
                $"{f.Name}: Reentrant dispatch-site flag conservation violated — expected {f.ReentrantSiteCount} flagged call(s), found {flagged} (a rebuild pass dropped or duplicated the flag)");
    }

    static void VerifyInstruction(CStmt inst, string fn)
    {
        switch (inst)
        {
            case CAssign a: RequireLeaf(a.Value, fn, "assign value"); break;
            case CStoreField sf: RequireLeaf(sf.Value, fn, "store value"); break;
            case CLoadField _: break;
            case CExprStmt es:
                switch (es.Expr)
                {
                    case CExternCall ec:
                        foreach (var arg in ec.Args) RequireLeaf(arg, fn, "extern arg", allowAddr: true);
                        break;
                    case CInternalCall ic:
                        foreach (var arg in ic.Args) RequireLeaf(arg, fn, "internal-call arg", allowAddr: true);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"{fn}: flat CExprStmt must wrap a call, got {es.Expr?.GetType().Name}");
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"{fn}: structured statement {inst?.GetType().Name} leaked into a flat block");
        }
    }

    static void VerifyTerminator(CTerminator t, HashSet<int> blockIds, string fn)
    {
        switch (t)
        {
            case CJump j: RequireBlock(j.TargetBlockId, blockIds, fn); break;
            case CBranch br:
                RequireLeaf(br.Cond, fn, "branch condition");
                RequireBlock(br.TrueBlockId, blockIds, fn);
                RequireBlock(br.FalseBlockId, blockIds, fn);
                break;
            case CRet ret:
                if (ret.Value != null) RequireLeaf(ret.Value, fn, "return value");
                break;
            default:
                throw new InvalidOperationException($"{fn}: unknown terminator {t?.GetType().Name}");
        }
    }

    static void RequireLeaf(CValue v, string fn, string ctx, bool allowAddr = false)
    {
        switch (v)
        {
            case CSlotRef _:
            case CConst _:
            case CFuncRef _:
                break;
            case CFieldAddr _:
                if (!allowAddr)
                    throw new InvalidOperationException(
                        $"{fn}: {ctx}: CFieldAddr is only valid as a direct extern/internal-call argument");
                break;
            case CFieldLoad _:
                throw new InvalidOperationException(
                    $"{fn}: {ctx}: CFieldLoad is not a flat leaf (must be materialized via CLoadField)");
            default:
                throw new InvalidOperationException(
                    $"{fn}: {ctx}: operand must be a leaf, got nested {v?.GetType().Name}");
        }
    }

    static void RequireBlock(int id, HashSet<int> blockIds, string fn)
    {
        if (!blockIds.Contains(id))
            throw new InvalidOperationException($"{fn}: terminator targets nonexistent block {id}");
    }
}
