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
        IConditionalAccessInstanceOperation => _ctx.ConditionalAccessTargets.Peek(),
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
            resultId = _ctx.Vars.DeclareTemp(resultType);
            var defaultConst = _ctx.Vars.DeclareConst(resultType, "null");
            _ctx.Module.AddCopy(defaultConst, resultId);
        }

        var targetId = VisitExpression(op.Operation);

        var nullConst = _ctx.Vars.DeclareConst("SystemObject", "null");
        var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
        var endLabel = _ctx.Module.DefineLabel("__condaccess_end");

        // condId = (target != null); JIF → jump when false (target IS null) → skip access
        _ctx.Module.AddPush(targetId);
        _ctx.Module.AddPush(nullConst);
        _ctx.Module.AddPush(condId);
        AddExternChecked("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean");
        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(endLabel);

        // target is not null → evaluate WhenNotNull with target as the instance
        _ctx.ConditionalAccessTargets.Push(targetId);
        var accessId = VisitExpression(op.WhenNotNull);
        _ctx.ConditionalAccessTargets.Pop();

        if (!isVoid && accessId != null)
            _ctx.Module.AddCopy(accessId, resultId);

        _ctx.Module.MarkLabel(endLabel);
        return resultId;
    }

    string VisitCoalesce(ICoalesceOperation op)
    {
        // a ?? b → var r = a; if (r == null) r = b;
        var resultType = GetUdonType(op.Type);
        var resultId = _ctx.Vars.DeclareTemp(resultType);
        var leftId = VisitExpression(op.Value);
        _ctx.Module.AddCopy(leftId, resultId);

        var nullConst = _ctx.Vars.DeclareConst("SystemObject", "null");
        var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
        var endLabel = _ctx.Module.DefineLabel("__coalesce_end");

        // condId = (left == null); JIF → jump when false (left NOT null) → skip right
        _ctx.Module.AddPush(leftId);
        _ctx.Module.AddPush(nullConst);
        _ctx.Module.AddPush(condId);
        AddExternChecked("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean");
        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(endLabel);
        // left IS null → use right
        var rightId = VisitExpression(op.WhenNull);
        _ctx.Module.AddCopy(rightId, resultId);
        _ctx.Module.MarkLabel(endLabel);

        return resultId;
    }

    string VisitCoalesceAssignment(ICoalesceAssignmentOperation op)
    {
        // x ??= expr → if (x == null) x = expr; return x
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var targetId = lv.ValueId;

        var nullConst = _ctx.Vars.DeclareConst("SystemObject", "null");
        var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
        var endLabel = _ctx.Module.DefineLabel("__coalesce_assign_end");

        _ctx.Module.AddPush(targetId);
        _ctx.Module.AddPush(nullConst);
        _ctx.Module.AddPush(condId);
        AddExternChecked("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean");
        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(endLabel);

        var rightId = VisitExpression(op.Value);
        _ctx.Module.AddCopy(rightId, targetId);

        EmitWriteBack(op.Target, rightId, lv);

        _ctx.Module.MarkLabel(endLabel);
        return targetId;
    }
}
