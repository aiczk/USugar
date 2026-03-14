using System;
using System.Collections.Generic;
using System.Text;

// ============================================================================
// IR Values
// ============================================================================

/// <summary>Base class for all SSA values.</summary>
public abstract class IrValue
{
    public abstract string Type { get; }
}

/// <summary>Virtual register in SSA form. Each VReg has exactly one definition.</summary>
public sealed class VReg : IrValue
{
    public readonly int Id;
    readonly string _type;

    public VReg(int id, string type)
    {
        Id = id;
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override string Type => _type;
    public override string ToString() => $"v{Id}:{_type}";
    public override int GetHashCode() => Id;
    public override bool Equals(object obj) => obj is VReg other && other.Id == Id;
}

/// <summary>Compile-time constant value.</summary>
public sealed class IrConst : IrValue
{
    public readonly object Value;
    readonly string _type;

    public IrConst(object value, string type)
    {
        Value = value;
        _type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override string Type => _type;
    public override string ToString() => $"const({Value ?? "null"}):{_type}";
    public override int GetHashCode() => HashCode.Combine(Value, _type);
    public override bool Equals(object obj)
        => obj is IrConst other && Equals(other.Value, Value) && other._type == _type;
}

/// <summary>Reference to a function's entry point address. Resolved to UInt32 by CodeGen.</summary>
public sealed class IrFuncRef : IrValue
{
    public readonly IrFunction Target;
    public IrFuncRef(IrFunction target) => Target = target ?? throw new ArgumentNullException(nameof(target));
    public override string Type => "SystemUInt32";
    public override string ToString() => $"funcref({Target.Name})";
}

// ============================================================================
// IR Instructions (discriminated union)
// ============================================================================

/// <summary>Base class for all IR instructions.</summary>
public abstract class IrInst
{
    /// <summary>The VReg defined by this instruction, or null if none.</summary>
    public abstract VReg Dest { get; }

    /// <summary>All IrValues read by this instruction.</summary>
    public abstract IReadOnlyList<IrValue> Operands { get; }
}

/// <summary>Call an Udon extern function.</summary>
public sealed class CallExtern : IrInst
{
    public readonly VReg Result;        // null for void externs
    public readonly string ExternSig;
    public readonly IrValue[] Args;
    public readonly bool IsPure;

    public CallExtern(VReg result, string externSig, IrValue[] args, bool isPure = false)
    {
        Result = result;
        ExternSig = externSig ?? throw new ArgumentNullException(nameof(externSig));
        Args = args ?? throw new ArgumentNullException(nameof(args));
        IsPure = isPure;
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands => Args;
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Result != null) sb.Append($"{Result} = ");
        sb.Append($"call_extern \"{ExternSig}\"(");
        for (int i = 0; i < Args.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Args[i]);
        }
        sb.Append(')');
        if (IsPure) sb.Append(" [pure]");
        return sb.ToString();
    }
}

/// <summary>Call an internal function (within the same Udon program).</summary>
public sealed class CallInternal : IrInst
{
    public readonly VReg Result;        // null for void functions
    public readonly IrFunction Target;
    public readonly IrValue[] Args;

    public CallInternal(VReg result, IrFunction target, IrValue[] args)
    {
        Result = result;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Args = args ?? throw new ArgumentNullException(nameof(args));
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands => Args;
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Result != null) sb.Append($"{Result} = ");
        sb.Append($"call {Target.Name}(");
        for (int i = 0; i < Args.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Args[i]);
        }
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>Load a value from a named field (heap variable).</summary>
public sealed class LoadField : IrInst
{
    public readonly VReg Result;
    public readonly string FieldName;

    public LoadField(VReg result, string fieldName)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands => Array.Empty<IrValue>();
    public override string ToString() => $"{Result} = load [{FieldName}]";
}

/// <summary>Store a value to a named field (heap variable).</summary>
public sealed class StoreField : IrInst
{
    public readonly string FieldName;
    public readonly IrValue Value;

