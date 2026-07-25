using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class LoopHandler : IOperationHandler
{
    readonly LoweringServices _lowering;
    public LoopHandler(LoweringServices lowering) => _lowering = lowering;

    // While/For/ForEach all carry OperationKind.Loop, distinguished by LoopKind inside Handle().
    public OperationKind[] HandledKinds { get; } = new[] { OperationKind.Loop };

    public void Handle(IOperation operation)
    {
        switch (operation)
        {
            case IWhileLoopOperation op: VisitWhileLoop(op); break;
            case IForLoopOperation op: VisitForLoop(op); break;
            case IForEachLoopOperation op: VisitForEachLoop(op); break;
            default: throw new System.NotSupportedException(operation.GetType().Name);
        }
    }

    void VisitWhileLoop(IWhileLoopOperation op)
    {
        if (op.ConditionIsTop)
        {
            // while (cond) { body }. Stage 2 §3 INV-1/INV-3: the Iteration env is (re)allocated at the
            // top of the condition block (runs each iteration before the condition test AND the body),
            // so a captured out-var/pattern in the condition writes into a live per-iteration env.
            _lowering.Builder.EmitWhile(() =>
            {
                EnvEmit.Alloc(_lowering.Builder, _lowering.State, _lowering.State.Closures.CaptureScope?.ScopeFor(op, CaptureScopeKind.Iteration));
                return _lowering.VisitExpression(op.Condition);
            }, _ =>
            {
                _lowering.State.ControlFlow.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Push(_lowering.UsingDisposableStack.Count);
                    _lowering.VisitOperation(op.Body);
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _lowering.State.ControlFlow.SwitchBreakLabels.Pop();
                }
            });
        }
        else
        {
            // do { body } while (cond). Stage 2 §3 INV-1: body top is correct here — the do-while
            // condition runs AFTER the body, so a fresh env at body top covers both the body's
            // captures and any closure the (later) condition creates over an Iteration-scoped var.
            _lowering.Builder.EmitWhile(() => _lowering.VisitExpression(op.Condition), _ =>
            {
                EnvEmit.Alloc(_lowering.Builder, _lowering.State, _lowering.State.Closures.CaptureScope?.ScopeFor(op, CaptureScopeKind.Iteration));
                _lowering.State.ControlFlow.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Push(_lowering.UsingDisposableStack.Count);
                    _lowering.VisitOperation(op.Body);
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _lowering.State.ControlFlow.SwitchBreakLabels.Pop();
                }
            }, isDoWhile: true);
        }
    }

    void VisitForLoop(IForLoopOperation op)
    {
        _lowering.Builder.EmitFor(
            _ =>
            {
                // Stage 2 §3: the ForInit env is allocated ONCE (init runs once), before the
                // init-clause declarations write their captured loop vars into it. A captured
                // `for (int i…)` variable therefore shares ONE cell across all iterations — the
                // classic C# for-loop capture semantics (every lambda sees the final i).
                EnvEmit.Alloc(_lowering.Builder, _lowering.State, _lowering.State.Closures.CaptureScope?.ScopeFor(op, CaptureScopeKind.ForInit));
                // Init: variable declarations register locals in _localBindings
                foreach (var init in op.Before)
                    _lowering.VisitOperation(init);
            },
            // Lazy condition: evaluated AFTER init so loop vars (e.g. 'i') are registered. Stage 2 §3
            // INV-1/INV-3: the Iteration env is (re)allocated at the TOP of the condition block, which
            // re-runs each iteration BEFORE both the condition test and the body — so a captured
            // out-var/pattern declared in the condition writes into a live per-iteration env, and a
            // body-declared captured local still gets its per-iteration freshness.
            () =>
            {
                EnvEmit.Alloc(_lowering.Builder, _lowering.State, _lowering.State.Closures.CaptureScope?.ScopeFor(op, CaptureScopeKind.Iteration));
                return op.Condition != null ? _lowering.VisitExpression(op.Condition) : null;
            },
            _ =>
            {
                // Update
                foreach (var atBottom in op.AtLoopBottom)
                    _lowering.VisitOperation(atBottom);
            },
            _ =>
            {
                // Body
                _lowering.State.ControlFlow.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Push(_lowering.UsingDisposableStack.Count);
                    _lowering.VisitOperation(op.Body);
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _lowering.State.ControlFlow.SwitchBreakLabels.Pop();
                }
            });
    }

    void VisitForEachLoop(IForEachLoopOperation op)
    {
        // Collection is wrapped in IConversionOperation (array → IEnumerable), unwrap it
        var collectionOp = op.Collection is IConversionOperation conv ? conv.Operand : op.Collection;

        if (collectionOp.Type is not IArrayTypeSymbol)
            throw new System.NotSupportedException(
                $"foreach over '{collectionOp.Type?.ToDisplayString() ?? "unknown"}' is not supported. Only arrays are supported.");

        var arrayTypeSymbol = (IArrayTypeSymbol)collectionOp.Type;
        var elemType = _lowering.GetStorageTypeName(arrayTypeSymbol.ElementType);

        // N-dim array (design 2026-07-04 §2): foreach visits the FLAT BACKING, not the bundle wrapper
        // — C# row-major order over all elements falls out of iterating the backing in flat-index
        // order (the backing's own construction already wrote elements row-major). Unwrap bundle[0]
        // ONCE here; everything below (cached length, index loop, Get) is then IDENTICAL to the rank-1
        // path, just against the backing array's own type instead of the bundle's SystemObjectArray tag.
        bool isNdim = NdimArrayAbi.IsNdimArray(arrayTypeSymbol);
        var backingTypeSymbol = isNdim ? NdimArrayAbi.BackingType(_lowering.Compilation, arrayTypeSymbol) : arrayTypeSymbol;
        var arrayType = _lowering.GetArrayType(backingTypeSymbol);
        var elemAccessorType = _lowering.GetArrayElemType(backingTypeSymbol);

        // collVal is a single-assignment scratch leaf under ANF — the collection reference is loop-invariant
        // and stable, so it can be re-read directly in the length read and the body without a snapshot slot.
        var collVal = _lowering.VisitExpression(collectionOp);
        if (isNdim) collVal = _lowering.EmitNdimGetBacking(collVal, backingTypeSymbol);

        // Declare loop variable
        var loopLocal = op.Locals.FirstOrDefault()
            ?? throw new System.InvalidOperationException("foreach has no loop variable");
        // Stage 2 §3: a CAPTURED foreach control var has no flat slot — it is owned by the Iteration
        // scope (fresh per iteration in C#), so its element value is written into the per-iteration
        // env cell at body top (below), not a flat field. Skip the flat bind entirely for it.
        bool loopVarCaptured = _lowering.State.Closures.TryGetEnvBinding(loopLocal, out _);
        string loopVarId = null;
        if (!loopVarCaptured)
        {
            loopVarId = _lowering.State.Storage.DeclareLocal(loopLocal.Name, new StorageType(elemType));
            _lowering.LocalBindings[loopLocal] = new LocalBinding(loopVarId);
        }
        // [Q4]/[R1] the iteration variable is READONLY in C# — struct method receivers reaching it
        // through a value-typed FIELD link get a defensive clone (EmitStructInstanceCall); a DIRECT
        // method call on the loop local mutates it (Roslyn ldloca — readonly only forbids assignment).
        _lowering.State.ForeachIterationLocals.Add(loopLocal);

        // Index variable
        var idxSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);

        // Cache array length before the loop
        var lenSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
        _lowering.EmitAssign(lenSlot, _lowering.ExternCall(UdonAbi.SystemArrayLength,
            new List<CLeaf> { collVal }, StorageTypes.Int32));

        _lowering.Builder.EmitFor(
            _ =>
            {
                // Init: idx = 0
                _lowering.EmitAssign(idxSlot, _lowering.Const(0, StorageTypes.Int32));
            },
            // Condition: idx < cachedLen — built via a factory so A-normal form re-materializes it inside the
            // loop's cond block (re-evaluated each iteration), not bound once before the loop.
            () => _lowering.ExternCall(
                UdonAbi.Int32LessThan,
                new List<CLeaf> { _lowering.SlotRef(idxSlot), _lowering.SlotRef(lenSlot) },
                StorageTypes.Boolean),
            _ =>
            {
                // Update: idx++
                var nextIdx = _lowering.ExternCall(
                    UdonAbi.Int32Add,
                    new List<CLeaf> { _lowering.SlotRef(idxSlot), _lowering.Const(1, StorageTypes.Int32) },
                    StorageTypes.Int32);
                _lowering.EmitAssign(idxSlot, nextIdx);
            },
            _ =>
            {
                // Stage 2 §3 INV-1: fresh per-iteration Iteration env at body top, BEFORE the control
                // var's element value is written into its (now-live) env cell.
                EnvEmit.Alloc(_lowering.Builder, _lowering.State, _lowering.State.Closures.CaptureScope?.ScopeFor(op, CaptureScopeKind.Iteration));
                // Body: loopVar = arr[idx]; <body>
                CLeaf elemVal = _lowering.ExternCall(
                    UdonAbi.ArrayGet(arrayType, elemAccessorType),
                    new List<CLeaf> { collVal, _lowering.SlotRef(idxSlot) },
                    new StorageType(elemType));
                // foreach yields a by-value COPY of the element. For an aggregate (struct/tuple) element the
                // raw __Get__ returns the LIVE backing object[]; deep-clone it so mutating the loop variable
                // does not write through to the array (C# value-copy semantics; mirrors VisitArrayElementReference).
                if (arrayTypeSymbol.ElementType is INamedTypeSymbol elemAgg && TypeClassifier.IsAggregateValue(elemAgg))
                    elemVal = AggregateAbi.DeepClone(_lowering.Builder, elemVal, elemAgg, _lowering.State.Aggregates.GetLayout);
                if (loopVarCaptured)
                    EnvEmit.Write(_lowering.Builder, _lowering.State, loopLocal, elemVal);
                else
                    _lowering.EmitStoreField(loopVarId, elemVal);

                _lowering.State.ControlFlow.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Push(_lowering.UsingDisposableStack.Count);
                    _lowering.VisitOperation(op.Body);
                    _lowering.State.ControlFlow.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _lowering.State.ControlFlow.SwitchBreakLabels.Pop();
                }
            });
    }

}
