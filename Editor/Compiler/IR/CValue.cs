using System;
using System.Collections.Generic;

// ============================================================================
// CFG value vocabulary. CLeaf values are operand-safe; calls are instruction payloads whose
// non-void results always name an explicit destination slot.
// Global namespace + plain sealed classes / readonly fields
// (must stay C# 9.0-compatible: Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================

/// <summary>Base class for typed CFG operands and call payloads.</summary>
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

/// <summary>
/// Why a raw representation copy is required. This is deliberately a closed vocabulary: adding a
/// new bypass of ordinary storage compatibility requires an explicit compiler-level decision.
/// </summary>
public enum RepresentationCastKind
{
    ClosedGenericObjectCast,
    EnumRepresentation,
    VerifiedUdonBehaviourComponent,
    ArraySetValueUnbox,

    /// <summary>An SDK component query answered through its erased `(System.Type)` overload, whose
    /// registered return is the `Component` base. The baked type token IS the narrowing proof: the
    /// SDK getter filters on it, so the returned instance is that type or null.</summary>
    ErasedComponentQueryResult,
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
    public CFuncRef(string funcName) : base(StorageTypes.UInt32)
        => FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
    public override string ToString() => $"funcref({FuncName})";
}

/// <summary>
/// Post-optimizer proof carried by a recursive/reentrant call site.
/// </summary>
public enum RecursionFrameState
{
    Unprotected,
    Protected,
}

/// <summary>Call an extern (Udon VM native) function.</summary>
public sealed class CExternCall : CValue
{
    public readonly BoundExtern Sig;
    public readonly IReadOnlyList<CLeaf> Args;
    public readonly int? DestSlot;
    /// <summary>Design §4.3: this call is a delegate-dispatch site that can re-enter its containing
    /// function (the cross-arm SendCustomEvent of a marked dispatch). InsertRecursionSpills wraps
    /// flagged instructions with the __recurStack frame spill/reload. The flag MUST be copied by every
    /// site that reconstructs the instruction. FlatVerify checks conservation against
    /// FlatFunction.ReentrantSiteCount.</summary>
    public readonly bool Reentrant;
    /// <summary>Wave-12 r2 [V1]: number of IMMEDIATELY-PRECEDING statements (the cross-call
    /// convention's SetProgramVariable copy-ins, same flat block by construction) that must sit
    /// INSIDE this Reentrant site's spill window. A same-program reentrant callee shares the
    /// caller's param heap vars (self-recursion through a variable receiver), so a copy-in write
    /// that precedes the save would be captured post-clobber and the reload would restore the
    /// clobbered value. Only ever non-zero on a Reentrant SendCustomEvent lowered from a reentrant
    /// reentrant cross dispatch / cross setter pair; copied by the same rebuild sites as
    /// <see cref="Reentrant"/>.</summary>
    public readonly int PreSpillStmts;
    public readonly RecursionFrameState FrameState;

    public CExternCall(BoundExtern sig, List<CLeaf> args, StorageType retType,
        int? destSlot = null, bool reentrant = false,
        int preSpillStmts = 0)
        : this(
            sig, args, retType, destSlot, reentrant, preSpillStmts,
            RecursionFrameState.Unprotected)
    {
    }

    CExternCall(
        BoundExtern sig,
        List<CLeaf> args,
        StorageType retType,
        int? destSlot,
        bool reentrant,
        int preSpillStmts,
        RecursionFrameState frameState)
        : base(retType)
    {
        Sig = sig ?? throw new ArgumentNullException(nameof(sig));
        Args = (args ?? new List<CLeaf>()).AsReadOnly();
        DestSlot = destSlot;
        Reentrant = reentrant;
        PreSpillStmts = preSpillStmts;
        FrameState = frameState;
    }

    /// <summary>Clone with new args/destSlot, copying Sig/Type/Reentrant/PreSpillStmts by construction —
    /// call-rebuild sites route through this
    /// instead of hand-copying the constructor argument list, so a new field can never drift out of sync.</summary>
    public CExternCall With(List<CLeaf> args, int? destSlot)
        => new CExternCall(
            Sig, args, Type, destSlot, Reentrant, PreSpillStmts,
            FrameState);

