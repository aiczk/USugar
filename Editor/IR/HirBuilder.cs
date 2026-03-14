using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// HIR construction API. Builds structured HIR from handler calls.
/// Manages current function, insertion point (statement list stack),
/// slot allocation, and constant deduplication.
/// </summary>
public sealed class HirBuilder
{
    readonly HModule _module;
    HFunction _currentFunc;
    readonly Stack<List<HStmt>> _stmtStack = new();
    readonly Dictionary<string, HConst> _constPool = new();

    public HirBuilder(HModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public HModule Module => _module;
    public HFunction CurrentFunction => _currentFunc;

    // ── Function management ──

    /// <summary>Begin building a new function. Sets it as the current insertion target.</summary>
    public HFunction BeginFunction(string name, string exportName = null)
    {
        _currentFunc = _module.AddFunction(name, exportName);
        _stmtStack.Clear();
        _stmtStack.Push(_currentFunc.Body.Stmts);
        return _currentFunc;
    }

    /// <summary>Set an existing function as the current build target.</summary>
    public void SetFunction(HFunction func)
    {
        _currentFunc = func ?? throw new ArgumentNullException(nameof(func));
        _stmtStack.Clear();
        _stmtStack.Push(func.Body.Stmts);
    }

    // ── Slot allocation ──

    /// <summary>Allocate a Pinned slot with a fixed UASM variable name.</summary>
    public int AllocPinned(string type, string fixedName)
        => _currentFunc.NewSlot(type, SlotClass.Pinned, fixedName);

    /// <summary>Allocate a Frame slot (survives across internal calls).</summary>
    public int AllocFrame(string type)
        => _currentFunc.NewSlot(type, SlotClass.Frame);

    /// <summary>Allocate a Scratch slot (temp, does not survive calls).</summary>
    public int AllocScratch(string type)
        => _currentFunc.NewSlot(type, SlotClass.Scratch);

    // ── Constant deduplication ──

    /// <summary>Get or create a deduplicated constant.</summary>
    public HConst Const(object value, string type)
    {
        var key = FormatConstKey(value, type);
        if (_constPool.TryGetValue(key, out var existing))
            return existing;
        var c = new HConst(value, type);
        _constPool[key] = c;
        return c;
    }

    /// <summary>Create a null constant of the given type.</summary>
    public HConst Null(string type) => Const(null, type);

    static string FormatConstKey(object value, string type)
    {
        if (value == null) return $"{type}_null";
        if (value is float f) return $"{type}_{f.ToString("R", CultureInfo.InvariantCulture)}";
        if (value is double d) return $"{type}_{d.ToString("R", CultureInfo.InvariantCulture)}";
        return $"{type}_{value}";
    }

    // ── Statement emission ──

    /// <summary>Add a statement to the current insertion point.</summary>
    public void Emit(HStmt stmt)
    {
        if (_stmtStack.Count == 0)
            throw new InvalidOperationException("No active statement list. Call BeginFunction first.");
        _stmtStack.Peek().Add(stmt);
    }

    /// <summary>Emit: slot = expr</summary>
    public void EmitAssign(int destSlot, HExpr value) => Emit(new HAssign(destSlot, value));

    /// <summary>Emit: fieldName = expr</summary>
    public void EmitStoreField(string fieldName, HExpr value) => Emit(new HStoreField(fieldName, value));

    /// <summary>Emit: return [value]</summary>
    public void EmitReturn(HExpr value = null) => Emit(new HReturn(value));

    /// <summary>Emit: break</summary>
    public void EmitBreak() => Emit(new HBreak());

    /// <summary>Emit: continue</summary>
    public void EmitContinue() => Emit(new HContinue());

    /// <summary>Emit: goto label</summary>
    public void EmitGoto(string label) => Emit(new HGoto(label));

    /// <summary>Emit: label:</summary>
    public void EmitLabel(string label) => Emit(new HLabelStmt(label));

    /// <summary>Emit: expr (as statement, for side-effecting calls)</summary>
    public void EmitExprStmt(HExpr expr) => Emit(new HExprStmt(expr));

    // ── Structured control flow ──

    /// <summary>
    /// Build an if/else. Callbacks receive the builder for emitting into then/else blocks.
    /// </summary>
    public void EmitIf(HExpr cond, Action<HirBuilder> thenBuilder, Action<HirBuilder> elseBuilder = null)
    {
        var thenBlock = new HBlock();
        var elseBlock = new HBlock();

        _stmtStack.Push(thenBlock.Stmts);
        thenBuilder(this);
        _stmtStack.Pop();

        if (elseBuilder != null)
        {
            _stmtStack.Push(elseBlock.Stmts);
            elseBuilder(this);
            _stmtStack.Pop();
        }

        Emit(new HIf(cond, thenBlock, elseBlock));
    }

    /// <summary>Build a while loop.</summary>
    public void EmitWhile(HExpr cond, Action<HirBuilder> bodyBuilder, bool isDoWhile = false)
    {
        var body = new HBlock();
        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();
        Emit(new HWhile(cond, body, isDoWhile));
    }

    /// <summary>Build a for loop.</summary>
    public void EmitFor(Action<HirBuilder> initBuilder, HExpr cond,
        Action<HirBuilder> updateBuilder, Action<HirBuilder> bodyBuilder)
    {
        var init = new HBlock();
        var update = new HBlock();
        var body = new HBlock();

        _stmtStack.Push(init.Stmts);
        initBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(update.Stmts);
        updateBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();

        Emit(new HFor(init, cond, update, body));
    }

    /// <summary>Build a for loop with lazy condition (evaluated after init).</summary>
    public void EmitFor(Action<HirBuilder> initBuilder, Func<HExpr> condFactory,
        Action<HirBuilder> updateBuilder, Action<HirBuilder> bodyBuilder)
    {
        var init = new HBlock();
        var update = new HBlock();
        var body = new HBlock();

        _stmtStack.Push(init.Stmts);
        initBuilder(this);
        _stmtStack.Pop();

        // Evaluate condition AFTER init so declared locals are registered
        var cond = condFactory();

        _stmtStack.Push(update.Stmts);
        updateBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();

        Emit(new HFor(init, cond, update, body));
    }

    /// <summary>Push a new nested scope for manual block construction.</summary>
    public HBlock BeginBlock()
    {
        var block = new HBlock();
        _stmtStack.Push(block.Stmts);
        return block;
    }

    /// <summary>Pop the current nested scope and emit the block as a statement.</summary>
    public void EndBlock()
    {
        if (_stmtStack.Count <= 1)
            throw new InvalidOperationException("Cannot pop the root statement list.");
        _stmtStack.Pop();
    }

    // ── Expression helpers ──

    /// <summary>Create a slot reference expression.</summary>
    public HSlotRef SlotRef(int slotId) => new(slotId, _currentFunc.Slots[slotId].Type);

    /// <summary>Create a field load expression.</summary>
    public HLoadField LoadField(string fieldName, string type) => new(fieldName, type);

    /// <summary>Create an extern call expression.</summary>
    public HExternCall ExternCall(string sig, List<HExpr> args, string retType, bool isPure = false)
        => new(sig, args, retType, isPure);

    /// <summary>Create a void extern call and emit as statement.</summary>
    public void EmitExternVoid(string sig, List<HExpr> args)
        => EmitExprStmt(new HExternCall(sig, args, "SystemVoid"));

    /// <summary>Create an internal call expression.</summary>
    public HInternalCall InternalCall(string funcName, List<HExpr> args, string retType)
        => new(funcName, args, retType);

    /// <summary>Create a select (ternary) expression.</summary>
    public HSelect Select(HExpr cond, HExpr trueVal, HExpr falseVal, string type)
        => new(cond, trueVal, falseVal, type);

    /// <summary>Create a function reference (for delegate/JUMP_INDIRECT).</summary>
    public HFuncRef FuncRef(string funcName) => new(funcName);
}
