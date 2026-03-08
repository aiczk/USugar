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
        var loopStart = _module.DefineLabel("__while_start");
        var loopEnd = _module.DefineLabel("__while_end");

        if (op.ConditionIsTop)
        {
            _breakLabels.Push(loopEnd);
            _continueLabels.Push(loopStart);

            _module.MarkLabel(loopStart);
            var condId = VisitExpression(op.Condition);
            _module.AddPush(condId);
            _module.AddJumpIfFalse(loopEnd);
            VisitOperation(op.Body);
            _module.AddJump(loopStart);
        }
        else
        {
            // do-while: body first, then condition
            var condLabel = _module.DefineLabel("__dowhile_cond");
            _breakLabels.Push(loopEnd);
            _continueLabels.Push(condLabel);

            _module.MarkLabel(loopStart);
            VisitOperation(op.Body);
            _module.MarkLabel(condLabel);
            var condId = VisitExpression(op.Condition);
            _module.AddPush(condId);
            _module.AddJumpIfFalse(loopEnd);
            _module.AddJump(loopStart);
        }

        _module.MarkLabel(loopEnd);
        _breakLabels.Pop();
        _continueLabels.Pop();
    }

    void VisitForLoop(IForLoopOperation op)
    {
        foreach (var init in op.Before)
            VisitOperation(init);

        var loopStart = _module.DefineLabel("__for_start");
        var loopEnd = _module.DefineLabel("__for_end");
        var continueLabel = _module.DefineLabel("__for_continue");

        _breakLabels.Push(loopEnd);
        _continueLabels.Push(continueLabel);

        _module.MarkLabel(loopStart);

        if (op.Condition != null)
        {
            var condId = VisitExpression(op.Condition);
            _module.AddPush(condId);
            _module.AddJumpIfFalse(loopEnd);
        }

        VisitOperation(op.Body);

        _module.MarkLabel(continueLabel);
        foreach (var atBottom in op.AtLoopBottom)
            VisitOperation(atBottom);

        _module.AddJump(loopStart);
        _module.MarkLabel(loopEnd);

        _breakLabels.Pop();
        _continueLabels.Pop();
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
        _vars.PushScope();
        var loopVarId = _vars.DeclareLocal(loopLocal.Name, elemType);
        _localVarIds[loopLocal] = loopVarId;

        // Index variable
        var idxId = _vars.DeclareTemp("SystemInt32");
        var zeroConst = _vars.DeclareConst("SystemInt32", "0");
        _module.AddCopy(zeroConst, idxId);

        var loopStart = _module.DefineLabel("__foreach_start");
        var loopEnd = _module.DefineLabel("__foreach_end");
        var continueLabel = _module.DefineLabel("__foreach_continue");

        _breakLabels.Push(loopEnd);
        _continueLabels.Push(continueLabel);

        // Hoist array length before loop (loop-invariant)
        var lenId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(collId);
        _module.AddPush(lenId);
        AddExternChecked("SystemArray.__get_Length__SystemInt32");

        _module.MarkLabel(loopStart);

        // Condition: idx < arr.Length (lenId already computed above)

        var condId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(idxId);
        _module.AddPush(lenId);
        _module.AddPush(condId);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _module.AddPush(condId);
        _module.AddJumpIfFalse(loopEnd);

        // Element: loopVar = arr[idx] (write directly to loop variable)
        _module.AddPush(collId);
        _module.AddPush(idxId);
        _module.AddPush(loopVarId);
        AddExternChecked($"{arrayType}.__Get__SystemInt32__{elemAccessorType}");

        // Body
        VisitOperation(op.Body);

        // Increment (Udon VM reads all PUSH inputs before writing output, so direct write-back is safe)
        _module.MarkLabel(continueLabel);
        var oneConst = _vars.DeclareConst("SystemInt32", "1");
        _module.AddPush(idxId);
        _module.AddPush(oneConst);
        _module.AddPush(idxId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        _module.AddJump(loopStart);
        _module.MarkLabel(loopEnd);

        _breakLabels.Pop();
        _continueLabels.Pop();
        _vars.PopScope();
    }

    void VisitSwitch(ISwitchOperation op)
    {
        var valueId = VisitExpression(op.Value);
        var valueType = GetUdonType(op.Value.Type);
        var endLabel = _module.DefineLabel("__switch_end");

        _breakLabels.Push(endLabel);

        var bodyLabels = new int[op.Cases.Length];
        for (int i = 0; i < op.Cases.Length; i++)
            bodyLabels[i] = _module.DefineLabel($"__case_body_{i}");

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
                        caseValueId = _vars.DeclareConst(underlyingUdon,
                            ToInvariantString(singleValue.Value.ConstantValue.Value));
                    }
                    else
                    {
                        caseValueId = VisitExpression(singleValue.Value);
                        caseValueId = EmitEnumToUnderlying(caseValueId, op.Value.Type);
                        if (op.Value.Type is INamedTypeSymbol n2 && n2.TypeKind == TypeKind.Enum)
                            eqType = GetUdonType(n2.EnumUnderlyingType);
                    }
                    var condId = _vars.DeclareTemp("SystemBoolean");
                    var eqSig = ExternResolver.BuildMethodSignature(
                        eqType, "__op_Equality", new[] { eqType, eqType }, "SystemBoolean");
                    _module.AddPush(convertedValueId);
                    _module.AddPush(caseValueId);
                    _module.AddPush(condId);
                    AddExternChecked(eqSig);
                    _module.AddPush(condId);

                    var skipLabel = _module.DefineLabel($"__case_skip_{i}");
                    _module.AddJumpIfFalse(skipLabel);
                    _module.AddJump(bodyLabels[i]);
                    _module.MarkLabel(skipLabel);
                }
                else if (clause is IPatternCaseClauseOperation patternCase)
                {
                    var checkId = EmitPatternCheck(valueId, op.Value.Type, patternCase.Pattern);
                    _module.AddPush(checkId);
                    var skipLabel = _module.DefineLabel($"__case_skip_{i}");
                    _module.AddJumpIfFalse(skipLabel);
                    if (patternCase.Guard != null)
                    {
                        var guardId = VisitExpression(patternCase.Guard);
                        _module.AddPush(guardId);
                        _module.AddJumpIfFalse(skipLabel);
                    }
                    _module.AddJump(bodyLabels[i]);
                    _module.MarkLabel(skipLabel);
                }
            }
        }

        // After all comparisons: jump to default or end
        _module.AddJump(defaultIndex >= 0 ? bodyLabels[defaultIndex] : endLabel);

        // Phase 2: emit case bodies
        for (int i = 0; i < op.Cases.Length; i++)
        {
            _module.MarkLabel(bodyLabels[i]);
            foreach (var stmt in op.Cases[i].Body)
                VisitOperation(stmt);
        }

        _module.MarkLabel(endLabel);
        _breakLabels.Pop();
    }
}
