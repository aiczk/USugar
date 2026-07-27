using System;
using System.Collections.Generic;

/// <summary>
/// Constructs the compiler's control-flow graph directly. Semantic producers are validated and
/// materialized at the call site; there is no second structured IR waiting to be flattened.
/// </summary>
public sealed class CoreBuilder
{
    sealed class FunctionState
    {
        public readonly FlatFunction Function;
        public readonly Stack<(FlatBlock Exit, FlatBlock Continue)> Loops = new();
        public readonly Dictionary<string, FlatBlock> Labels = new(StringComparer.Ordinal);
        public readonly HashSet<string> DefinedLabels = new(StringComparer.Ordinal);
        public readonly HashSet<string> ReferencedLabels = new(StringComparer.Ordinal);
        public FlatBlock Current;

        public FunctionState(FlatFunction function)
        {
            Function = function;
            Current = function.Entry ?? function.NewBlock();
        }
    }

    readonly FlatModule _module;
    readonly Dictionary<FlatFunction, FunctionState> _states = new();
    readonly Dictionary<ConstKey, CConst> _constPool = new();
    FlatFunction _currentFunc;

    public CoreBuilder(FlatModule module)
        => _module = module ?? throw new ArgumentNullException(nameof(module));

    public FlatModule Module => _module;
    public FlatFunction CurrentFunction => _currentFunc;
    FunctionState State => _currentFunc != null
        && _states.TryGetValue(_currentFunc, out var state)
            ? state
            : throw new InvalidOperationException(
                "No active CFG function. Call BeginFunction or SetFunction first.");

    public FlatFunction BeginFunction(string name, string exportName = null)
    {
        var function = _module.AddFunction(name, exportName);
        SetFunction(function);
        return function;
    }

    public void SetFunction(FlatFunction function)
    {
        _currentFunc = function ?? throw new ArgumentNullException(nameof(function));
        if (!_states.ContainsKey(function))
            _states.Add(function, new FunctionState(function));
    }

    public int AllocPinned(StorageType type, string fixedName)
        => CurrentFunction.NewSlot(type, SlotClass.Pinned, fixedName);

    public int AllocFrame(StorageType type)
        => CurrentFunction.NewSlot(type, SlotClass.Frame);

    public int AllocScratch(StorageType type)
        => CurrentFunction.NewSlot(type, SlotClass.Scratch);

    public CConst Const(object value, StorageType type)
    {
        var key = ConstFormat.Key(type.Name, value);
        if (_constPool.TryGetValue(key, out var existing))
            return existing;
        var constant = new CConst(value, type);
        _constPool[key] = constant;
        return constant;
    }

    public CConst Null(StorageType type) => Const(null, type);

    public void EmitAssign(int destSlot, CValue value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        RequireSlot(destSlot, "CAssign");
        AssertType(CurrentFunction.Slots[destSlot].Type, value.Type,
            $"CAssign to slot{destSlot}");

        switch (value)
        {
            case CLeaf leaf:
                RequireLeaf(leaf, "CAssign value");
                AddInstruction(new CAssign(destSlot, leaf));
                return;
            case CExternCall call:
                EmitCall(call.With(new List<CLeaf>(call.Args), destSlot));
                return;
            case CInternalCall call:
                EmitCall(call.With(new List<CLeaf>(call.Args), destSlot));
                return;
            default:
                throw new VerificationException(
                    $"Unsupported CFG value producer '{value.GetType().Name}' "
                    + $"(function '{CurrentFunction.Name}')");
        }
    }

    public void EmitStoreField(string fieldName, CLeaf value)
    {
        RequireLeaf(value, "CStoreField value");
        AssertField(fieldName, value.Type, "CStoreField");
        AddInstruction(new CStoreField(fieldName, value));
    }

