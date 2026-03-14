using System;
using System.Collections.Generic;
using System.Text;

// ============================================================================
// LIR (Low-level IR): Flat CFG with basic blocks, typed slots, no Phi nodes.
// ============================================================================

/// <summary>Operand in LIR — either a slot reference, a constant, a field reference, or a function reference.</summary>
public abstract class LOperand
{
    public abstract string Type { get; }
}

/// <summary>Reference to a virtual slot.</summary>
public sealed class LSlotRef : LOperand
{
    public readonly int SlotId;
    readonly string _type;

    public LSlotRef(int slotId, string type)
    {
        SlotId = slotId;
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override string Type => _type;
    public override string ToString() => $"slot{SlotId}:{_type}";
}

/// <summary>Constant value.</summary>
public sealed class LConst : LOperand
{
    public readonly object Value;
    readonly string _type;

    public LConst(object value, string type)
    {
        Value = value;
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override string Type => _type;
    public override string ToString() => $"const({Value ?? "null"}):{_type}";
}

/// <summary>Field reference (for load/store from heap).</summary>
public sealed class LFieldRef : LOperand
{
    public readonly string FieldName;
    readonly string _type;

    public LFieldRef(string fieldName, string type)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override string Type => _type;
    public override string ToString() => $"field[{FieldName}]:{_type}";
}

/// <summary>Function entry point reference (for delegates/JUMP_INDIRECT).</summary>
public sealed class LFuncRef : LOperand
{
    public readonly string FuncName;

    public LFuncRef(string funcName)
    {
        FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
    }

    public override string Type => "SystemUInt32";
    public override string ToString() => $"funcref({FuncName})";
}

// ============================================================================
// LIR Instructions
// ============================================================================

/// <summary>Base class for LIR instructions (non-terminator).</summary>
public abstract class LInst
{
    public abstract void Dump(StringBuilder sb);
}

/// <summary>Move: destSlot = src</summary>
public sealed class LMove : LInst
{
    public readonly int DestSlot;
    public readonly LOperand Src;
    public readonly string Type;

    public LMove(int destSlot, LOperand src, string type)
    {
        DestSlot = destSlot;
        Src = src ?? throw new ArgumentNullException(nameof(src));
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override void Dump(StringBuilder sb) => sb.AppendLine($"    slot{DestSlot} = {Src}");
}

/// <summary>Load from heap field into slot.</summary>
public sealed class LLoadField : LInst
{
    public readonly int DestSlot;
    public readonly string FieldName;
    public readonly string Type;

    public LLoadField(int destSlot, string fieldName, string type)
    {
        DestSlot = destSlot;
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override void Dump(StringBuilder sb) => sb.AppendLine($"    slot{DestSlot} = load [{FieldName}]");
}

/// <summary>Store to heap field from operand.</summary>
public sealed class LStoreField : LInst
{
    public readonly string FieldName;
    public readonly LOperand Value;

    public LStoreField(string fieldName, LOperand value)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override void Dump(StringBuilder sb) => sb.AppendLine($"    store [{FieldName}] = {Value}");
}

/// <summary>Extern call with optional destination slot.</summary>
public sealed class LCallExtern : LInst
{
    public readonly int? DestSlot; // null for void calls
    public readonly string Sig;
    public readonly List<LOperand> Args;
    public readonly string RetType;

    public LCallExtern(int? destSlot, string sig, List<LOperand> args, string retType)
    {
        DestSlot = destSlot;
        Sig = sig ?? throw new ArgumentNullException(nameof(sig));
        Args = args ?? new();
        RetType = retType ?? throw new ArgumentNullException(nameof(retType));
    }

    public override void Dump(StringBuilder sb)
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        sb.AppendLine($"    {dest}extern \"{Sig}\"({string.Join(", ", Args)})");
    }
}

/// <summary>Internal function call with optional destination slot.</summary>
public sealed class LCallInternal : LInst
{
    public readonly int? DestSlot;
    public readonly string FuncName;
    public readonly List<LOperand> Args;
    public readonly string RetType;

    public LCallInternal(int? destSlot, string funcName, List<LOperand> args, string retType)
    {
        DestSlot = destSlot;
        FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
        Args = args ?? new();
        RetType = retType;
    }

