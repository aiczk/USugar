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
            or IForEachLoopOperation
            or ISwitchOperation;

    public void Handle(IOperation operation)
    {
        switch (operation)
        {
            case IWhileLoopOperation op: VisitWhileLoop(op); break;
            case IForLoopOperation op: VisitForLoop(op); break;
            case IForEachLoopOperation op: VisitForEachLoop(op); break;
            case ISwitchOperation op: VisitSwitch(op); break;
            default: throw new System.NotSupportedException(operation.GetType().Name);
        }
    }

    void VisitWhileLoop(IWhileLoopOperation op)
    {
        var loopStart = _ctx.Module.DefineLabel("__while_start");
        var loopEnd = _ctx.Module.DefineLabel("__while_end");

        if (op.ConditionIsTop)
        {
            _ctx.BreakLabels.Push(loopEnd);
            _ctx.ContinueLabels.Push(loopStart);

            _ctx.Module.MarkLabel(loopStart);
            var condId = VisitExpression(op.Condition);
            _ctx.Module.AddPush(condId);
            _ctx.Module.AddJumpIfFalse(loopEnd);
            VisitOperation(op.Body);
            _ctx.Module.AddJump(loopStart);
        }
        else
        {
            // do-while: body first, then condition
            var condLabel = _ctx.Module.DefineLabel("__dowhile_cond");
            _ctx.BreakLabels.Push(loopEnd);
            _ctx.ContinueLabels.Push(condLabel);

            _ctx.Module.MarkLabel(loopStart);
            VisitOperation(op.Body);
            _ctx.Module.MarkLabel(condLabel);
            var condId = VisitExpression(op.Condition);
            _ctx.Module.AddPush(condId);
            _ctx.Module.AddJumpIfFalse(loopEnd);
            _ctx.Module.AddJump(loopStart);
        }

        _ctx.Module.MarkLabel(loopEnd);
        _ctx.BreakLabels.Pop();
        _ctx.ContinueLabels.Pop();
    }

    void VisitForLoop(IForLoopOperation op)
    {
        foreach (var init in op.Before)
            VisitOperation(init);

        var loopStart = _ctx.Module.DefineLabel("__for_start");
        var loopEnd = _ctx.Module.DefineLabel("__for_end");
        var continueLabel = _ctx.Module.DefineLabel("__for_continue");

        _ctx.BreakLabels.Push(loopEnd);
        _ctx.ContinueLabels.Push(continueLabel);

        _ctx.Module.MarkLabel(loopStart);

        if (op.Condition != null)
        {
            var condId = VisitExpression(op.Condition);
            _ctx.Module.AddPush(condId);
            _ctx.Module.AddJumpIfFalse(loopEnd);
        }

        VisitOperation(op.Body);

        _ctx.Module.MarkLabel(continueLabel);
        foreach (var atBottom in op.AtLoopBottom)
            VisitOperation(atBottom);

        _ctx.Module.AddJump(loopStart);
        _ctx.Module.MarkLabel(loopEnd);

        _ctx.BreakLabels.Pop();
        _ctx.ContinueLabels.Pop();
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

        var collId = VisitExpression(collectionOp);

        // Declare loop variable
        var loopLocal = op.Locals.FirstOrDefault()
            ?? throw new System.InvalidOperationException("foreach has no loop variable");
        _ctx.Vars.PushScope();
        var loopVarId = _ctx.Vars.DeclareLocal(loopLocal.Name, elemType);
        _ctx.LocalVarIds[loopLocal] = loopVarId;

        // Index variable
        var idxId = _ctx.Vars.DeclareTemp("SystemInt32");
        var zeroConst = _ctx.Vars.DeclareConst("SystemInt32", "0");
        _ctx.Module.AddCopy(zeroConst, idxId);

        var loopStart = _ctx.Module.DefineLabel("__foreach_start");
        var loopEnd = _ctx.Module.DefineLabel("__foreach_end");
        var continueLabel = _ctx.Module.DefineLabel("__foreach_continue");

        _ctx.BreakLabels.Push(loopEnd);
        _ctx.ContinueLabels.Push(continueLabel);

        // Hoist array length before loop (loop-invariant)
        var lenId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(collId);
        _ctx.Module.AddPush(lenId);
        AddExternChecked("SystemArray.__get_Length__SystemInt32");

        _ctx.Module.MarkLabel(loopStart);

        // Condition: idx < arr.Length (lenId already computed above)

        var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(condId);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(loopEnd);

        // Element: loopVar = arr[idx] (write directly to loop variable)
        _ctx.Module.AddPush(collId);
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(loopVarId);
        AddExternChecked($"{arrayType}.__Get__SystemInt32__{elemAccessorType}");

        // Body
        VisitOperation(op.Body);

        // Increment (Udon VM reads all PUSH inputs before writing output, so direct write-back is safe)
        _ctx.Module.MarkLabel(continueLabel);
        var oneConst = _ctx.Vars.DeclareConst("SystemInt32", "1");
        _ctx.Module.AddPush(idxId);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(idxId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        _ctx.Module.AddJump(loopStart);
        _ctx.Module.MarkLabel(loopEnd);

        _ctx.BreakLabels.Pop();
        _ctx.ContinueLabels.Pop();
        _ctx.Vars.PopScope();
    }

    void VisitSwitch(ISwitchOperation op)
    {
        var valueId = VisitExpression(op.Value);
        var valueType = GetUdonType(op.Value.Type);
        var endLabel = _ctx.Module.DefineLabel("__switch_end");

        _ctx.BreakLabels.Push(endLabel);

        var bodyLabels = new int[op.Cases.Length];
        for (int i = 0; i < op.Cases.Length; i++)
            bodyLabels[i] = _ctx.Module.DefineLabel($"__case_body_{i}");

        int defaultIndex = -1;

        // Pre-convert enum switch value once (Udon VM has no enum-typed operators)
        var convertedValueId = EmitEnumToUnderlying(valueId, op.Value.Type);

        // Phase 1: emit comparisons → jump to body
        for (int i = 0; i < op.Cases.Length; i++)
        {
            foreach (var clause in op.Cases[i].Clauses)
            {
                if (clause is IDefaultCaseClauseOperation)
                {
                    defaultIndex = i;
                    continue;
                }

                if (clause is ISingleValueCaseClauseOperation singleValue)
                {
                    string caseValueId;
                    var eqType = valueType;
                    // Enum case values are compile-time constants: declare underlying
                    // value directly instead of emitting a SystemConvert extern at runtime.
                    if (op.Value.Type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum
                        && singleValue.Value.ConstantValue.HasValue)
                    {
                        var underlyingUdon = GetUdonType(named.EnumUnderlyingType);
                        eqType = underlyingUdon;
                        caseValueId = _ctx.Vars.DeclareConst(underlyingUdon,
                            ToInvariantString(singleValue.Value.ConstantValue.Value));
                    }
                    else
                    {
                        caseValueId = VisitExpression(singleValue.Value);
                        caseValueId = EmitEnumToUnderlying(caseValueId, op.Value.Type);
                        if (op.Value.Type is INamedTypeSymbol n2 && n2.TypeKind == TypeKind.Enum)
                            eqType = GetUdonType(n2.EnumUnderlyingType);
                    }
                    var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
                    var eqSig = ExternResolver.BuildMethodSignature(
                        eqType, "__op_Equality", new[] { eqType, eqType }, "SystemBoolean");
                    _ctx.Module.AddPush(convertedValueId);
                    _ctx.Module.AddPush(caseValueId);
                    _ctx.Module.AddPush(condId);
                    AddExternChecked(eqSig);
                    _ctx.Module.AddPush(condId);

                    var skipLabel = _ctx.Module.DefineLabel($"__case_skip_{i}");
                    _ctx.Module.AddJumpIfFalse(skipLabel);
                    _ctx.Module.AddJump(bodyLabels[i]);
                    _ctx.Module.MarkLabel(skipLabel);
                }
                else if (clause is IPatternCaseClauseOperation patternCase)
                {
                    var checkId = EmitPatternCheck(valueId, op.Value.Type, patternCase.Pattern);
                    _ctx.Module.AddPush(checkId);
                    var skipLabel = _ctx.Module.DefineLabel($"__case_skip_{i}");
                    _ctx.Module.AddJumpIfFalse(skipLabel);
                    if (patternCase.Guard != null)
                    {
                        var guardId = VisitExpression(patternCase.Guard);
                        _ctx.Module.AddPush(guardId);
                        _ctx.Module.AddJumpIfFalse(skipLabel);
                    }
                    _ctx.Module.AddJump(bodyLabels[i]);
                    _ctx.Module.MarkLabel(skipLabel);
                }
            }
        }

        // After all comparisons: jump to default or end
        _ctx.Module.AddJump(defaultIndex >= 0 ? bodyLabels[defaultIndex] : endLabel);

        // Phase 2: emit case bodies
        for (int i = 0; i < op.Cases.Length; i++)
        {
            _ctx.Module.MarkLabel(bodyLabels[i]);
            foreach (var stmt in op.Cases[i].Body)
                VisitOperation(stmt);
        }

        _ctx.Module.MarkLabel(endLabel);
        _ctx.BreakLabels.Pop();
    }
}