    public void EmitProgramVariableStore(CLeaf instance, CLeaf variableName,
        StorageType variableType, CLeaf value)
    {
        ValidateProgramReceiver(instance, "CProgramVariableStore receiver");
        RequireLeaf(variableName, "CProgramVariableStore variable name");
        RequireLeaf(value, "CProgramVariableStore value");
        AssertType(StorageTypes.String, variableName.Type,
            "CProgramVariableStore variable name");
        AssertType(variableType, value.Type, "CProgramVariableStore value");
        EmitCall(new CExternCall(
            RequireExtern(ExternResolver.EventReceiverSetProgramVariable),
            new List<CLeaf> { instance, variableName, value },
            StorageTypes.Void));
    }

    public void EmitReturn(CLeaf value = null)
    {
        if (value != null) RequireLeaf(value, "CRet value");
        var returnsVoid = !CurrentFunction.ReturnType.HasValue
            || CurrentFunction.ReturnType.Value == StorageTypes.Void;
        if (returnsVoid && value != null)
            throw new VerificationException(
                $"Void function '{CurrentFunction.Name}' returns '{value.Type}'");
        if (!returnsVoid && value == null && CurrentFunction.ReturnSlots.Count == 0)
            throw new VerificationException(
                $"Non-void function '{CurrentFunction.Name}' returns without a value");
        if (value != null)
            AssertType(CurrentFunction.ReturnType.Value, value.Type, "CRet");
        Terminate(new CRet(value));
    }

    public void EmitBreak()
    {
        if (State.Loops.Count == 0)
            throw new VerificationException(
                $"break outside of loop (function '{CurrentFunction.Name}')");
        Terminate(new CJump(State.Loops.Peek().Exit.Id));
    }

    public void EmitContinue()
    {
        if (State.Loops.Count == 0)
            throw new VerificationException(
                $"continue outside of loop (function '{CurrentFunction.Name}')");
        Terminate(new CJump(State.Loops.Peek().Continue.Id));
    }

    public void EmitGoto(string label)
    {
        if (label == null) throw new ArgumentNullException(nameof(label));
        State.ReferencedLabels.Add(label);
        var target = RequireLabelBlock(State, label);
        Terminate(new CJump(target.Id));
    }

    public void EmitLabel(string label)
    {
        if (label == null) throw new ArgumentNullException(nameof(label));
        if (!State.DefinedLabels.Add(label))
            throw new VerificationException(
                $"Duplicate label '{label}' (function '{CurrentFunction.Name}')");
        var target = RequireLabelBlock(State, label);
        if (State.Current.Terminator == null)
            State.Current.Terminator = new CJump(target.Id);
        State.Current = target;
    }

    public void EmitExprStmt(CValue expression)
    {
        switch (expression)
        {
            case null:
            case CLeaf:
                return;
            case CExternCall call:
                EmitCall(call);
                return;
            case CInternalCall call:
                EmitCall(call);
                return;
            default:
                throw new VerificationException(
                    $"Expression statement cannot contain '{expression.GetType().Name}' "
                    + $"(function '{CurrentFunction.Name}')");
        }
    }

    public void EmitIf(CLeaf condition, Action<CoreBuilder> thenBuilder,
        Action<CoreBuilder> elseBuilder = null)
    {
        RequireBoolean(condition, "if condition");
        if (State.Current.Terminator != null)
        {
            RunDetached(thenBuilder);
            RunDetached(elseBuilder);
            return;
        }

        var owner = CurrentFunction;
        var state = State;
        var branchBlock = state.Current;
        var thenBlock = owner.NewBlock();
        var elseBlock = owner.NewBlock();
        var mergeBlock = owner.NewBlock();
        branchBlock.Terminator = new CBranch(condition, thenBlock.Id, elseBlock.Id);

        state.Current = thenBlock;
        thenBuilder?.Invoke(this);
        RequireOwner(owner, "if branch");
        if (state.Current.Terminator == null)
            state.Current.Terminator = new CJump(mergeBlock.Id);

        state.Current = elseBlock;
        elseBuilder?.Invoke(this);
        RequireOwner(owner, "else branch");
        if (state.Current.Terminator == null)
            state.Current.Terminator = new CJump(mergeBlock.Id);

        state.Current = mergeBlock;
    }

