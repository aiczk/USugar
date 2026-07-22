using System;
using System.Collections.Generic;

// ============================================================================
// Core IR value vocabulary. CLeaf (CSlotRef/CConst/CFuncRef/CFieldAddr) are the operand-safe leaves;
// the value-producing ops (CFieldLoad/CExternCall/CInternalCall/CSelect/CCrossCall) are bound to a
// fresh slot at construction (A-normal form), so they appear only as a CAssign RHS or a CExprStmt
// side-effect — never nested in an operand position.
// Global namespace + plain sealed classes / readonly fields
// (must stay C# 9.0-compatible: Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================

/// <summary>Base class for all Core IR values. Every value has a result type.</summary>
public abstract class CValue
{
    public readonly StorageType Type;
    protected CValue(StorageType type) => Type = type;
}

/// <summary>A value safe to use as an operand: pure, side-effect-free, order-stable (re-reading it
/// yields the same value regardless of intervening writes). THE A-normal-form invariant: every
/// operand position is typed <see cref="CLeaf"/>, so a value-producing op cannot nest in an operand —
/// it must first be bound to a slot. Leaves: CSlotRef / CConst / CFuncRef / CFieldAddr.</summary>
public abstract class CLeaf : CValue
{
    protected CLeaf(StorageType type) : base(type) { }
}

/// <summary>Reference to a virtual slot. Scratch slots are single-assignment under ANF → stable leaf.</summary>
public sealed class CSlotRef : CLeaf
{
    public readonly int SlotId;
    public CSlotRef(int slotId, StorageType type) : base(type) => SlotId = slotId;
    public override string ToString() => $"slot{SlotId}:{Type}";
}

/// <summary>Compile-time constant value.</summary>
public sealed class CConst : CLeaf
{
    public readonly object Value; // null for default/null literal
    public CConst(object value, StorageType type) : base(type) => Value = value;
    public override string ToString() => $"const({Value ?? "null"}):{Type}";
}

/// <summary>Read a heap field's value. Producer (NOT a leaf): re-reading after a write to the same
/// field observes the new value, so under ANF it is materialized to a slot at its read point.</summary>
public sealed class CFieldLoad : CValue
{
    public readonly string FieldName;
    public CFieldLoad(string fieldName, StorageType type) : base(type)
        => FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
    public override string ToString() => $"load [{FieldName}]:{Type}";
}

/// <summary>Heap address of a field, for extern out/ref parameters. A reference, not a value-read,
/// so it is a leaf — it appears only in out/ref argument positions.</summary>
public sealed class CFieldAddr : CLeaf
{
    public readonly string FieldName;
    public CFieldAddr(string fieldName, StorageType type) : base(type)
        => FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
    public override string ToString() => $"addr [{FieldName}]:{Type}";
}

/// <summary>Reference to a function entry point (delegate / JUMP_INDIRECT).</summary>
public sealed class CFuncRef : CLeaf
{
    public readonly string FuncName;
    public CFuncRef(string funcName) : base("SystemUInt32")
        => FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
    public override string ToString() => $"funcref({FuncName})";
}

/// <summary>Call an extern (Udon VM native) function. Value-producing op: may nest in args
/// (tree role, DestSlot null) or write a scratch slot (flat role, DestSlot set).
///</summary>
public sealed class CExternCall : CValue
{
    public readonly string Sig;
    public readonly List<CLeaf> Args;
    public readonly int? DestSlot; // null in tree role; set in flat (instruction) role
    /// <summary>Design §4.3: this call is a delegate-dispatch site that can re-enter its containing
    /// function (the cross-arm SendCustomEvent of a marked dispatch). InsertRecursionSpills wraps
    /// flagged instructions with the __recurStack frame spill/reload. The flag MUST be copied by every
    /// site that reconstructs the instruction (CoreFlatten.LowerExpr, CoreFlatOptimizer.RemapInst) —
    /// FlatVerify checks conservation against CFunction.ReentrantSiteCount.</summary>
    public readonly bool Reentrant;
    /// <summary>Wave-12 r2 [V1]: number of IMMEDIATELY-PRECEDING statements (the cross-call
    /// convention's SetProgramVariable copy-ins, same flat block by construction) that must sit
    /// INSIDE this Reentrant site's spill window. A same-program reentrant callee shares the
    /// caller's param heap vars (self-recursion through a variable receiver), so a copy-in write
    /// that precedes the save would be captured post-clobber and the reload would restore the
    /// clobbered value. Only ever non-zero on a Reentrant SendCustomEvent lowered from a reentrant
    /// CCrossCall / cross setter pair; copied by the same rebuild sites as <see cref="Reentrant"/>.</summary>
    public readonly int PreSpillStmts;

    public CExternCall(string sig, List<CLeaf> args, string retType, int? destSlot = null, bool reentrant = false, int preSpillStmts = 0) : base(retType)
    {
        Sig = sig ?? throw new ArgumentNullException(nameof(sig));
        Args = args ?? new List<CLeaf>();
        DestSlot = destSlot;
        Reentrant = reentrant;
        PreSpillStmts = preSpillStmts;
    }

