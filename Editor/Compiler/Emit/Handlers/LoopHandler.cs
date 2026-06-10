using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class LoopHandler : HandlerBase, IOperationHandler
{
    public LoopHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation operation)
        => operation is IWhileLoopOperation
            or IForLoopOperation
            or IForEachLoopOperation;

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
            // while (cond) { body }
            _builder.EmitWhile(() => VisitExpression(op.Condition), _ =>
            {
                _ctx.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _ctx.LoopUsingDepthStack.Push(_usingDisposableStack.Count);
                    VisitOperation(op.Body);
                    _ctx.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _ctx.SwitchBreakLabels.Pop();
                }
            });
        }
        else
        {
            // do { body } while (cond)
            _builder.EmitWhile(() => VisitExpression(op.Condition), _ =>
            {
                _ctx.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _ctx.LoopUsingDepthStack.Push(_usingDisposableStack.Count);
                    VisitOperation(op.Body);
                    _ctx.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _ctx.SwitchBreakLabels.Pop();
                }
            }, isDoWhile: true);
        }
    }

    void VisitForLoop(IForLoopOperation op)
    {
        _builder.EmitFor(
            _ =>
            {
                // Init: variable declarations register locals in _localBindings
                foreach (var init in op.Before)
                    VisitOperation(init);
            },
            // Lazy condition: evaluated AFTER init so loop vars (e.g. 'i') are registered
            () => op.Condition != null ? VisitExpression(op.Condition) : null,
            _ =>
            {
                // Update
                foreach (var atBottom in op.AtLoopBottom)
                    VisitOperation(atBottom);
            },
            _ =>
            {
                // Body
                _ctx.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _ctx.LoopUsingDepthStack.Push(_usingDisposableStack.Count);
                    VisitOperation(op.Body);
                    _ctx.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _ctx.SwitchBreakLabels.Pop();
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
        var elemType = GetUdonType(arrayTypeSymbol.ElementType);
        var arrayType = GetArrayType(arrayTypeSymbol);
        var elemAccessorType = GetArrayElemType(arrayTypeSymbol);

        // collVal is a single-assignment scratch leaf under ANF — the collection reference is loop-invariant
        // and stable, so it can be re-read directly in the length read and the body without a snapshot slot.
        var collVal = VisitExpression(collectionOp);

        // Declare loop variable
        var loopLocal = op.Locals.FirstOrDefault()
            ?? throw new System.InvalidOperationException("foreach has no loop variable");
        var loopVarId = _ctx.DeclareLocal(loopLocal.Name, elemType);
        _localBindings[loopLocal] = new EmitContext.LocalBinding(loopVarId);
        // [Q4] the iteration variable is READONLY in C# — struct method receivers rooted at it get
        // a defensive clone (EmitStructInstanceCall), matching the CLR's foreach-mutation no-op.
        _ctx.ForeachIterationLocals.Add(loopLocal);

        // Index variable
        var idxSlot = _ctx.AllocTemp("SystemInt32");

        // Cache array length before the loop
        var lenSlot = _ctx.AllocTemp("SystemInt32");
        EmitAssign(lenSlot, ExternCall("SystemArray.__get_Length__SystemInt32",
            new List<CLeaf> { collVal }, "SystemInt32"));

        _builder.EmitFor(
            _ =>
            {
                // Init: idx = 0
                EmitAssign(idxSlot, Const(0, "SystemInt32"));
            },
            // Condition: idx < cachedLen — built via a factory so A-normal form re-materializes it inside the
            // loop's cond block (re-evaluated each iteration), not bound once before the loop.
            () => ExternCall(
                "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<CLeaf> { SlotRef(idxSlot), SlotRef(lenSlot) },
                "SystemBoolean"),
            _ =>
            {
                // Update: idx++
                var nextIdx = ExternCall(
                    "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<CLeaf> { SlotRef(idxSlot), Const(1, "SystemInt32") },
                    "SystemInt32");
                EmitAssign(idxSlot, nextIdx);
            },
            _ =>
            {
                // Body: loopVar = arr[idx]; <body>
                CLeaf elemVal = ExternCall(
                    $"{arrayType}.__Get__SystemInt32__{elemAccessorType}",
                    new List<CLeaf> { collVal, SlotRef(idxSlot) },
                    elemType);
                // foreach yields a by-value COPY of the element. For an aggregate (struct/tuple) element the
                // raw __Get__ returns the LIVE backing object[]; deep-clone it so mutating the loop variable
                // does not write through to the array (C# value-copy semantics; mirrors VisitArrayElementReference).
                if (arrayTypeSymbol.ElementType is INamedTypeSymbol elemAgg && EmitContext.IsAggregateType(elemAgg))
                    elemVal = EmitDeepCloneAggregate(elemVal, elemAgg);
                EmitStoreField(loopVarId, elemVal);

                _ctx.SwitchBreakLabels.Push(null); // sentinel: loop break should not target switch
                try
                {
                    _ctx.LoopUsingDepthStack.Push(_usingDisposableStack.Count);
                    VisitOperation(op.Body);
                    _ctx.LoopUsingDepthStack.Pop();
                }
                finally
                {
                    _ctx.SwitchBreakLabels.Pop();
                }
            });
    }

}