    public void EmitWhile(Func<CLeaf> conditionFactory,
        Action<CoreBuilder> bodyBuilder, bool isDoWhile = false)
    {
        if (conditionFactory == null) throw new ArgumentNullException(nameof(conditionFactory));
        if (bodyBuilder == null) throw new ArgumentNullException(nameof(bodyBuilder));
        if (State.Current.Terminator != null)
        {
            CLeaf detachedCondition = null;
            RunDetached(_ => detachedCondition = conditionFactory());
            RequireBoolean(detachedCondition, "while condition");
            RunDetached(bodyBuilder, loop: true);
            return;
        }

        var owner = CurrentFunction;
        var state = State;
        var predecessor = state.Current;
        var header = owner.NewBlock();
        var body = owner.NewBlock();
        var exit = owner.NewBlock();

        state.Current = header;
        var condition = conditionFactory();
        RequireOwner(owner, "while condition");
        RequireBoolean(condition, "while condition");
        if (state.Current.Terminator != null)
            throw new VerificationException(
                $"While condition terminated its CFG block (function '{owner.Name}')");
        state.Current.Terminator = new CBranch(condition, body.Id, exit.Id);

        state.Current = body;
        state.Loops.Push((exit, header));
        try
        {
            bodyBuilder(this);
            RequireOwner(owner, "while body");
        }
        finally
        {
            state.Loops.Pop();
        }
        if (state.Current.Terminator == null)
            state.Current.Terminator = new CJump(header.Id);

        predecessor.Terminator = new CJump(isDoWhile ? body.Id : header.Id);
        state.Current = exit;
    }

    public void EmitFor(Action<CoreBuilder> initBuilder, Func<CLeaf> conditionFactory,
        Action<CoreBuilder> updateBuilder, Action<CoreBuilder> bodyBuilder)
    {
        if (State.Current.Terminator != null)
        {
            RunDetached(initBuilder);
            CLeaf detachedCondition = null;
            RunDetached(_ => detachedCondition = conditionFactory?.Invoke());
            if (detachedCondition != null)
                RequireBoolean(detachedCondition, "for condition");
            RunDetached(updateBuilder);
            RunDetached(bodyBuilder, loop: true);
            return;
        }

        var owner = CurrentFunction;
        var state = State;
        initBuilder?.Invoke(this);
        RequireOwner(owner, "for initializer");
        var predecessor = state.Current;
        if (predecessor.Terminator != null)
        {
            CLeaf detachedCondition = null;
            RunDetached(_ => detachedCondition = conditionFactory?.Invoke());
            if (detachedCondition != null)
                RequireBoolean(detachedCondition, "for condition");
            RunDetached(updateBuilder);
            RunDetached(bodyBuilder, loop: true);
            return;
        }

        var header = owner.NewBlock();
        var body = owner.NewBlock();
        var continueBlock = owner.NewBlock();
        var exit = owner.NewBlock();

        state.Current = header;
        var condition = conditionFactory?.Invoke();
        RequireOwner(owner, "for condition");
        if (condition != null) RequireBoolean(condition, "for condition");
        if (state.Current.Terminator != null)
            throw new VerificationException(
                $"For condition terminated its CFG block (function '{owner.Name}')");
        state.Current.Terminator = condition != null
            ? new CBranch(condition, body.Id, exit.Id)
            : new CJump(body.Id);

        state.Current = continueBlock;
        updateBuilder?.Invoke(this);
        RequireOwner(owner, "for update");
        if (state.Current.Terminator == null)
            state.Current.Terminator = new CJump(header.Id);

        state.Current = body;
        state.Loops.Push((exit, continueBlock));
        try
        {
            bodyBuilder?.Invoke(this);
            RequireOwner(owner, "for body");
        }
        finally
        {
            state.Loops.Pop();
        }
        if (state.Current.Terminator == null)
            state.Current.Terminator = new CJump(continueBlock.Id);

        predecessor.Terminator = new CJump(header.Id);
        state.Current = exit;
    }

    public CSlotRef SlotRef(int slotId)
    {
        RequireSlot(slotId, "CSlotRef");
        return new CSlotRef(slotId, CurrentFunction.Slots[slotId].Type);
    }

