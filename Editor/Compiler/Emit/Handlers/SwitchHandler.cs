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
internal sealed class SwitchHandler : IOperationHandler
{
    readonly LoweringServices _lowering;
    public SwitchHandler(LoweringServices lowering) => _lowering = lowering;

    public OperationKind[] HandledKinds { get; } = new[] { OperationKind.Switch };

    public void Handle(IOperation operation)
    {
        if (operation is ISwitchOperation op)
            VisitSwitch(op);
        else
            throw new System.NotSupportedException(operation.GetType().Name);
    }

    void VisitSwitch(ISwitchOperation op)
    {
        var valueType = _lowering.GetStorageTypeName(op.Value.Type);
        // Under ANF VisitExpression binds a side-effecting governor (`switch(Next())`) to a fresh scratch
        // exactly once, so the resulting leaf can be re-read across every case arm with no re-evaluation.
        var valueVal = _lowering.VisitExpression(op.Value);

        // Stage 2 §3: one Switch env per switch (shared by all sections), allocated before any case
        // pattern/section local is written — a case-label pattern var is bound while the case
        // CONDITION is built (below), so the env cell must already exist.
        EnvEmit.Alloc(_lowering.Builder, _lowering.State, _lowering.State.Captures?.ScopeFor(op, CaptureScopeKind.Switch));

        var endLabel = _lowering.State.ControlFlow.NextSwitchEndLabel();
        _lowering.State.ControlFlow.SwitchBreakLabels.Push(endLabel);
        _lowering.State.ControlFlow.LoopUsingDepthStack.Push(_lowering.UsingDisposableStack.Count);
        // Build a per-switch map: Roslyn goto-case/-default target name → sanitized UASM landing label. Only
        // targeted cases get a label (a switch with no goto-case keeps byte-identical UASM). Sorted for
        // determinism; labels derive from this switch's unique end-label counter.
        var gotoTargets = new HashSet<string>();
        CollectGotoCaseTargets(op, gotoTargets);
        var labelMap = new Dictionary<string, string>();
        var labelBase = endLabel.Replace("__switchEnd_", "__switchCase_");
        int gi = 0;
        foreach (var name in gotoTargets.OrderBy(n => n, System.StringComparer.Ordinal))
            labelMap[name] = $"{labelBase}_{gi++}";
        _lowering.State.ControlFlow.GotoCaseLabels.Push(labelMap);
        try
        {
            // Pre-convert enum switch value once (Udon VM has no enum-typed operators)
            var convertedValueVal = _lowering.EmitEnumToUnderlying(valueVal, op.Value.Type);

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
            _lowering.State.ControlFlow.GotoCaseLabels.Pop();
            _lowering.State.ControlFlow.LoopUsingDepthStack.Pop();
            _lowering.State.ControlFlow.SwitchBreakLabels.Pop();
        }
        _lowering.Builder.EmitLabel(endLabel);
    }

