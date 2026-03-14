using System;
using System.Collections.Generic;
using System.Text;

// ============================================================================
// Slot System
// ============================================================================

/// <summary>
/// Slot classification for variable lifetime management.
/// Pinned slots have fixed UASM names; Frame/Scratch are virtual and coalesced later.
/// </summary>
public enum SlotClass
{
    /// <summary>User field, synced/exported, ABI param/ret, this, delegate convention. Fixed UASM name.</summary>
    Pinned,
    /// <summary>Local that lives across internal calls. Function-private, not shared.</summary>
    Frame,
    /// <summary>Temp that does not span calls. Aggressively reused across functions.</summary>
    Scratch,
}

/// <summary>Declaration of a virtual slot (variable) in HIR.</summary>
public sealed class SlotDecl
{
    public readonly int Id;
    public readonly string Type;
    public readonly SlotClass Class;
    /// <summary>Non-null for Pinned slots — the fixed UASM variable name.</summary>
    public readonly string FixedName;

    public SlotDecl(int id, string type, SlotClass slotClass, string fixedName = null)
    {
        Id = id;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Class = slotClass;
        FixedName = fixedName;
    }

    public override string ToString()
    {
        var sb = new StringBuilder($"slot{Id}:{Type}[{Class}]");
        if (FixedName != null) sb.Append($" = \"{FixedName}\"");
        return sb.ToString();
    }
}

// ============================================================================
// HIR Statements
// ============================================================================

/// <summary>Base class for all HIR statements.</summary>
public abstract class HStmt
{
    public abstract void Dump(StringBuilder sb, int indent);

    protected static void Indent(StringBuilder sb, int indent)
    {
        for (int i = 0; i < indent; i++) sb.Append("  ");
    }
}

/// <summary>Sequence of statements.</summary>
public sealed class HBlock : HStmt
{
    public readonly List<HStmt> Stmts = new();

    public HBlock() { }
    public HBlock(List<HStmt> stmts) => Stmts = stmts ?? new();

    public override void Dump(StringBuilder sb, int indent)
    {
        foreach (var stmt in Stmts)
            stmt.Dump(sb, indent);
    }
}

/// <summary>Assign expression result to a slot: slot = expr</summary>
public sealed class HAssign : HStmt
{
    public readonly int DestSlot;
    public readonly HExpr Value;

    public HAssign(int destSlot, HExpr value)
    {
        DestSlot = destSlot;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"slot{DestSlot} = {Value}");
    }
}

/// <summary>Store expression to a heap field: fieldName = expr</summary>
public sealed class HStoreField : HStmt
{
    public readonly string FieldName;
    public readonly HExpr Value;

    public HStoreField(string fieldName, HExpr value)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"store [{FieldName}] = {Value}");
    }
}

/// <summary>Structured if/else.</summary>
public sealed class HIf : HStmt
{
    public readonly HExpr Cond;
    public readonly HBlock Then;
    public readonly HBlock Else; // may be empty

    public HIf(HExpr cond, HBlock thenBlock, HBlock elseBlock = null)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Then = thenBlock ?? new HBlock();
        Else = elseBlock ?? new HBlock();
    }

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"if ({Cond}):");
        Then.Dump(sb, indent + 1);
        if (Else.Stmts.Count > 0)
        {
            Indent(sb, indent);
            sb.AppendLine("else:");
            Else.Dump(sb, indent + 1);
        }
    }
}

/// <summary>Structured while/do-while loop.</summary>
public sealed class HWhile : HStmt
{
    public readonly HExpr Cond;
    public readonly HBlock Body;
    public readonly bool IsDoWhile;

    public HWhile(HExpr cond, HBlock body, bool isDoWhile = false)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        Body = body ?? new HBlock();
        IsDoWhile = isDoWhile;
    }

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine(IsDoWhile ? $"do-while ({Cond}):" : $"while ({Cond}):");
        Body.Dump(sb, indent + 1);
    }
}

/// <summary>Structured for loop with init, condition, update, body.</summary>
public sealed class HFor : HStmt
{
    public readonly HBlock Init;
    public readonly HExpr Cond; // null = infinite loop
    public readonly HBlock Update;
    public readonly HBlock Body;

    public HFor(HBlock init, HExpr cond, HBlock update, HBlock body)
    {
        Init = init ?? new HBlock();
        Cond = cond;
        Update = update ?? new HBlock();
        Body = body ?? new HBlock();
    }

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"for (... ; {Cond?.ToString() ?? "true"} ; ...):");
        Body.Dump(sb, indent + 1);
    }
}

/// <summary>Break out of the innermost loop/switch.</summary>
public sealed class HBreak : HStmt
{
    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine("break");
    }
}

/// <summary>Continue to the next iteration of the innermost loop.</summary>
public sealed class HContinue : HStmt
{
    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine("continue");
    }
}

/// <summary>Goto a named label.</summary>
public sealed class HGoto : HStmt
{
    public readonly string Label;

    public HGoto(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"goto {Label}");
    }
}

/// <summary>Named label (goto target).</summary>
public sealed class HLabelStmt : HStmt
{
    public readonly string Label;

    public HLabelStmt(string label) => Label = label ?? throw new ArgumentNullException(nameof(label));

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"{Label}:");
    }
}

/// <summary>Return from function with optional value.</summary>
public sealed class HReturn : HStmt
{
    public readonly HExpr Value; // null for void return