    public CFieldAddr FieldAddr(string fieldName, StorageType type)
    {
        AssertField(fieldName, type, "CFieldAddr");
        return new CFieldAddr(fieldName, type);
    }

    public CFuncRef FuncRef(string functionName) => new(functionName);

    public CLeaf RepresentationCast(CLeaf source, StorageType type,
        RepresentationCastKind kind)
    {
        RequireLeaf(source, "representation cast source");
        if (source.Type == type) return source;
        var destination = AllocScratch(type);
        AddInstruction(new CRepresentationCopy(
            destination, source, type, kind));
        return SlotRef(destination);
    }

    CSlotRef Bind(CValue producer, StorageType type)
    {
        var slot = AllocScratch(type);
        EmitAssign(slot, producer);
        return SlotRef(slot);
    }

    public CSlotRef LoadField(string fieldName, StorageType type)
    {
        AssertField(fieldName, type, "CLoadField");
        var destination = AllocScratch(type);
        AddInstruction(new CLoadField(destination, fieldName, type));
        return SlotRef(destination);
    }

    public CSlotRef LoadProgramVariable(CLeaf instance, CLeaf variableName,
        StorageType type)
    {
        ValidateProgramReceiver(instance, "program-variable load receiver");
        RequireLeaf(variableName, "program-variable load name");
        AssertType(StorageTypes.String, variableName.Type,
            "program-variable load name");
        var destination = AllocScratch(type);
        EmitCall(new CExternCall(
            RequireExtern(ExternResolver.EventReceiverGetProgramVariable),
            new List<CLeaf> { instance, variableName },
            type, destination));
        return SlotRef(destination);
    }

    public CSlotRef Select(CLeaf condition, CLeaf trueValue,
        CLeaf falseValue, StorageType type)
    {
        var destination = AllocScratch(type);
        EmitSelect(
            condition, trueValue, falseValue, type, destination);
        return SlotRef(destination);
    }

    public CSlotRef ExternCall(UdonAbiKey signature, List<CLeaf> arguments,
        StorageType returnType)
        => ExternCall(RequireExtern(signature), arguments, returnType);

    public CSlotRef ExternCall(BoundExtern bound, List<CLeaf> arguments,
        StorageType returnType)
    {
        var call = new CExternCall(bound, arguments, returnType);
        if (returnType == StorageTypes.Void)
        {
            EmitCall(call);
            return null;
        }
        return Bind(call, returnType);
    }

    public CSlotRef InternalCall(string functionName, List<CLeaf> arguments,
        StorageType returnType, bool tailSpared = false,
        bool reentrant = false)
    {
        var call = new CInternalCall(
            functionName, arguments, returnType,
            reentrant: reentrant,
            tailSpared: tailSpared);
        if (returnType == StorageTypes.Void)
        {
            EmitCall(call);
            return null;
        }
        return Bind(call, returnType);
    }

    public CSlotRef CrossCall(CLeaf instance, CrossCallTransportPlan transport,
        bool reentrant = false)
    {
        ValidateCrossCall(instance, transport);
        if (transport.ResultType == StorageTypes.Void)
        {
            EmitCrossCall(instance, transport, reentrant, null);
            return null;
        }
        var destination = AllocScratch(transport.ResultType);
        EmitCrossCall(instance, transport, reentrant, destination);
        return SlotRef(destination);
    }

    public void EmitExternVoid(UdonAbiKey signature, List<CLeaf> arguments,
        bool reentrant = false, int preSpillStmts = 0)
        => EmitExternVoid(RequireExtern(signature), arguments,
            reentrant, preSpillStmts);

    public void EmitExternVoid(BoundExtern bound, List<CLeaf> arguments,
        bool reentrant = false, int preSpillStmts = 0)
        => EmitCall(new CExternCall(
            bound, arguments, StorageTypes.Void, null,
            reentrant, preSpillStmts));

