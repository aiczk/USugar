using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class AssignmentHandler : HandlerBase, IOperationHandler, IExpressionHandler
{
    public AssignmentHandler(EmitContext ctx) : base(ctx) { }

    // ── IOperationHandler (void-returning operations) ──

    bool IOperationHandler.CanHandle(IOperation op) => op is IDeconstructionAssignmentOperation;

    void IOperationHandler.Handle(IOperation op)
    {
        switch (op)
        {
            case IDeconstructionAssignmentOperation decon: VisitDeconstructionAssignment(decon); break;
            default: throw new System.NotSupportedException(op.GetType().Name);
        }
    }

    // ── IExpressionHandler (HExpr-returning expressions) ──

    bool IExpressionHandler.CanHandle(IOperation op)
        => op is ISimpleAssignmentOperation
            or ICompoundAssignmentOperation
            or IIncrementOrDecrementOperation;

    HExpr IExpressionHandler.Handle(IOperation op) => op switch
    {
        ISimpleAssignmentOperation assign => VisitAssignment(assign),
        ICompoundAssignmentOperation compound => VisitCompoundAssignment(compound),
        IIncrementOrDecrementOperation incDec => VisitIncrementDecrement(incDec),
        _ => throw new System.NotSupportedException(op.GetType().Name),
    };

    // ── VisitDeconstructionAssignment ──

    void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation op)
    {
        // Unwrap DeclarationExpression wrapping a tuple: var (a, b) = ...
        var target = op.Target;
        if (target is IDeclarationExpressionOperation declExpr)
            target = declExpr.Expression;

        if (target is not ITupleOperation targetTuple)
            throw new System.NotSupportedException(
                $"Deconstruction target must be a tuple, got {target.GetType().Name} ({target.Kind})");

        if (op.Value is ITupleOperation valueTuple)
        {
            // (a, b) = (expr1, expr2) → element-wise assignment
            for (int i = 0; i < targetTuple.Elements.Length; i++)
            {
                var valueVal = VisitExpression(valueTuple.Elements[i]);
                AssignToTarget(targetTuple.Elements[i], valueVal);
            }
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
                // Same-class call: invoke method, then read from per-element return fields
                var callExpr = VisitExpression(op.Value);
                if (callExpr != null)
                    EmitExprStmt(callExpr);

                ReturnSlot[] callReturns = null;
                if (_methodReturns.TryGetValue(callTarget, out var localReturns))
                    callReturns = localReturns;
                else if (callTarget.ReturnType.IsTupleType)
                    callReturns = GetCalleeReturns(callTarget);

                if (callReturns == null || callReturns.Length == 0)
                    throw new System.NotSupportedException(
                        $"Cannot deconstruct return of '{callTarget.Name}': no return layout found.");

                for (int i = 0; i < targetTuple.Elements.Length && i < callReturns.Length; i++)
                {
                    var elemVal = LoadField(callReturns[i].Id, callReturns[i].UdonType);
                    AssignToTarget(targetTuple.Elements[i], elemVal);
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
                new List<HExpr> { instanceVal, nameConst, argVal });
        }

        // SendCustomEvent
        var eventConst = Const(exportName, "SystemString");
        EmitExternVoid(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            new List<HExpr> { instanceVal, eventConst });

        // GetProgramVariable for each tuple element and assign to target
        for (int i = 0; i < targetTuple.Elements.Length && i < callReturns.Length; i++)
        {
            var retNameConst = Const(callReturns[i].Id, "SystemString");
            var elemVal = ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<HExpr> { instanceVal, retNameConst },
                callReturns[i].UdonType);
            AssignToTarget(targetTuple.Elements[i], elemVal);
        }
    }

    void AssignToTarget(IOperation target, HExpr valueVal)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                // var x in deconstruction — declares a new local
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _ctx.DeclareLocal(localRef.Local.Name, udonType);
                    _localVarIds[localRef.Local] = localId;
                    EmitStoreField(localId, valueVal);
                }
                break;

            case ILocalReferenceOperation existingLocal:
                if (!_localVarIds.TryGetValue(existingLocal.Local, out var existingId))
                {
                    // New local from tuple deconstruction (var (a, b) pattern)
                    var udonType = GetUdonType(existingLocal.Type);
                    existingId = _ctx.DeclareLocal(existingLocal.Local.Name, udonType);
                    _localVarIds[existingLocal.Local] = existingId;
                }
                EmitStoreField(existingId, valueVal);
                break;

            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                EmitStoreField(fieldRef.Field.Name, valueVal);
                break;

            case IParameterReferenceOperation paramRef:
                EmitStoreField(GetParamVarId(paramRef.Parameter), valueVal);
                break;

            case IDiscardOperation:
                break; // _ = expr → discard

            default:
                throw new System.NotSupportedException(
                    $"Unsupported deconstruction target element: {target.GetType().Name}");
        }
    }

    // ── VisitAssignment ──

    HExpr VisitAssignment(ISimpleAssignmentOperation assign)
    {
        // Tuple local SROA: reassignment to an expanded tuple variable
        if (assign.Target is ILocalReferenceOperation targetLocal
            && targetLocal.Type.IsTupleType
            && targetLocal.Type is INamedTypeSymbol targetTupleType
            && _tupleLocalExpansion.TryGetValue(targetLocal.Local, out var expansion))
        {
            var value = UnwrapConversions(assign.Value);

            if (value is ITupleOperation tupleLit)
            {
                for (int i = 0; i < tupleLit.Elements.Length && i < expansion.Length; i++)
                    EmitStoreField(expansion[i].Id, VisitExpression(tupleLit.Elements[i]));
            }
            else if (value is IInvocationOperation invocation)
            {
                var callExpr = VisitExpression(assign.Value);
                if (callExpr != null) EmitExprStmt(callExpr);
                var callReturns = GetCalleeReturns(invocation.TargetMethod);
                for (int i = 0; i < expansion.Length && i < callReturns.Length; i++)
                    EmitStoreField(expansion[i].Id, LoadField(callReturns[i].Id, callReturns[i].UdonType));
            }
            else if (value is ILocalReferenceOperation otherLocal
                     && _tupleLocalExpansion.TryGetValue(otherLocal.Local, out var otherExpansion))
            {
                for (int i = 0; i < expansion.Length && i < otherExpansion.Length; i++)
                    EmitStoreField(expansion[i].Id, LoadField(otherExpansion[i].Id, otherExpansion[i].UdonType));
            }
            else
            {
                throw new System.NotSupportedException(
                    $"Cannot assign {value.GetType().Name} to tuple local variable '{targetLocal.Local.Name}'.");
            }
            return LoadField(expansion[0].Id, expansion[0].UdonType);
        }

        if (assign.Target is IArrayElementReferenceOperation arrayElem)
        {
            var arrayVal = VisitExpression(arrayElem.ArrayReference);
            var indexVal = VisitExpression(arrayElem.Indices[0]);
            var srcVal = VisitExpression(assign.Value);
            var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
            var arrayType = GetArrayType(arrSymbol);
            var elementType = GetArrayElemType(arrSymbol);
            EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<HExpr> { arrayVal, indexVal, srcVal });
            return srcVal;
        }

        // Self-delegate field assignment: _callback = MyMethod / _callback = () => { } / _callback = null
        if (assign.Target is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } selfDlg
            && selfDlg.Field.Type is INamedTypeSymbol selfDlgType
            && selfDlgType.DelegateInvokeMethod != null
            && _delegateFields.Contains(selfDlg.Field.Name))
        {
            var fieldName = selfDlg.Field.Name;

            // null assignment
            if (assign.Value.ConstantValue is { HasValue: true, Value: null })
            {
                EmitStoreField($"{fieldName}__target", Const(null, "SystemObject"));
                EmitStoreField($"{fieldName}__method", Const(null, "SystemString"));
                EmitStoreField($"{fieldName}__addr", Const(0u, "SystemUInt32"));
                return Const(null, "SystemObject");
            }

            IDelegateCreationOperation dc = assign.Value as IDelegateCreationOperation;
            if (dc == null && assign.Value is IConversionOperation convSelf && convSelf.Operand is IDelegateCreationOperation dc1)
                dc = dc1;

            if (dc != null)
            {
                var (bridgeName, funcRef, thirdParty) = ResolveDelegateBridge(dc);
                var thisRef = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
                var target = thirdParty ?? thisRef;
                var addr = thirdParty != null ? (HExpr)Const(0u, "SystemUInt32") : funcRef;

                if (dc.Target is IAnonymousFunctionOperation lambda && HasCaptures(lambda))
                    _diagnostics.Add(new EmitDiagnostic { Severity = "Warning",
                        Message = $"Capturing lambda assigned to delegate field '{fieldName}'. " +
                            "Captured variables are shared — reassignment overwrites previous captures." });

                EmitStoreField($"{fieldName}__target", target);
                EmitStoreField($"{fieldName}__method", Const(bridgeName, "SystemString"));
                EmitStoreField($"{fieldName}__addr", addr);
                return target;
            }

            throw new System.NotSupportedException($"Delegate field '{fieldName}' can only be assigned a method group, lambda, or null.");
        }

        // cross-behaviour field write → SetProgramVariable
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } ubTarget && ExternResolver.IsUdonSharpBehaviour(ubTarget.Field.ContainingType))
        {
            // Cross-behaviour delegate field → SetProgramVariable for bundle
            if (ubTarget.Field.Type is INamedTypeSymbol dlgType && dlgType.DelegateInvokeMethod != null)
            {
                if (dlgType.DelegateInvokeMethod.ReturnType.IsTupleType)
                    throw new System.NotSupportedException($"Tuple-return delegate field '{ubTarget.Field.Name}' is not supported.");

                var instanceVal = VisitExpression(ubTarget.Instance);
                var fn = ubTarget.Field.Name;

                // null assignment
                if (assign.Value.ConstantValue is { HasValue: true, Value: null })
                {
                    foreach (var (suffix, val) in new[] { ("__target", (HExpr)Const(null, "SystemObject")), ("__method", Const(null, "SystemString")), ("__addr", Const(0u, "SystemUInt32")) })
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                            new List<HExpr> { instanceVal, Const($"{fn}{suffix}", "SystemString"), val });
                    return Const(null, "SystemObject");
                }

                IDelegateCreationOperation dc = assign.Value as IDelegateCreationOperation;
                if (dc == null && assign.Value is IConversionOperation convCross && convCross.Operand is IDelegateCreationOperation dc2)
                    dc = dc2;

                if (dc != null)
                {
                    var (bridgeName, _, thirdParty) = ResolveDelegateBridge(dc);
                    var thisRef = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
                    var delegateTarget = thirdParty ?? thisRef;

                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<HExpr> { instanceVal, Const($"{fn}__target", "SystemString"), delegateTarget });
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<HExpr> { instanceVal, Const($"{fn}__method", "SystemString"), Const(bridgeName, "SystemString") });
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<HExpr> { instanceVal, Const($"{fn}__addr", "SystemString"), Const(0u, "SystemUInt32") });
                    return delegateTarget;
                }

                throw new System.NotSupportedException($"Delegate field '{fn}' can only be assigned a method group, lambda, or null.");
            }

            var srcVal = VisitExpression(assign.Value);
            var instanceVal2 = VisitExpression(ubTarget.Instance);
            var nameConst = Const(ubTarget.Field.Name, "SystemString");
            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<HExpr> { instanceVal2, nameConst, srcVal });
            return srcVal;
        }

        if (assign.Target is IFieldReferenceOperation { Instance: not null } fieldTarget
            && fieldTarget.Field.ContainingType.IsValueType)
        {
            var srcVal = VisitExpression(assign.Value);
            var containingType = GetUdonType(fieldTarget.Field.ContainingType);
            var instanceVal = fieldTarget.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(containingType), containingType)
                : VisitExpression(fieldTarget.Instance);
            var valueType = GetUdonType(fieldTarget.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldTarget.Field.Name, valueType);
            EmitExternVoid(sig, new List<HExpr> { instanceVal, srcVal });
            // COW dirty: struct field setter → copy back to force heap update
            var cowSlot = _ctx.AllocTemp(containingType);
            EmitAssign(cowSlot, instanceVal);
            return srcVal;
        }

        if (assign.Target is IPropertyReferenceOperation propRef)
        {
            var srcVal = VisitExpression(assign.Value);
            var propContainingUdon = GetUdonType(propRef.Property.ContainingType);

            // Static property setter (no instance) — e.g. Time.timeScale = 1.0f
            if (propRef.Instance == null)
            {
                var staticValType = GetUdonType(propRef.Property.Type);
                EmitExternVoid(ExternResolver.BuildPropertySetSignature(propContainingUdon, propRef.Property.Name, staticValType), new List<HExpr> { srcVal });
                return srcVal;
            }

            // Behaviour/MonoBehaviour have no Udon externs; resolve to actual type
            if (propContainingUdon is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            {
                propContainingUdon = propRef.Instance is IInstanceReferenceOperation
                    ? GetUdonType(_classSymbol)
                    : GetUdonType(propRef.Instance.Type);
            }
            var instanceVal = propRef.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(propContainingUdon), propContainingUdon)
                : VisitExpression(propRef.Instance);
            var containingType = propContainingUdon;
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                var indexArgs = new List<HExpr> { instanceVal };
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    indexArgs.Add(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                indexArgs.Add(srcVal);
                var indexParamStr = string.Join("_", indexTypes);
                EmitExternVoid($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid", indexArgs);
            }
            else switch (propRef.Instance)
            {
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod != null && _methodFunctions.TryGetValue(propRef.Property.SetMethod, out _):
                    // User-defined property setter on this → internal call
                    EmitExprStmt(EmitCallToMethod(propRef.Property.SetMethod, new List<HExpr> { srcVal }));
                    break;
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                         && ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType)
                         && propRef.Property.ContainingType.Name != "UdonSharpBehaviour":
                    // Auto-property set on this → direct variable assignment (user-defined classes only)
                    EmitStoreField(propRef.Property.Name, srcVal);
                    break;
                default:
                {
                    if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                    {
                        if (propRef.Property.Type is INamedTypeSymbol dlgPropType && dlgPropType.DelegateInvokeMethod != null)
                            throw new System.NotSupportedException("Delegate properties are not supported in v2.1. Use delegate fields instead.");

                        var isAutoSet = propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
                        if (isAutoSet || propRef.Property.SetMethod == null)
                        {
                            // Auto-property or read-only: direct SetProgramVariable("PropertyName")
                            var nameConst = Const(propRef.Property.Name, "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<HExpr> { instanceVal, nameConst, srcVal });
                        }
                        else
                        {
                            // Non-auto property setter: call via SendCustomEvent
                            var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);

                            // SetProgramVariable for the value parameter
                            var paramNameConst = Const(setParamIds[0], "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<HExpr> { instanceVal, paramNameConst, srcVal });

                            // SendCustomEvent to invoke setter
                            var eventConst = Const(exportName, "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<HExpr> { instanceVal, eventConst });
                        }
                    }
                    else
                    {
                        EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<HExpr> { instanceVal, srcVal });
                    }

                    break;
                }
            }
            // COW dirty: struct property setter → copy back to force heap update
            if (!propRef.Property.ContainingType.IsValueType)
                return srcVal;

            var cowSlot = _ctx.AllocTemp(containingType);
            EmitAssign(cowSlot, instanceVal);
            return srcVal;
        }

        // Non-this reference-type field assignment → extern field setter
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } refFieldTarget
            && !refFieldTarget.Field.ContainingType.IsValueType
            && !ExternResolver.IsUdonSharpBehaviour(refFieldTarget.Field.ContainingType))
        {
            var srcVal = VisitExpression(assign.Value);
            var instanceVal = VisitExpression(refFieldTarget.Instance);
            var containingType = GetUdonType(refFieldTarget.Field.ContainingType);
            var valueType = GetUdonType(refFieldTarget.Field.Type);
            EmitExternVoid(ExternResolver.BuildFieldSetSignature(containingType, refFieldTarget.Field.Name, valueType, isValueType: false), new List<HExpr> { instanceVal, srcVal });
            return srcVal;
        }

        // Fallback: local variable or this.field
        var srcFallback = VisitExpression(assign.Value);
        var targetFieldName = GetAssignTargetFieldName(assign.Target);
        EmitStoreField(targetFieldName, srcFallback);
        return srcFallback;
    }

    /// <summary>
    /// Resolve the field name (lvalue) for a simple assignment target.
    /// Used by the fallback path in VisitAssignment for locals and this.field.
    /// </summary>
    string GetAssignTargetFieldName(IOperation target)
    {
        switch (target)
        {
            case ILocalReferenceOperation localRef:
                if (_localVarIds.TryGetValue(localRef.Local, out var localId))
                    return localId;
                throw new System.InvalidOperationException(
                    $"Cannot resolve local variable '{localRef.Local.Name}' for assignment.");
            case IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fieldRef:
                return fieldRef.Field.Name;
            case IParameterReferenceOperation paramRef:
                return GetParamVarId(paramRef.Parameter);
            default:
                throw new System.NotSupportedException(
                    $"Unsupported simple assignment target: {target.GetType().Name}");
        }
    }

    // ── VisitCompoundAssignment ──

    HExpr VisitCompoundAssignment(ICompoundAssignmentOperation op)
    {
        // Block += / -= on delegate fields — Udon VM does not support Delegate.Combine/Remove
        if (op.Target is IFieldReferenceOperation fr
            && fr.Field.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
            throw new System.NotSupportedException("Multicast delegates (+=/-=) are not supported. Udon VM does not support Delegate.Combine/Remove.");

        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var leftVal = lv.Value;
        var rightVal = VisitExpression(op.Value);
        var resultType = GetUdonType(op.Type);

        // Promote small integers for the operation temp
        var opResultType = resultType;
        if (opResultType is "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16")
            opResultType = "SystemInt32";

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        HExpr resultVal = ExternCall(sig, new List<HExpr> { leftVal, rightVal }, opResultType);

        // Narrow back to original type if promoted
        if (opResultType != resultType)
            resultVal = ExternCall(ExternResolver.BuildConvertSignature(opResultType, resultType), new List<HExpr> { resultVal }, resultType);

        EmitWriteBack(op.Target, resultVal, lv);
        return resultVal;
    }

    // ── VisitIncrementDecrement ──

    HExpr VisitIncrementDecrement(IIncrementOrDecrementOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var targetVal = lv.Value;
        var udonType = GetUdonType(op.Type);

        // Promote small integers: Udon VM has no byte/sbyte/short/ushort operators
        var opType = udonType;
        if (opType is "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16")
            opType = "SystemInt32";

        var oneConst = Const(1, opType);

        // For postfix, save old value before modifying target (only if result is used)
        HExpr savedVal = null;
        if (op.IsPostfix)
        {
            var resultUsed = op.Parent is not IExpressionStatementOperation
                             && op.Parent is not IForLoopOperation;
            if (op.Parent == null || resultUsed)
            {
                // Save current value to a scratch slot
                var savedSlot = _ctx.AllocTemp(udonType);
                EmitAssign(savedSlot, targetVal);
                savedVal = SlotRef(savedSlot);
            }
        }

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        HExpr resultVal = ExternCall(sig, new List<HExpr> { targetVal, oneConst }, opType);

        // Narrow back to original type if promoted
        if (opType != udonType)
            resultVal = ExternCall(ExternResolver.BuildConvertSignature(opType, udonType), new List<HExpr> { resultVal }, udonType);

        // Materialize resultVal to a temp slot before write-back to avoid
        // the extern call being emitted twice (once for store, once for return value).
        if (!op.IsPostfix)
        {
            var tempSlot = _ctx.AllocTemp(udonType);
            EmitAssign(tempSlot, resultVal);
            resultVal = SlotRef(tempSlot);
        }

        EmitWriteBack(op.Target, resultVal, lv);

        return op.IsPostfix ? savedVal : resultVal;
    }

    // ── LValue Capture ──
    // Evaluates and caches sub-expressions of an l-value (array ref, index, instance)
    // to avoid re-evaluating side-effecting expressions during write-back.

    struct LValueCapture
    {
        public HExpr Value;         // The evaluated l-value value
        public HExpr ArrayVal;      // Cached array reference (for array elements)
        public HExpr IndexVal;      // Cached index (for array elements)
        public HExpr InstanceVal;   // Cached instance (for cross-behaviour fields/properties)
    }

    LValueCapture CaptureLValue(IOperation target)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation arrayElem:
            {
                var arrayVal = VisitExpression(arrayElem.ArrayReference);
                var indexVal = VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elemAccessorType = GetArrayElemType(arrSymbol);

                // Read current value: arr[idx]
                var valResult = ExternCall(
                    $"{arrayType}.__Get__SystemInt32__{elemAccessorType}",
                    new List<HExpr> { arrayVal, indexVal },
                    GetUdonType(arrayElem.Type));
                return new LValueCapture { Value = valResult, ArrayVal = arrayVal, IndexVal = indexVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceVal = VisitExpression(fieldRef.Instance);
                // Read via GetProgramVariable
                var nameConst = Const(fieldRef.Field.Name, "SystemString");
                var valResult = ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<HExpr> { instanceVal, nameConst },
                    "SystemObject");
                return new LValueCapture { Value = valResult, InstanceVal = instanceVal };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                var instanceVal = VisitExpression(fieldRef2.Instance);
                var containingType = GetUdonType(fieldRef2.Field.ContainingType);
                var valueType = GetUdonType(fieldRef2.Field.Type);
                var sig = ExternResolver.BuildPropertyGetSignature(containingType, fieldRef2.Field.Name, valueType);
                var valResult = ExternCall(sig, new List<HExpr> { instanceVal }, valueType);
                return new LValueCapture { Value = valResult, InstanceVal = instanceVal };
            }
            default:
                // Simple l-value (local, field on this): just evaluate normally
                return new LValueCapture { Value = VisitExpression(target) };
        }
    }

    // ── EmitWriteBack ──
    // Write back a computed value to non-trivial l-value targets (array elements, properties).
    // For local/field variables, also writes back via EmitStoreField.

    void EmitWriteBack(IOperation target, HExpr valueVal, LValueCapture lv = default)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation arrayElem:
            {
                // Use captured array/index if available (avoid double evaluation)
                var arrayVal = lv.ArrayVal ?? VisitExpression(arrayElem.ArrayReference);
                var indexVal = lv.IndexVal ?? VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<HExpr> { arrayVal, indexVal, valueVal });
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                // Cross-behaviour field write-back → SetProgramVariable
                var instanceVal = lv.InstanceVal ?? VisitExpression(fieldRef.Instance);
                var nameConst = Const(fieldRef.Field.Name, "SystemString");
                EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<HExpr> { instanceVal, nameConst, valueVal });
                break;
            }
            // Auto-property on this → backing field already handled by write-back to field (user-defined classes only)
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } propRef when propRef.Property.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true && ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Property.ContainingType.Name != "UdonSharpBehaviour":
                return;
            // User-defined property on this → call setter
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { SetMethod: not null } } propRef when _methodFunctions.TryGetValue(propRef.Property.SetMethod, out _):
                EmitExprStmt(EmitCallToMethod(propRef.Property.SetMethod, new List<HExpr> { valueVal }));
                return;
            // Cross-behaviour UdonSharpBehaviour property → SetProgramVariable / SendCustomEvent
            case IPropertyReferenceOperation propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation:
            {
                var instanceVal = VisitExpression(propRef.Instance);
                var isAutoSet = propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
                if (isAutoSet || propRef.Property.SetMethod == null)
                {
                    var nameConst = Const(propRef.Property.Name, "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<HExpr> { instanceVal, nameConst, valueVal });
                }
                else
                {
                    var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                    var paramNameConst = Const(setParamIds[0], "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<HExpr> { instanceVal, paramNameConst, valueVal });
                    var eventConst = Const(exportName, "SystemString");
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<HExpr> { instanceVal, eventConst });
                }
                return;
            }
            // Resolve containing type and instance
            case IPropertyReferenceOperation propRef:
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                if (containingType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
                    containingType = propRef.Instance is IInstanceReferenceOperation
                        ? GetUdonType(_classSymbol)
                        : GetUdonType(propRef.Instance.Type);

                HExpr wbInstanceVal;
                if (propRef.Instance is IInstanceReferenceOperation)
                    wbInstanceVal = LoadField(_ctx.DeclareThisOnce(containingType), containingType);
                else if (propRef.Instance != null)
                    wbInstanceVal = VisitExpression(propRef.Instance);
                else
                {
                    // Static property: no instance
                    var valueType = GetUdonType(propRef.Property.Type);
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<HExpr> { valueVal });
                    return;
                }

                var propValueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    var indexArgs = new List<HExpr> { wbInstanceVal };
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        indexArgs.Add(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    indexArgs.Add(valueVal);
                    var indexParamStr = string.Join("_", indexTypes);
                    EmitExternVoid($"{containingType}.__set_Item__{indexParamStr}_{propValueType}__SystemVoid", indexArgs);
                }
                else
                {
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, propValueType), new List<HExpr> { wbInstanceVal, valueVal });
                }
                // COW dirty: struct property setter → copy back to force heap update
                if (propRef.Property.ContainingType.IsValueType)
                {
                    var cowSlot = _ctx.AllocTemp(containingType);
                    EmitAssign(cowSlot, wbInstanceVal);
                }

                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef2
                when fieldRef2.Field.ContainingType.IsValueType:
            {
                // Struct field setter (e.g., vec.y += 3f where vec is an array element)
                var instanceVal = lv.InstanceVal ?? VisitExpression(fieldRef2.Instance);
                var containingType = GetUdonType(fieldRef2.Field.ContainingType);
                var valueType = GetUdonType(fieldRef2.Field.Type);
                var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef2.Field.Name, valueType);
                EmitExternVoid(sig, new List<HExpr> { instanceVal, valueVal });
                break;
            }
            default:
            {
                // Simple l-value (local, field on this): write back via EmitStoreField
                var fieldName = GetAssignTargetFieldName(target);
                EmitStoreField(fieldName, valueVal);
                break;
            }
        }
    }

    // ── EmitMemberSet ──
    // Called from VisitAssignment for non-self member writes.
    void EmitMemberSet(HExpr instanceVal, IOperation target, HExpr valueVal)
    {
        switch (target)
        {
            case IFieldReferenceOperation fieldRef when fieldRef.Field.ContainingType.IsValueType:
            {
                var containingType = GetUdonType(fieldRef.Field.ContainingType);
                var valueType = GetUdonType(fieldRef.Field.Type);
                var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef.Field.Name, valueType);
                EmitExternVoid(sig, new List<HExpr> { instanceVal, valueVal });
                break;
            }
            case IPropertyReferenceOperation propRef:
            {
                var containingType = GetUdonType(propRef.Property.ContainingType);
                var valueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    var indexArgs = new List<HExpr> { instanceVal };
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        indexArgs.Add(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    indexArgs.Add(valueVal);
                    var indexParamStr = string.Join("_", indexTypes);
                    EmitExternVoid($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid", indexArgs);
                }
                else
                {
                    EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<HExpr> { instanceVal, valueVal });
                }

                break;
            }
            case IFieldReferenceOperation fieldRef2:
                // Non-struct field assignment (class fields via SetProgramVariable or direct)
                EmitStoreField(fieldRef2.Field.Name, valueVal);
                break;
        }
    }
}
