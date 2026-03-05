using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

[Flags]
public enum VarFlags { None = 0, Export = 1, Sync = 2 }

public struct JumpLabel
{
    public string Name;
    public uint Address;
    public bool IsMarked;
}

public enum InstKind { Push, PushLabel, Pop, Copy, Jump, JumpIfFalse, JumpIndirect, Extern, Nop, Export, Label, Comment, LabelAddr }

public struct Inst
{
    public InstKind Kind;
    public string Operand;
    public int LabelIndex;
}

public static class InstInfo
{
    public static readonly Dictionary<InstKind, uint> Size = new()
    {
        [InstKind.Push]         = 8,
        [InstKind.PushLabel]    = 8,
        [InstKind.Pop]          = 4,
        [InstKind.Copy]         = 4,
        [InstKind.Jump]         = 8,
        [InstKind.JumpIfFalse]  = 8,
        [InstKind.JumpIndirect] = 8,
        [InstKind.Extern]       = 8,
        [InstKind.Nop]          = 4,
        [InstKind.Export]       = 0,
        [InstKind.Label]        = 0,
        [InstKind.Comment]      = 0,
        [InstKind.LabelAddr]    = 0,
    };

    public static bool IsZeroSize(InstKind kind) =>
        kind is InstKind.Export or InstKind.Label or InstKind.Comment or InstKind.LabelAddr;

    public static bool IsJump(InstKind kind) =>
        kind is InstKind.Jump or InstKind.JumpIfFalse or InstKind.JumpIndirect;

    public static bool IsVoidExtern(string signature) => signature.EndsWith("__SystemVoid");
}

public class UasmModule
{
    struct VarEntry
    {
        public string Id, UdonType, DefaultValue;
        public VarFlags Flags;
        public string SyncMode;
        public object ConstValue;
    }

    readonly List<VarEntry> _vars = new();
    readonly List<Inst> _insts = new();
    readonly List<JumpLabel> _labels = new();
    readonly HashSet<string> _externs = new();
    string _headerComment;
    uint _currentAddress;

    public void SetHeader(string text) => _headerComment = text;

    void Emit(InstKind kind, string operand = null, int labelIndex = -1)
    {
        _insts.Add(new Inst { Kind = kind, Operand = operand, LabelIndex = labelIndex });
        _currentAddress += InstInfo.Size[kind];
    }

    public void DeclareVariable(string id, string udonType, string defaultValue, VarFlags flags, string syncMode = null, object constValue = null)
    {
        _vars.Add(new VarEntry { Id = id, UdonType = udonType, DefaultValue = defaultValue, Flags = flags, SyncMode = syncMode, ConstValue = constValue });
    }

    public int DefineLabel(string name)
    {
        _labels.Add(new JumpLabel { Name = name, Address = uint.MaxValue });
        return _labels.Count - 1;
    }

    public void MarkLabel(int labelIndex)
    {
        var lbl = _labels[labelIndex];
        lbl.Address = _currentAddress;
        lbl.IsMarked = true;
        _labels[labelIndex] = lbl;
        Emit(InstKind.LabelAddr, labelIndex: labelIndex);
    }

    public void AddPush(string varId) => Emit(InstKind.Push, varId);
    public void AddPushLabel(int labelIndex) => Emit(InstKind.PushLabel, labelIndex: labelIndex);
    public void AddPop() => Emit(InstKind.Pop);
    public void AddCopyRaw() => Emit(InstKind.Copy);
    public void AddCopy(string src, string dst) { AddPush(src); AddPush(dst); AddCopyRaw(); }
    public void AddJump(int labelIndex) => Emit(InstKind.Jump, labelIndex: labelIndex);
    public void AddJumpIfFalse(int labelIndex) => Emit(InstKind.JumpIfFalse, labelIndex: labelIndex);
    public void AddJumpIndirect(string varId) => Emit(InstKind.JumpIndirect, varId);
    public void AddExtern(string signature) { Emit(InstKind.Extern, signature); _externs.Add(signature); }
    public void AddReturn(string retaddrVar) { AddPush(retaddrVar); AddCopyRaw(); AddJumpIndirect(retaddrVar); }
    public void AddComment(string text) => Emit(InstKind.Comment, text);

