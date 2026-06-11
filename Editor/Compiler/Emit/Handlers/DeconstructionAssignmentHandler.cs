using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `var (a, b) = ...` and `(a, b) = method()` tuple deconstruction.</summary>
public class DeconstructionAssignmentHandler : AssignmentHandlerBase, IOperationHandler
{
    public DeconstructionAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation op) => op is IDeconstructionAssignmentOperation;

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
                i => GetUdonType(innerTuple.Elements[i].Type) == GetUdonType(targetTuple.Elements[i].Type)))
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
            // §2.8: a lambda element deconstructed into a delegate-field lvalue is a long-lived store
            // (record for the aliasing detector); escaping element targets are guarded like plain stores.
            for (int i = 0; i < targetTuple.Elements.Length; i++)
            {
                RecordLongLivedLambdaStore(targetTuple.Elements[i], valueTuple.Elements[i]);
                GuardCaptureEscapeStore(targetTuple.Elements[i], valueTuple.Elements[i]);
            }
            var snapshots = new List<CLeaf>(targetTuple.Elements.Length);
            for (int i = 0; i < targetTuple.Elements.Length; i++)
                snapshots.Add(VisitExpression(valueTuple.Elements[i]));
            for (int i = 0; i < targetTuple.Elements.Length; i++)
                AssignToLValue(targetTuple.Elements[i], snapshots[i]);
        }
        else
        {
            // Peel conversions to find the underlying RHS.
            var callValue = op.Value;
            while (callValue is IConversionOperation conv2) callValue = conv2.Operand;

            // §2.8 round-5 [N3]: every non-tuple-literal branch below assigns deconstructed
            // elements via AssignToLValue with no escape guard, so a capture-carrying source
            // shipped bundles into non-local lvalues silently (VM-proven: `t.Item1 = () => v;
            // (fs[i], x) = t;` in a loop). Guard ALL branches uniformly before any emission.
            GuardCaptureEscapeDeconstructionSource(callValue, targetTuple);

            // (a, b) = tup where the RHS is a tuple/struct-typed VALUE expression (local/parameter/field/array
            // element), emulated as object[]. Read each element from the backing array; snapshot ALL reads
            // before any store (value semantics + swap safety), deep-cloning aggregate elements so a later
            // mutation of a target does not alias the source tuple.
            if (callValue is not IInvocationOperation
                && callValue.Type is INamedTypeSymbol valAggType && EmitContext.IsAggregateType(valAggType))
            {
                var arrVal = LoadInstanceRaw(callValue);
                var snaps = new List<CLeaf>(targetTuple.Elements.Length);
                for (int i = 0; i < targetTuple.Elements.Length; i++)
                {
                    var raw = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                        new List<CLeaf> { arrVal, Const(i, "SystemInt32") }, "SystemObject");
                    snaps.Add(targetTuple.Elements[i].Type is INamedTypeSymbol et && EmitContext.IsAggregateType(et)
                        ? EmitDeepCloneAggregate(raw, et) : raw);
                }
                for (int i = 0; i < targetTuple.Elements.Length; i++)
                    AssignToLValue(targetTuple.Elements[i], snaps[i]);
                return;
            }

            if (callValue is not IInvocationOperation invocation)
                throw new System.NotSupportedException(
                    $"Unsupported tuple deconstruction value: {op.Value.GetType().Name}");

            var callTarget = invocation.TargetMethod;
            var isCrossBehaviour = ExternResolver.IsUdonSharpBehaviour(callTarget.ContainingType)
                && invocation.Instance is not IInstanceReferenceOperation
                && callTarget.ContainingType.Name != "UdonSharpBehaviour";
            var isInterface = callTarget.ContainingType.TypeKind == TypeKind.Interface;

            if (isCrossBehaviour || isInterface)
            {
                // Cross-behaviour or interface tuple call:
                // Emit the protocol manually and read back each element via GetProgramVariable
                EmitCrossBehaviourTupleDeconstruction(invocation, callTarget, targetTuple, isCrossBehaviour);
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
                else if (callTarget.ReturnType.IsTupleType || EmitContext.IsAggregateType(callTarget.ReturnType))
                    callReturns = GetCalleeReturns(callTarget);

                if (callReturns == null || callReturns.Length == 0)
                    throw new System.NotSupportedException(
                        $"Cannot deconstruct return of '{callTarget.Name}': no return layout found.");

                // Single SystemObjectArray return slot: load the array, then index into it
                if (callReturns.Length == 1 && callReturns[0].UdonType == "SystemObjectArray")
                {
                    var arrExpr = LoadField(callReturns[0].Id, "SystemObjectArray");
                    for (int i = 0; i < targetTuple.Elements.Length; i++)
                    {
                        var elemVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                            new List<CLeaf> { arrExpr, Const(i, "SystemInt32") }, "SystemObject");
                        AssignToLValue(targetTuple.Elements[i], elemVal);
                    }
                }
                else
                {
                    // Legacy per-element return slots (non-aggregate types)
                    for (int i = 0; i < targetTuple.Elements.Length && i < callReturns.Length; i++)
                    {
                        var elemVal = LoadField(callReturns[i].Id, callReturns[i].UdonType);
                        AssignToLValue(targetTuple.Elements[i], elemVal);
                    }
                }
            }
        }
    }

    /// <summary>
    /// §2.8 round-5 [N3] escape guard for the non-tuple-literal deconstruction branches, mirroring
    /// GuardCaptureEscapeStore element-wise. A source that carries capture — a tainted container
    /// local (`t.Item1 = () => v` taints t), a laundering member read (capture-receiving /
    /// param-rooted / foreign / interface member), a tainted invocation result (identity-callee
    /// laundering), or a bare delegate-capable PARAM (the callee is blind to what the caller
    /// packed; mirrors the `fs[k] = t.Item1` member-read taint) — must not deconstruct into a
    /// non-local lvalue: each delegate-capable element either taints its LOCAL target (F4) or
    /// rejects loudly. Non-capable elements (int legs) and untainted sources (method-group-only
    /// aggregates, guarded same-class returns) stay legal.
    /// </summary>
    void GuardCaptureEscapeDeconstructionSource(IOperation source, ITupleOperation targetTuple)
    {
        bool tainted = IsCaptureTaintedRead(source) || IsLaunderingMemberRead(source)
            || IsTaintedDelegateInvocationResult(source)
            || (source is IParameterReferenceOperation pr && IsDelegateCapableType(pr.Parameter.Type));
        if (!tainted) return;
        foreach (var element in targetTuple.Elements)
        {
            var target = element is IDeclarationExpressionOperation de ? de.Expression : element;
            if (target is IDiscardOperation) continue;
            if (!IsDelegateCapableType(target.Type)) continue;
            if (target is ILocalReferenceOperation localTarget)
            {
                _ctx.CapturingLambdaLocals.Add(localTarget.Local);
                continue;
            }
            throw new System.NotSupportedException(CaptureEscapeError);
        }
    }

    /// <summary>
    /// Handle tuple deconstruction from a cross-behaviour or interface method call.
    /// Emits SetProgramVariable for params, SendCustomEvent, then GetProgramVariable for each element.
    /// </summary>
    void EmitCrossBehaviourTupleDeconstruction(IInvocationOperation invocation, IMethodSymbol callTarget,
        ITupleOperation targetTuple, bool isCrossBehaviour)
    {
        // §2.8 round-2: this manual emission path bypasses InvocationHandler, so the erasing-typed
        // argument guard must run here too.
        GuardCaptureEscapeArguments(invocation.Arguments);

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
            var nameConst = Const(paramId, "SystemString");
            EmitExternVoid(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<CLeaf> { instanceVal, nameConst, argVal });
        }

        // SendCustomEvent
        var eventConst = Const(exportName, "SystemString");
        EmitExternVoid(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<CLeaf> { instanceVal, eventConst });

        // GetProgramVariable for return value and deconstruct
        if (callReturns.Length == 1 && callReturns[0].UdonType == "SystemObjectArray")
        {
            // Single SystemObjectArray return: get the array, then index into it
            var retNameConst = Const(callReturns[0].Id, "SystemString");
            var arrVal = ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<CLeaf> { instanceVal, retNameConst },
                "SystemObjectArray");
            for (int i = 0; i < targetTuple.Elements.Length; i++)
            {
                var elemVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<CLeaf> { arrVal, Const(i, "SystemInt32") }, "SystemObject");
                AssignToLValue(targetTuple.Elements[i], elemVal);
            }
        }
        else
        {
            // Legacy per-element return slots
            for (int i = 0; i < targetTuple.Elements.Length && i < callReturns.Length; i++)
            {
                var retNameConst = Const(callReturns[i].Id, "SystemString");
                var elemVal = ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<CLeaf> { instanceVal, retNameConst },
                    callReturns[i].UdonType);
                AssignToLValue(targetTuple.Elements[i], elemVal);
            }
        }
    }
}
