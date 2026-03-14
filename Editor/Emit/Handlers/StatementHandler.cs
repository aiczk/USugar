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
                if (_ctx.GotoLabels.TryGetValue(labeled.Label, out var gotoLbl))
                {
                    _ctx.Module.AddLabel(gotoLbl);
                    _ctx.Module.MarkLabel(gotoLbl);
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
                        var localId = _ctx.LocalVarIds.TryGetValue(declarator.Symbol, out var lid) ? lid : declarator.Symbol.Name;
                        _ctx.UsingDisposableStack.Peek().Add((localId, declarator.Symbol.Type));
                    }
                }
                break;
            default: throw new System.NotSupportedException(operation.GetType().Name);
        }
    }

    void HandleBlock(IBlockOperation block)
    {
        _ctx.UsingDisposableStack.Push(new List<(string, ITypeSymbol)>());
        foreach (var stmt in block.Operations)
            VisitOperation(stmt);
        var disposables = _ctx.UsingDisposableStack.Pop();
        for (int i = disposables.Count - 1; i >= 0; i--)
        {
            var (varId, type) = disposables[i];
            _ctx.Module.AddPush(varId);
            AddExternChecked($"{GetUdonType(type)}.__Dispose__SystemVoid");
        }
    }

    void VisitConditional(IConditionalOperation op)
    {
        // Optimization: if (!cond) → skip negation extern, invert branch
        if (op.Condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary)
        {
            var condId = VisitExpression(unary.Operand);
            var endLabel = _ctx.Module.DefineLabel("__if_end");
            _ctx.Module.AddPush(condId);

            if (op.WhenFalse != null)
            {
                // if (!c) A else B → push(c), JumpIfFalse → A branch, fall through → B branch
                var thenLabel = _ctx.Module.DefineLabel("__if_neg_then");
                _ctx.Module.AddJumpIfFalse(thenLabel);
                VisitOperation(op.WhenFalse);
                _ctx.Module.AddJump(endLabel);
                _ctx.Module.MarkLabel(thenLabel);
                VisitOperation(op.WhenTrue);
            }
            else
            {
                // if (!c) A → push(c), JumpIfFalse(body), JUMP(end), body: A
                // c=true → doesn't jump → JUMP(end) skips body. c=false → jumps to body.
                var bodyLabel = _ctx.Module.DefineLabel("__if_neg_body");
                _ctx.Module.AddJumpIfFalse(bodyLabel);
                _ctx.Module.AddJump(endLabel);
                _ctx.Module.MarkLabel(bodyLabel);
                VisitOperation(op.WhenTrue);
            }
            _ctx.Module.MarkLabel(endLabel);
            return;
        }

        var condId2 = VisitExpression(op.Condition);
        var endLabel2 = _ctx.Module.DefineLabel("__if_end");

        _ctx.Module.AddPush(condId2);

        if (op.WhenFalse != null)
        {
            var elseLabel = _ctx.Module.DefineLabel("__if_else");
            _ctx.Module.AddJumpIfFalse(elseLabel);
            VisitOperation(op.WhenTrue);
            _ctx.Module.AddJump(endLabel2);
            _ctx.Module.MarkLabel(elseLabel);
            VisitOperation(op.WhenFalse);
            _ctx.Module.MarkLabel(endLabel2);
        }
        else
        {
            _ctx.Module.AddJumpIfFalse(endLabel2);
            VisitOperation(op.WhenTrue);
            _ctx.Module.MarkLabel(endLabel2);
        }
    }

    void VisitReturn(IReturnOperation op)
    {
        // Tail call optimization: return self(args) → overwrite params + JUMP
        if (op.ReturnedValue is IInvocationOperation tailCall
            && _ctx.CurrentMethod != null
            && SymbolEqualityComparer.Default.Equals(tailCall.TargetMethod, _ctx.CurrentMethod))
        {
            EmitTailCall(tailCall);
            return;
        }

        if (op.ReturnedValue != null && _ctx.CurrentMethod != null
            && _ctx.MethodTupleRetVars.TryGetValue(_ctx.CurrentMethod, out var tupleRetVars))
        {
            CopyTupleValueToSlots(op.ReturnedValue, tupleRetVars, $"return from '{_ctx.CurrentMethod.Name}'");
            _ctx.Module.AddReturn("__intnl_returnJump_SystemUInt32_0");
            return;
        }

        if (op.ReturnedValue != null && _ctx.CurrentMethod != null && _ctx.MethodRetVars.TryGetValue(_ctx.CurrentMethod, out var retVarId))
        {
            _ctx.TargetHint = retVarId;
            var srcId = VisitExpression(op.ReturnedValue);
            _ctx.TargetHint = null;
            if (srcId != retVarId)
                _ctx.Module.AddCopy(srcId, retVarId);

            // VRChat reads OnOwnershipRequest's return value from __returnValue (bool)
            if (_ctx.CurrentMethod.Name == "OnOwnershipRequest")
            {
                _ctx.Vars.TryDeclareVar("__returnValue", "SystemBoolean");
                _ctx.Module.AddCopy(retVarId, "__returnValue");
            }
        }
        _ctx.Module.AddReturn("__intnl_returnJump_SystemUInt32_0");
    }

    void EmitTailCall(IInvocationOperation tailCall)
    {
        var paramIds = _ctx.MethodParamVarIds[_ctx.CurrentMethod];

        // Evaluate args into temps first (avoid overwriting params before they're read)
        var argTemps = new string[tailCall.Arguments.Length];
        var tupleArgTemps = new string[tailCall.Arguments.Length][];
        for (int i = 0; i < tailCall.Arguments.Length; i++)
        {
            if (TryGetMethodTupleParamVarIds(_ctx.CurrentMethod, i, out var tupleParamIds))
            {
                if (!TryResolveTupleValue(tailCall.Arguments[i].Value, out var srcTupleIds))
                    throw new System.NotSupportedException(
                        $"Unsupported tuple tail-call argument for parameter '{_ctx.CurrentMethod.Parameters[i].Name}'.");

                var tempTuple = new string[tupleParamIds.Length];
                for (int ei = 0; ei < tupleParamIds.Length; ei++)
                {
                    var elemType = _ctx.Vars.GetDeclaredType(tupleParamIds[ei]);
                    tempTuple[ei] = _ctx.Vars.DeclareTemp(elemType);
                    _ctx.Module.AddCopy(srcTupleIds[ei], tempTuple[ei]);
                }
                tupleArgTemps[i] = tempTuple;
            }
            else
            {
                argTemps[i] = VisitExpression(tailCall.Arguments[i].Value);
            }
        }

        // Overwrite param vars with new values
        for (int i = 0; i < tailCall.Arguments.Length; i++)
        {
            if (TryGetMethodTupleParamVarIds(_ctx.CurrentMethod, i, out var tupleParamIds))
            {
                for (int ei = 0; ei < tupleParamIds.Length; ei++)
                    _ctx.Module.AddCopy(tupleArgTemps[i][ei], tupleParamIds[ei]);
            }
            else
            {
                _ctx.Module.AddCopy(argTemps[i], paramIds[i]);
            }
        }

        // Jump back to method body (skip re-entrance preamble)
        int jumpTarget = _ctx.MethodBodyLabels.TryGetValue(_ctx.CurrentMethod, out var bodyLabel)
            ? bodyLabel : _ctx.MethodLabels[_ctx.CurrentMethod];
        _ctx.Module.AddJump(jumpTarget);
    }

    void VisitBranch(IBranchOperation op)
    {
        if (op.BranchKind == BranchKind.Break && _ctx.BreakLabels.Count > 0)
            _ctx.Module.AddJump(_ctx.BreakLabels.Peek());
        else if (op.BranchKind == BranchKind.Continue && _ctx.ContinueLabels.Count > 0)
            _ctx.Module.AddJump(_ctx.ContinueLabels.Peek());
        else if (op.BranchKind == BranchKind.GoTo && _ctx.GotoLabels.TryGetValue(op.Target, out var targetLbl))
            _ctx.Module.AddJump(targetLbl);
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
            var label = _ctx.Module.DefineLabel($"__goto_{labeled.Label.Name}");
            _ctx.GotoLabels[labeled.Label] = label;
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
                    var localId = _ctx.LocalVarIds.TryGetValue(declarator.Symbol, out var id) ? id : declarator.Symbol.Name;
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
            _ctx.Module.AddPush(varId);
            AddExternChecked($"{udonType}.__Dispose__SystemVoid");
        }
    }

    void VisitVariableDeclaration(IVariableDeclarationOperation decl)
    {
        foreach (var declarator in decl.Declarators)
        {
            var local = declarator.Symbol;
            if (local.Type.IsTupleType && local.Type is INamedTypeSymbol tupleType)
            {
                var tupleLocalIds = new string[tupleType.TupleElements.Length];
                for (int ei = 0; ei < tupleType.TupleElements.Length; ei++)
                {
                    var elemType = GetUdonType(tupleType.TupleElements[ei].Type);
                    tupleLocalIds[ei] = _ctx.Vars.DeclareLocal($"{local.Name}__item{ei}", elemType);
                }
                _ctx.TupleLocalVarIds[local] = tupleLocalIds;

                var initTuple = declarator.Initializer;
                if (initTuple != null)
                    CopyTupleValueToSlots(initTuple.Value, tupleLocalIds, $"initializer for tuple local '{local.Name}'");
                continue;
            }

            // Delegate-typed locals → SystemUInt32 (holds label address; Udon has no delegate types)
            var udonType = local.Type.TypeKind == TypeKind.Delegate
                ? "SystemUInt32"
                : GetUdonType(local.Type);
            var id = _ctx.Vars.DeclareLocal(local.Name, udonType);
            _ctx.LocalVarIds[local] = id;

            var init = declarator.Initializer;
            if (init != null)
            {
                // Track delegate variable → hoisted method mapping
                if (init.Value is IDelegateCreationOperation delegateInit
                    && delegateInit.Target is IAnonymousFunctionOperation lambdaInit)
                {
                    var hoisted = HoistLambdaToMethod(lambdaInit);
                    _ctx.DelegateVarMap[local] = hoisted;
                }

                _ctx.TargetHint = id;
                var srcId = VisitExpression(init.Value);
                _ctx.TargetHint = null;
                if (srcId != id) // hint was not consumed
                    _ctx.Module.AddCopy(srcId, id);
            }
        }
    }

}
