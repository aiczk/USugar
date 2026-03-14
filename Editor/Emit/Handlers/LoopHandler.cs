using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class LoopHandler : HandlerBase, IOperationHandler
{
    // Thread-safe switch break label stack (emit runs in parallel per-behaviour).
    // StatementHandler reads this to distinguish switch breaks from loop breaks.
    [ThreadStatic] static Stack<string> s_switchBreakLabels;
    internal static Stack<string> SwitchBreakLabels => s_switchBreakLabels ??= new();

    static int s_switchLabelCounter;

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
        var condExpr = VisitExpression(op.Condition);

        if (op.ConditionIsTop)
        {
            // while (cond) { body }
            _builder.EmitWhile(condExpr, _ =>
            {
                VisitOperation(op.Body);
            });
        }
        else
        {
            // do { body } while (cond)
            _builder.EmitWhile(condExpr, _ =>
            {
                VisitOperation(op.Body);
            }, isDoWhile: true);
        }
    }

    void VisitForLoop(IForLoopOperation op)
    {
        _builder.EmitFor(
            _ =>
            {
                // Init: variable declarations register locals in _localVarIds
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
                VisitOperation(op.Body);
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

        var collVal = VisitExpression(collectionOp);

        // Store collection in a temp field so it can be re-read in condition/body
        var collField = _ctx.DeclareTemp(arrayType);
        EmitStoreField(collField, collVal);

        // Declare loop variable
        var loopLocal = op.Locals.FirstOrDefault()
            ?? throw new System.InvalidOperationException("foreach has no loop variable");
        var loopVarId = _ctx.DeclareLocal(loopLocal.Name, elemType);
        _localVarIds[loopLocal] = loopVarId;

        // Index variable
        var idxField = _ctx.DeclareTemp("SystemInt32");

        // Condition: idx < arr.Length
        var condExpr = ExternCall(
            "SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
            new List<HExpr> { LoadField(idxField, "SystemInt32"),
                              ExternCall("SystemArray.__get_Length__SystemInt32",
                                         new List<HExpr> { LoadField(collField, arrayType) },
                                         "SystemInt32") },
            "SystemBoolean");

        _builder.EmitFor(
            _ =>
            {
                // Init: idx = 0
                EmitStoreField(idxField, Const(0, "SystemInt32"));
            },
            condExpr,
            _ =>
            {
                // Update: idx++
                var nextIdx = ExternCall(
                    "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { LoadField(idxField, "SystemInt32"), Const(1, "SystemInt32") },
                    "SystemInt32");
                EmitStoreField(idxField, nextIdx);
            },
            _ =>
            {
                // Body: loopVar = arr[idx]; <body>
                var elemVal = ExternCall(
                    $"{arrayType}.__Get__SystemInt32__{elemAccessorType}",
                    new List<HExpr> { LoadField(collField, arrayType), LoadField(idxField, "SystemInt32") },
                    elemType);
                EmitStoreField(loopVarId, elemVal);

                VisitOperation(op.Body);
            });
    }

    void VisitSwitch(ISwitchOperation op)
    {
        var valueVal = VisitExpression(op.Value);
        var valueType = GetUdonType(op.Value.Type);

        // Generate a unique end label for this switch
        var endLabel = $"__switchEnd_{Interlocked.Increment(ref s_switchLabelCounter)}";
        SwitchBreakLabels.Push(endLabel);

        // Pre-convert enum switch value once (Udon VM has no enum-typed operators)
        var convertedValueVal = EmitEnumToUnderlying(valueVal, op.Value.Type);

        // Store converted value in a temp so it can be re-read for each comparison
        string convertedField = null;
        if (op.Cases.Length > 1)
        {
            var convertedType = valueType;
            if (op.Value.Type is INamedTypeSymbol namedEnum && namedEnum.TypeKind == TypeKind.Enum)
                convertedType = GetUdonType(namedEnum.EnumUnderlyingType);
            convertedField = _ctx.DeclareTemp(convertedType);
            EmitStoreField(convertedField, convertedValueVal);
        }

        // Also store the original value for pattern matching
        string origValueField = null;
        if (op.Cases.Any(c => c.Clauses.Any(cl => cl is IPatternCaseClauseOperation)))
        {
            origValueField = _ctx.DeclareTemp(valueType);
            EmitStoreField(origValueField, valueVal);
        }

        // Find default case index
        int defaultIndex = -1;
        for (int i = 0; i < op.Cases.Length; i++)
            if (op.Cases[i].Clauses.Any(c => c is IDefaultCaseClauseOperation))
                defaultIndex = i;

        // Lower switch to if/else chain
        EmitSwitchCases(op, convertedField, convertedValueVal, origValueField, valueVal, valueType, defaultIndex, 0);

        _builder.EmitLabel(endLabel);
        SwitchBreakLabels.Pop();
    }

    void EmitSwitchCases(ISwitchOperation op, string convertedField, HExpr convertedValueVal,
        string origValueField, HExpr origValueVal, string valueType, int defaultIndex, int startIdx)
    {
        // Find the next non-default case starting from startIdx
        int caseIdx = -1;
        for (int i = startIdx; i < op.Cases.Length; i++)
        {
            if (i == defaultIndex && op.Cases[i].Clauses.All(c => c is IDefaultCaseClauseOperation))
                continue; // Skip pure default cases; handle at the end
            caseIdx = i;
            break;
        }

        if (caseIdx < 0)
        {
            // No more non-default cases; emit default body if present
            if (defaultIndex >= 0)
                EmitCaseBody(op.Cases[defaultIndex]);
            return;
        }

        // Build condition: OR of all clauses for this case
        HExpr caseCond = null;
        var caseSection = op.Cases[caseIdx];
        foreach (var clause in caseSection.Clauses)
        {
            if (clause is IDefaultCaseClauseOperation)
                continue;

            HExpr clauseCond = null;
            if (clause is ISingleValueCaseClauseOperation singleValue)
            {
                var caseValueVal = VisitExpression(singleValue.Value);
                caseValueVal = EmitEnumToUnderlying(caseValueVal, op.Value.Type);

                var eqType = valueType;
                if (op.Value.Type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
                    eqType = GetUdonType(named.EnumUnderlyingType);
                var eqSig = ExternResolver.BuildMethodSignature(
                    eqType, "__op_Equality", new[] { eqType, eqType }, "SystemBoolean");

                var lhs = convertedField != null ? LoadField(convertedField, eqType) : convertedValueVal;
                clauseCond = ExternCall(eqSig, new List<HExpr> { lhs, caseValueVal }, "SystemBoolean");
            }
            else if (clause is IPatternCaseClauseOperation patternCase)
            {
                var patValue = origValueField != null ? LoadField(origValueField, valueType) : origValueVal;
                clauseCond = EmitPatternCheck(patValue, op.Value.Type, patternCase.Pattern);

                if (patternCase.Guard != null)
                {
                    var guardVal = VisitExpression(patternCase.Guard);
                    // Both pattern and guard must pass: clauseCond && guardVal
                    clauseCond = ExternCall(
                        "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<HExpr> { clauseCond, guardVal },
                        "SystemBoolean");
                }
            }

            if (clauseCond != null)
            {
                caseCond = caseCond == null
                    ? clauseCond
                    : ExternCall(
                        "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<HExpr> { caseCond, clauseCond },
                        "SystemBoolean");
            }
        }

        if (caseCond != null)
        {
            _builder.EmitIf(caseCond,
                _ => EmitCaseBody(caseSection),
                _ => EmitSwitchCases(op, convertedField, convertedValueVal,
                                     origValueField, origValueVal, valueType, defaultIndex, caseIdx + 1));
        }
        else
        {
            // Case with only default clause — treated as else (handled by fallthrough)
            EmitSwitchCases(op, convertedField, convertedValueVal,
                            origValueField, origValueVal, valueType, defaultIndex, caseIdx + 1);
        }
    }

    void EmitCaseBody(ISwitchCaseOperation caseSection)
    {
        foreach (var stmt in caseSection.Body)
            VisitOperation(stmt);
    }
}
