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
                        var localId = _localVarIds.TryGetValue(declarator.Symbol, out var lid) ? lid : declarator.Symbol.Name;
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

        if (op.ReturnedValue != null && _currentMethod != null && _methodRetVars.TryGetValue(_currentMethod, out _))
        {
            var srcVal = VisitExpression(op.ReturnedValue);

            // VRChat reads OnOwnershipRequest's return value from __returnValue (bool)
            if (_currentMethod.Name == "OnOwnershipRequest")
            {
                _ctx.TryDeclareVar("__returnValue", "SystemBoolean");
                EmitStoreField("__returnValue", srcVal);
            }

            EmitReturn(srcVal);
        }
        else
        {
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
            // Switch breaks use goto to end label; loop breaks use structured HBreak
            if (LoopHandler.SwitchBreakLabels.Count > 0)
                _builder.EmitGoto(LoopHandler.SwitchBreakLabels.Peek());
            else
                _builder.EmitBreak();
        }
        else if (op.BranchKind == BranchKind.Continue)
        {
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
                    var localId = _localVarIds.TryGetValue(declarator.Symbol, out var id) ? id : declarator.Symbol.Name;
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

        if (op.Body != null)
            VisitOperation(op.Body);

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
            // Delegate-typed locals → SystemUInt32 (holds label address; Udon has no delegate types)
            var udonType = local.Type.TypeKind == TypeKind.Delegate
                ? "SystemUInt32"
                : GetUdonType(local.Type);
            var id = _ctx.DeclareLocal(local.Name, udonType);
            _localVarIds[local] = id;

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

}
