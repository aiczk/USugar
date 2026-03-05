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

    // ── IExpressionHandler (string-returning expressions) ──

    bool IExpressionHandler.CanHandle(IOperation op)
        => op is ISimpleAssignmentOperation
            or ICompoundAssignmentOperation
            or IIncrementOrDecrementOperation;

    string IExpressionHandler.Handle(IOperation op) => op switch
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
                var valueId = VisitExpression(valueTuple.Elements[i]);
                AssignToTarget(targetTuple.Elements[i], valueId);
            }
        }
        else
        {
            throw new System.NotSupportedException(
                $"Unsupported tuple deconstruction value: {op.Value.GetType().Name}");
        }
    }

    void AssignToTarget(IOperation target, string valueId)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declExpr:
                // var x in deconstruction — declares a new local
                if (declExpr.Expression is ILocalReferenceOperation localRef)
                {
                    var udonType = GetUdonType(localRef.Type);
                    var localId = _vars.DeclareLocal(localRef.Local.Name, udonType);
                    _localVarIds[localRef.Local] = localId;
                    _module.AddCopy(valueId, localId);
                }
                break;

            case ILocalReferenceOperation existingLocal:
                var existingId = _vars.Lookup(existingLocal.Local.Name)
                    ?? _localVarIds.GetValueOrDefault(existingLocal.Local);
                if (existingId == null)
                {
                    // New local from tuple deconstruction (var (a, b) pattern)
                    var udonType = GetUdonType(existingLocal.Type);
                    existingId = _vars.DeclareLocal(existingLocal.Local.Name, udonType);
                    _localVarIds[existingLocal.Local] = existingId;
                }
                _module.AddCopy(valueId, existingId);
                break;

            case IFieldReferenceOperation fieldRef when fieldRef.Instance is IInstanceReferenceOperation:
                _module.AddCopy(valueId, fieldRef.Field.Name);
                break;

            case IDiscardOperation:
                break; // _ = expr → discard

            default:
                throw new System.NotSupportedException(
                    $"Unsupported deconstruction target element: {target.GetType().Name}");
        }
    }

    // ── VisitAssignment ──

    string VisitAssignment(ISimpleAssignmentOperation assign)
    {
        if (assign.Target is IArrayElementReferenceOperation arrayElem)
        {
            var arrayId = VisitExpression(arrayElem.ArrayReference);
            var indexId = VisitExpression(arrayElem.Indices[0]);
            var srcId = VisitExpression(assign.Value);
            var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
            var arrayType = GetArrayType(arrSymbol);
            var elementType = GetArrayElemType(arrSymbol);
            _module.AddPush(arrayId);
            _module.AddPush(indexId);
            _module.AddPush(srcId);
            AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
            return srcId;
        }

        // cross-behaviour field write → SetProgramVariable
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } ubTarget && ExternResolver.IsUdonSharpBehaviour(ubTarget.Field.ContainingType))
        {
            var srcId = VisitExpression(assign.Value);
            var instanceId = VisitExpression(ubTarget.Instance);
            var nameConst = _vars.DeclareConst("SystemString", ubTarget.Field.Name);
            _module.AddPush(instanceId);
            _module.AddPush(nameConst);
            _module.AddPush(srcId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
            return srcId;
        }

        if (assign.Target is IFieldReferenceOperation { Instance: not null } fieldTarget
            && fieldTarget.Field.ContainingType.IsValueType)
        {
            var srcId = VisitExpression(assign.Value);
            var instanceId = fieldTarget.Instance is IInstanceReferenceOperation 
                ? _vars.DeclareThisOnce(GetUdonType(fieldTarget.Field.ContainingType)) 
                : VisitExpression(fieldTarget.Instance);
            var containingType = GetUdonType(fieldTarget.Field.ContainingType);
            var valueType = GetUdonType(fieldTarget.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldTarget.Field.Name, valueType);
            _module.AddPush(instanceId);
            _module.AddPush(srcId);
            AddExternChecked(sig);
            return srcId;
        }

        if (assign.Target is IPropertyReferenceOperation propRef)
        {
            var srcId = VisitExpression(assign.Value);
            var propContainingUdon = GetUdonType(propRef.Property.ContainingType);

            // Static property setter (no instance) — e.g. Time.timeScale = 1.0f
            if (propRef.Instance == null)
            {
                var staticValType = GetUdonType(propRef.Property.Type);
                _module.AddPush(srcId);
                AddExternChecked(ExternResolver.BuildPropertySetSignature(propContainingUdon, propRef.Property.Name, staticValType));
                return srcId;
            }

            // Behaviour/MonoBehaviour have no Udon externs; resolve to actual type
            if (propContainingUdon is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            {
                propContainingUdon = propRef.Instance is IInstanceReferenceOperation
                    ? GetUdonType(_classSymbol)
                    : GetUdonType(propRef.Instance.Type);
            }
            var instanceId = propRef.Instance is IInstanceReferenceOperation 
                ? _vars.DeclareThisOnce(propContainingUdon) 
                : VisitExpression(propRef.Instance);
            var containingType = propContainingUdon;
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                _module.AddPush(instanceId);
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    _module.AddPush(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                _module.AddPush(srcId);
                var indexParamStr = string.Join("_", indexTypes);
                AddExternChecked($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid");
            }
            else switch (propRef.Instance)
            {
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod != null && _methodLabels.TryGetValue(propRef.Property.SetMethod, out var setterLabel):
                    // User-defined property setter on this → JUMP call
                    _module.AddCopy(srcId, GetParamVarId(propRef.Property.SetMethod.Parameters[0]));
                    EmitCallByLabel(propRef.Property.SetMethod, setterLabel);
                    break;
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod?.IsImplicitlyDeclared == true && ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType):
                    // Auto-property set on this → direct variable assignment
                    _module.AddCopy(srcId, propRef.Property.Name);
                    break;
                default:
                {
                    if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                    {
                        var isAutoSet = propRef.Property.SetMethod?.IsImplicitlyDeclared == true;
                        if (isAutoSet || propRef.Property.SetMethod == null)
                        {
                            // Auto-property or read-only: direct SetProgramVariable("PropertyName")
                            var nameConst = _vars.DeclareConst("SystemString", propRef.Property.Name);
                            _module.AddPush(instanceId);
                            _module.AddPush(nameConst);
                            _module.AddPush(srcId);
                            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                        }
                        else
                        {
                            // Non-auto property setter: call via SendCustomEvent
                            var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);

                            // SetProgramVariable for the value parameter
                            var paramNameConst = _vars.DeclareConst("SystemString", setParamIds[0]);
                            _module.AddPush(instanceId);
                            _module.AddPush(paramNameConst);
                            _module.AddPush(srcId);
                            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");

                            // SendCustomEvent to invoke setter
                            var eventConst = _vars.DeclareConst("SystemString", exportName);
                            _module.AddPush(instanceId);
                            _module.AddPush(eventConst);
                            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");
                        }
                    }
                    else
                    {
                        _module.AddPush(instanceId);
                        _module.AddPush(srcId);
                        AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType));
                    }

                    break;
                }
            }
            return srcId;
        }

        // Non-this reference-type field assignment → extern field setter
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } refFieldTarget
            && !refFieldTarget.Field.ContainingType.IsValueType
            && !ExternResolver.IsUdonSharpBehaviour(refFieldTarget.Field.ContainingType))
        {
            var srcId = VisitExpression(assign.Value);
            var instanceId = VisitExpression(refFieldTarget.Instance);
            var containingType = GetUdonType(refFieldTarget.Field.ContainingType);
            var valueType = GetUdonType(refFieldTarget.Field.Type);
            _module.AddPush(instanceId);
            _module.AddPush(srcId);
            AddExternChecked(ExternResolver.BuildFieldSetSignature(containingType, refFieldTarget.Field.Name, valueType, isValueType: false));
            return srcId;
        }

        var dst = VisitExpression(assign.Target);
        _ctx.TargetHint = dst;
        var src = VisitExpression(assign.Value);
        _ctx.TargetHint = null;
        if (src != dst) // hint was not consumed
            _module.AddCopy(src, dst);
        return dst;
    }

    // ── VisitCompoundAssignment ──

    string VisitCompoundAssignment(ICompoundAssignmentOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var leftId = lv.ValueId;
        var rightId = VisitExpression(op.Value);
        var resultType = GetUdonType(op.Type);

        // Promote small integers for the operation temp
        var opResultType = resultType;
        if (opResultType is "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16")
            opResultType = "SystemInt32";

        // Use leftId directly as extern output (Udon VM reads all inputs before writing output)
        var tempId = (opResultType == resultType) ? leftId : _vars.DeclareTemp(opResultType);

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        _module.AddPush(leftId);
        _module.AddPush(rightId);
        _module.AddPush(tempId);
        AddExternChecked(sig);

        // Narrow back to original type if promoted
        if (opResultType != resultType)
        {
            var narrowed = _vars.DeclareTemp(resultType);
            _module.AddPush(tempId);
            _module.AddPush(narrowed);
            AddExternChecked(ExternResolver.BuildConvertSignature(opResultType, resultType));
            tempId = narrowed;
        }

        if (tempId != leftId)
            _module.AddCopy(tempId, leftId);
        EmitWriteBack(op.Target, tempId, lv);
        return leftId;
    }

    // ── VisitIncrementDecrement ──

    string VisitIncrementDecrement(IIncrementOrDecrementOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var targetId = lv.ValueId;
        var udonType = GetUdonType(op.Type);

        // Promote small integers: Udon VM has no byte/sbyte/short/ushort operators
        var opType = udonType;
        if (opType is "SystemByte" or "SystemSByte" or "SystemInt16" or "SystemUInt16")
            opType = "SystemInt32";

        var oneConst = _vars.DeclareConst(opType, "1");
        // Direct write-back when no type promotion (Udon VM reads all inputs before writing output)
        var tempId = (opType == udonType) ? targetId : _vars.DeclareTemp(opType);

        // For postfix, save old value before modifying target (only if result is used)
        string savedId = null;
        if (op.IsPostfix)
        {
            var resultUsed = op.Parent is not IExpressionStatementOperation
                             && op.Parent is not IForLoopOperation;
            if (op.Parent == null || resultUsed)
            {
                savedId = _vars.DeclareTemp(udonType);
                _module.AddCopy(targetId, savedId);
            }
        }

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        _module.AddPush(targetId);
        _module.AddPush(oneConst);
        _module.AddPush(tempId);
        AddExternChecked(sig);

        // Narrow back to original type if promoted
        if (opType != udonType)
        {
            var narrowed = _vars.DeclareTemp(udonType);
            _module.AddPush(tempId);
            _module.AddPush(narrowed);
            AddExternChecked(ExternResolver.BuildConvertSignature(opType, udonType));
            tempId = narrowed;
        }

        if (tempId != targetId)
            _module.AddCopy(tempId, targetId);

        EmitWriteBack(op.Target, tempId, lv);

        return op.IsPostfix ? savedId : tempId;
    }

    // ── LValue Capture ──
    // Evaluates and caches sub-expressions of an l-value (array ref, index, instance)
    // to avoid re-evaluating side-effecting expressions during write-back.

    struct LValueCapture
    {
        public string ValueId;       // The evaluated l-value variable ID
        public string ArrayId;       // Cached array reference (for array elements)
        public string IndexId;       // Cached index (for array elements)
        public string InstanceId;    // Cached instance (for cross-behaviour fields/properties)
    }

    LValueCapture CaptureLValue(IOperation target)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation arrayElem:
            {
                var arrayId = VisitExpression(arrayElem.ArrayReference);
                var indexId = VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elemAccessorType = GetArrayElemType(arrSymbol);

                // Read current value: arr[idx]
                var valId = _vars.DeclareTemp(GetUdonType(arrayElem.Type));
                _module.AddPush(arrayId);
                _module.AddPush(indexId);
                _module.AddPush(valId);
                AddExternChecked($"{arrayType}.__Get__SystemInt32__{elemAccessorType}");
                return new LValueCapture { ValueId = valId, ArrayId = arrayId, IndexId = indexId };
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                var instanceId = VisitExpression(fieldRef.Instance);
                // Read via GetProgramVariable
                var fldType = GetUdonType(fieldRef.Field.Type);
                var valId = _vars.DeclareTemp(fldType);
                var nameConst = _vars.DeclareConst("SystemString", fieldRef.Field.Name);
                _module.AddPush(instanceId);
                _module.AddPush(nameConst);
                _module.AddPush(valId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
                return new LValueCapture { ValueId = valId, InstanceId = instanceId };
            }
            default:
                // Simple l-value (local, field on this): just evaluate normally
                return new LValueCapture { ValueId = VisitExpression(target) };
        }
    }

    // ── EmitWriteBack ──
    // Write back a computed value to non-trivial l-value targets (array elements, properties).
    // For local/field variables, the COPY to targetId already suffices.

    void EmitWriteBack(IOperation target, string valueId, LValueCapture lv = default)
    {
        switch (target)
        {
            case IArrayElementReferenceOperation arrayElem:
            {
                // Use captured array/index if available (avoid double evaluation)
                var arrayId = lv.ArrayId ?? VisitExpression(arrayElem.ArrayReference);
                var indexId = lv.IndexId ?? VisitExpression(arrayElem.Indices[0]);
                var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
                var arrayType = GetArrayType(arrSymbol);
                var elementType = GetArrayElemType(arrSymbol);
                _module.AddPush(arrayId);
                _module.AddPush(indexId);
                _module.AddPush(valueId);
                AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
                break;
            }
            case IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } fieldRef
                when ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType):
            {
                // Cross-behaviour field write-back → SetProgramVariable
                var instanceId = lv.InstanceId ?? VisitExpression(fieldRef.Instance);
                var nameConst = _vars.DeclareConst("SystemString", fieldRef.Field.Name);
                _module.AddPush(instanceId);
                _module.AddPush(nameConst);
                _module.AddPush(valueId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                break;
            }
            // Auto-property on this → COPY to backing field already handled by caller
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { GetMethod: { IsImplicitlyDeclared: true } } } propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType):
                return;
            // User-defined property on this → call setter
            case IPropertyReferenceOperation { Instance: IInstanceReferenceOperation, Property: { SetMethod: not null } } propRef when _methodLabels.TryGetValue(propRef.Property.SetMethod, out var setterLabel): 
                _module.AddCopy(valueId, GetParamVarId(propRef.Property.SetMethod.Parameters[0]));
                EmitCallByLabel(propRef.Property.SetMethod, setterLabel);
                return;
            // Cross-behaviour UdonSharpBehaviour property → SetProgramVariable / SendCustomEvent
            case IPropertyReferenceOperation propRef when ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation:
            {
                var instanceId = VisitExpression(propRef.Instance);
                var isAutoSet = propRef.Property.SetMethod?.IsImplicitlyDeclared == true;
                if (isAutoSet || propRef.Property.SetMethod == null)
                {
                    var nameConst = _vars.DeclareConst("SystemString", propRef.Property.Name);
                    _module.AddPush(instanceId);
                    _module.AddPush(nameConst);
                    _module.AddPush(valueId);
                    AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                }
                else
                {
                    var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);
                    var paramNameConst = _vars.DeclareConst("SystemString", setParamIds[0]);
                    _module.AddPush(instanceId);
                    _module.AddPush(paramNameConst);
                    _module.AddPush(valueId);
                    AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                    var eventConst = _vars.DeclareConst("SystemString", exportName);
                    _module.AddPush(instanceId);
                    _module.AddPush(eventConst);
                    AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");
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

                string wbInstanceId;
                if (propRef.Instance is IInstanceReferenceOperation)
                    wbInstanceId = _vars.DeclareThisOnce(containingType);
                else if (propRef.Instance != null)
                    wbInstanceId = VisitExpression(propRef.Instance);
                else
                {
                    // Static property: no instance
                    var valueType = GetUdonType(propRef.Property.Type);
                    _module.AddPush(valueId);
                    AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType));
                    return;
                }

                var propValueType = GetUdonType(propRef.Property.Type);
                if (propRef.Property.IsIndexer)
                {
                    _module.AddPush(wbInstanceId);
                    var indexTypes = new List<string>();
                    foreach (var arg in propRef.Arguments)
                    {
                        _module.AddPush(VisitExpression(arg.Value));
                        indexTypes.Add(GetUdonType(arg.Value.Type));
                    }
                    _module.AddPush(valueId);
                    var indexParamStr = string.Join("_", indexTypes);
                    AddExternChecked($"{containingType}.__set_Item__{indexParamStr}_{propValueType}__SystemVoid");
                }
                else
                {
                    _module.AddPush(wbInstanceId);
                    _module.AddPush(valueId);
                    AddExternChecked(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, propValueType));
                }
                break;
            }
        }
    }

}
