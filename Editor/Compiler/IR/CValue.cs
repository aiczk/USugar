using System;
using System.Collections.Generic;

// ============================================================================
// Core IR value vocabulary (Phase 1 of "Core IR by absorption").
// One value representation that both HIR expressions and LIR operands/calls map to
// field-for-field. Leaves (CSlotRef/CConst/CFieldRef/CFuncRef) are operand-level; the
// value-producing ops (CExternCall/CInternalCall) may nest in args (tree role, DestSlot
// null) or write a scratch slot (flat role, DestSlot set). See
// docs/superpowers/specs/2026-06-01-core-ir-by-absorption-design.md §3.1.
// Global namespace + plain sealed classes / readonly fields, matching HirTypes/LirTypes
// (must stay C# 9.0-compatible: Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================

/// <summary>Base class for all Core IR values. Every value has a result type.</summary>
public abstract class CValue
{
    public readonly string Type;
    protected CValue(string type) => Type = type ?? throw new ArgumentNullException(nameof(type));
}

/// <summary>Reference to a virtual slot. [= HSlotRef + LSlotRef]</summary>
public sealed class CSlotRef : CValue
{
    public readonly int SlotId;
    public CSlotRef(int slotId, string type) : base(type) => SlotId = slotId;
    public override string ToString() => $"slot{SlotId}:{Type}";
}

/// <summary>Compile-time constant value. [= HConst + LConst]</summary>
public sealed class CConst : CValue
{
    public readonly object Value; // null for default/null literal
    public CConst(object value, string type) : base(type) => Value = value;
    public override string ToString() => $"const({Value ?? "null"}):{Type}";
}

/// <summary>How a field is referenced: read its value, or take its heap address.</summary>
public enum CFieldMode
{
    /// <summary>Read the field's value (= HLoadField / LLoadField).</summary>
    Load,
    /// <summary>Heap address for extern out/ref parameters (= HFieldAddr / LFieldRef).</summary>
    Addr,
}

/// <summary>Field reference, unifying value-load and address-ref forms via <see cref="Mode"/>.
/// [= HLoadField/HFieldAddr + LFieldRef/LLoadField]</summary>
public sealed class CFieldRef : CValue
{
    public readonly string FieldName;
    public readonly CFieldMode Mode;

    public CFieldRef(string fieldName, string type, CFieldMode mode) : base(type)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Mode = mode;
    }

    public override string ToString()
        => $"{(Mode == CFieldMode.Addr ? "addr" : "load")} [{FieldName}]:{Type}";
}

/// <summary>Reference to a function entry point (delegate / JUMP_INDIRECT). [= HFuncRef + LFuncRef]</summary>
public sealed class CFuncRef : CValue
{
    public readonly string FuncName;
    public CFuncRef(string funcName) : base("SystemUInt32")
        => FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
    public override string ToString() => $"funcref({FuncName})";
}

/// <summary>Call an extern (Udon VM native) function. Value-producing op: may nest in args
/// (tree role, DestSlot null) or write a scratch slot (flat role, DestSlot set).
/// [= HExternCall + LCallExtern]</summary>
public sealed class CExternCall : CValue
{
    public readonly string Sig;
    public readonly List<CValue> Args;
    public readonly int? DestSlot; // null in tree role; set in flat (instruction) role

    public CExternCall(string sig, List<CValue> args, string retType, int? destSlot = null) : base(retType)
    {
        Sig = sig ?? throw new ArgumentNullException(nameof(sig));
        Args = args ?? new List<CValue>();
        DestSlot = destSlot;
    }

    public override string ToString()
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        return $"{dest}extern \"{Sig}\"({string.Join(", ", Args)}):{Type}";
    }
}

/// <summary>Call an internal (user-defined) function. [= HInternalCall + LCallInternal]</summary>
public sealed class CInternalCall : CValue
{
    public readonly string FuncName;
    public readonly List<CValue> Args;
    public readonly int? DestSlot;

    public CInternalCall(string funcName, List<CValue> args, string retType, int? destSlot = null) : base(retType)
    {
        FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
        Args = args ?? new List<CValue>();
        DestSlot = destSlot;
    }

    public override string ToString()
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        return $"{dest}call {FuncName}({string.Join(", ", Args)}):{Type}";
    }
}