    public void AddExport(string name, int labelIndex)
        => Emit(InstKind.Export, name, labelIndex);

    public void AddLabel(int labelIndex)
        => Emit(InstKind.Label, labelIndex: labelIndex);

    public uint GetHeapSize()
    {
        int retaddrCount = 0;
        foreach (var inst in _insts)
            if (inst.Kind == InstKind.PushLabel) retaddrCount++;
        return (uint)(_vars.Count + _externs.Count + retaddrCount);
    }

    public string BuildUasmStr()
    {
        // Validate that all labels referenced by jump instructions are marked.
        // Labels that are defined but never referenced are harmless orphans.
        ValidateJumpTargetLabels();

        // Collect PushLabel references and create retaddr const vars (local only — idempotent)
        var retaddrVars = new Dictionary<int, string>(); // inst index → var name
        var retaddrEntries = new List<VarEntry>();
        int retaddrIdx = 0;
        for (int i = 0; i < _insts.Count; i++)
        {
            if (_insts[i].Kind != InstKind.PushLabel) continue;
            var addr = _labels[_insts[i].LabelIndex].Address;
            var varName = $"__const_retaddr_SystemUInt32_{retaddrIdx++}";
            retaddrVars[i] = varName;
            retaddrEntries.Add(new VarEntry { Id = varName, UdonType = "SystemUInt32", DefaultValue = $"0x{addr:x8}", Flags = VarFlags.None });
        }

        var sb = new StringBuilder();
        if (_headerComment != null)
            Line(sb, $"# {_headerComment}");
        BuildDataBlock(sb, retaddrEntries);
        BuildCodeBlock(sb, retaddrVars);
        return sb.ToString();
    }

    void Line(StringBuilder sb, string s) => sb.Append(s).Append('\n');

    void BuildDataBlock(StringBuilder sb, List<VarEntry> retaddrEntries)
    {
        Line(sb, ".data_start");
        foreach (var v in _vars)
            if ((v.Flags & VarFlags.Export) != 0)
                Line(sb, $"    .export {v.Id}");
        foreach (var v in _vars)
            if ((v.Flags & VarFlags.Sync) != 0)
                Line(sb, $"    .sync {v.Id}, {v.SyncMode ?? "none"}");
        foreach (var v in _vars)
        {
            var def = v.DefaultValue ?? "null";
            Line(sb, $"    {v.Id}: %{v.UdonType}, {def}");
        }
        foreach (var v in retaddrEntries)
        {
            var def = v.DefaultValue ?? "null";
            Line(sb, $"    {v.Id}: %{v.UdonType}, {def}");
        }
        Line(sb, ".data_end");
    }

    void BuildCodeBlock(StringBuilder sb, Dictionary<int, string> retaddrVars)
    {
        Line(sb, ".code_start");
        for (int i = 0; i < _insts.Count; i++)
        {
            var inst = _insts[i];
            switch (inst.Kind)
            {
                case InstKind.Export:
                    Line(sb, $"    .export {inst.Operand}");
                    var exportLabel = _labels[inst.LabelIndex];
                    Line(sb, $"    {exportLabel.Name}:");
                    break;
                case InstKind.Label:
                    var labelOnly = _labels[inst.LabelIndex];
                    Line(sb, $"    {labelOnly.Name}:");
                    break;
                case InstKind.Push:
                    Line(sb, $"        PUSH, {inst.Operand}");
                    break;
                case InstKind.PushLabel:
                    Line(sb, $"        PUSH, {retaddrVars[i]}");
                    break;
                case InstKind.Pop:
                    Line(sb, "        POP");
                    break;
                case InstKind.Copy:
                    Line(sb, "        COPY");
                    break;
                case InstKind.Jump:
                    Line(sb, $"        JUMP, 0x{_labels[inst.LabelIndex].Address:x8}");
                    break;
                case InstKind.JumpIfFalse:
                    Line(sb, $"        JUMP_IF_FALSE, 0x{_labels[inst.LabelIndex].Address:x8}");
                    break;
                case InstKind.JumpIndirect:
                    Line(sb, $"        JUMP_INDIRECT, {inst.Operand}");
                    break;
                case InstKind.Extern:
                    Line(sb, $"        EXTERN, \"{inst.Operand}\"");
                    break;
                case InstKind.Comment:
                    Line(sb, $"# {inst.Operand}");
                    break;
            }
        }
        Line(sb, ".code_end");
    }