    /// <summary>Clone with new args/destSlot, copying Sig/Type/Reentrant/PreSpillStmts by construction —
    /// the two call-rebuild sites (CoreFlatten.LowerExpr, CoreFlatOptimizer.RemapInst) route through this
    /// instead of hand-copying the constructor argument list, so a new field can never drift out of sync.</summary>
    public CExternCall With(List<CLeaf> args, int? destSlot) => new CExternCall(Sig, args, Type, destSlot, Reentrant, PreSpillStmts);

    public override string ToString()
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        return $"{dest}extern \"{Sig}\"({string.Join(", ", Args)}):{Type}";
    }
}

/// <summary>Call an internal (user-defined) function.</summary>
public sealed class CInternalCall : CValue
{
    public readonly string FuncName;
    public readonly List<CLeaf> Args;
    public readonly int? DestSlot;
    /// <summary>Design §4.3: this call is a delegate-dispatch site that can re-enter its containing
    /// function (the self-arm __indirect of a marked dispatch). See <see cref="CExternCall.Reentrant"/>.</summary>
    public readonly bool Reentrant;
    /// <summary>Wave-9 round-9 [Y3]: this is a recursive-edge call SITE in tail position — the frame
    /// reads nothing after it, so InsertRecursionSpills must NOT wrap it even though the callee name
    /// is in RecursiveCalleeNames (one non-tail site used to make every site of that callee spill,
    /// overflowing the 8192-entry __recurStack on deep mixed tail/non-tail recursion). Must be copied
    /// by every site that reconstructs the instruction (CoreFlatten.LowerExpr,
    /// CoreFlatOptimizer.RemapInst), like <see cref="Reentrant"/>.</summary>
    public readonly bool TailSpared;

    public CInternalCall(string funcName, List<CLeaf> args, string retType, int? destSlot = null,
        bool reentrant = false, bool tailSpared = false) : base(retType)
    {
        FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
        Args = args ?? new List<CLeaf>();
        DestSlot = destSlot;
        Reentrant = reentrant;
        TailSpared = tailSpared;
    }

    /// <summary>Clone with new args/destSlot, copying FuncName/Type/Reentrant/TailSpared by construction —
    /// see <see cref="CExternCall.With"/> for why the two rebuild sites route through this.</summary>
    public CInternalCall With(List<CLeaf> args, int? destSlot) => new CInternalCall(FuncName, args, Type, destSlot, Reentrant, TailSpared);

    public override string ToString()
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        return $"{dest}call {FuncName}({string.Join(", ", Args)}):{Type}";
    }
}

/// <summary>Ternary select: cond ? trueVal : falseVal. Structured-only Core node — has no flat
/// operand form; it is expanded to branch blocks in CoreFlatten.</summary>
public sealed class CSelect : CValue
{
    public readonly CLeaf Cond;
    public readonly CLeaf TrueVal;
    public readonly CLeaf FalseVal;

    public CSelect(CLeaf cond, CLeaf trueVal, CLeaf falseVal, string type) : base(type)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueVal = trueVal ?? throw new ArgumentNullException(nameof(trueVal));
        FalseVal = falseVal ?? throw new ArgumentNullException(nameof(falseVal));
    }

    public override string ToString() => $"select({Cond}, {TrueVal}, {FalseVal}):{Type}";
}

/// <summary>Cross-behaviour call (SetProgramVariable* + SendCustomEvent + GetProgramVariable*).
/// Stays opaque/atomic until expanded in CoreFlatten — never exposed to structured optimizers.
///</summary>
public sealed class CCrossCall : CValue
{
    public readonly CLeaf Instance;
    public readonly string EventName;
    public readonly List<(string ParamName, CLeaf Value)> Params; // SetProgramVariable pairs
    public readonly IReadOnlyList<ReturnSlot> Returns;             // empty for void
    /// <summary>Wave-12 r2 [V1]: this cross dispatch can land back on THIS program's own recursion
    /// cycle (same-typed / base-typed / interface-typed variable receiver holding `this` at runtime)
    /// — LowerCrossCall marks the SendCustomEvent Reentrant (with the param copy-ins inside the
    /// spill window via <see cref="CExternCall.PreSpillStmts"/>). Counted into
    /// CFunction.ReentrantSiteCount at the CoreBuilder.CrossCall creation choke point.</summary>
    public readonly bool Reentrant;

    public CCrossCall(CLeaf instance, string eventName,
        List<(string, CLeaf)> parameters, IReadOnlyList<ReturnSlot> returns, string retType,
        bool reentrant = false) : base(retType)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        EventName = eventName ?? throw new ArgumentNullException(nameof(eventName));
        Params = parameters ?? new List<(string, CLeaf)>();
        Returns = returns ?? Array.Empty<ReturnSlot>();
        Reentrant = reentrant;
    }

    public override string ToString() =>
        $"cross_call {Instance}.{EventName}({string.Join(", ", Params.ConvertAll(p => p.ParamName))}):{Type}";
}
