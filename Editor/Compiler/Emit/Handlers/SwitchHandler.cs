using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Lowers ISwitchOperation to an if/else chain over the case clauses.
/// Split out of LoopHandler in v2.2 — switch is structurally distinct from loops, and the
/// 140-line lowering deserves its own home. Pattern matching (IPatternCaseClauseOperation)
/// and enum-typed switch values (with EmitEnumToUnderlying conversion) are supported.
/// </summary>
public class SwitchHandler : HandlerBase, IOperationHandler
{
    public SwitchHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation operation) => operation is ISwitchOperation;

    public void Handle(IOperation operation)
    {
        if (operation is ISwitchOperation op)
            VisitSwitch(op);
        else
            throw new System.NotSupportedException(operation.GetType().Name);
    }

    void VisitSwitch(ISwitchOperation op)
    {
        var valueVal = VisitExpression(op.Value);
        var valueType = GetUdonType(op.Value.Type);

        var endLabel = _ctx.NextSwitchEndLabel();
        _ctx.SwitchBreakLabels.Push(endLabel);
        _ctx.LoopUsingDepthStack.Push(_usingDisposableStack.Count);
        try
        {
            // Pre-convert enum switch value once (Udon VM has no enum-typed operators)
            var convertedValueVal = EmitEnumToUnderlying(valueVal, op.Value.Type);

            // Cache the converted value when there's more than one case so we don't re-evaluate.
            int convertedSlot = -1;
            string convertedSlotType = null;
            if (op.Cases.Length > 1)
            {
                convertedSlotType = valueType;
                if (op.Value.Type is INamedTypeSymbol namedEnum && namedEnum.TypeKind == TypeKind.Enum)
                    convertedSlotType = GetUdonType(namedEnum.EnumUnderlyingType);
                convertedSlot = _ctx.AllocTemp(convertedSlotType);
                EmitAssign(convertedSlot, convertedValueVal);
            }

            // Pattern matching needs the original (non-converted) value.
            int origValueSlot = -1;
            if (op.Cases.Any(c => c.Clauses.Any(cl => cl is IPatternCaseClauseOperation)))
            {
                origValueSlot = _ctx.AllocTemp(valueType);
                EmitAssign(origValueSlot, valueVal);
            }

            int defaultIndex = -1;
            for (int i = 0; i < op.Cases.Length; i++)
                if (op.Cases[i].Clauses.Any(c => c is IDefaultCaseClauseOperation))
                    defaultIndex = i;

            EmitSwitchCases(op, convertedSlot, convertedSlotType, convertedValueVal,
                origValueSlot, valueVal, valueType, defaultIndex, 0);
        }
        finally
        {
            _ctx.LoopUsingDepthStack.Pop();
            _ctx.SwitchBreakLabels.Pop();
        }
        _builder.EmitLabel(endLabel);
    }

    void EmitSwitchCases(ISwitchOperation op, int convertedSlot, string convertedSlotType, HExpr convertedValueVal,
        int origValueSlot, HExpr origValueVal, string valueType, int defaultIndex, int startIdx)
    {
        // Find the next non-default case from startIdx.
        int caseIdx = -1;
        for (int i = startIdx; i < op.Cases.Length; i++)
        {
            if (i == defaultIndex && op.Cases[i].Clauses.All(c => c is IDefaultCaseClauseOperation))
                continue;
            caseIdx = i;
            break;
        }

        if (caseIdx < 0)
        {
            // All non-default cases handled — emit default body if any.
            if (defaultIndex >= 0)
                EmitCaseBody(op.Cases[defaultIndex]);
            return;
        }

        var caseCond = BuildCaseCondition(op.Cases[caseIdx], op.Value.Type,
            convertedSlot, convertedValueVal, origValueSlot, origValueVal, valueType);

        if (caseCond != null)
        {
            _builder.EmitIf(caseCond,
                _ => EmitCaseBody(op.Cases[caseIdx]),
                _ => EmitSwitchCases(op, convertedSlot, convertedSlotType, convertedValueVal,
                                     origValueSlot, origValueVal, valueType, defaultIndex, caseIdx + 1));
        }
        else
        {
            // Case with only default clause — fall through to next.
            EmitSwitchCases(op, convertedSlot, convertedSlotType, convertedValueVal,
                            origValueSlot, origValueVal, valueType, defaultIndex, caseIdx + 1);
        }
    }

    HExpr BuildCaseCondition(ISwitchCaseOperation caseSection, ITypeSymbol switchValueType,
        int convertedSlot, HExpr convertedValueVal,
        int origValueSlot, HExpr origValueVal, string valueType)
    {
        HExpr caseCond = null;
        foreach (var clause in caseSection.Clauses)
        {
            if (clause is IDefaultCaseClauseOperation) continue;
            var clauseCond = BuildClauseCondition(clause, switchValueType,
                convertedSlot, convertedValueVal, origValueSlot, origValueVal, valueType);
            if (clauseCond == null) continue;
            caseCond = caseCond == null
                ? clauseCond
                : ExternCall(
                    "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean",
                    new List<HExpr> { caseCond, clauseCond },
                    "SystemBoolean");
        }
        return caseCond;
    }

    HExpr BuildClauseCondition(ICaseClauseOperation clause, ITypeSymbol switchValueType,
        int convertedSlot, HExpr convertedValueVal,
        int origValueSlot, HExpr origValueVal, string valueType)
    {
        switch (clause)
        {
            case ISingleValueCaseClauseOperation singleValue:
            {
                var eqType = valueType;
                if (switchValueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
                    eqType = GetUdonType(named.EnumUnderlyingType);

                HExpr caseValueVal;
                // Compile-time fold for enum / numeric constant case labels.
                // Avoids runtime SystemConvert.__ToInt32__SystemObject__SystemInt32 per case.
                if (singleValue.Value.ConstantValue.HasValue)
                {
                    caseValueVal = Const(singleValue.Value.ConstantValue.Value, eqType);
                }
                else
                {
                    caseValueVal = VisitExpression(singleValue.Value);
                    caseValueVal = EmitEnumToUnderlying(caseValueVal, switchValueType);
                }

                var eqSig = ExternResolver.BuildMethodSignature(
                    eqType, "__op_Equality", new[] { eqType, eqType }, "SystemBoolean");

                var lhs = convertedSlot >= 0 ? SlotRef(convertedSlot) : convertedValueVal;
                return ExternCall(eqSig, new List<HExpr> { lhs, caseValueVal }, "SystemBoolean");
            }
            case IPatternCaseClauseOperation patternCase:
            {
                var patValue = origValueSlot >= 0 ? SlotRef(origValueSlot) : origValueVal;
                var cond = EmitPatternCheck(patValue, switchValueType, patternCase.Pattern);
                if (patternCase.Guard != null)
                {
                    var guardVal = VisitExpression(patternCase.Guard);
                    cond = ExternCall(
                        "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<HExpr> { cond, guardVal },
                        "SystemBoolean");
                }
                return cond;
            }
            default:
                return null;
        }
    }

    void EmitCaseBody(ISwitchCaseOperation caseSection)
    {
        foreach (var stmt in caseSection.Body)
            VisitOperation(stmt);
    }
}
