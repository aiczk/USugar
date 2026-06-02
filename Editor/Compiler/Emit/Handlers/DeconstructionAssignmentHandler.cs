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
            // (a, b) = (expr1, expr2): C# evaluates the ENTIRE RHS tuple before assigning ANY target. Snapshot
            // every RHS element into a fresh temp first (eager evaluation against pre-store values), THEN assign;
            // otherwise a later element that reads an already-overwritten target (swap (a,b)=(b,a), rotate,
            // Fibonacci step) reads the clobbered value. Aggregate elements are deep-cloned by clone-on-read.
            var snapshots = new List<CValue>(targetTuple.Elements.Length);
            for (int i = 0; i < targetTuple.Elements.Length; i++)
            {
                var elemVal = VisitExpression(valueTuple.Elements[i]);
                var tmp = _ctx.AllocTemp(GetUdonType(targetTuple.Elements[i].Type));
                EmitAssign(tmp, elemVal);
                snapshots.Add(SlotRef(tmp));
            }
            for (int i = 0; i < targetTuple.Elements.Length; i++)
                AssignToLValue(targetTuple.Elements[i], snapshots[i]);
        }
        else
        {
            // Method call returning tuple: call the method, then read back per-element return fields
            var callValue = op.Value;
            while (callValue is IConversionOperation conv2) callValue = conv2.Operand;

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
                            new List<CValue> { arrExpr, Const(i, "SystemInt32") }, "SystemObject");
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
    /// Handle tuple deconstruction from a cross-behaviour or interface method call.
    /// Emits SetProgramVariable for params, SendCustomEvent, then GetProgramVariable for each element.
    /// </summary>
    void EmitCrossBehaviourTupleDeconstruction(IInvocationOperation invocation, IMethodSymbol callTarget,
        ITupleOperation targetTuple, bool isCrossBehaviour)
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

        // SetProgramVariable for each param
        for (int i = 0; i < invocation.Arguments.Length; i++)
        {
            var argVal = VisitExpression(invocation.Arguments[i].Value);
            var nameConst = Const(paramIds[i], "SystemString");
            EmitExternVoid(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                new List<CValue> { instanceVal, nameConst, argVal });
        }

        // SendCustomEvent
        var eventConst = Const(exportName, "SystemString");
        EmitExternVoid(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<CValue> { instanceVal, eventConst });

        // GetProgramVariable for return value and deconstruct
        if (callReturns.Length == 1 && callReturns[0].UdonType == "SystemObjectArray")
        {
            // Single SystemObjectArray return: get the array, then index into it
            var retNameConst = Const(callReturns[0].Id, "SystemString");
            var arrVal = ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<CValue> { instanceVal, retNameConst },
                "SystemObjectArray");
            for (int i = 0; i < targetTuple.Elements.Length; i++)
            {
                var elemVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<CValue> { arrVal, Const(i, "SystemInt32") }, "SystemObject");
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
                    new List<CValue> { instanceVal, retNameConst },
                    callReturns[i].UdonType);
                AssignToLValue(targetTuple.Elements[i], elemVal);
            }
        }
    }
}
