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
            _ctx.Module.AddPush(arrayId);
            _ctx.Module.AddPush(indexId);
            _ctx.Module.AddPush(srcId);
            AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
            return srcId;
        }

        // cross-behaviour field write → SetProgramVariable
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } ubTarget && ExternResolver.IsUdonSharpBehaviour(ubTarget.Field.ContainingType))
        {
            var srcId = VisitExpression(assign.Value);
            var instanceId = VisitExpression(ubTarget.Instance);
            var nameConst = _ctx.Vars.DeclareConst("SystemString", ubTarget.Field.Name);
            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(nameConst);
            _ctx.Module.AddPush(srcId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
            return srcId;
        }

        if (assign.Target is IFieldReferenceOperation { Instance: not null } fieldTarget
            && fieldTarget.Field.ContainingType.IsValueType)
        {
            var srcId = VisitExpression(assign.Value);
            var instanceId = fieldTarget.Instance is IInstanceReferenceOperation 
                ? _ctx.Vars.DeclareThisOnce(GetUdonType(fieldTarget.Field.ContainingType)) 
                : VisitExpression(fieldTarget.Instance);
            var containingType = GetUdonType(fieldTarget.Field.ContainingType);
            var valueType = GetUdonType(fieldTarget.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldTarget.Field.Name, valueType);
            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(srcId);
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
                _ctx.Module.AddPush(srcId);
                AddExternChecked(ExternResolver.BuildPropertySetSignature(propContainingUdon, propRef.Property.Name, staticValType));
                return srcId;
            }

            // Behaviour/MonoBehaviour have no Udon externs; resolve to actual type
            if (propContainingUdon is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            {
                propContainingUdon = propRef.Instance is IInstanceReferenceOperation
                    ? GetUdonType(_ctx.ClassSymbol)
                    : GetUdonType(propRef.Instance.Type);
            }
            var instanceId = propRef.Instance is IInstanceReferenceOperation 
                ? _ctx.Vars.DeclareThisOnce(propContainingUdon) 
                : VisitExpression(propRef.Instance);
            var containingType = propContainingUdon;
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                _ctx.Module.AddPush(instanceId);
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    _ctx.Module.AddPush(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                _ctx.Module.AddPush(srcId);
                var indexParamStr = string.Join("_", indexTypes);
                AddExternChecked($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid");
            }
            else switch (propRef.Instance)
            {
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod != null && _ctx.MethodLabels.TryGetValue(propRef.Property.SetMethod, out var setterLabel):
                    // User-defined property setter on this → JUMP call
                    _ctx.Module.AddCopy(srcId, GetParamVarId(propRef.Property.SetMethod.Parameters[0]));
                    EmitCallByLabel(propRef.Property.SetMethod, setterLabel);
                    break;
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod?.IsImplicitlyDeclared == true && ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType):
                    // Auto-property set on this → direct variable assignment
                    _ctx.Module.AddCopy(srcId, propRef.Property.Name);
                    break;
                default:
                {
                    if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                    {
                        var isAutoSet = propRef.Property.SetMethod?.IsImplicitlyDeclared == true;
                        if (isAutoSet || propRef.Property.SetMethod == null)
                        {
                            // Auto-property or read-only: direct SetProgramVariable("PropertyName")
                            var nameConst = _ctx.Vars.DeclareConst("SystemString", propRef.Property.Name);
                            _ctx.Module.AddPush(instanceId);
                            _ctx.Module.AddPush(nameConst);
                            _ctx.Module.AddPush(srcId);
                            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");
                        }
                        else
                        {
                            // Non-auto property setter: call via SendCustomEvent
                            var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);

                            // SetProgramVariable for the value parameter
                            var paramNameConst = _ctx.Vars.DeclareConst("SystemString", setParamIds[0]);
                            _ctx.Module.AddPush(instanceId);
                            _ctx.Module.AddPush(paramNameConst);
                            _ctx.Module.AddPush(srcId);
                            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid");

                            // SendCustomEvent to invoke setter
                            var eventConst = _ctx.Vars.DeclareConst("SystemString", exportName);
                            _ctx.Module.AddPush(instanceId);
                            _ctx.Module.AddPush(eventConst);
                            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");
                        }
                    }
                    else
                    {
                        _ctx.Module.AddPush(instanceId);
                        _ctx.Module.AddPush(srcId);
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
            _ctx.Module.AddPush(instanceId);
            _ctx.Module.AddPush(srcId);
            AddExternChecked(ExternResolver.BuildFieldSetSignature(containingType, refFieldTarget.Field.Name, valueType, isValueType: false));
            return srcId;
        }

        var dst = VisitExpression(assign.Target);
        _ctx.TargetHint = dst;
        var src = VisitExpression(assign.Value);
        _ctx.TargetHint = null;
        if (src != dst) // hint was not consumed
            _ctx.Module.AddCopy(src, dst);
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
        var tempId = (opResultType == resultType) ? leftId : _ctx.Vars.DeclareTemp(opResultType);

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        _ctx.Module.AddPush(leftId);
        _ctx.Module.AddPush(rightId);
        _ctx.Module.AddPush(tempId);
        AddExternChecked(sig);

        // Narrow back to original type if promoted
        if (opResultType != resultType)
        {
            var narrowed = _ctx.Vars.DeclareTemp(resultType);
            _ctx.Module.AddPush(tempId);
            _ctx.Module.AddPush(narrowed);
            AddExternChecked(ExternResolver.BuildConvertSignature(opResultType, resultType));
            tempId = narrowed;
        }

        if (tempId != leftId)
            _ctx.Module.AddCopy(tempId, leftId);
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

        var oneConst = _ctx.Vars.DeclareConst(opType, "1");
        // Direct write-back when no type promotion (Udon VM reads all inputs before writing output)
        var tempId = (opType == udonType) ? targetId : _ctx.Vars.DeclareTemp(opType);

        // For postfix, save old value before modifying target (only if result is used)
        string savedId = null;
        if (op.IsPostfix)
        {
            var resultUsed = op.Parent is not IExpressionStatementOperation
                             && op.Parent is not IForLoopOperation;
            if (op.Parent == null || resultUsed)
            {
                savedId = _ctx.Vars.DeclareTemp(udonType);
                _ctx.Module.AddCopy(targetId, savedId);
            }
        }

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        _ctx.Module.AddPush(targetId);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(tempId);
        AddExternChecked(sig);

        // Narrow back to original type if promoted
        if (opType != udonType)
        {
            var narrowed = _ctx.Vars.DeclareTemp(udonType);
            _ctx.Module.AddPush(tempId);
            _ctx.Module.AddPush(narrowed);
            AddExternChecked(ExternResolver.BuildConvertSignature(opType, udonType));
            tempId = narrowed;
        }

        if (tempId != targetId)
            _ctx.Module.AddCopy(tempId, targetId);

        EmitWriteBack(op.Target, tempId, lv);

        return op.IsPostfix ? savedId : tempId;
    }

}
