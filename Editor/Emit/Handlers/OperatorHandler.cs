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
            return _vars.DeclareConst(GetUdonType(op.Type), ToInvariantString(op.ConstantValue.Value));

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
            var objLeft = _vars.DeclareTemp("UnityEngineObject");
            _module.AddCopy(leftId, objLeft);
            var objRight = _vars.DeclareTemp("UnityEngineObject");
            _module.AddCopy(rightId, objRight);
            _module.AddPush(objLeft);
            _module.AddPush(objRight);
        }
        else
        {
            _module.AddPush(leftId);
            _module.AddPush(rightId);
        }
        _module.AddPush(tempId);
        AddExternChecked(sig);

        return tempId;
    }

    string VisitConditionalAnd(IBinaryOperation op)
    {
        // a && b → eval a; if false → result=false; else eval b → result=b
        var resultId = _vars.DeclareTemp("SystemBoolean");
        var falseConst = _vars.DeclareConst("SystemBoolean", "False");
        var endLabel = _module.DefineLabel("__and_end");
        var shortLabel = _module.DefineLabel("__and_short");

        var leftId = VisitExpression(op.LeftOperand);
        _module.AddPush(leftId);
        _module.AddJumpIfFalse(shortLabel);
        // left is true → evaluate right
        var rightId = VisitExpression(op.RightOperand);
        _module.AddCopy(rightId, resultId);
        _module.AddJump(endLabel);
        // left is false → result = false
        _module.MarkLabel(shortLabel);
        _module.AddCopy(falseConst, resultId);
        _module.MarkLabel(endLabel);

        return resultId;
    }

    string VisitConditionalOr(IBinaryOperation op)
    {
        // a || b → eval a; if true → result=true; else eval b → result=b
        var resultId = _vars.DeclareTemp("SystemBoolean");
        var trueConst = _vars.DeclareConst("SystemBoolean", "True");
        var endLabel = _module.DefineLabel("__or_end");
        var evalRightLabel = _module.DefineLabel("__or_right");

        var leftId = VisitExpression(op.LeftOperand);
        _module.AddPush(leftId);
        _module.AddJumpIfFalse(evalRightLabel);
        // left is true → result = true
        _module.AddCopy(trueConst, resultId);
        _module.AddJump(endLabel);
        // left is false → evaluate right
        _module.MarkLabel(evalRightLabel);
        var rightId = VisitExpression(op.RightOperand);
        _module.AddCopy(rightId, resultId);
        _module.MarkLabel(endLabel);

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
            return _vars.DeclareConst(GetUdonType(op.Type), ToInvariantString(op.ConstantValue.Value));

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

        _module.AddPush(operandId);
        _module.AddPush(tempId);
        AddExternChecked(sig);

        return tempId;
    }

    string VisitBitwiseNot(IUnaryOperation op)
    {
        var operandId = VisitExpression(op.Operand);
        var operandType = GetUdonType(op.Operand.Type);
        var resultType = GetUdonType(op.Type);
        var resultId = _vars.DeclareTemp(resultType);

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
        var allBitsId = _vars.DeclareConst(operandType, allBitsValue);

        _module.AddPush(operandId);
        _module.AddPush(allBitsId);
        _module.AddPush(resultId);
        AddExternChecked(ExternResolver.ResolveBinaryExtern(
            BinaryOperatorKind.ExclusiveOr, null,
            ResolveType(op.Operand.Type), ResolveType(op.Operand.Type), ResolveType(op.Type)));

        return resultId;
    }

    // ── Is-type / Is-pattern ──

    string VisitIsType(IIsTypeOperation op)
    {
        var valueId = VisitExpression(op.ValueOperand);
        var typeConstId = _vars.DeclareConst("SystemType",
            GetUdonType(op.TypeOperand));
        var resultId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(typeConstId);
        _module.AddPush(valueId);
        _module.AddPush(resultId);
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
                var resultId = _vars.DeclareTemp("SystemBoolean");
                _module.AddPush(convertedValueId);
                _module.AddPush(constId);
                _module.AddPush(resultId);
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
                var negId = _vars.DeclareTemp("SystemBoolean");
                _module.AddPush(innerId);
                _module.AddPush(negId);
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
                    var localId = _vars.DeclareLocal(local.Name, localType);
                    // Only assign when type check succeeds — avoid invalid type COPY on mismatch
                    var skipLabel = _module.DefineLabel("__declpat_skip");
                    _module.AddPush(checkId);
                    _module.AddJumpIfFalse(skipLabel);
                    _module.AddCopy(valueId, localId);
                    _module.MarkLabel(skipLabel);
                }
                return checkId;
            }
            case IDiscardPatternOperation:
                return _vars.DeclareConst("SystemBoolean", "True");

            case IRelationalPatternOperation relPat:
            {
                var constId = VisitExpression(relPat.Value);
                var valType = GetUdonType(valueType);
                var resultId = _vars.DeclareTemp("SystemBoolean");
                _module.AddPush(valueId);
                _module.AddPush(constId);
                _module.AddPush(resultId);
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
                var leftId = EmitPatternCheckImpl(valueId, valueType, binPat.LeftPattern);
                var rightId = EmitPatternCheckImpl(valueId, valueType, binPat.RightPattern);
                var resultId = _vars.DeclareTemp("SystemBoolean");
                _module.AddPush(leftId);
                _module.AddPush(rightId);
                _module.AddPush(resultId);
                var opName = binPat.OperatorKind == BinaryOperatorKind.And
                    ? "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean"
                    : "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean";
                AddExternChecked(opName);
                return resultId;
            }

            default:
                throw new System.NotSupportedException($"Unsupported pattern: {pattern.GetType().Name}");
        }
    }

    string EmitTypeCheck(string valueId, ITypeSymbol targetType)
    {
        var typeConstId = _vars.DeclareConst("SystemType",
            GetUdonType(targetType));
        var resultId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(typeConstId);
        _module.AddPush(valueId);
        _module.AddPush(resultId);
        AddExternChecked("SystemType.__IsInstanceOfType__SystemObject__SystemBoolean");
        return resultId;
    }

    // ── Switch expression ──

    string VisitSwitchExpression(ISwitchExpressionOperation op)
    {
        var resultType = GetUdonType(op.Type);
        var resultId = _vars.DeclareTemp(resultType);
        // Initialize result to default in case no arm matches (non-exhaustive)
        var defaultConst = _vars.DeclareConst(resultType, GetDefaultConstValue(resultType));
        _module.AddCopy(defaultConst, resultId);
        var valueId = VisitExpression(op.Value);
        var endLabel = _module.DefineLabel("__switchexpr_end");

        foreach (var arm in op.Arms)
        {
            var nextArmLabel = _module.DefineLabel("__switchexpr_next");

            if (arm.Pattern is not IDiscardPatternOperation)
            {
                var checkId = EmitPatternCheckImpl(valueId, op.Value.Type, arm.Pattern);
                _module.AddPush(checkId);
                _module.AddJumpIfFalse(nextArmLabel);

                if (arm.Guard != null)
                {
                    var guardId = VisitExpression(arm.Guard);
                    _module.AddPush(guardId);
                    _module.AddJumpIfFalse(nextArmLabel);
                }
            }

            var armValueId = VisitExpression(arm.Value);
            _module.AddCopy(armValueId, resultId);
            _module.AddJump(endLabel);
            _module.MarkLabel(nextArmLabel);
        }

        _module.MarkLabel(endLabel);
        return resultId;
    }

    // ── Conditional (ternary) expression ──

    string VisitConditionalExpression(IConditionalOperation op)
    {
        var resultType = GetUdonType(op.Type);
        var resultId = _vars.DeclareTemp(resultType);

        var condId = VisitExpression(op.Condition);
        var elseLabel = _module.DefineLabel("__ternary_else");
        var endLabel = _module.DefineLabel("__ternary_end");

        _module.AddPush(condId);
        _module.AddJumpIfFalse(elseLabel);

        var trueId = VisitExpression(op.WhenTrue);
        _module.AddCopy(trueId, resultId);
        _module.AddJump(endLabel);

        _module.MarkLabel(elseLabel);
        var falseId = VisitExpression(op.WhenFalse);
        _module.AddCopy(falseId, resultId);

        _module.MarkLabel(endLabel);
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