    public void EmitInternalVoid(string functionName, List<CLeaf> arguments,
        bool reentrant = false)
        => EmitCall(new CInternalCall(
            functionName, arguments, StorageTypes.Void, null, reentrant));

    public void PrependInternalVoid(
        FlatFunction function, string functionName)
    {
        if (function == null) throw new ArgumentNullException(nameof(function));
        var entry = function.Entry
            ?? throw new InvalidOperationException(
                $"Function '{function.Name}' has no CFG entry block.");
        entry.Instructions.Insert(0, new CExprStmt(new CInternalCall(
            functionName, new List<CLeaf>(), StorageTypes.Void)));
    }

    public void Complete()
    {
        foreach (var function in _module.Functions)
        {
            if (!_states.TryGetValue(function, out var state))
            {
                state = new FunctionState(function);
                _states.Add(function, state);
            }

            foreach (var target in state.ReferencedLabels)
                if (!state.DefinedLabels.Contains(target))
                    throw new VerificationException(
                        $"goto targets undefined label '{target}' "
                        + $"(function '{function.Name}')");

            if (state.Current.Terminator == null)
                state.Current.Terminator = new CRet();
            foreach (var block in function.Blocks)
                if (block.Terminator == null)
                    block.Terminator = new CRet();
        }
    }

    void EmitSelect(CLeaf condition, CLeaf trueValue,
        CLeaf falseValue, StorageType type, int destination)
    {
        RequireBoolean(condition, "select condition");
        RequireLeaf(trueValue, "select true value");
        RequireLeaf(falseValue, "select false value");
        var destinationType = CurrentFunction.Slots[destination].Type;
        AssertType(destinationType, type, "select result");
        AssertType(destinationType, trueValue.Type,
            $"CAssign to slot{destination} from select true arm");
        AssertType(destinationType, falseValue.Type,
            $"CAssign to slot{destination} from select false arm");
        if (State.Current.Terminator != null) return;

        var function = CurrentFunction;
        var state = State;
        var trueBlock = function.NewBlock();
        var falseBlock = function.NewBlock();
        var mergeBlock = function.NewBlock();
        state.Current.Terminator = new CBranch(
            condition, trueBlock.Id, falseBlock.Id);

        state.Current = trueBlock;
        AddInstruction(new CAssign(destination, trueValue));
        state.Current.Terminator = new CJump(mergeBlock.Id);

        state.Current = falseBlock;
        AddInstruction(new CAssign(destination, falseValue));
        state.Current.Terminator = new CJump(mergeBlock.Id);
        state.Current = mergeBlock;
    }

    void EmitCrossCall(CLeaf instance, CrossCallTransportPlan transport,
        bool reentrant, int? destination)
    {
        ValidateCrossCall(instance, transport);
        foreach (var parameter in transport.Parameters)
            EmitCall(new CExternCall(
                RequireExtern(ExternResolver.EventReceiverSetProgramVariable),
                new List<CLeaf>
                {
                    instance,
                    Const(parameter.Id, StorageTypes.String),
                    parameter.Value,
                },
                StorageTypes.Void));

        EmitCall(new CExternCall(
            RequireExtern(ExternResolver.EventReceiverSendCustomEvent),
            new List<CLeaf> { instance, transport.EventName },
            StorageTypes.Void, null, reentrant,
            reentrant ? transport.Parameters.Count : 0));

        if (transport.Returns.Count == 1)
        {
            var result = transport.Returns[0];
            if (!destination.HasValue)
                destination = AllocScratch(transport.ResultType);
            EmitCall(new CExternCall(
                RequireExtern(ExternResolver.EventReceiverGetProgramVariable),
                new List<CLeaf>
                {
                    instance,
                    Const(result.Id, StorageTypes.String),
                },
                transport.ResultType, destination));
            return;
        }

        foreach (var result in transport.Returns)
        {
            var slot = AllocScratch(result.StorageType);
            EmitCall(new CExternCall(
                RequireExtern(ExternResolver.EventReceiverGetProgramVariable),
                new List<CLeaf>
                {
                    instance,
                    Const(result.Id, StorageTypes.String),
                },
                result.StorageType, slot));
        }
    }

