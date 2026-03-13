using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class OperatorHandler : HandlerBase, IExpressionHandler
{
    public OperatorHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IBinaryOperation
            or IUnaryOperation
            or IConditionalOperation
            or IIsTypeOperation
            or IIsPatternOperation
            or ISwitchExpressionOperation;

    public string Handle(IOperation expression) => expression switch
    {
        IBinaryOperation op => VisitBinary(op),
        IUnaryOperation op => VisitUnary(op),
        IConditionalOperation op => VisitConditionalExpression(op),
        IIsTypeOperation op => VisitIsType(op),
        IIsPatternOperation op => VisitIsPattern(op),
        ISwitchExpressionOperation op => VisitSwitchExpression(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── Binary ──

    string VisitBinary(IBinaryOperation op)
    {
        // Short-circuit evaluation for && and ||
        if (op.OperatorKind == BinaryOperatorKind.ConditionalAnd)
            return VisitConditionalAnd(op);
        if (op.OperatorKind == BinaryOperatorKind.ConditionalOr)
            return VisitConditionalOr(op);

        // Constant folding: compile-time evaluable binary expressions
        if (op.ConstantValue.HasValue)
            return _ctx.Vars.DeclareConst(GetUdonType(op.Type), ToInvariantString(op.ConstantValue.Value));

        // Save and clear hint to prevent premature consumption by sub-expressions
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;

        var leftId = VisitExpression(op.LeftOperand);
        var rightId = VisitExpression(op.RightOperand);

        // Enum operands → convert to underlying type before comparison
        // (Udon VM has no enum-typed operators; pure compiler uses SystemConvert)
        leftId = EmitEnumToUnderlying(leftId, op.LeftOperand.Type);
        rightId = EmitEnumToUnderlying(rightId, op.RightOperand.Type);

        _ctx.TargetHint = savedHint;
        var resultType = GetUdonType(op.Type);
        var tempId = ConsumeTargetHintOrTemp(resultType);
        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.LeftOperand.Type), ResolveType(op.RightOperand.Type), ResolveType(op.Type));

        // UnityEngineObject equality/inequality: cast operands to UnityEngineObject temps
        if (op.OperatorMethod != null
            && GetUdonType(op.OperatorMethod.ContainingType) == "UnityEngineObject"
            && (op.OperatorKind == BinaryOperatorKind.Equals
                || op.OperatorKind == BinaryOperatorKind.NotEquals))
        {
            var objLeft = _ctx.Vars.DeclareTemp("UnityEngineObject");
            _ctx.Module.AddCopy(leftId, objLeft);
            var objRight = _ctx.Vars.DeclareTemp("UnityEngineObject");
            _ctx.Module.AddCopy(rightId, objRight);
            _ctx.Module.AddPush(objLeft);
            _ctx.Module.AddPush(objRight);
        }
        else
        {
            _ctx.Module.AddPush(leftId);
            _ctx.Module.AddPush(rightId);
        }
        _ctx.Module.AddPush(tempId);
        AddExternChecked(sig);

        return tempId;
    }

    string VisitConditionalAnd(IBinaryOperation op)
    {
        // a && b → eval a; if false → result=false; else eval b → result=b
        // Consume TargetHint to write directly into the caller's destination variable,
        // avoiding an extra COPY when the result is assigned (e.g., `bool x = a && b;`).
        var resultId = ConsumeTargetHintOrTemp("SystemBoolean");
        var falseConst = _ctx.Vars.DeclareConst("SystemBoolean", "False");
        var endLabel = _ctx.Module.DefineLabel("__and_end");
        var shortLabel = _ctx.Module.DefineLabel("__and_short");

        var leftId = VisitExpression(op.LeftOperand);
        _ctx.Module.AddPush(leftId);
        _ctx.Module.AddJumpIfFalse(shortLabel);
        // left is true → evaluate right
        var rightId = VisitExpression(op.RightOperand);
        _ctx.Module.AddCopy(rightId, resultId);
        _ctx.Module.AddJump(endLabel);
        // left is false → result = false
        _ctx.Module.MarkLabel(shortLabel);
        _ctx.Module.AddCopy(falseConst, resultId);
        _ctx.Module.MarkLabel(endLabel);

        return resultId;
    }

    string VisitConditionalOr(IBinaryOperation op)
    {
        // a || b → eval a; if true → result=true; else eval b → result=b
        // Consume TargetHint to write directly into the caller's destination variable.
        var resultId = ConsumeTargetHintOrTemp("SystemBoolean");
        var trueConst = _ctx.Vars.DeclareConst("SystemBoolean", "True");
        var endLabel = _ctx.Module.DefineLabel("__or_end");
        var evalRightLabel = _ctx.Module.DefineLabel("__or_right");

        var leftId = VisitExpression(op.LeftOperand);
        _ctx.Module.AddPush(leftId);
        _ctx.Module.AddJumpIfFalse(evalRightLabel);
        // left is true → result = true
        _ctx.Module.AddCopy(trueConst, resultId);
        _ctx.Module.AddJump(endLabel);
        // left is false → evaluate right
        _ctx.Module.MarkLabel(evalRightLabel);
        var rightId = VisitExpression(op.RightOperand);
        _ctx.Module.AddCopy(rightId, resultId);
        _ctx.Module.MarkLabel(endLabel);

        return resultId;
    }

    // ── Unary ──

    string VisitUnary(IUnaryOperation op)
    {
        // Bitwise NOT (~): Udon VM has no unary complement extern → synthesize as XOR with all-bits-set
        if (op.OperatorKind == UnaryOperatorKind.BitwiseNegation)
            return VisitBitwiseNot(op);

        // Constant folding: compile-time evaluable unary expressions (e.g., -5)
        if (op.ConstantValue.HasValue)
            return _ctx.Vars.DeclareConst(GetUdonType(op.Type), ToInvariantString(op.ConstantValue.Value));

        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;
        var operandId = VisitExpression(op.Operand);
        _ctx.TargetHint = savedHint;
        var resultType = GetUdonType(op.Type);
        var tempId = ConsumeTargetHintOrTemp(resultType);

        string sig;
        if (op.OperatorMethod != null && !ExternResolver.IsNumericType(op.Operand.Type))
            sig = BuildExternSignature(op.OperatorMethod);
        else
            sig = BuildBuiltinUnarySignature(op);

        _ctx.Module.AddPush(operandId);
        _ctx.Module.AddPush(tempId);
        AddExternChecked(sig);

        return tempId;
    }

    string VisitBitwiseNot(IUnaryOperation op)
    {
        var operandId = VisitExpression(op.Operand);
        var operandType = GetUdonType(op.Operand.Type);
        var resultType = GetUdonType(op.Type);
        var resultId = _ctx.Vars.DeclareTemp(resultType);

        // ~x ≡ x ^ allBits  (signed: -1 = all bits set, unsigned: MaxValue)
        var allBitsValue = op.Operand.Type.SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int16
                or SpecialType.System_Int64 or SpecialType.System_SByte => "-1",
            SpecialType.System_UInt32 => uint.MaxValue.ToString(),
            SpecialType.System_UInt64 => ulong.MaxValue.ToString(),
            SpecialType.System_UInt16 => ushort.MaxValue.ToString(),
            SpecialType.System_Byte => byte.MaxValue.ToString(),
            _ => throw new System.NotSupportedException(
                $"Bitwise NOT (~) is not supported on type {operandType}")
        };
        var allBitsId = _ctx.Vars.DeclareConst(operandType, allBitsValue);

        _ctx.Module.AddPush(operandId);
        _ctx.Module.AddPush(allBitsId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked(ExternResolver.ResolveBinaryExtern(
            BinaryOperatorKind.ExclusiveOr, null,
            ResolveType(op.Operand.Type), ResolveType(op.Operand.Type), ResolveType(op.Type)));

        return resultId;
    }

    // ── Is-type / Is-pattern ──

    string VisitIsType(IIsTypeOperation op)
    {
        var valueId = VisitExpression(op.ValueOperand);
        var typeConstId = _ctx.Vars.DeclareConst("SystemType",
            GetUdonType(op.TypeOperand));
        var resultId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(typeConstId);
        _ctx.Module.AddPush(valueId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked("SystemType.__IsInstanceOfType__SystemObject__SystemBoolean");
        return resultId;
    }

    string VisitIsPattern(IIsPatternOperation op)
    {
        var valueId = VisitExpression(op.Value);
        return EmitPatternCheckImpl(valueId, op.Value.Type, op.Pattern);
    }

    // ── Pattern matching (public — called from LoopHandler via EmitContext dispatch) ──

    public string EmitPatternCheckImpl(string valueId, ITypeSymbol valueType, IPatternOperation pattern)
    {
        switch (pattern)
        {
            case IConstantPatternOperation constPat:
            {
                var constId = VisitExpression(constPat.Value);
                var eqType = GetUdonType(valueType);
                // Enum comparison → convert operands and use underlying type
                var convertedValueId = EmitEnumToUnderlying(valueId, valueType);
                constId = EmitEnumToUnderlying(constId, valueType);
                if (valueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
                    eqType = GetUdonType(named.EnumUnderlyingType);
                var resultId = _ctx.Vars.DeclareTemp("SystemBoolean");
                _ctx.Module.AddPush(convertedValueId);
                _ctx.Module.AddPush(constId);
                _ctx.Module.AddPush(resultId);
                // null comparisons use SystemObject equality
                var cmpType = constPat.Value.ConstantValue is { HasValue: true, Value: null }
                    ? "SystemObject" : eqType;
                AddExternChecked(ExternResolver.BuildMethodSignature(
                    cmpType, "__op_Equality", new[] { cmpType, cmpType }, "SystemBoolean"));
                return resultId;
            }
            case INegatedPatternOperation negated:
            {
                var innerId = EmitPatternCheckImpl(valueId, valueType, negated.Pattern);
                var negId = _ctx.Vars.DeclareTemp("SystemBoolean");
                _ctx.Module.AddPush(innerId);
                _ctx.Module.AddPush(negId);
                AddExternChecked("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean");
                return negId;
            }
            case ITypePatternOperation typePat:
                return EmitTypeCheck(valueId, typePat.MatchedType);

            case IDeclarationPatternOperation declPat:
            {
                var checkId = EmitTypeCheck(valueId, declPat.MatchedType);
                if (declPat.DeclaredSymbol is ILocalSymbol local)
                {
                    var localType = GetUdonType(local.Type);
                    var localId = _ctx.Vars.DeclareLocal(local.Name, localType);
                    // Only assign when type check succeeds — avoid invalid type COPY on mismatch
                    var skipLabel = _ctx.Module.DefineLabel("__declpat_skip");
                    _ctx.Module.AddPush(checkId);
                    _ctx.Module.AddJumpIfFalse(skipLabel);
                    _ctx.Module.AddCopy(valueId, localId);
                    _ctx.Module.MarkLabel(skipLabel);
                }
                return checkId;
            }
            case IDiscardPatternOperation:
                return _ctx.Vars.DeclareConst("SystemBoolean", "True");

            case IRelationalPatternOperation relPat:
            {
                var constId = VisitExpression(relPat.Value);
                var valType = GetUdonType(valueType);
                var resultId = _ctx.Vars.DeclareTemp("SystemBoolean");
                _ctx.Module.AddPush(valueId);
                _ctx.Module.AddPush(constId);
                _ctx.Module.AddPush(resultId);
                var opName = relPat.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => "__op_LessThan",
                    BinaryOperatorKind.LessThanOrEqual => "__op_LessThanOrEqual",
                    BinaryOperatorKind.GreaterThan => "__op_GreaterThan",
                    BinaryOperatorKind.GreaterThanOrEqual => "__op_GreaterThanOrEqual",
                    _ => throw new System.NotSupportedException(
                        $"Unsupported relational operator: {relPat.OperatorKind}")
                };
                AddExternChecked(ExternResolver.BuildMethodSignature(
                    valType, opName, new[] { valType, valType }, "SystemBoolean"));
                return resultId;
            }
            case IBinaryPatternOperation binPat:
            {
                var resultId = _ctx.Vars.DeclareTemp("SystemBoolean");
                if (binPat.OperatorKind == BinaryOperatorKind.And)
                {
                    // and pattern: short-circuit — skip right if left is false
                    var falseConst = _ctx.Vars.DeclareConst("SystemBoolean", "False");
                    var shortLabel = _ctx.Module.DefineLabel("__patand_short");
                    var endLabel = _ctx.Module.DefineLabel("__patand_end");

                    var leftId = EmitPatternCheckImpl(valueId, valueType, binPat.LeftPattern);
                    _ctx.Module.AddPush(leftId);
                    _ctx.Module.AddJumpIfFalse(shortLabel);
                    var rightId = EmitPatternCheckImpl(valueId, valueType, binPat.RightPattern);
                    _ctx.Module.AddCopy(rightId, resultId);
                    _ctx.Module.AddJump(endLabel);
                    _ctx.Module.MarkLabel(shortLabel);
                    _ctx.Module.AddCopy(falseConst, resultId);
                    _ctx.Module.MarkLabel(endLabel);
                }
                else
                {
                    // or pattern: short-circuit — skip right if left is true
                    var trueConst = _ctx.Vars.DeclareConst("SystemBoolean", "True");
                    var evalRightLabel = _ctx.Module.DefineLabel("__pator_right");
                    var endLabel = _ctx.Module.DefineLabel("__pator_end");

                    var leftId = EmitPatternCheckImpl(valueId, valueType, binPat.LeftPattern);
                    _ctx.Module.AddPush(leftId);
                    _ctx.Module.AddJumpIfFalse(evalRightLabel);
                    _ctx.Module.AddCopy(trueConst, resultId);
                    _ctx.Module.AddJump(endLabel);
                    _ctx.Module.MarkLabel(evalRightLabel);
                    var rightId = EmitPatternCheckImpl(valueId, valueType, binPat.RightPattern);
                    _ctx.Module.AddCopy(rightId, resultId);
                    _ctx.Module.MarkLabel(endLabel);
                }
                return resultId;
            }

            default:
                throw new System.NotSupportedException($"Unsupported pattern: {pattern.GetType().Name}");
        }
    }

    string EmitTypeCheck(string valueId, ITypeSymbol targetType)
    {
        var typeConstId = _ctx.Vars.DeclareConst("SystemType",
            GetUdonType(targetType));
        var resultId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(typeConstId);
        _ctx.Module.AddPush(valueId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked("SystemType.__IsInstanceOfType__SystemObject__SystemBoolean");
        return resultId;
    }

    // ── Switch expression ──

    string VisitSwitchExpression(ISwitchExpressionOperation op)
    {
        var resultType = GetUdonType(op.Type);
        var resultId = _ctx.Vars.DeclareTemp(resultType);
        // Initialize result to default in case no arm matches (non-exhaustive)
        var defaultConst = _ctx.Vars.DeclareConst(resultType, GetDefaultConstValue(resultType));
        _ctx.Module.AddCopy(defaultConst, resultId);
        var valueId = VisitExpression(op.Value);
        var endLabel = _ctx.Module.DefineLabel("__switchexpr_end");

        foreach (var arm in op.Arms)
        {
            var nextArmLabel = _ctx.Module.DefineLabel("__switchexpr_next");

            if (arm.Pattern is not IDiscardPatternOperation)
            {
                var checkId = EmitPatternCheckImpl(valueId, op.Value.Type, arm.Pattern);
                _ctx.Module.AddPush(checkId);
                _ctx.Module.AddJumpIfFalse(nextArmLabel);

                if (arm.Guard != null)
                {
                    var guardId = VisitExpression(arm.Guard);
                    _ctx.Module.AddPush(guardId);
                    _ctx.Module.AddJumpIfFalse(nextArmLabel);
                }
            }

            var armValueId = VisitExpression(arm.Value);
            _ctx.Module.AddCopy(armValueId, resultId);
            _ctx.Module.AddJump(endLabel);
            _ctx.Module.MarkLabel(nextArmLabel);
        }

        _ctx.Module.MarkLabel(endLabel);
        return resultId;
    }

    // ── Conditional (ternary) expression ──

    string VisitConditionalExpression(IConditionalOperation op)
    {
        var resultType = GetUdonType(op.Type);
        var resultId = _ctx.Vars.DeclareTemp(resultType);

        var condId = VisitExpression(op.Condition);
        var elseLabel = _ctx.Module.DefineLabel("__ternary_else");
        var endLabel = _ctx.Module.DefineLabel("__ternary_end");

        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(elseLabel);

        var trueId = VisitExpression(op.WhenTrue);
        _ctx.Module.AddCopy(trueId, resultId);
        _ctx.Module.AddJump(endLabel);

        _ctx.Module.MarkLabel(elseLabel);
        var falseId = VisitExpression(op.WhenFalse);
        _ctx.Module.AddCopy(falseId, resultId);

        _ctx.Module.MarkLabel(endLabel);
        return resultId;
    }

    // ── Extern signature helpers ──

    static readonly Dictionary<UnaryOperatorKind, string> UnaryOpNames = new()
    {
        [UnaryOperatorKind.Minus] = "op_UnaryMinus",
        [UnaryOperatorKind.Not] = "op_UnaryNegation",
    };

    string BuildBuiltinUnarySignature(IUnaryOperation op)
    {
        var operandType = GetUdonType(op.Operand.Type);
        var returnType = GetUdonType(op.Type);
        if (!UnaryOpNames.TryGetValue(op.OperatorKind, out var opName))
            throw new System.NotSupportedException(
                $"Unsupported unary operator: {op.OperatorKind} on type {GetUdonType(op.Operand.Type)}");
        // Decimal uses C# method name: op_UnaryNegation (not op_UnaryMinus)
        if (operandType == "SystemDecimal" && op.OperatorKind == UnaryOperatorKind.Minus)
            opName = "op_UnaryNegation";
        return ExternResolver.BuildMethodSignature(operandType, $"__{opName}", new[] { operandType }, returnType);
    }

    string BuildExternSignature(IMethodSymbol method)
    {
        var containingType = GetUdonType(method.ContainingType);
        var methodName = ExternResolver.GetOperatorExternName(method.Name);
        var paramTypes = method.Parameters.Select(p => GetUdonType(p.Type)).ToArray();
        var returnType = GetUdonType(method.ReturnType);
        return ExternResolver.BuildMethodSignature(containingType, methodName, paramTypes, returnType);
    }

    static string GetDefaultConstValue(string udonType) => udonType switch
    {
        "SystemBoolean" => "False",
        "SystemString" => "null",
        "SystemSingle" or "SystemDouble" or "SystemDecimal" => "0",
        _ => "0"
    };
}
