using System;
using System.Collections.Generic;

// ============================================================================
// Core IR statement vocabulary (structured form) + flat-block form + Shape invariant.
// Structured statements are the authored form over CValue; the flat role (CBlock.Terminator + Id,
// CTerminator) is the lowered form populated by CoreFlatten. The Shape enum makes the no-Phi
// structured-vs-flat boundary machine-checkable.
// Global namespace, C# 9.0-compatible (Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================

/// <summary>Authoritative form of a function body: Structured (pre-flatten) or Flat (post-flatten).
/// Flatten is the one one-way gate that sets Flat; every pass asserts its required Shape.</summary>
public enum Shape { Structured, Flat }

// ── Structured statements (12 kinds) ──

/// <summary>Base for Core IR structured statements.</summary>
public abstract class CStmt { }

/// <summary>Sequence of statements (structured role) OR a flat basic block (flat role:
/// Terminator + Id set, Stmts holds flat instructions only). Role governed by CFunction.Shape.</summary>
public sealed class CBlock : CStmt
{
    public readonly List<CStmt> Stmts = new List<CStmt>();
    public CTerminator Terminator; // null in structured role; set in flat role
    public int Id;             // basic-block id in flat role
    public string Hint;            // optional label-name hint for codegen (flat role)

    public CBlock() { }
    public CBlock(List<CStmt> stmts) => Stmts = stmts ?? new List<CStmt>();
}

/// <summary>Assign value to a slot: slot = value.</summary>
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

/// <summary>Store value to a heap field.</summary>
public sealed class CStoreField : CStmt
{
    public readonly string FieldName;
    public readonly CLeaf Value;
    public CStoreField(string fieldName, CLeaf value)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>Structured if/else.</summary>
public sealed class CIf : CStmt
{
    public readonly CLeaf Cond;
    public readonly CBlock Then;
    public readonly CBlock Else;
    public CIf(CLeaf cond, CBlock thenBlock, CBlock elseBlock = null)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Then = thenBlock ?? new CBlock();
        Else = elseBlock ?? new CBlock();
    }
}

/// <summary>Structured while / do-while. CondBlock runs each iteration before Cond.</summary>
public sealed class CWhile : CStmt
{
    public readonly CBlock CondBlock;
    public readonly CLeaf Cond;
    public readonly CBlock Body;
    public readonly bool IsDoWhile;
    public CWhile(CLeaf cond, CBlock body, bool isDoWhile = false, CBlock condBlock = null)
    {
        CondBlock = condBlock ?? new CBlock();
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Body = body ?? new CBlock();
        IsDoWhile = isDoWhile;
    }
}

/// <summary>Structured for loop. Cond null = infinite.</summary>
public sealed class CFor : CStmt
{
    public readonly CBlock Init;
    public readonly CBlock CondBlock;
    public readonly CLeaf Cond; // null = infinite
    public readonly CBlock Update;
    public readonly CBlock Body;
    public CFor(CBlock init, CLeaf cond, CBlock update, CBlock body, CBlock condBlock = null)
    {
        Init = init ?? new CBlock();
        CondBlock = condBlock ?? new CBlock();
        Cond = cond;
        Update = update ?? new CBlock();
        Body = body ?? new CBlock();
    }
}

/// <summary>Break the innermost loop/switch.</summary>
public sealed class CBreak : CStmt { }

/// <summary>Continue the innermost loop.</summary>
public sealed class CContinue : CStmt { }

/// <summary>Goto a named label.</summary>
public sealed class CGoto : CStmt
{
    public readonly string Label;
    public CGoto(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));
}

/// <summary>Named label (goto target).</summary>
public sealed class CLabel : CStmt
{
    public readonly string Label;
    public CLabel(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));
}

/// <summary>Return with optional value.</summary>
public sealed class CReturn : CStmt
{
    public readonly CLeaf Value; // null for void
    public CReturn(CLeaf value = null) => Value = value;
}

/// <summary>Expression used as a statement (side-effecting call etc.).</summary>
public sealed class CExprStmt : CStmt
{
    public readonly CValue Expr;
    public CExprStmt(CValue expr) => Expr = expr ?? throw new ArgumentNullException(nameof(expr));
}

/// <summary>Flat instruction (flat role only): load a heap field into a slot. In structured
/// form a field read is a CFieldRef(Load) value; CoreFlatten materializes it into this.</summary>
public sealed class CLoadField : CStmt
{
    public readonly int DestSlot;
    public readonly string FieldName;
    public readonly string Type;
    public CLoadField(int destSlot, string fieldName, string type)
    {
        DestSlot = destSlot;
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }
}

// ── Flat-block terminators (flat role only) ──

