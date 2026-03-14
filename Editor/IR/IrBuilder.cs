using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// IR construction API. Provides a high-level interface for building IR instructions
/// within a function. Manages the current insertion block and VReg allocation.
///
/// Replaces the pattern of _module.AddPush/AddExtern/AddCopy with structured IR construction.
/// </summary>
public sealed class IrBuilder
{
    readonly IrModule _module;
    IrFunction _currentFunc;
    IrBlock _currentBlock;

    // Constant deduplication (same semantics as VariableTable._constPool)
    readonly Dictionary<string, IrConst> _constPool = new();

    public IrBuilder(IrModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public IrModule Module => _module;
    public IrFunction CurrentFunction => _currentFunc;
    public IrBlock CurrentBlock => _currentBlock;

    /// <summary>Whether the current block has been terminated (Jump/Branch/Return/Unreachable).</summary>
    public bool IsTerminated => _currentBlock?.Terminator is Jump or Branch or Return or Unreachable;

    // ── Function/Block management ──

    /// <summary>Begin building a new function.</summary>
    public IrFunction BeginFunction(string name, string exportName = null)
    {
        _currentFunc = _module.AddFunction(name, exportName);
        _currentBlock = _currentFunc.Entry;
        return _currentFunc;
    }

    /// <summary>Set an existing function as the current build target.</summary>
    public void SetFunction(IrFunction func)
    {
        _currentFunc = func ?? throw new ArgumentNullException(nameof(func));
        _currentBlock = func.Entry;
    }

    /// <summary>Create a new block in the current function.</summary>
    public IrBlock NewBlock()
    {
        return _currentFunc.NewBlock();
    }

    /// <summary>Set the insertion point to a specific block.</summary>
    public void SetInsertBlock(IrBlock block)
    {
        _currentBlock = block ?? throw new ArgumentNullException(nameof(block));
    }

    /// <summary>Allocate a new virtual register.</summary>
    public VReg NewReg(string type)
    {
        return _currentFunc.NewReg(type);
    }

    // ── Constants ──

    /// <summary>Get or create a deduplicated constant.</summary>
    public IrConst Const(object value, string type)
    {
        var key = $"{type}_{FormatConstKey(value)}";
        if (_constPool.TryGetValue(key, out var existing))
            return existing;
        var c = new IrConst(value, type);
        _constPool[key] = c;
        return c;
    }

    /// <summary>Create an integer constant.</summary>
    public IrConst ConstInt(int value) => Const(value, "SystemInt32");

    /// <summary>Create a uint constant.</summary>
    public IrConst ConstUInt(uint value) => Const(value, "SystemUInt32");

    /// <summary>Create a boolean constant.</summary>
    public IrConst ConstBool(bool value) => Const(value, "SystemBoolean");

    /// <summary>Create a float constant.</summary>
    public IrConst ConstFloat(float value) => Const(value, "SystemSingle");

    /// <summary>Create a double constant.</summary>
    public IrConst ConstDouble(double value) => Const(value, "SystemDouble");

    /// <summary>Create a long constant.</summary>
    public IrConst ConstLong(long value) => Const(value, "SystemInt64");

    /// <summary>Create a string constant.</summary>
    public IrConst ConstString(string value) => Const(value, "SystemString");

    /// <summary>Create a null constant of the given type.</summary>
    public IrConst ConstNull(string type) => Const(null, type);

    /// <summary>Create the sentinel 0xFFFFFFFF constant used for return addresses.</summary>
    public IrConst ConstSentinel() => Const(0xFFFFFFFFu, "SystemUInt32");

    static string FormatConstKey(object value)
    {
        if (value == null) return "null";
        if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
        if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
        if (value is bool b) return b ? "True" : "False";
        if (value is string s) return s;
        if (value is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString();
    }

    // ── Instruction emission ──

    void Emit(IrInst inst)
    {
        // If current block is already terminated, start a dead block for unreachable code
        if (IsTerminated)
            SetInsertBlock(NewBlock());
        _currentBlock.Append(inst);
    }

    /// <summary>
    /// Call an Udon extern function.
    /// Returns the result VReg, or null for void externs.
    /// </summary>
    public VReg EmitCallExtern(string resultType, string externSig, IrValue[] args, bool isPure = false)
    {
        VReg dest = resultType != null ? NewReg(resultType) : null;
        Emit(new CallExtern(dest, externSig, args, isPure));
        return dest;
    }

    /// <summary>Emit a void extern call.</summary>
    public void EmitCallExternVoid(string externSig, IrValue[] args, bool isPure = false)
    {
        Emit(new CallExtern(null, externSig, args, isPure));
    }

    /// <summary>Call an internal function.</summary>
    public VReg EmitCallInternal(IrFunction target, IrValue[] args)
    {
        VReg dest = target.ReturnType != null ? NewReg(target.ReturnType) : null;
        Emit(new CallInternal(dest, target, args));
        return dest;
    }

    /// <summary>Call through a runtime method pointer (delegate invocation).</summary>
    public VReg EmitCallIndirect(string resultType, IrValue methodPtr,
        IrValue[] args, string[] argFields, string retField)
    {
        VReg dest = resultType != null ? NewReg(resultType) : null;
        Emit(new CallIndirect(dest, methodPtr, args, argFields, retField));
        return dest;
    }

    /// <summary>Load a value from a field.</summary>
    public VReg EmitLoad(string fieldName, string type)
    {
        var dest = NewReg(type);
        Emit(new LoadField(dest, fieldName));
        return dest;
    }

    /// <summary>Store a value to a field.</summary>
    public void EmitStore(string fieldName, IrValue value)
    {
        Emit(new StoreField(fieldName, value));
    }

    /// <summary>Emit a ternary select.</summary>
    public VReg EmitSelect(string type, IrValue cond, IrValue trueValue, IrValue falseValue)
    {
        var dest = NewReg(type);
        Emit(new Select(dest, cond, trueValue, falseValue));
        return dest;
    }

    /// <summary>Emit a phi node (used during SSA construction).</summary>
    public Phi EmitPhi(string type)
    {
        var dest = NewReg(type);
        var phi = new Phi(dest);
        Emit(phi);
        return phi;
    }

    /// <summary>Emit a non-SSA copy (only after PhiElimination).</summary>
    public VReg EmitCopy(string type, IrValue source)
    {
        var dest = NewReg(type);
        Emit(new Copy(dest, source));
        return dest;
    }

    // ── Terminators ──

    /// <summary>Emit an unconditional jump and link blocks.</summary>
    public void EmitJump(IrBlock target)
    {
        Emit(new Jump(target));
        IrFunction.LinkBlocks(_currentBlock, target);
    }

    /// <summary>Emit a conditional branch and link blocks.</summary>
    public void EmitBranch(IrValue cond, IrBlock trueTarget, IrBlock falseTarget)
    {
        Emit(new Branch(cond, trueTarget, falseTarget));
        IrFunction.LinkBlocks(_currentBlock, trueTarget);
        if (falseTarget != trueTarget)
            IrFunction.LinkBlocks(_currentBlock, falseTarget);
    }

    /// <summary>Emit a return instruction.</summary>
    public void EmitReturn(IrValue value = null)
    {
        Emit(new Return(value));
    }

    /// <summary>Emit an unreachable marker.</summary>
    public void EmitUnreachable()
    {
        Emit(new Unreachable());
    }

    // ── Convenience patterns ──

    /// <summary>
    /// Emit a binary operation via extern call.
    /// Pattern: result = extern(lhs, rhs)
    /// </summary>
    public VReg EmitBinaryOp(string resultType, string externSig, IrValue lhs, IrValue rhs, bool isPure = true)
    {
        return EmitCallExtern(resultType, externSig, new[] { lhs, rhs }, isPure);
    }

    /// <summary>
    /// Emit a unary operation via extern call.
    /// Pattern: result = extern(operand)
    /// </summary>
    public VReg EmitUnaryOp(string resultType, string externSig, IrValue operand, bool isPure = true)
    {
        return EmitCallExtern(resultType, externSig, new[] { operand }, isPure);
    }

    /// <summary>
    /// Emit a property getter extern.
    /// Pattern: result = instance.PropertyName
    /// </summary>
    public VReg EmitPropertyGet(string resultType, string externSig, IrValue instance)
    {
        return EmitCallExtern(resultType, externSig, new[] { instance }, isPure: true);
    }

    /// <summary>
    /// Emit a static property getter extern (no instance argument).
    /// </summary>
    public VReg EmitStaticPropertyGet(string resultType, string externSig)
    {
        return EmitCallExtern(resultType, externSig, Array.Empty<IrValue>(), isPure: true);
    }

    /// <summary>
    /// Emit a property setter extern.
    /// Pattern: instance.PropertyName = value
    /// </summary>
    public void EmitPropertySet(string externSig, IrValue instance, IrValue value)
    {
        EmitCallExternVoid(externSig, new[] { instance, value });
    }
}