    public override void Dump(StringBuilder sb)
    {
        var dest = DestSlot.HasValue ? $"slot{DestSlot.Value} = " : "";
        sb.AppendLine($"    {dest}call {FuncName}({string.Join(", ", Args)})");
    }
}

// ============================================================================
// LIR Terminators
// ============================================================================

/// <summary>Base class for block terminators.</summary>
public abstract class LTerminator
{
    public abstract void Dump(StringBuilder sb);
}

/// <summary>Unconditional jump.</summary>
public sealed class LJump : LTerminator
{
    public int TargetBlockId;

    public LJump(int targetBlockId) => TargetBlockId = targetBlockId;

    public override void Dump(StringBuilder sb) => sb.AppendLine($"    jump bb{TargetBlockId}");
}

/// <summary>Conditional branch.</summary>
public sealed class LBranch : LTerminator
{
    public readonly LOperand Cond;
    public int TrueBlockId;
    public int FalseBlockId;

    public LBranch(LOperand cond, int trueBlockId, int falseBlockId)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueBlockId = trueBlockId;
        FalseBlockId = falseBlockId;
    }

    public override void Dump(StringBuilder sb) =>
        sb.AppendLine($"    branch {Cond}, bb{TrueBlockId}, bb{FalseBlockId}");
}

/// <summary>Return from function.</summary>
public sealed class LReturn : LTerminator
{
    public readonly LOperand Value; // null for void

    public LReturn(LOperand value = null) => Value = value;

    public override void Dump(StringBuilder sb) =>
        sb.AppendLine(Value != null ? $"    return {Value}" : "    return");
}

// ============================================================================
// LIR Block, Function, Module
// ============================================================================

/// <summary>Basic block in LIR. Contains a list of instructions and a terminator.</summary>
public sealed class LBlock
{
    public readonly int Id;
    public readonly List<LInst> Insts = new();
    public LTerminator Term; // set during lowering
    public string Hint; // Optional label name hint for codegen

    public LBlock(int id) => Id = id;

    public void Dump(StringBuilder sb)
    {
        sb.AppendLine($"  bb{Id}:");
        foreach (var inst in Insts)
            inst.Dump(sb);
        Term?.Dump(sb);
    }
}

/// <summary>Function in LIR. Contains basic blocks and slot declarations.</summary>
public sealed class LFunction
{
    public readonly string Name;
    public readonly string ExportName;
    public readonly List<LBlock> Blocks = new();
    public readonly List<SlotDecl> Slots = new(); // shared with HIR
    public string ReturnType;
    /// <summary>UASM field names for parameters. Used for internal call ABI.</summary>
    public readonly List<string> ParamFieldNames = new();
    /// <summary>UASM field name for the return value. Null for void.</summary>
    public string ReturnFieldName;
    int _nextBlockId;

    public LFunction(string name, string exportName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ExportName = exportName;
    }

    public LBlock Entry => Blocks.Count > 0 ? Blocks[0] : null;

    public LBlock NewBlock()
    {
        var block = new LBlock(_nextBlockId++);
        Blocks.Add(block);
        return block;
    }

    public string Dump()
    {
        var sb = new StringBuilder();
        sb.Append($"function {Name}");
        if (ExportName != null) sb.Append($" [export: {ExportName}]");
        sb.AppendLine($"() -> {ReturnType ?? "void"}");

        foreach (var slot in Slots)
            sb.AppendLine($"  {slot}");
        if (Slots.Count > 0) sb.AppendLine();

        foreach (var block in Blocks)
            block.Dump(sb);

        return sb.ToString();
    }
}

/// <summary>Top-level LIR module.</summary>
public sealed class LModule
{
    public readonly List<LFunction> Functions = new();
    public readonly List<FieldDecl> Fields = new();
    public string ClassName;

    public string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"module {ClassName ?? "(anonymous)"}");
        sb.AppendLine();

        foreach (var field in Fields)
            sb.AppendLine($"  {field}");
        if (Fields.Count > 0) sb.AppendLine();

        foreach (var func in Functions)
            sb.Append(func.Dump());

        return sb.ToString();
    }
}
