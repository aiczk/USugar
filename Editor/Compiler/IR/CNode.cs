using System;
using System.Collections.Generic;

// ============================================================================
// Structured Core IR statement vocabulary. Flat containers live in FlatIr.cs; CoreFlatten is the
// only conversion between the two phase-specific type families.
// Global namespace, C# 9.0-compatible (Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================


// ── Structured statements (12 kinds) ──

/// <summary>Base for Core IR structured statements.</summary>
public abstract class CStmt { }

/// <summary>Sequence of structured statements.</summary>
public sealed class StructuredBlock : CStmt
{
    public readonly List<CStmt> Stmts = new List<CStmt>();

    public StructuredBlock() { }
    public StructuredBlock(List<CStmt> stmts) => Stmts = stmts ?? new List<CStmt>();
}

/// <summary>Marker for the closed flat-instruction vocabulary accepted by <see cref="FlatBlock"/>.
/// Structured control-flow statements do not implement it and cannot enter a flat block.</summary>
public interface IFlatInstruction { }

/// <summary>Assign value to a slot: slot = value.</summary>
public sealed class CAssign : CStmt, IFlatInstruction
{
    public readonly int DestSlot;
    public readonly CValue Value;
    public CAssign(int destSlot, CValue value)
    {
        DestSlot = destSlot;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// Flat-only instruction produced from <see cref="CRepresentationCast"/>. It is intentionally not
/// an ordinary assignment: source and destination storage types may differ, and this node is the
/// sole structural proof that the mismatch came from an approved representation-preserving cast.
/// </summary>
public sealed class CRepresentationCopy : IFlatInstruction
{
    public readonly int DestSlot;
    public readonly CLeaf Source;
    public readonly StorageType DestinationType;
    public readonly RepresentationCastKind Kind;

    public CRepresentationCopy(int destSlot, CLeaf source,
        StorageType destinationType, RepresentationCastKind kind)
    {
        DestSlot = destSlot;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        DestinationType = destinationType;
        Kind = kind;
    }
}

/// <summary>Store value to a heap field.</summary>
public sealed class CStoreField : CStmt, IFlatInstruction
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
    public readonly StructuredBlock Then;
    public readonly StructuredBlock Else;
    public CIf(CLeaf cond, StructuredBlock thenBlock, StructuredBlock elseBlock = null)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Then = thenBlock ?? new StructuredBlock();
        Else = elseBlock ?? new StructuredBlock();
    }
}

/// <summary>Structured while / do-while. CondBlock runs each iteration before Cond.</summary>
public sealed class CWhile : CStmt
{
    public readonly StructuredBlock CondBlock;
    public readonly CLeaf Cond;
    public readonly StructuredBlock Body;
    public readonly bool IsDoWhile;
    public CWhile(CLeaf cond, StructuredBlock body, bool isDoWhile = false, StructuredBlock condBlock = null)
    {
        CondBlock = condBlock ?? new StructuredBlock();
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Body = body ?? new StructuredBlock();
        IsDoWhile = isDoWhile;
    }
}

