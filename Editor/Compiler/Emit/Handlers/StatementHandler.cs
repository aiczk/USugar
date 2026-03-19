using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class StatementHandler : HandlerBase, IOperationHandler
{
    public StatementHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation operation)
        => operation is IBlockOperation
            or IExpressionStatementOperation
            or IVariableDeclarationGroupOperation
            or IConditionalOperation
            or IReturnOperation
            or IBranchOperation
            or ILabeledOperation
            or ILocalFunctionOperation
            or IUsingOperation
            or IUsingDeclarationOperation;

    public void Handle(IOperation operation)
    {
        switch (operation)
        {
            case IBlockOperation op: HandleBlock(op); break;
            case IExpressionStatementOperation exprStmt
                when exprStmt.Operation is IDeconstructionAssignmentOperation deconExpr:
                _ctx.VisitOperation(deconExpr);
                break;
            case IExpressionStatementOperation exprStmt:
            {
                var innerOp = exprStmt.Operation;
                // Assignment/increment handlers already emit their side effects during VisitExpression.
                // Only emit as ExprStmt if the expression is purely for its side effects (method call, etc.)
                if (innerOp is ISimpleAssignmentOperation
                    or ICompoundAssignmentOperation
                    or IIncrementOrDecrementOperation
                    or ICoalesceAssignmentOperation
                    or IDeconstructionAssignmentOperation)
                {
                    VisitExpression(innerOp);
                }
                else
                {
                    var expr = VisitExpression(innerOp);
                    if (expr != null)
                        EmitExprStmt(expr);
                }
                break;
            }
            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                    VisitVariableDeclaration(decl);
                break;
            case IConditionalOperation op: VisitConditional(op); break;
            case IReturnOperation op: VisitReturn(op); break;
            case IBranchOperation op: VisitBranch(op); break;
            case ILocalFunctionOperation op: RegisterLocalFunction(op.Symbol); break;
            case ILabeledOperation labeled:
                _builder.EmitLabel(labeled.Label.Name);
                if (labeled.Operation != null)
                    VisitOperation(labeled.Operation);
                break;
            case IUsingOperation op: VisitUsing(op); break;
            case IUsingDeclarationOperation usingDecl:
                foreach (var decl in usingDecl.DeclarationGroup.Declarations)
                {
                    VisitVariableDeclaration(decl);
                    foreach (var declarator in decl.Declarators)
                    {
                        var localId = _localBindings.TryGetValue(declarator.Symbol, out var ub) && !ub.IsTuple ? ub.ScalarId : declarator.Symbol.Name;
                        var localType = GetUdonType(declarator.Symbol.Type);
                        _usingDisposableStack.Peek().Add((LoadField(localId, localType), declarator.Symbol.Type));
                    }
                }
                break;
            default: throw new System.NotSupportedException(operation.GetType().Name);
        }
    }

    void HandleBlock(IBlockOperation block)
    {
        _usingDisposableStack.Push(new List<(HExpr, ITypeSymbol)>());
        foreach (var stmt in block.Operations)
            VisitOperation(stmt);
        var disposables = _usingDisposableStack.Pop();
        for (int i = disposables.Count - 1; i >= 0; i--)
        {
            var (val, type) = disposables[i];
            EmitExternVoid($"{GetUdonType(type)}.__Dispose__SystemVoid", new List<HExpr> { val });
        }
    }

    void VisitConditional(IConditionalOperation op)
    {
        // Optimization: if (!cond) → invert branches to avoid negation extern
        if (op.Condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary)
        {
            var condVal = VisitExpression(unary.Operand);

            if (op.WhenFalse != null)
            {
                // if (!c) A else B → if (c) B else A
                _builder.EmitIf(condVal,
                    _ => VisitOperation(op.WhenFalse),
                    _ => VisitOperation(op.WhenTrue));
            }
            else
            {
                // if (!c) A → if (c) {} else A
                _builder.EmitIf(condVal,
                    _ => { },
                    _ => VisitOperation(op.WhenTrue));
            }
            return;
        }

        var condVal2 = VisitExpression(op.Condition);

        if (op.WhenFalse != null)
        {
            _builder.EmitIf(condVal2,
                _ => VisitOperation(op.WhenTrue),
                _ => VisitOperation(op.WhenFalse));
        }
        else
        {
            _builder.EmitIf(condVal2,
                _ => VisitOperation(op.WhenTrue));
        }
    }

    void VisitReturn(IReturnOperation op)
    {
        // Tail call optimization: return self(args) → overwrite params + goto entry
        if (op.ReturnedValue is IInvocationOperation tailCall
            && _currentMethod != null
            && SymbolEqualityComparer.Default.Equals(tailCall.TargetMethod, _currentMethod))
        {
            EmitTailCall(tailCall);
            return;
        }

        if (op.ReturnedValue != null && _currentMethod != null
            && _methodReturns.TryGetValue(_currentMethod, out var retSlots) && retSlots.Length > 0)
        {
            if (retSlots.Length > 1)
            {
                // Multi-return (tuple): store each element in its return slot
                var tupleOp = UnwrapConversions(op.ReturnedValue);
                if (tupleOp is ITupleOperation tuple)
                {
                    for (int i = 0; i < tuple.Elements.Length && i < retSlots.Length; i++)
                    {
                        var elemVal = VisitExpression(tuple.Elements[i]);
                        EmitStoreField(retSlots[i].Id, elemVal);
                    }
                }
                else if (tupleOp is ILocalReferenceOperation localRef
                         && _localBindings.TryGetValue(localRef.Local, out var retBinding) && retBinding.IsTuple
                         && localRef.Type is INamedTypeSymbol retTupleType)
                {
                    var expandedIds = retBinding.TupleIds;
                    var retElements = retTupleType.TupleElements;
                    for (int i = 0; i < expandedIds.Length && i < retSlots.Length; i++)
                        EmitStoreField(retSlots[i].Id, LoadField(expandedIds[i], GetUdonType(retElements[i].Type)));
                }
                else
                {
                    throw new System.NotSupportedException(
                        $"Tuple-returning method must return a tuple literal or tuple local, got {op.ReturnedValue.GetType().Name}");
                }
                EmitPendingDispose();
                EmitReturn();
            }
            else
            {
                // Single return
                var srcVal = VisitExpression(op.ReturnedValue);
                if (_currentMethod.Name == "OnOwnershipRequest")
                {
                    _ctx.TryDeclareVar("__returnValue", "SystemBoolean");
                    EmitStoreField("__returnValue", srcVal);
                }
                EmitPendingDispose();
                EmitReturn(srcVal);
            }
            return;
        }
        else
        {
            EmitPendingDispose();
            EmitReturn();
        }
    }

    void EmitTailCall(IInvocationOperation tailCall)
    {
        var paramIds = _methodParamVarIds[_currentMethod];

        // Evaluate args into HExprs first (avoid overwriting params before they're read)
        var argVals = new HExpr[tailCall.Arguments.Length];
        for (int i = 0; i < tailCall.Arguments.Length; i++)
            argVals[i] = VisitExpression(tailCall.Arguments[i].Value);

        // Overwrite param vars with new values
        for (int i = 0; i < tailCall.Arguments.Length; i++)
            EmitStoreField(paramIds[i], argVals[i]);

        // Jump back to method entry via goto label
        var func = _methodFunctions[_currentMethod];
        _builder.EmitGoto($"__tco_{func.Name}");
    }

    void VisitBranch(IBranchOperation op)
    {
        if (op.BranchKind == BranchKind.Break)
        {
            EmitPendingDisposeForBreakContinue();
            // Switch breaks use goto to end label; loop breaks use structured HBreak
            if (LoopHandler.SwitchBreakLabels.Count > 0 && LoopHandler.SwitchBreakLabels.Peek() != null)
                _builder.EmitGoto(LoopHandler.SwitchBreakLabels.Peek());
            else
                _builder.EmitBreak();
        }
        else if (op.BranchKind == BranchKind.Continue)
        {
            EmitPendingDisposeForBreakContinue();
            _builder.EmitContinue();
        }
        else if (op.BranchKind == BranchKind.GoTo)
        {
            _builder.EmitGoto(op.Target.Name);
        }
        else
        {
            throw new System.InvalidOperationException(
                $"Unresolved branch: {op.BranchKind}"
              + (op.BranchKind == BranchKind.GoTo ? $" to '{op.Target?.Name}'" : "")
              + ". No matching label on the stack.");
        }
    }

    /// <summary>
    /// Emit Dispose() for all active using disposables (innermost scope first).
    /// Called before return to ensure all scopes are cleaned up.
    /// </summary>
    void EmitPendingDispose()
    {
        foreach (var scope in _usingDisposableStack)
        {
            for (int i = scope.Count - 1; i >= 0; i--)
            {
                var (val, type) = scope[i];
                var disposeType = GetUdonType(type);
                EmitExternVoid($"{disposeType}.__Dispose__SystemVoid", new List<HExpr> { val });
            }
        }
    }

    /// <summary>
    /// Emit Dispose() only for using scopes inside the current loop/switch.
    /// Called before break/continue to clean up scopes that will be exited.
    /// </summary>
    void EmitPendingDisposeForBreakContinue()
    {
        var loopDepth = _ctx.LoopUsingDepthStack.Count > 0
            ? _ctx.LoopUsingDepthStack.Peek()
            : 0;
        var currentDepth = _usingDisposableStack.Count;
        var scopesToDispose = currentDepth - loopDepth;
        if (scopesToDispose <= 0) return;

        int count = 0;
        foreach (var scope in _usingDisposableStack)
        {
            if (count >= scopesToDispose) break;
            for (int i = scope.Count - 1; i >= 0; i--)
            {
                var (val, type) = scope[i];
                var disposeType = GetUdonType(type);
                EmitExternVoid($"{disposeType}.__Dispose__SystemVoid", new List<HExpr> { val });
            }
            count++;
        }
    }

    public void PreScanGotoLabels(IOperation op)
    {
        // In HIR, labels are string-based (EmitLabel/EmitGoto).
        // No pre-scan needed — labels are resolved by name at lowering time.
    }

    void VisitUsing(IUsingOperation op)
    {
        // Collect declared locals (for Dispose calls after body)
        var disposableVars = new List<(HExpr val, ITypeSymbol type)>();
        if (op.Resources is IVariableDeclarationGroupOperation declGroup)
        {
            foreach (var decl in declGroup.Declarations)
            {
                VisitVariableDeclaration(decl);
                foreach (var declarator in decl.Declarators)
                {
                    var localId = _localBindings.TryGetValue(declarator.Symbol, out var ub2) && !ub2.IsTuple ? ub2.ScalarId : declarator.Symbol.Name;
                    var localType = GetUdonType(declarator.Symbol.Type);
                    disposableVars.Add((LoadField(localId, localType), declarator.Symbol.Type));
                }
            }
        }
        else if (op.Resources != null)
        {
            var resourceVal = VisitExpression(op.Resources);
            disposableVars.Add((resourceVal, op.Resources.Type));
        }

        // Push onto using stack so early exit (return/break/continue) can emit Dispose
        _usingDisposableStack.Push(disposableVars);

        if (op.Body != null)
            VisitOperation(op.Body);

        _usingDisposableStack.Pop();

        // Emit Dispose() in reverse declaration order (no try/finally in Udon)
        for (int i = disposableVars.Count - 1; i >= 0; i--)
        {
            var (val, type) = disposableVars[i];
            var udonType = GetUdonType(type);
            EmitExternVoid($"{udonType}.__Dispose__SystemVoid", new List<HExpr> { val });
        }
    }

    void VisitVariableDeclaration(IVariableDeclarationOperation decl)
    {
        foreach (var declarator in decl.Declarators)
        {
            var local = declarator.Symbol;

            // Tuple-typed local → SROA: expand into per-element scalar variables
            if (local.Type.IsTupleType && local.Type is INamedTypeSymbol tupleType)
            {
                VisitTupleLocalDeclaration(local, tupleType, declarator.Initializer);
                continue;
            }

            // Delegate-typed locals → SystemUInt32 (holds label address; Udon has no delegate types)
            var udonType = local.Type.TypeKind == TypeKind.Delegate
                ? "SystemUInt32"
                : GetUdonType(local.Type);
            var id = _ctx.DeclareLocal(local.Name, udonType);
            _localBindings[local] = EmitContext.LocalBinding.Scalar(id);

            var init = declarator.Initializer;
            if (init != null)
            {
                // Track delegate variable → hoisted method mapping
                if (init.Value is IDelegateCreationOperation delegateInit
                    && delegateInit.Target is IAnonymousFunctionOperation lambdaInit)
                {
                    var hoisted = HoistLambdaToMethod(lambdaInit);
                    _delegateVarMap[local] = hoisted;
                }

                var srcVal = VisitExpression(init.Value);
                EmitStoreField(id, srcVal);
            }
        }
    }

    void VisitTupleLocalDeclaration(ILocalSymbol local, INamedTypeSymbol tupleType,
        IVariableInitializerOperation init)
    {
        // Register per-element field IDs for this tuple local (synthetic field pattern)
        if (!_localBindings.TryGetValue(local, out var existingTuple) || !existingTuple.IsTuple)
        {
            var elements = tupleType.TupleElements;

            // Check for nested tuples (not supported - Udon VM has no ValueTuple)
            for (int i = 0; i < elements.Length; i++)
            {
                if (elements[i].Type.IsTupleType)
                    throw new System.NotSupportedException(
                        $"Nested tuple type in local variable '{local.Name}' is not supported. " +
                        "Udon VM does not have ValueTuple type support.");
            }

            var ids = new string[elements.Length];
            for (int i = 0; i < elements.Length; i++)
                ids[i] = _ctx.DeclareLocal($"{local.Name}__{i}", GetUdonType(elements[i].Type));
            _localBindings[local] = EmitContext.LocalBinding.Tuple(ids);
        }

        if (init == null) return;
        var ids2 = _localBindings[local].TupleIds;
        var value = UnwrapConversions(init.Value);

        if (value is ITupleOperation tupleLit)
        {
            for (int i = 0; i < tupleLit.Elements.Length && i < ids2.Length; i++)
                EmitStoreField(ids2[i], VisitExpression(tupleLit.Elements[i]));
        }
        else if (value is IInvocationOperation invocation)
        {
            var callExpr = VisitExpression(init.Value);
            if (callExpr != null) EmitExprStmt(callExpr);
            var callReturns = GetCalleeReturns(invocation.TargetMethod);
            for (int i = 0; i < ids2.Length && i < callReturns.Length; i++)
                EmitStoreField(ids2[i], LoadField(callReturns[i].Id, callReturns[i].UdonType));
        }
        else if (value is ILocalReferenceOperation otherLocal
                 && _localBindings.TryGetValue(otherLocal.Local, out var otherBinding) && otherBinding.IsTuple)
        {
            var otherIds = otherBinding.TupleIds;
            var otherType = (INamedTypeSymbol)otherLocal.Type;
            var otherElements = otherType.TupleElements;
            for (int i = 0; i < ids2.Length && i < otherIds.Length; i++)
                EmitStoreField(ids2[i], LoadField(otherIds[i], GetUdonType(otherElements[i].Type)));
        }
        else
        {
            throw new System.NotSupportedException(
                $"Cannot initialize tuple local '{local.Name}' from {value.GetType().Name}.");
        }
    }

}
