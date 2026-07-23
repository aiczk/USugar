using System;
using System.Collections.Generic;

// ============================================================================
// CoreBuilder — the Core IR construction API targeted by the emit handlers. Builds the structured
// CModule/CFunction/CStmt/CValue tree and manages the current function, the insertion point
// (statement-list stack), slot allocation, and constant deduplication.
// ============================================================================

public sealed class CoreBuilder
{
    readonly CModule _module;
    CFunction _currentFunc;
    readonly Stack<List<CStmt>> _stmtStack = new Stack<List<CStmt>>();
    readonly Dictionary<ConstKey, CConst> _constPool = new Dictionary<ConstKey, CConst>();

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

    public int AllocPinned(StorageType type, string fixedName)
        => _currentFunc.NewSlot(type, SlotClass.Pinned, fixedName);

    public int AllocFrame(StorageType type)
        => _currentFunc.NewSlot(type, SlotClass.Frame);

    public int AllocScratch(StorageType type)
        => _currentFunc.NewSlot(type, SlotClass.Scratch);

    // ── Constant deduplication ──

    public CConst Const(object value, StorageType type)
    {
        var key = ConstFormat.Key(type.Name, value);
        if (_constPool.TryGetValue(key, out var existing))
            return existing;
        var c = new CConst(value, type);
        _constPool[key] = c;
        return c;
    }

    public CConst Null(StorageType type) => Const(null, type);

    // ── Statement emission ──

    public void Emit(CStmt stmt)
    {
        if (_stmtStack.Count == 0)
            throw new InvalidOperationException("No active statement list. Call BeginFunction first.");
        _stmtStack.Peek().Add(stmt);
    }

    public void EmitAssign(int destSlot, CValue value) => Emit(new CAssign(destSlot, value));
    public void EmitStoreField(string fieldName, CLeaf value) => Emit(new CStoreField(fieldName, value));
    public void EmitProgramVariableStore(CLeaf instance, CLeaf variableName,
        StorageType variableType, CLeaf value)
        => Emit(new CProgramVariableStore(instance, variableName, variableType, value));
    public void EmitReturn(CLeaf value = null) => Emit(new CReturn(value));
    public void EmitBreak() => Emit(new CBreak());
    public void EmitContinue() => Emit(new CContinue());
    public void EmitGoto(string label) => Emit(new CGoto(label));
    public void EmitLabel(string label) => Emit(new CLabel(label));
    // A-normal form: a value-producing call is materialized at construction, so a leaf or null reaching
    // here has no remaining side effect — skip it. (Void calls return null after self-emitting.)
    public void EmitExprStmt(CValue expr)
    {
        if (expr == null || expr is CLeaf) return;
        Emit(new CExprStmt(expr));
    }

    // ── Structured control flow ──

    public void EmitIf(CLeaf cond, Action<CoreBuilder> thenBuilder, Action<CoreBuilder> elseBuilder = null)
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

    public void EmitWhile(Func<CLeaf> condFactory, Action<CoreBuilder> bodyBuilder, bool isDoWhile = false)
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

    public void EmitFor(Action<CoreBuilder> initBuilder, Func<CLeaf> condFactory,
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
    public CFieldAddr FieldAddr(string fieldName, StorageType type) => new CFieldAddr(fieldName, type);
    public CFuncRef FuncRef(string funcName) => new CFuncRef(funcName);
    public CLeaf RepresentationCast(CLeaf source, StorageType type, RepresentationCastKind kind)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (source.Type == type) return source;
        return Bind(new CRepresentationCast(source, type, kind), type);
    }

    // ── Value producers (A-normal form) ──
    // Each binds its producer to a fresh scratch slot at the current insertion point (program order)
    // and returns that slot leaf, so a producer never nests in an operand position. Void calls have no
    // value: they emit as a side-effecting statement and return null (callers must not use the result).

    /// <summary>Bind a value-producing node to a fresh scratch slot and return the slot leaf.</summary>
    CSlotRef Bind(CValue producer, StorageType type)
    {
        var t = AllocScratch(type);
        Emit(new CAssign(t, producer));
        return SlotRef(t);
    }