    void ValidateCrossCall(CLeaf instance, CrossCallTransportPlan transport)
    {
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        ValidateProgramReceiver(instance, "cross-call receiver");
        RequireLeaf(transport.EventName, "cross-call event name");
        AssertType(StorageTypes.String, transport.EventName.Type,
            "cross-call event name");
        if (transport.EventName is CConst { Value: string eventName }
            && string.IsNullOrEmpty(eventName))
            throw new VerificationException(
                $"Cross-call has an empty event name "
                + $"(function '{CurrentFunction.Name}')");

        var parameterIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < transport.Parameters.Count; i++)
        {
            var parameter = transport.Parameters[i];
            if (parameter == null || parameter.Ordinal != i
                || string.IsNullOrEmpty(parameter.Id)
                || !parameterIds.Add(parameter.Id))
                throw new VerificationException(
                    $"Cross-call parameter {i} has a non-canonical ordinal "
                    + $"or empty/duplicate id (function '{CurrentFunction.Name}')");
            RequireLeaf(parameter.Value, $"cross-call parameter {i}");
            AssertType(parameter.StorageType, parameter.Value.Type,
                $"cross-call parameter {i} ('{parameter.Id}')");
        }

        var returnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in transport.Returns)
            if (result == null || string.IsNullOrEmpty(result.Id)
                || !returnIds.Add(result.Id))
                throw new VerificationException(
                    $"Cross-call has an empty or duplicate return id "
                    + $"(function '{CurrentFunction.Name}')");

        if (transport.Returns.Count == 1)
            AssertType(transport.Returns[0].StorageType, transport.ResultType,
                "cross-call result");
        else
            AssertType(StorageTypes.Void, transport.ResultType,
                transport.Returns.Count == 0
                    ? "cross-call void result"
                    : "cross-call tuple result");
    }

    void EmitCall(CExternCall call)
    {
        foreach (var argument in call.Args)
            RequireLeaf(argument, "extern-call argument", allowAddress: true);
        UdonAbiVerifier.VerifyInvocation(
            call, _module.TypeFacts, CurrentFunction.Name);
        ValidateCallDestination(call.Type, call.DestSlot,
            $"extern '{call.Sig}'");
        if (AddInstruction(new CExprStmt(call)) && call.Reentrant)
            CurrentFunction.ReentrantSiteCount++;
    }

    void EmitCall(CInternalCall call)
    {
        foreach (var argument in call.Args)
            RequireLeaf(argument, "internal-call argument", allowAddress: true);
        ValidateCallDestination(call.Type, call.DestSlot,
            $"internal call '{call.FuncName}'");
        if (AddInstruction(new CExprStmt(call)) && call.Reentrant)
            CurrentFunction.ReentrantSiteCount++;
    }

    void ValidateCallDestination(StorageType returnType, int? destination,
        string context)
    {
        if (returnType == StorageTypes.Void)
        {
            if (destination.HasValue)
                throw new VerificationException(
                    $"{context} is void but writes slot{destination.Value} "
                    + $"(function '{CurrentFunction.Name}')");
            return;
        }
        if (!destination.HasValue)
            throw new VerificationException(
                $"{context} returns '{returnType}' without a destination "
                + $"(function '{CurrentFunction.Name}')");
        RequireSlot(destination.Value, context);
        AssertType(CurrentFunction.Slots[destination.Value].Type,
            returnType, $"{context} destination");
    }

    bool AddInstruction(IFlatInstruction instruction)
    {
        if (State.Current.Terminator != null) return false;
        State.Current.Instructions.Add(instruction);
        return true;
    }

    void Terminate(CTerminator terminator)
    {
        if (State.Current.Terminator == null)
            State.Current.Terminator = terminator;
    }

    void RunDetached(Action<CoreBuilder> action, bool loop = false)
    {
        if (action == null) return;
        var owner = CurrentFunction;
        var state = State;
        var saved = state.Current;
        var sink = new FlatBlock(-1) { Terminator = new CRet() };
        state.Current = sink;
        if (loop)
            state.Loops.Push((sink, sink));
        try
        {
            action(this);
            RequireOwner(owner, "detached CFG");
        }
        finally
        {
            if (loop) state.Loops.Pop();
            state.Current = saved;
        }
    }

    void RequireOwner(FlatFunction owner, string context)
    {
        if (!ReferenceEquals(CurrentFunction, owner))
            throw new InvalidOperationException(
                $"{context} left CFG emission in function "
                + $"'{CurrentFunction?.Name ?? "<none>"}' instead of '{owner.Name}'.");
    }

    FlatBlock RequireLabelBlock(FunctionState state, string label)
    {
        if (state.Labels.TryGetValue(label, out var block))
            return block;
        block = state.Function.NewBlock();
        block.Hint = $"__goto_{state.Function.Name}_{label}";
        state.Labels.Add(label, block);
        return block;
    }

    BoundExtern RequireExtern(UdonAbiKey signature)
        => _module.RequireAbi().RequireExact(signature);

    void RequireSlot(int slotId, string context)
    {
        if (slotId < 0 || slotId >= CurrentFunction.Slots.Count)
            throw new VerificationException(
                $"Undeclared slot{slotId} in {context} "
                + $"(function '{CurrentFunction.Name}')");
    }

    void RequireLeaf(CLeaf leaf, string context, bool allowAddress = false)
    {
        if (leaf == null)
            throw new VerificationException(
                $"{context} is null (function '{CurrentFunction.Name}')");
        switch (leaf)
        {
            case CSlotRef slot:
                RequireSlot(slot.SlotId, context);
                AssertType(CurrentFunction.Slots[slot.SlotId].Type,
                    slot.Type, $"{context} slot{slot.SlotId}");
                break;
            case CFieldAddr field:
                AssertField(field.FieldName, field.Type, context);
                if (!allowAddress)
                    throw new VerificationException(
                        $"{context}: field address '{field.FieldName}' is only valid "
                        + $"as a call argument (function '{CurrentFunction.Name}')");
                break;
            case CConst:
            case CFuncRef:
                break;
            default:
                throw new VerificationException(
                    $"{context} is not a CFG leaf: {leaf.GetType().Name} "
                    + $"(function '{CurrentFunction.Name}')");
        }
    }

    void RequireBoolean(CLeaf condition, string context)
    {
        RequireLeaf(condition, context);
        AssertType(StorageTypes.Boolean, condition.Type, context);
    }

    void ValidateProgramReceiver(CLeaf instance, string context)
    {
        RequireLeaf(instance, context);
        if (instance.Type != StorageTypes.UdonEventReceiver
            && instance.Type != StorageTypes.UdonBehaviour
            && instance.Type != StorageTypes.Component
            && instance.Type != StorageTypes.Object)
            throw new VerificationException(
                $"{context} has non-program type '{instance.Type}' "
                + $"(function '{CurrentFunction.Name}')");
    }

    void AssertField(string fieldName, StorageType actualType, string context)
    {
        foreach (var field in _module.Fields)
            if (string.Equals(field.Name, fieldName, StringComparison.Ordinal))
            {
                AssertType(field.Type, actualType,
                    $"{context} field '{fieldName}'");
                return;
            }
        foreach (var function in _module.Functions)
            foreach (var result in function.ReturnSlots)
                if (string.Equals(result.Id, fieldName, StringComparison.Ordinal))
                {
                    AssertType(result.StorageType, actualType,
                        $"{context} field '{fieldName}'");
                    return;
                }
        throw new VerificationException(
            $"Undeclared field '{fieldName}' in {context} "
            + $"(function '{CurrentFunction.Name}')");
    }

    void AssertType(StorageType expected, StorageType actual, string context)
    {
        var why = RawCopyCompatibility.WhyIncompatible(
            expected.Name, actual.Name, _module.TypeFacts);
        if (why != null)
            throw new VerificationException(
                $"Type mismatch in {context}: expected '{expected}', got '{actual}' "
                + $"- {why} (function '{CurrentFunction.Name}')");
    }
}