    public StoreField(string fieldName, IrValue value)
    {
        FieldName = fieldName ?? throw new ArgumentNullException(nameof(fieldName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override VReg Dest => null;
    public override IReadOnlyList<IrValue> Operands => new[] { Value };
    public override string ToString() => $"store [{FieldName}] = {Value}";
}

/// <summary>Ternary select: Dest = Cond ? TrueValue : FalseValue.</summary>
public sealed class Select : IrInst
{
    public readonly VReg Result;
    public readonly IrValue Cond;
    public readonly IrValue TrueValue;
    public readonly IrValue FalseValue;

    public Select(VReg result, IrValue cond, IrValue trueValue, IrValue falseValue)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueValue = trueValue ?? throw new ArgumentNullException(nameof(trueValue));
        FalseValue = falseValue ?? throw new ArgumentNullException(nameof(falseValue));
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands => new IrValue[] { Cond, TrueValue, FalseValue };
    public override string ToString() => $"{Result} = select {Cond}, {TrueValue}, {FalseValue}";
}

/// <summary>SSA phi node: merges values from predecessor blocks.</summary>
public sealed class Phi : IrInst
{
    public readonly VReg Result;
    public readonly List<(IrValue Value, IrBlock Block)> Entries;

    public Phi(VReg result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Entries = new List<(IrValue, IrBlock)>();
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands
    {
        get
        {
            var ops = new IrValue[Entries.Count];
            for (int i = 0; i < Entries.Count; i++) ops[i] = Entries[i].Value;
            return ops;
        }
    }
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"{Result} = phi ");
        for (int i = 0; i < Entries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"[{Entries[i].Value}, bb{Entries[i].Block.Id}]");
        }
        return sb.ToString();
    }
}

/// <summary>Call through a runtime method pointer (delegate invocation).</summary>
public sealed class CallIndirect : IrInst
{
    public readonly VReg Result;          // null for void delegates
    public readonly IrValue MethodPtr;    // UInt32 method pointer
    public readonly IrValue[] Args;       // argument values to copy to convention fields
    public readonly string[] ArgFields;   // convention field names for args
    public readonly string RetField;      // convention field name for return (null = void)

    public CallIndirect(VReg result, IrValue methodPtr, IrValue[] args, string[] argFields, string retField)
    {
        Result = result;
        MethodPtr = methodPtr ?? throw new ArgumentNullException(nameof(methodPtr));
        Args = args ?? throw new ArgumentNullException(nameof(args));
        ArgFields = argFields ?? throw new ArgumentNullException(nameof(argFields));
        RetField = retField;
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands
    {
        get
        {
            var ops = new IrValue[Args.Length + 1];
            ops[0] = MethodPtr;
            Args.CopyTo(ops, 1);
            return ops;
        }
    }
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Result != null) sb.Append($"{Result} = ");
        sb.Append($"call_indirect {MethodPtr}(");
        for (int i = 0; i < Args.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Args[i]);
        }
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>Non-SSA copy instruction. Introduced only after PhiElimination.</summary>
public sealed class Copy : IrInst
{
    public readonly VReg Result;
    public readonly IrValue Source;

    public Copy(VReg result, IrValue source)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public override VReg Dest => Result;
    public override IReadOnlyList<IrValue> Operands => new[] { Source };
    public override string ToString() => $"{Result} = copy {Source}";
}

// ============================================================================
// Terminators (control flow instructions — exactly one per block, at the end)
// ============================================================================

/// <summary>Unconditional jump.</summary>
public sealed class Jump : IrInst
{
    public IrBlock Target;

    public Jump(IrBlock target) => Target = target ?? throw new ArgumentNullException(nameof(target));

    public override VReg Dest => null;
    public override IReadOnlyList<IrValue> Operands => Array.Empty<IrValue>();
    public override string ToString() => $"jump bb{Target.Id}";
}

/// <summary>Conditional branch.</summary>
public sealed class Branch : IrInst
{
    public readonly IrValue Cond;
    public IrBlock TrueTarget;
    public IrBlock FalseTarget;

    public Branch(IrValue cond, IrBlock trueTarget, IrBlock falseTarget)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueTarget = trueTarget ?? throw new ArgumentNullException(nameof(trueTarget));
        FalseTarget = falseTarget ?? throw new ArgumentNullException(nameof(falseTarget));
    }

    public override VReg Dest => null;
    public override IReadOnlyList<IrValue> Operands => new[] { Cond };
    public override string ToString() => $"branch {Cond}, bb{TrueTarget.Id}, bb{FalseTarget.Id}";
}

/// <summary>Return from function.</summary>
public sealed class Return : IrInst
{
    public readonly IrValue Value;  // null for void returns

    public Return(IrValue value = null) => Value = value;

    public override VReg Dest => null;
    public override IReadOnlyList<IrValue> Operands => Value != null ? new[] { Value } : Array.Empty<IrValue>();
    public override string ToString() => Value != null ? $"return {Value}" : "return";
}

/// <summary>Unreachable code marker (e.g., after a throw).</summary>
public sealed class Unreachable : IrInst
{
    public override VReg Dest => null;
    public override IReadOnlyList<IrValue> Operands => Array.Empty<IrValue>();
    public override string ToString() => "unreachable";
}

// ============================================================================
// Structural Types
// ============================================================================

/// <summary>A basic block containing a linear sequence of instructions ending with a terminator.</summary>
public sealed class IrBlock
{
    public readonly int Id;
    public readonly List<IrInst> Insts = new();
    public readonly List<IrBlock> Predecessors = new();
    public readonly List<IrBlock> Successors = new();
    public string Hint;  // Optional label name hint (e.g., "__goto_top" for goto targets)

    public IrBlock(int id) => Id = id;

    /// <summary>The terminator instruction (last instruction in the block).</summary>
    public IrInst Terminator => Insts.Count > 0 ? Insts[^1] : null;

    /// <summary>Add a non-terminator instruction.</summary>
    public void Append(IrInst inst) => Insts.Add(inst);

