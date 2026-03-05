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
                VisitExpression(exprStmt.Operation);
                break;
            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                    VisitVariableDeclaration(decl);
                break;
            case IConditionalOperation op: VisitConditional(op); break;
            case IReturnOperation op: VisitReturn(op); break;
            case IBranchOperation op: VisitBranch(op); break;
            case ILocalFunctionOperation op: RegisterLocalFunction(op.Symbol); break;
            case ILabeledOperation labeled:
                if (_gotoLabels.TryGetValue(labeled.Label, out var gotoLbl))
                {
                    _module.AddLabel(gotoLbl);
                    _module.MarkLabel(gotoLbl);
                }
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
                        _usingDisposableStack.Peek().Add((localId, declarator.Symbol.Type));
                    }
                }
                break;
            default: throw new System.NotSupportedException(operation.GetType().Name);
        }
    }

    void HandleBlock(IBlockOperation block)
    {
        _usingDisposableStack.Push(new List<(string, ITypeSymbol)>());
        foreach (var stmt in block.Operations)
            VisitOperation(stmt);
        var disposables = _usingDisposableStack.Pop();
        for (int i = disposables.Count - 1; i >= 0; i--)
        {
            var (varId, type) = disposables[i];
            _module.AddPush(varId);
            AddExternChecked($"{GetUdonType(type)}.__Dispose__SystemVoid");
        }
    }

    void VisitConditional(IConditionalOperation op)
    {
        // Optimization: if (!cond) → skip negation extern, invert branch
        if (op.Condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary)
        {
            var condId = VisitExpression(unary.Operand);
            var endLabel = _module.DefineLabel("__if_end");
            _module.AddPush(condId);

            if (op.WhenFalse != null)
            {
                // if (!c) A else B → push(c), JumpIfFalse → A branch, fall through → B branch
                var thenLabel = _module.DefineLabel("__if_neg_then");
                _module.AddJumpIfFalse(thenLabel);
                VisitOperation(op.WhenFalse);
                _module.AddJump(endLabel);
                _module.MarkLabel(thenLabel);
                VisitOperation(op.WhenTrue);
            }
            else
            {
                // if (!c) A → push(c), JumpIfFalse(body), JUMP(end), body: A
                // c=true → doesn't jump → JUMP(end) skips body. c=false → jumps to body.
                var bodyLabel = _module.DefineLabel("__if_neg_body");
                _module.AddJumpIfFalse(bodyLabel);
                _module.AddJump(endLabel);
                _module.MarkLabel(bodyLabel);
                VisitOperation(op.WhenTrue);
            }
            _module.MarkLabel(endLabel);
            return;
        }

        var condId2 = VisitExpression(op.Condition);
        var endLabel2 = _module.DefineLabel("__if_end");

        _module.AddPush(condId2);

        if (op.WhenFalse != null)
        {
            var elseLabel = _module.DefineLabel("__if_else");
            _module.AddJumpIfFalse(elseLabel);
            VisitOperation(op.WhenTrue);
            _module.AddJump(endLabel2);
            _module.MarkLabel(elseLabel);
            VisitOperation(op.WhenFalse);
            _module.MarkLabel(endLabel2);
        }
        else
        {
            _module.AddJumpIfFalse(endLabel2);
            VisitOperation(op.WhenTrue);
            _module.MarkLabel(endLabel2);
        }
    }

    void VisitReturn(IReturnOperation op)
    {
        // Tail call optimization: return self(args) → overwrite params + JUMP
        if (op.ReturnedValue is IInvocationOperation tailCall
            && _currentMethod != null
            && SymbolEqualityComparer.Default.Equals(tailCall.TargetMethod, _currentMethod))
        {
            EmitTailCall(tailCall);
            return;
        }

        if (op.ReturnedValue != null && _currentMethod != null && _methodRetVars.TryGetValue(_currentMethod, out var retVarId))
        {
            _ctx.TargetHint = retVarId;
            var srcId = VisitExpression(op.ReturnedValue);
            _ctx.TargetHint = null;
            if (srcId != retVarId)
                _module.AddCopy(srcId, retVarId);

            // VRChat reads OnOwnershipRequest's return value from __returnValue (bool)
            if (_currentMethod.Name == "OnOwnershipRequest")
            {
                _vars.TryDeclareVar("__returnValue", "SystemBoolean");
                _module.AddCopy(retVarId, "__returnValue");
            }
        }
        _module.AddReturn("__intnl_returnJump_SystemUInt32_0");
    }

    void EmitTailCall(IInvocationOperation tailCall)
    {
        var paramIds = _methodParamVarIds[_currentMethod];

        // Evaluate args into temps first (avoid overwriting params before they're read)
        var argTemps = new string[tailCall.Arguments.Length];
        for (int i = 0; i < tailCall.Arguments.Length; i++)
            argTemps[i] = VisitExpression(tailCall.Arguments[i].Value);

        // Overwrite param vars with new values
        for (int i = 0; i < tailCall.Arguments.Length; i++)
            _module.AddCopy(argTemps[i], paramIds[i]);

        // Jump back to method body (skip re-entrance preamble)
        int jumpTarget = _methodBodyLabels.TryGetValue(_currentMethod, out var bodyLabel)
            ? bodyLabel : _methodLabels[_currentMethod];
        _module.AddJump(jumpTarget);
    }

    void VisitBranch(IBranchOperation op)
    {
        if (op.BranchKind == BranchKind.Break && _breakLabels.Count > 0)
            _module.AddJump(_breakLabels.Peek());
        else if (op.BranchKind == BranchKind.Continue && _continueLabels.Count > 0)
            _module.AddJump(_continueLabels.Peek());
        else if (op.BranchKind == BranchKind.GoTo && _gotoLabels.TryGetValue(op.Target, out var targetLbl))
            _module.AddJump(targetLbl);
        else
            throw new System.InvalidOperationException(
                $"Unresolved branch: {op.BranchKind}"
              + (op.BranchKind == BranchKind.GoTo ? $" to '{op.Target?.Name}'" : "")
              + ". No matching label on the stack.");
    }

    public void PreScanGotoLabels(IOperation op)
    {
        if (op == null) return;
        if (op is ILabeledOperation labeled)
        {
            var label = _module.DefineLabel($"__goto_{labeled.Label.Name}");
            _gotoLabels[labeled.Label] = label;
        }
        foreach (var child in op.Children)
            PreScanGotoLabels(child);
    }

    void VisitUsing(IUsingOperation op)
    {
        // Collect declared locals (for Dispose calls after body)
        var disposableVars = new List<(string varId, ITypeSymbol type)>();
        if (op.Resources is IVariableDeclarationGroupOperation declGroup)
        {
            foreach (var decl in declGroup.Declarations)
            {
                VisitVariableDeclaration(decl);
                foreach (var declarator in decl.Declarators)
                {
                    var localId = _localVarIds.TryGetValue(declarator.Symbol, out var id) ? id : declarator.Symbol.Name;
                    disposableVars.Add((localId, declarator.Symbol.Type));
                }
            }
        }
        else if (op.Resources != null)
        {
            var resourceId = VisitExpression(op.Resources);
            disposableVars.Add((resourceId, op.Resources.Type));
        }

        if (op.Body != null)
            VisitOperation(op.Body);

        // Emit Dispose() in reverse declaration order (no try/finally in Udon)
        for (int i = disposableVars.Count - 1; i >= 0; i--)
        {
            var (varId, type) = disposableVars[i];
            var udonType = GetUdonType(type);
            _module.AddPush(varId);
            AddExternChecked($"{udonType}.__Dispose__SystemVoid");
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
            var id = _vars.DeclareLocal(local.Name, udonType);
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

                _ctx.TargetHint = id;
                var srcId = VisitExpression(init.Value);
                _ctx.TargetHint = null;
                if (srcId != id) // hint was not consumed
                    _module.AddCopy(srcId, id);
            }
        }
    }

}
