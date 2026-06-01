using System;
using System.Collections.Generic;
using System.Globalization;

// ============================================================================
// CoreBuilder — the Core IR construction API. A 1:1 mirror of HirBuilder that builds the
// unified CModule/CFunction/CStmt/CValue instead of HIR. This is the builder that emit handlers
// will target in Phase 3 (replacing HirBuilder); same method surface, so handler call sites move
// across by retyping HExpr->CValue / HStmt->CStmt. Manages current function, insertion point
// (statement-list stack), slot allocation, and constant deduplication — identically to HirBuilder.
// ============================================================================

public sealed class CoreBuilder
{
    readonly CModule _module;
    CFunction _currentFunc;
    readonly Stack<List<CStmt>> _stmtStack = new Stack<List<CStmt>>();
    readonly Dictionary<string, CConst> _constPool = new Dictionary<string, CConst>();

    public CoreBuilder(CModule module)
        => _module = module ?? throw new ArgumentNullException(nameof(module));

    public CModule Module => _module;
    public CFunction CurrentFunction => _currentFunc;

    // ── Function management ──

    public CFunction BeginFunction(string name, string exportName = null)
    {
        _currentFunc = _module.AddFunction(name, exportName);
        _stmtStack.Clear();
        _stmtStack.Push(_currentFunc.Body.Stmts);
        return _currentFunc;
    }

    public void SetFunction(CFunction func)
    {
        _currentFunc = func ?? throw new ArgumentNullException(nameof(func));
        _stmtStack.Clear();
        _stmtStack.Push(func.Body.Stmts);
    }

    // ── Slot allocation ──

    public int AllocPinned(string type, string fixedName)
        => _currentFunc.NewSlot(type, SlotClass.Pinned, fixedName);

    public int AllocFrame(string type)
        => _currentFunc.NewSlot(type, SlotClass.Frame);

    public int AllocScratch(string type)
        => _currentFunc.NewSlot(type, SlotClass.Scratch);

    // ── Constant deduplication ──

    public CConst Const(object value, string type)
    {
        var key = FormatConstKey(value, type);
        if (_constPool.TryGetValue(key, out var existing))
            return existing;
        var c = new CConst(value, type);
        _constPool[key] = c;
        return c;
    }

    public CConst Null(string type) => Const(null, type);

    static string FormatConstKey(object value, string type)
    {
        if (value == null) return $"{type}_null";
        if (value is float f) return $"{type}_{f.ToString("R", CultureInfo.InvariantCulture)}";
        if (value is double d) return $"{type}_{d.ToString("R", CultureInfo.InvariantCulture)}";
        return $"{type}_{value}";
    }

    // ── Statement emission ──

    public void Emit(CStmt stmt)
    {
        if (_stmtStack.Count == 0)
            throw new InvalidOperationException("No active statement list. Call BeginFunction first.");
        _stmtStack.Peek().Add(stmt);
    }

    public void EmitAssign(int destSlot, CValue value) => Emit(new CAssign(destSlot, value));
    public void EmitStoreField(string fieldName, CValue value) => Emit(new CStoreField(fieldName, value));
    public void EmitReturn(CValue value = null) => Emit(new CReturn(value));
    public void EmitBreak() => Emit(new CBreak());
    public void EmitContinue() => Emit(new CContinue());
    public void EmitGoto(string label) => Emit(new CGoto(label));
    public void EmitLabel(string label) => Emit(new CLabel(label));
    public void EmitExprStmt(CValue expr) => Emit(new CExprStmt(expr));

    // ── Structured control flow ──

    public void EmitIf(CValue cond, Action<CoreBuilder> thenBuilder, Action<CoreBuilder> elseBuilder = null)
    {
        var thenBlock = new CBlock();
        var elseBlock = new CBlock();

        if (thenBuilder != null)
        {
            _stmtStack.Push(thenBlock.Stmts);
            thenBuilder(this);
            _stmtStack.Pop();
        }

        if (elseBuilder != null)
        {
            _stmtStack.Push(elseBlock.Stmts);
            elseBuilder(this);
            _stmtStack.Pop();
        }

        Emit(new CIf(cond, thenBlock, elseBlock));
    }

    public void EmitWhile(CValue cond, Action<CoreBuilder> bodyBuilder, bool isDoWhile = false, CBlock condBlock = null)
    {
        var body = new CBlock();
        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();
        Emit(new CWhile(cond, body, isDoWhile, condBlock));
    }

    public void EmitWhile(Func<CValue> condFactory, Action<CoreBuilder> bodyBuilder, bool isDoWhile = false)
    {
        var condBlock = new CBlock();
        _stmtStack.Push(condBlock.Stmts);
        var cond = condFactory();
        _stmtStack.Pop();

        var body = new CBlock();
        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();

        Emit(new CWhile(cond, body, isDoWhile, condBlock));
    }

    public void EmitFor(Action<CoreBuilder> initBuilder, CValue cond,
        Action<CoreBuilder> updateBuilder, Action<CoreBuilder> bodyBuilder)
    {
        var init = new CBlock();
        var update = new CBlock();
        var body = new CBlock();

        _stmtStack.Push(init.Stmts);
        initBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(update.Stmts);
        updateBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();

        Emit(new CFor(init, cond, update, body));
    }

    public void EmitFor(Action<CoreBuilder> initBuilder, Func<CValue> condFactory,
        Action<CoreBuilder> updateBuilder, Action<CoreBuilder> bodyBuilder)
    {
        var init = new CBlock();
        var condBlock = new CBlock();
        var update = new CBlock();
        var body = new CBlock();

        _stmtStack.Push(init.Stmts);
        initBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(condBlock.Stmts);
        var cond = condFactory();
        _stmtStack.Pop();

        _stmtStack.Push(update.Stmts);
        updateBuilder(this);
        _stmtStack.Pop();

        _stmtStack.Push(body.Stmts);
        bodyBuilder(this);
        _stmtStack.Pop();

        Emit(new CFor(init, cond, update, body, condBlock));
    }

    public CBlock BeginBlock()
    {
        var block = new CBlock();
        _stmtStack.Push(block.Stmts);
        return block;
    }

    public void EndBlock()
    {
        if (_stmtStack.Count <= 1)
            throw new InvalidOperationException("Cannot pop the root statement list.");
        _stmtStack.Pop();
    }

    // ── Expression helpers ──

    public CSlotRef SlotRef(int slotId) => new CSlotRef(slotId, _currentFunc.Slots[slotId].Type);
    public CFieldRef LoadField(string fieldName, string type) => new CFieldRef(fieldName, type, CFieldMode.Load);
    public CFieldRef FieldAddr(string fieldName, string type) => new CFieldRef(fieldName, type, CFieldMode.Addr);
    public CExternCall ExternCall(string sig, List<CValue> args, string retType) => new CExternCall(sig, args, retType);
    public void EmitExternVoid(string sig, List<CValue> args) => EmitExprStmt(new CExternCall(sig, args, "SystemVoid"));
    public CInternalCall InternalCall(string funcName, List<CValue> args, string retType) => new CInternalCall(funcName, args, retType);
    public CSelect Select(CValue cond, CValue trueVal, CValue falseVal, string type) => new CSelect(cond, trueVal, falseVal, type);
    public CFuncRef FuncRef(string funcName) => new CFuncRef(funcName);
}