/// <summary>Structured for loop. Cond null = infinite.</summary>
public sealed class CFor : CStmt
{
    public readonly StructuredBlock Init;
    public readonly StructuredBlock CondBlock;
    public readonly CLeaf Cond; // null = infinite
    public readonly StructuredBlock Update;
    public readonly StructuredBlock Body;
    public CFor(StructuredBlock init, CLeaf cond, StructuredBlock update, StructuredBlock body, StructuredBlock condBlock = null)
    {
        Init = init ?? new StructuredBlock();
        CondBlock = condBlock ?? new StructuredBlock();
        Cond = cond;
        Update = update ?? new StructuredBlock();
        Body = body ?? new StructuredBlock();
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
public sealed class CExprStmt : CStmt, IFlatInstruction
{
    public readonly CValue Expr;
    public CExprStmt(CValue expr) => Expr = expr ?? throw new ArgumentNullException(nameof(expr));
}

/// <summary>Flat instruction (flat role only): load a heap field into a slot. In structured
/// form a field read is a CFieldRef(Load) value; CoreFlatten materializes it into this.</summary>
public sealed class CLoadField : IFlatInstruction
{
    public readonly int DestSlot;
    public readonly string FieldName;
    public readonly StorageType Type;
    public CLoadField(int destSlot, string fieldName, StorageType type)
    {
        DestSlot = destSlot;
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Type = type;
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
    public readonly int TargetBlockId;
    public CJump(int targetBlockId) => TargetBlockId = targetBlockId;
}

/// <summary>Conditional branch.</summary>
public sealed class CBranch : CTerminator
{
    public readonly CLeaf Cond;
    public readonly int TrueBlockId;
    public readonly int FalseBlockId;
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

// Structured function and module

/// <summary>A function before CFG flattening.</summary>
public sealed class StructuredFunction
{
    public readonly string Name;
    public readonly string ExportName;
    public StructuredBlock Body = new StructuredBlock();
    public readonly List<SlotDecl> Slots = new List<SlotDecl>();
    public StorageType? ReturnType;
    public readonly List<string> ParamFieldNames = new List<string>();
    public readonly List<ReturnSlot> ReturnSlots = new List<ReturnSlot>();

    // Recursion frame-stack metadata (set during emit; consumed by the post-coalesce InsertRecursionSpills
    // pass). RecursiveCalleeNames = internal-call target names that are recursive edges FROM this function
    // (every such call needs a spill/reload). RecursionSpillFields = the named heap fields (params / frame
    // locals / struct receiver) that must be saved across any recursive call. The SLOTS to spill are NOT
    // listed here — they are computed per call site from post-coalesce liveness (only slots live across the
    // call), which is what keeps the spill bounded under A-normal form.
    public readonly HashSet<string> RecursiveCalleeNames = new HashSet<string>();
    public readonly List<(string Name, StorageType Type)> RecursionSpillFields = new List<(string, StorageType)>();

    /// <summary>Number of Reentrant-flagged call instructions this function must carry (design §4.3).
    /// Incremented by CoreBuilder when a flagged dispatch arm is emitted; decremented by CoreFlatten
    /// when an unreachable (dead-code) statement containing a flagged call is dropped. FlatVerify
    /// asserts the flat instruction stream carries exactly this many flags after every rebuild pass
    /// (CoreFlatten, CoalesceSlots/RemapInst, InsertRecursionSpills) — a silent flag loss would
    /// silently lose the recursion spill.</summary>
    public int ReentrantSiteCount;

    public StructuredFunction(string name, string exportName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ExportName = exportName;
    }

    /// <summary>Allocate a new slot and return its ID.</summary>
    public int NewSlot(StorageType type, SlotClass slotClass, string fixedName = null)
    {
        var id = Slots.Count;
        Slots.Add(new SlotDecl(id, type, slotClass, fixedName));
        return id;
    }

    /// <summary>UASM field name for the single return value (N=1 only).</summary>
    public string ReturnFieldName => ReturnSlots.Count == 1 ? ReturnSlots[0].Id : null;
}

/// <summary>Top-level Core IR module.</summary>
public sealed class StructuredModule
{
    public readonly List<StructuredFunction> Functions = new List<StructuredFunction>();
    public readonly List<FieldDecl> Fields = new List<FieldDecl>();
    public UdonTypeFactRegistry TypeFacts { get; private set; }
    BoundAbiPlan _abi;
    public string ClassName;

    public StructuredModule(UdonTypeFactRegistry typeFacts = null, UdonAbiCatalog abiCatalog = null)
    {
        TypeFacts = typeFacts ?? new UdonTypeFactRegistry();
        if (abiCatalog != null)
            _abi = BoundAbiPlan.ExactCatalog(abiCatalog);
    }

    internal BoundAbiPlan RequireAbi()
        => _abi ?? throw new InvalidOperationException(
            "The structured module ABI was not published.");

    internal void PublishSemantics(
        BoundAbiPlan abi,
        UdonTypeFactRegistry typeFacts)
    {
        if (_abi != null)
            throw new InvalidOperationException(
                "The structured module semantics were published twice.");
        if (typeFacts == null)
            throw new ArgumentNullException(nameof(typeFacts));
        if (!typeFacts.IsFrozen)
            throw new InvalidOperationException(
                "Structured IR requires a frozen type-fact snapshot.");
        _abi = abi ?? throw new ArgumentNullException(nameof(abi));
        TypeFacts = typeFacts;
    }

    public StructuredFunction AddFunction(string name, string exportName = null)
    {
        var func = new StructuredFunction(name, exportName);
        Functions.Add(func);
        return func;
    }
}

/// <summary>
/// Typed write to another Udon program's heap. This remains semantic Core IR until flattening so
/// the verifier can check the source value against the remote variable's declared storage type.
/// </summary>
public sealed class CProgramVariableStore : CStmt
{
    public readonly CLeaf Instance;
    public readonly CLeaf VariableName;
    public readonly StorageType VariableType;
    public readonly CLeaf Value;

    public CProgramVariableStore(CLeaf instance, CLeaf variableName,
        StorageType variableType, CLeaf value)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        VariableName = variableName ?? throw new ArgumentNullException(nameof(variableName));
        VariableType = variableType;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}