    public CSlotRef LoadField(string fieldName, StorageType type) => Bind(new CFieldLoad(fieldName, type), type);
    public CSlotRef LoadProgramVariable(CLeaf instance, CLeaf variableName, StorageType type)
        => Bind(new CProgramVariableLoad(instance, variableName, type), type);
    public CSlotRef Select(CLeaf cond, CLeaf trueVal, CLeaf falseVal, StorageType type)
        => Bind(new CSelect(cond, trueVal, falseVal, type), type);

    public CSlotRef ExternCall(UdonAbiKey sig, List<CLeaf> args, StorageType retType)
    {
        var bound = RequireExtern(sig);
        return ExternCall(bound, args, retType);
    }

    public CSlotRef ExternCall(BoundExtern bound, List<CLeaf> args, StorageType retType)
    {
        if (bound == null) throw new ArgumentNullException(nameof(bound));
        if (retType == StorageTypes.Void) { Emit(new CExprStmt(new CExternCall(bound, args, retType))); return null; }
        return Bind(new CExternCall(bound, args, retType), retType);
    }

    public CSlotRef InternalCall(string funcName, List<CLeaf> args, StorageType retType, bool tailSpared = false)
    {
        if (retType == StorageTypes.Void) { Emit(new CExprStmt(new CInternalCall(funcName, args, retType, tailSpared: tailSpared))); return null; }
        return Bind(new CInternalCall(funcName, args, retType, tailSpared: tailSpared), retType);
    }

    /// <summary>Cross-behaviour call (SetProgramVariable* + SendCustomEvent + GetProgramVariable*).
    /// A single-return call is a value: bind it to a fresh scratch slot (A-normal form) and return the
    /// leaf. A void OR multi-return call (retType "SystemVoid") carries no single value — emit it as a
    /// side-effecting statement at the current insertion point and return null. Binding at the construction
    /// point keeps the SendCustomEvent in program order; ternary branches construct their cross-call inside
    /// the branch block (VisitConditionalExpression uses EmitIf, not CSelect), so the bind is conditional.</summary>
    public CSlotRef CrossCall(CLeaf instance, CrossCallTransportPlan transport,
        bool reentrant = false)
    {
        // Wave-12 r2 [V1]: a reentrant cross dispatch is a §4.3 spill site — the flag is counted here
        // (creation choke point) and materialized on the SendCustomEvent by CoreFlatten.LowerCrossCall.
        if (reentrant) CurrentFunction.ReentrantSiteCount++;
        var cc = new CCrossCall(instance, transport, reentrant);
        if (transport.ResultType == StorageTypes.Void) { Emit(new CExprStmt(cc)); return null; }
        return Bind(cc, transport.ResultType);
    }

    // The reentrant flag (design §4.3) marks a delegate-dispatch arm that can re-enter the containing
    // function; the count increment here is the single creation choke point FlatVerify's conservation
    // check is balanced against. preSpillStmts remains the generic extern form.
    // Cross-program dispatch uses CCrossCall so CoreFlatten counts its typed copy-ins directly.
    public void EmitExternVoid(UdonAbiKey sig, List<CLeaf> args, bool reentrant = false, int preSpillStmts = 0)
        => EmitExternVoid(RequireExtern(sig), args, reentrant, preSpillStmts);

    public void EmitExternVoid(BoundExtern bound, List<CLeaf> args, bool reentrant = false, int preSpillStmts = 0)
    {
        if (bound == null) throw new ArgumentNullException(nameof(bound));
        if (reentrant) CurrentFunction.ReentrantSiteCount++;
        Emit(new CExprStmt(new CExternCall(
            bound, args, StorageTypes.Void, null, reentrant, preSpillStmts)));
    }

    BoundExtern RequireExtern(UdonAbiKey signature)
        => (_module.AbiCatalog
            ?? throw new InvalidOperationException(
                "CoreBuilder cannot emit an extern without a Udon ABI catalog."))
            .Require(signature);

    public void EmitInternalVoid(string funcName, List<CLeaf> args, bool reentrant = false)
    {
        if (reentrant) CurrentFunction.ReentrantSiteCount++;
        Emit(new CExprStmt(new CInternalCall(funcName, args, StorageTypes.Void, null, reentrant)));
    }
}