    // Set to 0-5 to control which new optimization passes are enabled.
    // 0 = none (pre-change baseline), 5 = all new passes enabled.
    // Used for binary-search debugging of runtime errors.
    public static int OptLevel = 0;

    public void Optimize()
    {
        var cfg = ControlFlowGraph.Build(this);
        cfg.SimplifyCFG();
        cfg.CopyPropagation();
        if (OptLevel >= 1) cfg.ConstantFolding(this);  // NEW
        if (OptLevel >= 2) cfg.CopyPropagation();       // NEW (2nd pass)
        cfg.ComputeLiveness();
        cfg.DeadStoreElimination();
        if (OptLevel >= 3) cfg.ComputeDominators();     // NEW
        if (OptLevel >= 3) cfg.GlobalValueNumbering();  // NEW
        if (OptLevel >= 4) cfg.LoopInvariantCodeMotion(this); // NEW
        if (OptLevel >= 3) cfg.ComputeLiveness();              // refresh after GVN/LICM
        cfg.ReduceVariables();
        cfg.Linearize(this);
        RemoveUnusedVariables();
        RecalculateAddresses();
    }

    void RecalculateAddresses()
    {
        uint addr = 0;
        for (int i = 0; i < _insts.Count; i++)
        {
            var inst = _insts[i];
            if (inst.Kind is (InstKind.Label or InstKind.Export or InstKind.LabelAddr) && inst.LabelIndex >= 0)
            {
                var lbl = _labels[inst.LabelIndex];
                lbl.Address = addr;
                _labels[inst.LabelIndex] = lbl;
            }
            addr += InstInfo.Size[inst.Kind];
        }
        _currentAddress = addr;
    }

    void RemoveUnusedVariables()
    {
        var usedVars = new HashSet<string>();
        foreach (var inst in _insts)
        {
            if (inst.Kind == InstKind.Push && inst.Operand != null) usedVars.Add(inst.Operand);
            if (inst.Kind == InstKind.JumpIndirect && inst.Operand != null) usedVars.Add(inst.Operand);
        }
        _vars.RemoveAll(v => v.Id.StartsWith("__intnl_")
            && v.Id != "__intnl_returnJump_SystemUInt32_0"
            && !usedVars.Contains(v.Id));
    }

    void ValidateJumpTargetLabels()
    {
        foreach (var inst in _insts)
        {
            if (inst.Kind is InstKind.Jump or InstKind.JumpIfFalse or InstKind.PushLabel)
            {
                var lbl = _labels[inst.LabelIndex];
                if (!lbl.IsMarked)
                    throw new System.InvalidOperationException(
                        $"Jump to unmarked label '{lbl.Name}' (index {inst.LabelIndex})");
            }
        }
    }

    // Accessors for CFG
    public List<Inst> GetInstructions() => _insts;
    public List<JumpLabel> GetLabels() => _labels;

    public Dictionary<string, string> GetVariableTypes()
    {
        var types = new Dictionary<string, string>();
        foreach (var v in _vars)
            types[v.Id] = v.UdonType;
        return types;
    }

    public Dictionary<string, object> GetConstValues()
    {
        var vals = new Dictionary<string, object>();
        foreach (var v in _vars)
            if (v.ConstValue != null)
                vals[v.Id] = v.ConstValue;
        return vals;
    }

    readonly Dictionary<(string type, object val), string> _constCache = new();

    public string CreateConstVariable(string udonType, object value)
    {
        var key = (udonType, value);
        if (_constCache.TryGetValue(key, out var existing))
            return existing;

        // Count existing consts of this type to generate unique index
        int idx = 0;
        foreach (var v in _vars)
            if (v.Id.StartsWith($"__const_{udonType}_")) idx++;

        var id = $"__const_{udonType}_{idx}";
        _vars.Add(new VarEntry { Id = id, UdonType = udonType, ConstValue = value });
        _constCache[key] = id;
        return id;
    }
}