/// <summary>Base for flat basic-block terminators.</summary>
public abstract class CTerminator
{
    /// <summary>THE successor enumeration of the flat CFG — the single shared edge source for
    /// CoreFlatOptimizer's liveness/RPO and CoreToUasm's RPO linearization (formerly two hand-kept
    /// copies). Exhaustive over the terminator kinds; an unknown kind throws rather than silently
    /// yielding no edges, which would truncate liveness and let CoalesceSlots merge live slots.</summary>
    public static int[] GetSuccessors(CTerminator term) => term switch
    {
        CJump j => new[] { j.TargetBlockId },
        CBranch b => new[] { b.TrueBlockId, b.FalseBlockId },
        CRet => Array.Empty<int>(), // function exit — no successors
        _ => throw new VerificationException(
            $"Unknown CTerminator kind in CTerminator.GetSuccessors: {(term == null ? "null" : term.GetType().Name)}"),
    };
}

/// <summary>Unconditional jump.</summary>
public sealed class CJump : CTerminator
{
    public int TargetBlockId;
    public CJump(int targetBlockId) => TargetBlockId = targetBlockId;
}

/// <summary>Conditional branch.</summary>
public sealed class CBranch : CTerminator
{
    public readonly CLeaf Cond;
    public int TrueBlockId;
    public int FalseBlockId;
    public CBranch(CLeaf cond, int trueBlockId, int falseBlockId)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueBlockId = trueBlockId;
        FalseBlockId = falseBlockId;
    }
}

/// <summary>Return terminator (flat role). Distinct from the CReturn statement.</summary>
public sealed class CRet : CTerminator
{
    public readonly CLeaf Value; // null for void
    public CRet(CLeaf value = null) => Value = value;
}

// ── Function (unified) ──

/// <summary>A function in the unified Core IR. Structured <see cref="Body"/> is authoritative
/// when Shape=Structured; <see cref="FlatBlocks"/> when Shape=Flat. CoreFlatten sets FlatBlocks,
/// appends scratch to Slots, and sets Shape=Flat.</summary>
public sealed class CFunction
{
    public readonly string Name;
    public readonly string ExportName;
    public CBlock Body = new CBlock();                            // structured form
    public readonly List<CBlock> FlatBlocks = new List<CBlock>(); // flat form (post-flatten)
    public readonly List<SlotDecl> Slots = new List<SlotDecl>();
    public string ReturnType;
    public readonly List<string> ParamFieldNames = new List<string>();
    public readonly List<ReturnSlot> ReturnSlots = new List<ReturnSlot>();
    public Shape Shape = Shape.Structured;

    // Recursion frame-stack metadata (set during emit; consumed by the post-coalesce InsertRecursionSpills
    // pass). RecursiveCalleeNames = internal-call target names that are recursive edges FROM this function
    // (every such call needs a spill/reload). RecursionSpillFields = the named heap fields (params / frame
    // locals / struct receiver) that must be saved across any recursive call. The SLOTS to spill are NOT
    // listed here — they are computed per call site from post-coalesce liveness (only slots live across the
    // call), which is what keeps the spill bounded under A-normal form.
    public readonly HashSet<string> RecursiveCalleeNames = new HashSet<string>();
    public readonly List<(string Name, string Type)> RecursionSpillFields = new List<(string, string)>();

    /// <summary>Number of Reentrant-flagged call instructions this function must carry (design §4.3).
    /// Incremented by CoreBuilder when a flagged dispatch arm is emitted; decremented by CoreFlatten
    /// when an unreachable (dead-code) statement containing a flagged call is dropped. FlatVerify
    /// asserts the flat instruction stream carries exactly this many flags after every rebuild pass
    /// (CoreFlatten, CoalesceSlots/RemapInst, InsertRecursionSpills) — a silent flag loss would
    /// silently lose the recursion spill.</summary>
    public int ReentrantSiteCount;

    public CFunction(string name, string exportName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ExportName = exportName;
    }

    /// <summary>Allocate a new slot and return its ID.</summary>
    public int NewSlot(string type, SlotClass slotClass, string fixedName = null)
    {
        var id = Slots.Count;
        Slots.Add(new SlotDecl(id, type, slotClass, fixedName));
        return id;
    }

    int _nextBlockId;

    /// <summary>Allocate a fresh flat block with the next sequential id and append it to
    /// <see cref="FlatBlocks"/>. The flat-role counterpart of slot allocation.</summary>
    public CBlock NewBlock()
    {
        var block = new CBlock { Id = _nextBlockId++ };
        FlatBlocks.Add(block);
        return block;
    }

    /// <summary>First flat block (flat role).</summary>
    public CBlock Entry => FlatBlocks.Count > 0 ? FlatBlocks[0] : null;

    /// <summary>UASM field name for the single return value (N=1 only).</summary>
    public string ReturnFieldName => ReturnSlots.Count == 1 ? ReturnSlots[0].Id : null;
}

/// <summary>Top-level Core IR module.</summary>
public sealed class CModule
{
    public readonly List<CFunction> Functions = new List<CFunction>();
    public readonly List<FieldDecl> Fields = new List<FieldDecl>();
    public string ClassName;

    public CFunction AddFunction(string name, string exportName = null)
    {
        var func = new CFunction(name, exportName);
        Functions.Add(func);
        return func;
    }
}
