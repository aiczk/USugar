using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `var (a, b) = ...` and `(a, b) = method()` tuple deconstruction.</summary>
public class DeconstructionAssignmentHandler : AssignmentHandlerBase, IOperationHandler
{
    public DeconstructionAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public OperationKind[] HandledKinds { get; } = new[] { OperationKind.DeconstructionAssignment };

    public void Handle(IOperation op)
    {
        if (op is IDeconstructionAssignmentOperation decon)
            VisitDeconstructionAssignment(decon);
        else
            throw new System.NotSupportedException(op.GetType().Name);
    }

    void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation op)
    {
        // Unwrap DeclarationExpression wrapping a tuple: var (a, b) = ...
        var target = op.Target;
        if (target is IDeclarationExpressionOperation declExpr)
            target = declExpr.Expression;

        if (target is not ITupleOperation targetTuple)
            throw new System.NotSupportedException(
                $"Deconstruction target must be a tuple, got {target.GetType().Name} ({target.Kind})");

        // `(a,b)=(b,a)` with existing-lvalue targets wraps the RHS tuple in a tuple→tuple IConversionOperation,
        // whereas `var (a,b)=(1,2)` is a bare tuple. Unwrap only a same-shape, same-element-type conversion (the
        // swap/rotate/Fibonacci idiom); a genuinely narrowing/widening element conversion is left to the method
        // path below rather than risk a silently mistyped store.
        var value = op.Value;
        if (value is IConversionOperation vconv && vconv.Operand is ITupleOperation innerTuple
            && innerTuple.Elements.Length == targetTuple.Elements.Length
            && Enumerable.Range(0, innerTuple.Elements.Length).All(
                i => GetStorageTypeName(innerTuple.Elements[i].Type) == GetStorageTypeName(targetTuple.Elements[i].Type)))
            value = innerTuple;

        if (value is ITupleOperation valueTuple)
        {
            // (a, b) = (expr1, expr2): C# evaluates the ENTIRE RHS tuple before assigning ANY target. The
            // two-loop split is what enforces that: every RHS element is evaluated (loop 1) before any store
            // (loop 2), so a later element reading an already-overwritten target (swap (a,b)=(b,a), rotate,
            // Fibonacci step) still sees the pre-store value. Under ANF each VisitExpression result is already
            // a single-assignment scratch leaf — pinned at its phase-1 read point and never clobbered by a
            // phase-2 EmitStoreField (which targets a named heap id, not the scratch) — so no extra temp is
            // needed. Aggregate elements are deep-cloned by clone-on-read inside VisitExpression.
            // Wave-9 round-6 [X2]/[X4]/[X5]: every target's receiver/index legs evaluate left-to-right
            // BEFORE the RHS (C# order); the deferred stores below consume the cached legs.
            var prepared = PrepareDeconstructionTargets(targetTuple);
            // Wave-11 round-11 [Z2]: Roslyn's deconstruction lowering does NOT evaluate tuple-literal
            // components in plain textual order when a NESTED deconstruction target is present.
            // Components paired with non-deconstructed LEAF targets evaluate first (the lowering's
            // `init` effects), then components feeding a nested deconstruction (its `deconstructions`
            // effects) — textual order within each group, recursing through nested tuple LITERALS
            // without leaving the init group (DiffFuzz-proven: ((a, t), u) = (MPair(), V3()) runs V3
            // BEFORE MPair, ref trace=32; both-nested and all-literal shapes stay textual). Both
            // snapshot walks complete before any store, so swap/rotate value safety is unchanged, and
            // a literal without nested targets takes walk 1 only — the old textual loop exactly.
            var snapshots = new Dictionary<IOperation, CLeaf>();
            SnapshotLeafPairedComponents(targetTuple, valueTuple, snapshots);
            SnapshotNestedPairedComponents(targetTuple, valueTuple, snapshots);
            AssignPairedComponents(targetTuple, valueTuple, snapshots, prepared);
        }
        else
        {
            // Peel conversions to find the underlying RHS.
            var callValue = op.Value;
            while (callValue is IConversionOperation conv2) callValue = conv2.Operand;

            // Wave-9 round-6 [X3]/[X4]: target legs evaluate BEFORE the RHS on the non-tuple-literal
            // branches too — C# evaluates each target's component expressions first, then the RHS.
            var prepared = PrepareDeconstructionTargets(targetTuple);

            // Call/unpack RHS components have no per-component operation to classify — report each
            // cross-program delegate target with an Unknown value (rejects on the same conservative
            // body-mentions rule as an unclassified field copy).
            GuardDeconstructionDelegateTargets(targetTuple);

            // Tuple-return delegate invocation (`var (a,b) = f(...)` where f is Action/Func-shaped):
            // route through the SAME unified dispatch every other delegate call site uses (guard
            // ladder, self/cross routing) — VisitExpression already returns the dispatched conv-ret,
            // which for a tuple-return delegate IS the packed SystemObjectArray aggregate (Stage 1.75
            // design 2026-07-04 §1) — deconstruct it like any other aggregate value. A delegate's
            // ContainingType is a BCL Func/Action, never planned by LayoutPlanner, so this must be
            // checked BEFORE the same-class/cross-behaviour method-call arms below.
            if (callValue is IInvocationOperation dlgInvocation
                && dlgInvocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
            {
                var dlgResult = VisitExpression(op.Value);
                var dlgSnaps = new List<CLeaf>(targetTuple.Elements.Length);
                for (int i = 0; i < targetTuple.Elements.Length; i++)
                {
                    var raw = AggregateAbi.ReadSlot(_builder, dlgResult, i, StorageTypes.Object);
                    dlgSnaps.Add(AggregateAbi.CloneIfAggregate(_builder, raw,
                        ResolveType(targetTuple.Elements[i].Type), _ctx.Aggregates.GetLayout));
                }
                for (int i = 0; i < targetTuple.Elements.Length; i++)
                    AssignToLValue(targetTuple.Elements[i], dlgSnaps[i], prepared);
                return;
            }

            // (a, b) = tup where the RHS is a tuple/struct-typed VALUE expression (local/parameter/field/array
            // element), emulated as object[]. Read each element from the backing array; snapshot ALL reads
            // before any store (value semantics + swap safety), deep-cloning aggregate elements so a later
            // mutation of a target does not alias the source tuple.
            if (callValue is not IInvocationOperation
                && callValue.Type is INamedTypeSymbol valAggType && TypeClassifier.IsAggregateValue(valAggType))
            {
                var arrVal = LoadInstanceRaw(callValue);
                var snaps = new List<CLeaf>(targetTuple.Elements.Length);
                for (int i = 0; i < targetTuple.Elements.Length; i++)
                {
                    var raw = AggregateAbi.ReadSlot(_builder, arrVal, i, StorageTypes.Object);
                    snaps.Add(AggregateAbi.CloneIfAggregate(_builder, raw,
                        ResolveType(targetTuple.Elements[i].Type), _ctx.Aggregates.GetLayout));
                }
                for (int i = 0; i < targetTuple.Elements.Length; i++)
                    AssignToLValue(targetTuple.Elements[i], snaps[i], prepared);
                return;
            }

            // User-defined Deconstruct(out ...): Roslyn represents the RHS as the original value rather
            // than an invocation node. Resolve the selected Deconstruct symbol from the assignment syntax,
            // invoke it through the normal user-member ABI, then copy its out parameter fields into the
            // already-prepared targets. This intentionally handles the flat form here; nested forms are
            // recursively described by Conversion elements and remain a loud reject until they can preserve
            // the compiler-defined multi-stage evaluation order.
            if (op.Syntax is AssignmentExpressionSyntax assignmentSyntax)
            {
                var model = _compilation.GetSemanticModel(assignmentSyntax.SyntaxTree);
                var deconstruct = model.GetDeconstructionInfo(assignmentSyntax).Method;
                if (deconstruct != null)
                {
                    if (targetTuple.Elements.Any(e => e is ITupleOperation
                        || e is IDeclarationExpressionOperation { Expression: ITupleOperation }))
                        throw new System.NotSupportedException(
                            "Nested user-defined Deconstruct targets are not supported yet.");
                    var method = ResolveStructMember(SubstituteMethodTypeArgs(deconstruct));
                    if (method.Parameters.Length != targetTuple.Elements.Length
                        || method.Parameters.Any(p => p.RefKind != RefKind.Out))
                        throw new System.NotSupportedException(
                            $"Deconstruct method '{method.ToDisplayString()}' has an unsupported signature.");

                    var receiver = VisitExpression(op.Value);
                    var args = new List<CLeaf> { receiver };
                    foreach (var parameter in method.Parameters)
                        args.Add(SlotRef(_builder.AllocScratch(GetStorageType(parameter.Type))));
                    EmitExprStmt(EmitCallToMethod(method, args, op.Syntax));

                    if (!_methodParamVarIds.TryGetValue(method, out var paramIds))
                        throw new System.InvalidOperationException(
                            $"Deconstruct method '{method.ToDisplayString()}' was not registered.");
                    for (int i = 0; i < targetTuple.Elements.Length; i++)
                    {
                        var valueOut = LoadField(paramIds[i], GetStorageType(method.Parameters[i].Type));
                        AssignToLValue(targetTuple.Elements[i], valueOut, prepared);
                    }
                    return;
                }
            }

            if (callValue is not IInvocationOperation invocation)
                throw new System.NotSupportedException(
                    $"Unsupported tuple deconstruction value: {op.Value.GetType().Name}");

            // Wave-9 round-7 [Y5]: a same-class generic callee inside a generic body carries OPEN
            // type args (`var (a,b) = P2<T>(x)` with the enclosing T in scope) — resolve through
            // the enclosing specialization's type-param map so the return-slot lookup below sees
            // the same monomorphized symbol the invocation emission registers. Closed/non-generic
            // callees pass through unchanged (SubstituteMethodTypeArgs is identity for them).
            var callTarget = SubstituteMethodTypeArgs(invocation.TargetMethod);
            var isCrossBehaviour = ExternResolver.IsUdonSharpBehaviour(callTarget.ContainingType)
                && invocation.Instance is not IInstanceReferenceOperation
                && callTarget.ContainingType.Name != "UdonSharpBehaviour";
            var isInterface = callTarget.ContainingType.TypeKind == TypeKind.Interface;

            if (isCrossBehaviour || isInterface)
            {
                // Cross-behaviour or interface tuple call:
                // Emit the protocol manually and read back each element via GetProgramVariable
                EmitCrossBehaviourTupleDeconstruction(invocation, callTarget, targetTuple, isCrossBehaviour, prepared);
            }
            else
            {
                // Same-class call: invoke method, then read from return slot
                var callExpr = VisitExpression(op.Value);
                if (callExpr != null)
                    EmitExprStmt(callExpr);

                ReturnSlot[] callReturns = null;
                if (_methodReturns.TryGetValue(callTarget, out var localReturns))
                    callReturns = localReturns;
                else if (callTarget.ReturnType.IsTupleType || TypeClassifier.IsAggregateValue(callTarget.ReturnType))
                    callReturns = GetCalleeReturns(callTarget);

                if (callReturns == null || callReturns.Length == 0)
                    throw new System.NotSupportedException(
                        $"Cannot deconstruct return of '{callTarget.Name}': no return layout found.");

                // Single SystemObjectArray return slot: load the array, then index into it
                if (callReturns.Length == 1 && callReturns[0].StorageType.Name == AggregateAbi.ArrayType)
                {
                    var arrExpr = LoadField(callReturns[0].Id, new StorageType(AggregateAbi.ArrayType));
                    for (int i = 0; i < targetTuple.Elements.Length; i++)
                    {
                        // CW29: same clone rule as the sibling arms — this arm relied on every
                        // return-site materialization being fresh, an invariant enforced nowhere.
                        var elemVal = AggregateAbi.CloneIfAggregate(_builder,
                            AggregateAbi.ReadSlot(_builder, arrExpr, i, StorageTypes.Object),
                            ResolveType(targetTuple.Elements[i].Type), _ctx.Aggregates.GetLayout);
                        AssignToLValue(targetTuple.Elements[i], elemVal, prepared);
                    }
                }
                else
                {
                    throw new System.NotSupportedException(
                        $"Cannot deconstruct return of '{callTarget.Name}': tuple returns must use a single "
                        + "SystemObjectArray return slot.");
                }
            }
        }
    }

    // ── Wave-11 round-11 [Z2]: tuple-literal component evaluation in Roslyn's lowering order ──

    static IOperation UnwrapDeclaration(IOperation element)
        => element is IDeclarationExpressionOperation de ? de.Expression : element;

    /// <summary>The nested tuple LITERAL a component pairs with — the component itself, or its
    /// operand under the same same-shape, same-element-type tuple conversion the top-level arm
    /// peels — or null when the component is a non-literal expression (call/local/field/…): those
    /// evaluate whole in the deconstructions group and element-read at assign time.</summary>
    ITupleOperation MatchNestedLiteral(ITupleOperation nestedTarget, IOperation component)
    {
        var v = component;
        if (v is IConversionOperation conv && conv.Operand is ITupleOperation inner
            && inner.Elements.Length == nestedTarget.Elements.Length
            && Enumerable.Range(0, inner.Elements.Length).All(
                i => GetStorageTypeName(inner.Elements[i].Type) == GetStorageTypeName(nestedTarget.Elements[i].Type)))
            v = inner;
        return v is ITupleOperation lit && lit.Elements.Length == nestedTarget.Elements.Length ? lit : null;
    }

    /// <summary>Walk 1 (Roslyn `init` effects): evaluate every component paired with a
    /// non-deconstructed LEAF target, textual order, recursing through nested tuple literals.</summary>
    void SnapshotLeafPairedComponents(ITupleOperation targets, ITupleOperation values,
        Dictionary<IOperation, CLeaf> snapshots)
    {
        for (int i = 0; i < targets.Elements.Length; i++)
        {
            var component = values.Elements[i];
            if (UnwrapDeclaration(targets.Elements[i]) is ITupleOperation nestedTarget)
            {
                if (MatchNestedLiteral(nestedTarget, component) is { } nestedLiteral)
                    SnapshotLeafPairedComponents(nestedTarget, nestedLiteral, snapshots);
                // non-literal nested components evaluate in walk 2 (the deconstructions group)
            }
            else
                snapshots[component] = VisitExpression(component);
        }
    }

    /// <summary>Walk 2 (Roslyn `deconstructions` effects): evaluate every NON-literal component
    /// paired with a nested deconstruction target, textual order, recursing through literals.</summary>
    void SnapshotNestedPairedComponents(ITupleOperation targets, ITupleOperation values,
        Dictionary<IOperation, CLeaf> snapshots)
    {
        for (int i = 0; i < targets.Elements.Length; i++)
        {
            var component = values.Elements[i];
            if (UnwrapDeclaration(targets.Elements[i]) is not ITupleOperation nestedTarget) continue;
            if (MatchNestedLiteral(nestedTarget, component) is { } nestedLiteral)
                SnapshotNestedPairedComponents(nestedTarget, nestedLiteral, snapshots);
            else
                snapshots[component] = VisitExpression(component);
        }
    }

    /// <summary>Stores, textual leaf order (C# assigns left-to-right after the whole RHS). A nested
    /// target paired with a nested LITERAL assigns each leaf directly from its snapshot (the leaves
    /// were evaluated individually — there is no intermediate tuple aggregate to element-read); a
    /// nested target paired with a non-literal component keeps the AssignToLValue element-read path.</summary>
    void GuardDeconstructionDelegateTargets(ITupleOperation targets)
    {
        foreach (var element in targets.Elements)
        {
            var target = UnwrapDeclaration(element);
            if (target is ITupleOperation nested) GuardDeconstructionDelegateTargets(nested);
            else RejectUnsafeCrossProgramDelegateWrite(target, default);
        }
    }

    void AssignPairedComponents(ITupleOperation targets, ITupleOperation values,
        Dictionary<IOperation, CLeaf> snapshots, Dictionary<IOperation, LValuePlan> prepared)
    {
        for (int i = 0; i < targets.Elements.Length; i++)
        {
            var component = values.Elements[i];
            if (UnwrapDeclaration(targets.Elements[i]) is ITupleOperation nestedTarget
                && MatchNestedLiteral(nestedTarget, component) is { } nestedLiteral)
                AssignPairedComponents(nestedTarget, nestedLiteral, snapshots, prepared);
            else
            {
                RejectUnsafeCrossProgramDelegateWrite(
                    UnwrapDeclaration(targets.Elements[i]), _ctx.Boundary.ClassifyValue(component));
                AssignToLValue(targets.Elements[i], snapshots[component], prepared);
            }
        }
    }

    /// <summary>
    /// Handle tuple deconstruction from a cross-behaviour or interface method call.
    /// Emits SetProgramVariable for params, SendCustomEvent, then GetProgramVariable for each element.
    /// Target legs arrive pre-evaluated in <paramref name="prepared"/> (wave-9 round-6 [X3]/[X4]).
    /// </summary>
    void EmitCrossBehaviourTupleDeconstruction(IInvocationOperation invocation, IMethodSymbol callTarget,
        ITupleOperation targetTuple, bool isCrossBehaviour,
        Dictionary<IOperation, LValuePlan> prepared)
    {
        // Get layout for the target method
        ReturnSlot[] callReturns;
        string exportName;
        string[] paramIds;

        if (isCrossBehaviour)
        {
            var (exp, pids, _) = GetCalleeLayout(callTarget);
            exportName = exp;
            paramIds = pids;
            callReturns = GetCalleeReturns(callTarget);
        }
        else
        {
            // Interface call
            var ifaceType = callTarget.ContainingType as INamedTypeSymbol;
            var ifaceLayout = _planner.GetLayout(ifaceType);
            if (!ifaceLayout.Methods.TryGetValue(callTarget, out var ifaceMl))
                throw new System.InvalidOperationException(
                    $"Cannot resolve interface method layout for '{callTarget.ContainingType?.Name}.{callTarget.Name}'.");
            exportName = ifaceMl.ExportName;
            paramIds = ifaceMl.ParamIds.ToArray();
            callReturns = ifaceMl.Returns.ToArray();
        }

        if (callReturns == null || callReturns.Length == 0)
            throw new System.NotSupportedException(
                $"Cannot deconstruct tuple return of cross-behaviour method '{callTarget.Name}': no tuple return layout found.");

        var instanceVal = VisitExpression(invocation.Instance);

        // SetProgramVariable for each param — by parameter ordinal, textual evaluation order
        // (wave-9 round-3 [W4]: named/reordered args used to bind positionally on this path too).
        foreach (var (paramId, argVal) in CrossCallArgPairs(invocation.Arguments, paramIds))
        {
            var nameConst = Const(paramId, StorageTypes.String);
            EmitExternVoid(
                ExternResolver.EventReceiverSetProgramVariable,
                new List<CLeaf> { instanceVal, nameConst, argVal });
        }

        // SendCustomEvent
        var eventConst = Const(exportName, StorageTypes.String);
        EmitExternVoid(
            ExternResolver.EventReceiverSendCustomEvent,
            new List<CLeaf> { instanceVal, eventConst });

        // GetProgramVariable for return value and deconstruct
        if (callReturns.Length == 1 && callReturns[0].StorageType.Name == AggregateAbi.ArrayType)
        {
            // Single SystemObjectArray return: get the array, then index into it
            var retNameConst = Const(callReturns[0].Id, StorageTypes.String);
            var arrVal = ExternCall(
                ExternResolver.EventReceiverGetProgramVariable,
                new List<CLeaf> { instanceVal, retNameConst },
                new StorageType(AggregateAbi.ArrayType));
            for (int i = 0; i < targetTuple.Elements.Length; i++)
            {
                // CW29: same clone rule as the sibling arms (see the same-class call arm).
                var elemVal = AggregateAbi.CloneIfAggregate(_builder,
                    AggregateAbi.ReadSlot(_builder, arrVal, i, StorageTypes.Object),
                    ResolveType(targetTuple.Elements[i].Type), _ctx.Aggregates.GetLayout);
                AssignToLValue(targetTuple.Elements[i], elemVal, prepared);
            }
        }
        else
        {
            throw new System.NotSupportedException(
                $"Cannot deconstruct cross-behaviour return of '{callTarget.Name}': tuple returns must use "
                + "a single SystemObjectArray return slot.");
        }
    }
}