    public HReturn(HExpr value = null) => Value = value;

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine(Value != null ? $"return {Value}" : "return");
    }
}

/// <summary>Expression used as a statement (side-effecting call, etc.).</summary>
public sealed class HExprStmt : HStmt
{
    public readonly HExpr Expr;

    public HExprStmt(HExpr expr) => Expr = expr ?? throw new ArgumentNullException(nameof(expr));

    public override void Dump(StringBuilder sb, int indent)
    {
        Indent(sb, indent);
        sb.AppendLine($"{Expr}");
    }
}

// ============================================================================
// HIR Expressions
// ============================================================================

/// <summary>Base class for all HIR expressions. Every expression has a result type.</summary>
public abstract class HExpr
{
    public readonly string Type;

    protected HExpr(string type) => Type = type ?? throw new ArgumentNullException(nameof(type));
}

/// <summary>Compile-time constant value.</summary>
public sealed class HConst : HExpr
{
    public readonly object Value; // null for default/null literal

    public HConst(object value, string type) : base(type) => Value = value;

    public override string ToString() => $"const({Value ?? "null"}):{Type}";
}

/// <summary>Reference to a virtual slot.</summary>
public sealed class HSlotRef : HExpr
{
    public readonly int SlotId;

    public HSlotRef(int slotId, string type) : base(type) => SlotId = slotId;

    public override string ToString() => $"slot{SlotId}:{Type}";
}

/// <summary>Load value from a heap field.</summary>
public sealed class HLoadField : HExpr
{
    public readonly string FieldName;

    public HLoadField(string fieldName, string type) : base(type)
        => FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));

    public override string ToString() => $"load [{FieldName}]:{Type}";
}

/// <summary>Call an extern (Udon VM native) function.</summary>
public sealed class HExternCall : HExpr
{
    public readonly string Sig;
    public readonly List<HExpr> Args;
    public readonly bool IsPure;

    public HExternCall(string sig, List<HExpr> args, string retType, bool isPure = false) : base(retType)
    {
        Sig = sig ?? throw new ArgumentNullException(nameof(sig));
        Args = args ?? new();
        IsPure = isPure;
    }

    public override string ToString()
    {
        var argStr = string.Join(", ", Args);
        return $"extern \"{Sig}\"({argStr}):{Type}";
    }
}

/// <summary>Call an internal (user-defined) function.</summary>
public sealed class HInternalCall : HExpr
{
    public readonly string FuncName;
    public readonly List<HExpr> Args;

    public HInternalCall(string funcName, List<HExpr> args, string retType) : base(retType)
    {
        FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));
        Args = args ?? new();
    }

    public override string ToString()
    {
        var argStr = string.Join(", ", Args);
        return $"call {FuncName}({argStr}):{Type}";
    }
}

/// <summary>Ternary select: cond ? trueVal : falseVal</summary>
public sealed class HSelect : HExpr
{
    public readonly HExpr Cond;
    public readonly HExpr TrueVal;
    public readonly HExpr FalseVal;

    public HSelect(HExpr cond, HExpr trueVal, HExpr falseVal, string type) : base(type)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueVal = trueVal ?? throw new ArgumentNullException(nameof(trueVal));
        FalseVal = falseVal ?? throw new ArgumentNullException(nameof(falseVal));
    }

    public override string ToString() => $"select({Cond}, {TrueVal}, {FalseVal}):{Type}";
}

/// <summary>Reference to a function entry point (for delegate/JUMP_INDIRECT).</summary>
public sealed class HFuncRef : HExpr
{
    public readonly string FuncName;

    public HFuncRef(string funcName) : base("SystemUInt32")
        => FuncName = funcName ?? throw new ArgumentNullException(nameof(funcName));

    public override string ToString() => $"funcref({FuncName})";
}

// ============================================================================
// HIR Function & Module
// ============================================================================

/// <summary>A function in HIR. Contains structured body, slot declarations, and metadata.</summary>
public sealed class HFunction
{
    public readonly string Name;
    public readonly string ExportName; // null for internal-only functions
    public readonly HBlock Body = new();
    public readonly List<SlotDecl> Slots = new();
    public string ReturnType; // null for void
    /// <summary>UASM field names for parameters (set by emitter). Used for internal call ABI.</summary>
    public readonly List<string> ParamFieldNames = new();
    /// <summary>UASM field name for the return value (set by emitter). Null for void.</summary>
    public string ReturnFieldName;

    public HFunction(string name, string exportName = null)
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

    public string Dump()
    {
        var sb = new StringBuilder();
        sb.Append($"function {Name}");
        if (ExportName != null) sb.Append($" [export: {ExportName}]");
        sb.AppendLine($"() -> {ReturnType ?? "void"}");

        foreach (var slot in Slots)
            sb.AppendLine($"  {slot}");
        if (Slots.Count > 0) sb.AppendLine();

        Body.Dump(sb, 1);
        return sb.ToString();
    }
}

/// <summary>Top-level HIR module containing functions and field declarations.</summary>
public sealed class HModule
{
    public readonly List<HFunction> Functions = new();
    public readonly List<FieldDecl> Fields = new(); // reuse existing FieldDecl from IrTypes
    public string ClassName;

    public HFunction AddFunction(string name, string exportName = null)
    {
        var func = new HFunction(name, exportName);
        Functions.Add(func);
        return func;
    }

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
