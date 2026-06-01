using System;
using System.Collections.Generic;

// ============================================================================
// Core IR statement vocabulary (structured form) + flat-block form + Shape invariant.
// Phase 2 of "Core IR by absorption". Structured statements mirror HStmt 1:1 over CValue;
// the flat role (CBlock.Terminator + FlatId, CTerminator) mirrors LIR and is populated by
// CoreFlatten. The Shape enum makes the no-Phi structured-vs-flat boundary machine-checkable.
// Global namespace, C# 9.0-compatible (Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================

/// <summary>Authoritative form of a function body: Structured (pre-flatten) or Flat (post-flatten).
/// Flatten is the one one-way gate that sets Flat; every pass asserts its required Shape.</summary>
public enum Shape { Structured, Flat }

// ── Structured statements (12 kinds, mirror HStmt) ──

/// <summary>Base for Core IR structured statements.</summary>
public abstract class CStmt { }

/// <summary>Sequence of statements (structured role) OR a flat basic block (flat role:
/// Terminator + FlatId set, Stmts holds flat instructions only). Role governed by CFunction.Shape.</summary>
public sealed class CBlock : CStmt
{
    public readonly List<CStmt> Stmts = new List<CStmt>();
    public CTerminator Terminator; // null in structured role; set in flat role
    public int FlatId;             // basic-block id in flat role

    public CBlock() { }
    public CBlock(List<CStmt> stmts) => Stmts = stmts ?? new List<CStmt>();
}

/// <summary>Assign value to a slot: slot = value. [= HAssign]</summary>
public sealed class CAssign : CStmt
{
    public readonly int DestSlot;
    public readonly CValue Value;
    public CAssign(int destSlot, CValue value)
    {
        DestSlot = destSlot;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>Store value to a heap field. [= HStoreField]</summary>
public sealed class CStoreField : CStmt
{
    public readonly string FieldName;
    public readonly CValue Value;
    public CStoreField(string fieldName, CValue value)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>Structured if/else. [= HIf]</summary>
public sealed class CIf : CStmt
{
    public readonly CValue Cond;
    public readonly CBlock Then;
    public readonly CBlock Else;
    public CIf(CValue cond, CBlock thenBlock, CBlock elseBlock = null)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Then = thenBlock ?? new CBlock();
        Else = elseBlock ?? new CBlock();
    }
}

/// <summary>Structured while / do-while. CondBlock runs each iteration before Cond. [= HWhile]</summary>
public sealed class CWhile : CStmt
{
    public readonly CBlock CondBlock;
    public readonly CValue Cond;
    public readonly CBlock Body;
    public readonly bool IsDoWhile;
    public CWhile(CValue cond, CBlock body, bool isDoWhile = false, CBlock condBlock = null)
    {
        CondBlock = condBlock ?? new CBlock();
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Body = body ?? new CBlock();
        IsDoWhile = isDoWhile;
    }
}

/// <summary>Structured for loop. Cond null = infinite. [= HFor]</summary>
public sealed class CFor : CStmt
{
    public readonly CBlock Init;
    public readonly CBlock CondBlock;
    public readonly CValue Cond; // null = infinite
    public readonly CBlock Update;
    public readonly CBlock Body;
    public CFor(CBlock init, CValue cond, CBlock update, CBlock body, CBlock condBlock = null)
    {
        Init = init ?? new CBlock();
        CondBlock = condBlock ?? new CBlock();
        Cond = cond;
        Update = update ?? new CBlock();
        Body = body ?? new CBlock();
    }
}

/// <summary>Break the innermost loop/switch. [= HBreak]</summary>
public sealed class CBreak : CStmt { }

/// <summary>Continue the innermost loop. [= HContinue]</summary>
public sealed class CContinue : CStmt { }

/// <summary>Goto a named label. [= HGoto]</summary>
public sealed class CGoto : CStmt
{
    public readonly string Label;
    public CGoto(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));
}

/// <summary>Named label (goto target). [= HLabelStmt]</summary>
public sealed class CLabel : CStmt
{
    public readonly string Label;
    public CLabel(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));
}

/// <summary>Return with optional value. [= HReturn]</summary>
public sealed class CReturn : CStmt
{
    public readonly CValue Value; // null for void
    public CReturn(CValue value = null) => Value = value;
}

/// <summary>Expression used as a statement (side-effecting call etc.). [= HExprStmt]</summary>
public sealed class CExprStmt : CStmt
{
    public readonly CValue Expr;
    public CExprStmt(CValue expr) => Expr = expr ?? throw new ArgumentNullException(nameof(expr));
}

// ── Flat-block terminators (flat role only, mirror LTerminator) ──

/// <summary>Base for flat basic-block terminators.</summary>
public abstract class CTerminator { }

/// <summary>Unconditional jump. [= LJump]</summary>
public sealed class CJump : CTerminator
{
    public int TargetBlockId;
    public CJump(int targetBlockId) => TargetBlockId = targetBlockId;
}

/// <summary>Conditional branch. [= LBranch]</summary>
public sealed class CBranch : CTerminator
{
    public readonly CValue Cond;
    public int TrueBlockId;
    public int FalseBlockId;
    public CBranch(CValue cond, int trueBlockId, int falseBlockId)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueBlockId = trueBlockId;
        FalseBlockId = falseBlockId;
    }
}

/// <summary>Return terminator (flat role). Distinct from the CReturn statement. [= LReturn]</summary>
public sealed class CRet : CTerminator
{
    public readonly CValue Value; // null for void
    public CRet(CValue value = null) => Value = value;
}
