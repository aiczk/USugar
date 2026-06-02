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
        var valueType = GetUdonType(op.Value.Type);
        // Under ANF VisitExpression binds a side-effecting governor (`switch(Next())`) to a fresh scratch
        // exactly once, so the resulting leaf can be re-read across every case arm with no re-evaluation.
        var valueVal = VisitExpression(op.Value);

        var endLabel = _ctx.NextSwitchEndLabel();
        _ctx.SwitchBreakLabels.Push(endLabel);
        _ctx.LoopUsingDepthStack.Push(_usingDisposableStack.Count);
        try
        {
            // Pre-convert enum switch value once (Udon VM has no enum-typed operators)
            var convertedValueVal = EmitEnumToUnderlying(valueVal, op.Value.Type);

            // convertedValueVal (enum→underlying) and valueVal are single-assignment governor leaves under
            // ANF — stable and re-readable across every case condition without a snapshot slot.
            int defaultIndex = -1;
            for (int i = 0; i < op.Cases.Length; i++)
                if (op.Cases[i].Clauses.Any(c => c is IDefaultCaseClauseOperation))
                    defaultIndex = i;

            EmitSwitchCases(op, convertedValueVal, valueVal, valueType, defaultIndex, 0);
        }
        finally
        {
            _ctx.LoopUsingDepthStack.Pop();
            _ctx.SwitchBreakLabels.Pop();
        }
        _builder.EmitLabel(endLabel);
    }

    void EmitSwitchCases(ISwitchOperation op, CLeaf convertedValueVal,
        CLeaf origValueVal, string valueType, int defaultIndex, int startIdx)
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
            convertedValueVal, origValueVal, valueType);

        if (caseCond != null)
        {
            _builder.EmitIf(caseCond,
                _ => EmitCaseBody(op.Cases[caseIdx]),
                _ => EmitSwitchCases(op, convertedValueVal, origValueVal, valueType, defaultIndex, caseIdx + 1));
        }
        else
        {
            // Case with only default clause — fall through to next.
            EmitSwitchCases(op, convertedValueVal, origValueVal, valueType, defaultIndex, caseIdx + 1);
        }
    }

    CLeaf BuildCaseCondition(ISwitchCaseOperation caseSection, ITypeSymbol switchValueType,
        CLeaf convertedValueVal, CLeaf origValueVal, string valueType)
    {
        CLeaf caseCond = null;
        foreach (var clause in caseSection.Clauses)
        {
            if (clause is IDefaultCaseClauseOperation) continue;
            var clauseCond = BuildClauseCondition(clause, switchValueType,
                convertedValueVal, origValueVal, valueType);
            if (clauseCond == null) continue;
            caseCond = caseCond == null
                ? clauseCond
                : ExternCall(
                    "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean",
                    new List<CLeaf> { caseCond, clauseCond },
                    "SystemBoolean");
        }
        return caseCond;
    }

    CLeaf BuildClauseCondition(ICaseClauseOperation clause, ITypeSymbol switchValueType,
        CLeaf convertedValueVal, CLeaf origValueVal, string valueType)
    {
        switch (clause)
        {
            case ISingleValueCaseClauseOperation singleValue:
            {
                var eqType = valueType;
                if (switchValueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
                    eqType = GetUdonType(named.EnumUnderlyingType);

                CLeaf caseValueVal;
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

                var lhs = convertedValueVal;
                return ExternCall(eqSig, new List<CLeaf> { lhs, caseValueVal }, "SystemBoolean");
            }
            case IPatternCaseClauseOperation patternCase:
            {
                var patValue = origValueVal;
                var cond = EmitPatternCheck(patValue, switchValueType, patternCase.Pattern);
                if (patternCase.Guard != null)
                {
                    var guardVal = VisitExpression(patternCase.Guard);
                    cond = ExternCall(
                        "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<CLeaf> { cond, guardVal },
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
