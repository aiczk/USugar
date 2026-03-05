using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class NullableHandler : HandlerBase, IExpressionHandler
{
    public NullableHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IConditionalAccessOperation
            or ICoalesceOperation
            or IConditionalAccessInstanceOperation
            or ICoalesceAssignmentOperation;

    public string Handle(IOperation expression) => expression switch
    {
        IConditionalAccessOperation op => VisitConditionalAccess(op),
        ICoalesceOperation op => VisitCoalesce(op),
        IConditionalAccessInstanceOperation => _conditionalAccessTargets.Peek(),
        ICoalesceAssignmentOperation op => VisitCoalesceAssignment(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    string VisitConditionalAccess(IConditionalAccessOperation op)
    {
        bool isVoid = op.Type == null || op.Type.SpecialType == SpecialType.System_Void;

        string resultId = null;
        if (!isVoid)
        {
            var resultType = GetUdonType(op.Type);
            resultId = _vars.DeclareTemp(resultType);
            var defaultConst = _vars.DeclareConst(resultType, "null");
            _module.AddCopy(defaultConst, resultId);
        }

        var targetId = VisitExpression(op.Operation);

        var nullConst = _vars.DeclareConst("SystemObject", "null");
        var condId = _vars.DeclareTemp("SystemBoolean");
        var endLabel = _module.DefineLabel("__condaccess_end");

        // condId = (target != null); JIF → jump when false (target IS null) → skip access
        _module.AddPush(targetId);
        _module.AddPush(nullConst);
        _module.AddPush(condId);
        AddExternChecked("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean");
        _module.AddPush(condId);
        _module.AddJumpIfFalse(endLabel);

        // target is not null → evaluate WhenNotNull with target as the instance
        _conditionalAccessTargets.Push(targetId);
        var accessId = VisitExpression(op.WhenNotNull);
        _conditionalAccessTargets.Pop();

        if (!isVoid && accessId != null)
            _module.AddCopy(accessId, resultId);

        _module.MarkLabel(endLabel);
        return resultId;
    }

    string VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = GetUdonType(op.Type);
        var resultId = _vars.DeclareTemp(resultType);
        var leftId = VisitExpression(op.Value);
        _module.AddCopy(leftId, resultId);

        var nullConst = _vars.DeclareConst("SystemObject", "null");
        var condId = _vars.DeclareTemp("SystemBoolean");
        var endLabel = _module.DefineLabel("__coalesce_end");

        // condId = (left == null); JIF → jump when false (left NOT null) → skip right
        _module.AddPush(leftId);
        _module.AddPush(nullConst);
        _module.AddPush(condId);
        AddExternChecked("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean");
        _module.AddPush(condId);
        _module.AddJumpIfFalse(endLabel);
        // left IS null → use right
        var rightId = VisitExpression(op.WhenNull);
        _module.AddCopy(rightId, resultId);
        _module.MarkLabel(endLabel);

        return resultId;
    }

    string VisitCoalesceAssignment(ICoalesceAssignmentOperation op)
    {
        // x ??= expr → if (x == null) x = expr; return x
        // Capture lvalue sub-expressions once to avoid double evaluation
        string targetId;
        string cachedArrayId = null, cachedIndexId = null, cachedInstanceId = null;

        if (op.Target is IArrayElementReferenceOperation arrayElemTarget)
        {
            cachedArrayId = VisitExpression(arrayElemTarget.ArrayReference);
            cachedIndexId = VisitExpression(arrayElemTarget.Indices[0]);
            var arrSym = arrayElemTarget.ArrayReference.Type as IArrayTypeSymbol;
            var arrType = GetArrayType(arrSym);
            var elemType = GetArrayElemType(arrSym);
            targetId = _vars.DeclareTemp(GetUdonType(arrayElemTarget.Type));
            _module.AddPush(cachedArrayId);
            _module.AddPush(cachedIndexId);
            _module.AddPush(targetId);
            AddExternChecked($"{arrType}.__Get__SystemInt32__{elemType}");
        }
        else if (op.Target is IPropertyReferenceOperation propTarget)
        {
            if (propTarget.Instance is IInstanceReferenceOperation)
                cachedInstanceId = _vars.DeclareThisOnce(GetUdonType(propTarget.Property.ContainingType));
            else if (propTarget.Instance != null)
                cachedInstanceId = VisitExpression(propTarget.Instance);
            targetId = VisitExpression(op.Target);
        }
        else
        {
            targetId = VisitExpression(op.Target);
        }

        var nullConst = _vars.DeclareConst("SystemObject", "null");
        var condId = _vars.DeclareTemp("SystemBoolean");
        var endLabel = _module.DefineLabel("__coalesce_assign_end");

        _module.AddPush(targetId);
        _module.AddPush(nullConst);
        _module.AddPush(condId);
        AddExternChecked("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean");
        _module.AddPush(condId);
        _module.AddJumpIfFalse(endLabel);

        var rightId = VisitExpression(op.Value);
        _module.AddCopy(rightId, targetId);

        // Write-back for non-local targets using cached sub-expressions
        if (op.Target is IArrayElementReferenceOperation arrayElem)
        {
            var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
            var arrayType = GetArrayType(arrSymbol);
            var elementType = GetArrayElemType(arrSymbol);
            _module.AddPush(cachedArrayId);
            _module.AddPush(cachedIndexId);
            _module.AddPush(rightId);
            AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
        }
        else if (op.Target is IPropertyReferenceOperation propRef && propRef.Property.SetMethod != null)
        {
            var containingType = GetUdonType(propRef.Property.ContainingType);
            var valueType = GetUdonType(propRef.Property.Type);
            var sig = ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType);
            if (cachedInstanceId != null) _module.AddPush(cachedInstanceId);
            _module.AddPush(rightId);
            AddExternChecked(sig);
        }

        _module.MarkLabel(endLabel);
        return targetId;
    }
}
