using System;
using System.Collections.Generic;

// ============================================================================
// FlatVerify — post-condition verifier for a flattened Core function (Phase 2 of
// "Core IR by absorption"). Re-arms, as a checked invariant, the compile-time guarantee that
// LIR's LOperand gave for free (operands cannot nest calls), which the unified CValue gives up.
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
            if (!blockIds.Add(b.FlatId))
                throw new InvalidOperationException($"Duplicate flat block id {b.FlatId} in {f.Name}");
        }

        foreach (var b in f.FlatBlocks)
        {
            if (b.Terminator == null)
                throw new InvalidOperationException($"Flat block {b.FlatId} in {f.Name} has no terminator");

            foreach (var inst in b.Stmts)
                VerifyInstruction(inst, f.Name);

            VerifyTerminator(b.Terminator, blockIds, f.Name);
        }
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
            case CFieldRef fr when fr.Mode == CFieldMode.Addr:
                if (!allowAddr)
                    throw new InvalidOperationException(
                        $"{fn}: {ctx}: CFieldRef(Addr) is only valid as a direct extern/internal-call argument");
                break;
            case CFieldRef _:
                throw new InvalidOperationException(
                    $"{fn}: {ctx}: CFieldRef(Load) is not a flat leaf (must be materialized via CLoadField)");
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
