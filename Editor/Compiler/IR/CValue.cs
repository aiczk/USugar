using System;

// ============================================================================
// Core IR value vocabulary (Phase 1 of "Core IR by absorption").
// One operand-leaf representation that both HIR leaf expressions and LIR operands
// map to field-for-field. See docs/superpowers/specs/2026-06-01-core-ir-by-absorption-design.md §3.1.
// Global namespace + plain sealed classes / readonly fields, matching HirTypes/LirTypes
// (must stay C# 9.0-compatible: Unity compiles Editor/ at C# 9.0 LCD).
// ============================================================================

/// <summary>Base class for all Core IR operand leaves. Every value has a result type.</summary>
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