    internal CExternCall WithFrameState(
        RecursionFrameState frameState)
        => new CExternCall(
            Sig, new List<CLeaf>(Args), Type, DestSlot, Reentrant,
            PreSpillStmts, frameState);

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
    public readonly IReadOnlyList<CLeaf> Args;
    public readonly int? DestSlot;
    /// <summary>Design §4.3: this call is a delegate-dispatch site that can re-enter its containing
    /// function (the self-arm __indirect of a marked dispatch). See <see cref="CExternCall.Reentrant"/>.</summary>
    public readonly bool Reentrant;
    /// <summary>Wave-9 round-9 [Y3]: this is a recursive-edge call SITE in tail position — the frame
    /// reads nothing after it, so InsertRecursionSpills must NOT wrap it even though the callee name
    /// is in RecursiveCalleeNames (one non-tail site used to make every site of that callee spill,
    /// overflowing the 8192-entry __recurStack on deep mixed tail/non-tail recursion). Must be copied
    /// by every site that reconstructs the instruction, like <see cref="Reentrant"/>.</summary>
    public readonly bool TailSpared;
    public readonly RecursionFrameState FrameState;

    public CInternalCall(string funcName, List<CLeaf> args, StorageType retType, int? destSlot = null,
        bool reentrant = false, bool tailSpared = false)
        : this(
            funcName, args, retType, destSlot, reentrant, tailSpared,
            RecursionFrameState.Unprotected)
    {
    }

    CInternalCall(
        string funcName,
        List<CLeaf> args,
        StorageType retType,
        int? destSlot,
        bool reentrant,
        bool tailSpared,
        RecursionFrameState frameState)
        : base(retType)
    {
        FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
        Args = (args ?? new List<CLeaf>()).AsReadOnly();
        DestSlot = destSlot;
        Reentrant = reentrant;
        TailSpared = tailSpared;
        FrameState = frameState;
    }

    /// <summary>Clone with new args/destSlot, copying FuncName/Type/Reentrant/TailSpared by construction —
    /// see <see cref="CExternCall.With"/> for why the two rebuild sites route through this.</summary>
    public CInternalCall With(List<CLeaf> args, int? destSlot)
        => new CInternalCall(
            FuncName, args, Type, destSlot, Reentrant, TailSpared,
            FrameState);

    internal CInternalCall WithFrameState(
        RecursionFrameState frameState)
        => new CInternalCall(
            FuncName, new List<CLeaf>(Args), Type, DestSlot, Reentrant,
            TailSpared, frameState);

    public override string ToString()
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        return $"{dest}call {FuncName}({string.Join(", ", Args)}):{Type}";
    }
}

/// <summary>One typed parameter transported by a cross-behaviour call.</summary>
public sealed class CrossCallParameter
{
    public readonly int Ordinal;
    public readonly string Id;
    public readonly StorageType StorageType;
    public readonly CLeaf Value;

    public CrossCallParameter(int ordinal, string id, StorageType storageType, CLeaf value)
    {
        Ordinal = ordinal;
        Id = id;
        StorageType = storageType;
        Value = value;
    }

    public override string ToString() => $"{Ordinal}:{Id}:{StorageType}";
}

/// <summary>
/// Complete typed heap/event transport ABI for one cross-program call. The CFG builder validates
/// this plan before materializing its copy-in, dispatch, and copy-out instructions.
/// </summary>
public sealed class CrossCallTransportPlan
{
    public readonly CLeaf EventName;
    public readonly IReadOnlyList<CrossCallParameter> Parameters;
    public readonly IReadOnlyList<ReturnSlot> Returns;
    public readonly StorageType ResultType;

    public CrossCallTransportPlan(CLeaf eventName, IReadOnlyList<CrossCallParameter> parameters,
        IReadOnlyList<ReturnSlot> returns, StorageType resultType)
    {
        EventName = eventName ?? throw new ArgumentNullException(nameof(eventName));
        Parameters = parameters == null
            ? Array.Empty<CrossCallParameter>()
            : new List<CrossCallParameter>(parameters).AsReadOnly();
        Returns = returns == null
            ? Array.Empty<ReturnSlot>()
            : new List<ReturnSlot>(returns).AsReadOnly();
        ResultType = resultType;
    }
}