    // Collect the label names of goto-case / goto-default branches inside THIS switch (a `goto case 2;` targets
    // a label named "case 2:", `goto default;` → "default"). Does not descend into a nested switch — that
    // switch's own VisitSwitch collects its targets. Used to decide which case bodies need a jump label.
    static void CollectGotoCaseTargets(IOperation op, HashSet<string> into)
    {
        foreach (var child in op.ChildOps())
        {
            if (child is ISwitchOperation) continue; // nested switch owns its own goto-case labels
            if (child is IBranchOperation { BranchKind: BranchKind.GoTo, Target: { } t }
                && (t.Name.StartsWith("case ") || t.Name == "default"))
                into.Add(t.Name);
            CollectGotoCaseTargets(child, into);
        }
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
            _lowering.Builder.EmitIf(caseCond,
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
                : _lowering.ExternCall(
                    UdonAbi.BooleanConditionalOr,
                    new List<CLeaf> { caseCond, clauseCond },
                    StorageTypes.Boolean);
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
                // CW19: a Nullable<T> scrutinee is a boxed SystemObject, and SystemObject.__op_Equality
                // is REFERENCE equality — the raw box never equals a freshly-boxed label const, so every
                // non-null constant case silently fell to default while the sibling pattern clause
                // (`x is 5`) answered true. Mirror the pattern clause's lowering: `case null:` is the
                // object null check; any other label gates on HasValue and compares on the underlying
                // type through the shared constant-equality lowering (int32-promoted small tags).
                if (EmitPolicy.IsNullableT(switchValueType, out var nblUnderlying))
                {
                    if (singleValue.Value.ConstantValue is { HasValue: true, Value: null })
                        return NullableAbi.IsNull(_lowering.Builder, origValueVal);
                    return NullableAbi.EmitNullGatedMatch(_lowering.Builder, origValueVal, false,
                        boxed => _lowering.EmitConstantEquality(boxed, nblUnderlying,
                            _lowering.VisitExpression(singleValue.Value), false));
                }

                var eqType = valueType;
                if (switchValueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
                    eqType = _lowering.GetStorageTypeName(named.EnumUnderlyingType);

                CLeaf caseValueVal;
                // Compile-time fold for enum / numeric constant case labels.
                // Avoids runtime SystemConvert.__ToInt32__SystemObject__SystemInt32 per case.
                if (singleValue.Value.ConstantValue.HasValue)
                {
                    caseValueVal = _lowering.Const(singleValue.Value.ConstantValue.Value, new StorageType(eqType));
                }
                else
                {
                    caseValueVal = _lowering.VisitExpression(singleValue.Value);
                    caseValueVal = _lowering.EmitEnumToUnderlying(caseValueVal, switchValueType);
                }

                var eqSig = UdonAbiKey.Method(
                    eqType, "op_Equality", new[] { eqType, eqType }, "SystemBoolean");

                var lhs = convertedValueVal;
                return _lowering.ExternCall(eqSig, new List<CLeaf> { lhs, caseValueVal }, StorageTypes.Boolean);
            }
            case IPatternCaseClauseOperation patternCase:
            {
                var patValue = origValueVal;
                var cond = _lowering.EmitPatternCheck(patValue, switchValueType, patternCase.Pattern);
                if (patternCase.Guard != null)
                {
                    // `when` short-circuits on the type check: a strict AND extern would evaluate the
                    // guard even when the pattern did not match, and the guard reads the pattern-bound
                    // variable (e.g. `d.id` where `d` is only validly bound on a match) → a null-bundle
                    // read → VmFault. Evaluate the guard ONLY inside the matched branch.
                    var guarded = _lowering.State.Builder.AllocScratch(StorageTypes.Boolean);
                    _lowering.EmitAssign(guarded, _lowering.Const(false, StorageTypes.Boolean));
                    _lowering.Builder.EmitIf(cond, _ => _lowering.EmitAssign(guarded, _lowering.VisitExpression(patternCase.Guard)));
                    cond = _lowering.SlotRef(guarded);
                }
                return cond;
            }
            default:
                return null;
        }
    }

    void EmitCaseBody(ISwitchCaseOperation caseSection)
    {
        // If a goto-case / goto-default jumps to one of this section's clauses, emit the matching (sanitized)
        // landing label before the body; StatementHandler.VisitBranch resolves the goto through the same map.
        // Only emitted when targeted, so a switch without goto-case is unchanged. Roslyn names the targets
        // "case <const>:" and "default".
        if (_lowering.State.ControlFlow.GotoCaseLabels.Count > 0 && _lowering.State.ControlFlow.GotoCaseLabels.Peek().Count > 0)
        {
            var map = _lowering.State.ControlFlow.GotoCaseLabels.Peek();
            foreach (var clause in caseSection.Clauses)
            {
                string roslynName = clause switch
                {
                    IDefaultCaseClauseOperation => "default",
                    ISingleValueCaseClauseOperation { Value: { ConstantValue: { HasValue: true, Value: { } cv } } }
                        => "case " + LoweringServices.ToInvariantString(cv) + ":",
                    _ => null,
                };
                if (roslynName != null && map.TryGetValue(roslynName, out var uasmLabel))
                    _lowering.Builder.EmitLabel(uasmLabel);
            }
        }
        foreach (var stmt in caseSection.Body)
            _lowering.VisitOperation(stmt);
    }
}