    /// <summary>Replace the terminator (last instruction). Asserts the block already has one.</summary>
    public void ReplaceTerminator(IrInst newTerm)
    {
        if (Insts.Count == 0)
            throw new InvalidOperationException("Block has no instructions to replace");
        Insts[^1] = newTerm;
    }

    public override string ToString() => $"bb{Id}";
}

/// <summary>A function within the IR module.</summary>
public sealed class IrFunction
{
    public readonly string Name;
    public readonly string ExportName;  // null for non-exported (internal) functions
    public readonly IrBlock Entry;
    public readonly List<IrBlock> Blocks = new();
    public readonly List<(string Name, string Type)> Parameters = new();
    public string ReturnType;           // null for void functions
    public string ReturnVarId;          // LayoutPlanner-assigned return variable ID (null = synthesize from Name)

    int _nextBlockId;
    int _nextVRegId;

    public IrFunction(string name, string exportName = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ExportName = exportName;
        Entry = NewBlock();
    }

    public IrBlock NewBlock()
    {
        var block = new IrBlock(_nextBlockId++);
        Blocks.Add(block);
        return block;
    }

    public VReg NewReg(string type)
    {
        return new VReg(_nextVRegId++, type);
    }

    /// <summary>Link two blocks (adds predecessor/successor edges).</summary>
    public static void LinkBlocks(IrBlock from, IrBlock to)
    {
        if (!from.Successors.Contains(to))
            from.Successors.Add(to);
        if (!to.Predecessors.Contains(from))
            to.Predecessors.Add(from);
    }

    /// <summary>Unlink two blocks (removes predecessor/successor edges).</summary>
    public static void UnlinkBlocks(IrBlock from, IrBlock to)
    {
        from.Successors.Remove(to);
        to.Predecessors.Remove(from);
    }

    /// <summary>Rebuild all predecessor/successor edges from terminators.</summary>
    public void RebuildEdges()
    {
        foreach (var block in Blocks)
        {
            block.Predecessors.Clear();
            block.Successors.Clear();
        }
        foreach (var block in Blocks)
        {
            switch (block.Terminator)
            {
                case Jump j:
                    LinkBlocks(block, j.Target);
                    break;
                case Branch b:
                    LinkBlocks(block, b.TrueTarget);
                    if (b.FalseTarget != b.TrueTarget)
                        LinkBlocks(block, b.FalseTarget);
                    break;
                // Return and Unreachable have no successors
            }
        }
    }

    /// <summary>Print IR text for debugging.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        sb.Append($"function {Name}");
        if (ExportName != null) sb.Append($" [export: {ExportName}]");
        sb.Append('(');
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"{Parameters[i].Name}:{Parameters[i].Type}");
        }
        sb.Append(')');
        if (ReturnType != null) sb.Append($" -> {ReturnType}");
        sb.AppendLine();

        foreach (var block in Blocks)
        {
            sb.Append($"  bb{block.Id}:");
            if (block.Predecessors.Count > 0)
            {
                sb.Append("  ; preds =");
                foreach (var pred in block.Predecessors)
                    sb.Append($" bb{pred.Id}");
            }
            sb.AppendLine();

            foreach (var inst in block.Insts)
                sb.AppendLine($"    {inst}");
        }
        return sb.ToString();
    }
}

/// <summary>Field flags for module-level field declarations.</summary>
[Flags]
public enum FieldFlags
{
    None = 0,
    Export = 1,
    Sync = 2,
}

/// <summary>Module-level field declaration.</summary>
public sealed class FieldDecl
{
    public readonly string Name;
    public readonly string Type;
    public object DefaultValue;
    public FieldFlags Flags;
    public string SyncMode;    // "none", "linear", "smooth" (null = not synced)

    public FieldDecl(string name, string type)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"field {Name}: {Type}");
        if ((Flags & FieldFlags.Export) != 0) sb.Append(" [export]");
        if ((Flags & FieldFlags.Sync) != 0) sb.Append($" [sync:{SyncMode ?? "none"}]");
        if (DefaultValue != null) sb.Append($" = {DefaultValue}");
        return sb.ToString();
    }
}

/// <summary>Top-level IR module containing all functions and field declarations.</summary>
public sealed class IrModule
{
    public readonly List<IrFunction> Functions = new();
    public readonly List<FieldDecl> Fields = new();
    public string ClassName;

    public IrFunction AddFunction(string name, string exportName = null)
    {
        var func = new IrFunction(name, exportName);
        Functions.Add(func);
        return func;
    }

    public FieldDecl AddField(string name, string type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        var field = new FieldDecl(name, type)
        {
            Flags = flags,
            DefaultValue = defaultValue,
            SyncMode = syncMode,
        };
        Fields.Add(field);
        return field;
    }

    /// <summary>Find a field by name, or null if not found.</summary>
    public FieldDecl FindField(string name)
    {
        foreach (var f in Fields)
            if (f.Name == name) return f;
        return null;
    }

    /// <summary>Print full IR dump.</summary>
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
