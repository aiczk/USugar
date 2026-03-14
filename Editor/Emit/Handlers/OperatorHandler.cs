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

    public HExpr Handle(IOperation expression) => expression switch
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

    HExpr VisitBinary(IBinaryOperation op)
    {
        // Short-circuit evaluation for && and ||
        if (op.OperatorKind == BinaryOperatorKind.ConditionalAnd)
            return VisitConditionalAnd(op);
        if (op.OperatorKind == BinaryOperatorKind.ConditionalOr)
            return VisitConditionalOr(op);

        // Constant folding: compile-time evaluable binary expressions
        if (op.ConstantValue.HasValue)
        {
            var constType = GetUdonType(op.Type);
            return Const(EmitContext.ParseConstValue(constType, ToInvariantString(op.ConstantValue.Value)), constType);
        }

        var leftVal = VisitExpression(op.LeftOperand);
        var rightVal = VisitExpression(op.RightOperand);

        // Enum operands → convert to underlying type before comparison
        leftVal = EmitEnumToUnderlying(leftVal, op.LeftOperand.Type);
        rightVal = EmitEnumToUnderlying(rightVal, op.RightOperand.Type);

        var resultType = GetUdonType(op.Type);
        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.LeftOperand.Type), ResolveType(op.RightOperand.Type), ResolveType(op.Type));

        // UnityEngineObject equality/inequality: cast operands to UnityEngineObject temps
        if (op.OperatorMethod != null
            && GetUdonType(op.OperatorMethod.ContainingType) == "UnityEngineObject"
            && (op.OperatorKind == BinaryOperatorKind.Equals
                || op.OperatorKind == BinaryOperatorKind.NotEquals))
        {
            var objLeftField = _ctx.DeclareTemp("UnityEngineObject");
            EmitStoreField(objLeftField, leftVal);
            var objRightField = _ctx.DeclareTemp("UnityEngineObject");
            EmitStoreField(objRightField, rightVal);
            var objLeftVal = LoadField(objLeftField, "UnityEngineObject");
            var objRightVal = LoadField(objRightField, "UnityEngineObject");
            return ExternCall(sig, new List<HExpr> { objLeftVal, objRightVal }, resultType);
        }

        return ExternCall(sig, new List<HExpr> { leftVal, rightVal }, resultType);
    }

    HExpr VisitConditionalAnd(IBinaryOperation op)
    {
        // a && b → Select(a, b, false)
        var leftVal = VisitExpression(op.LeftOperand);
        var rightVal = VisitExpression(op.RightOperand);
        return Select(leftVal, rightVal, Const(false, "SystemBoolean"), "SystemBoolean");
    }

    HExpr VisitConditionalOr(IBinaryOperation op)
    {
        // a || b → Select(a, true, b)
        var leftVal = VisitExpression(op.LeftOperand);
        var rightVal = VisitExpression(op.RightOperand);
        return Select(leftVal, Const(true, "SystemBoolean"), rightVal, "SystemBoolean");
    }

    // ── Unary ──

    HExpr VisitUnary(IUnaryOperation op)
    {
        // Bitwise NOT (~): Udon VM has no unary complement extern → synthesize as XOR with all-bits-set
        if (op.OperatorKind == UnaryOperatorKind.BitwiseNegation)
            return VisitBitwiseNot(op);

        // Constant folding: compile-time evaluable unary expressions (e.g., -5)
        if (op.ConstantValue.HasValue)
        {
            var constType = GetUdonType(op.Type);
            return Const(EmitContext.ParseConstValue(constType, ToInvariantString(op.ConstantValue.Value)), constType);
        }

        var operandVal = VisitExpression(op.Operand);
        var resultType = GetUdonType(op.Type);

        string sig;
        if (op.OperatorMethod != null && !ExternResolver.IsNumericType(op.Operand.Type))
            sig = BuildExternSignature(op.OperatorMethod);
        else
            sig = BuildBuiltinUnarySignature(op);

        return ExternCall(sig, new List<HExpr> { operandVal }, resultType);
    }

    HExpr VisitBitwiseNot(IUnaryOperation op)
    {
        var operandVal = VisitExpression(op.Operand);
        var operandType = GetUdonType(op.Operand.Type);
        var resultType = GetUdonType(op.Type);

        // ~x ≡ x ^ allBits  (signed: -1 = all bits set, unsigned: MaxValue)
        object allBitsValue = op.Operand.Type.SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int16
                or SpecialType.System_Int64 or SpecialType.System_SByte => EmitContext.ParseConstValue(operandType, "-1"),
            SpecialType.System_UInt32 => uint.MaxValue,
            SpecialType.System_UInt64 => ulong.MaxValue,
            SpecialType.System_UInt16 => ushort.MaxValue,
            SpecialType.System_Byte => byte.MaxValue,
            _ => throw new System.NotSupportedException(
                $"Bitwise NOT (~) is not supported on type {operandType}")
        };
        var allBitsConst = Const(allBitsValue, operandType);

        return ExternCall(
            ExternResolver.ResolveBinaryExtern(
                BinaryOperatorKind.ExclusiveOr, null,
                ResolveType(op.Operand.Type), ResolveType(op.Operand.Type), ResolveType(op.Type)),
            new List<HExpr> { operandVal, allBitsConst },
            resultType);
    }

    // ── Is-type / Is-pattern ──

    HExpr VisitIsType(IIsTypeOperation op)
    {
        var valueVal = VisitExpression(op.ValueOperand);
        var typeConst = Const(GetUdonType(op.TypeOperand), "SystemType");
        return ExternCall(
            "SystemType.__IsInstanceOfType__SystemObject__SystemBoolean",
            new List<HExpr> { typeConst, valueVal },
            "SystemBoolean");
    }

    HExpr VisitIsPattern(IIsPatternOperation op)
    {
        var valueVal = VisitExpression(op.Value);
        return EmitPatternCheckImpl(valueVal, op.Value.Type, op.Pattern);
    }

    // ── Pattern matching (public — called from LoopHandler via EmitContext dispatch) ──

    public HExpr EmitPatternCheckImpl(HExpr valueVal, ITypeSymbol valueType, IPatternOperation pattern)
    {
        switch (pattern)
        {
            case IConstantPatternOperation constPat:
            {
                var constVal = VisitExpression(constPat.Value);
                var eqType = GetUdonType(valueType);
                // Enum comparison → convert operands and use underlying type
                var convertedValueVal = EmitEnumToUnderlying(valueVal, valueType);
                constVal = EmitEnumToUnderlying(constVal, valueType);
                if (valueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
                    eqType = GetUdonType(named.EnumUnderlyingType);
                // null comparisons use SystemObject equality
                var cmpType = constPat.Value.ConstantValue is { HasValue: true, Value: null }
                    ? "SystemObject" : eqType;
                return ExternCall(
                    ExternResolver.BuildMethodSignature(
                        cmpType, "__op_Equality", new[] { cmpType, cmpType }, "SystemBoolean"),
                    new List<HExpr> { convertedValueVal, constVal },
                    "SystemBoolean");
            }
            case INegatedPatternOperation negated:
            {
                var innerVal = EmitPatternCheckImpl(valueVal, valueType, negated.Pattern);
                return ExternCall(
                    "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<HExpr> { innerVal },
                    "SystemBoolean");
            }
            case ITypePatternOperation typePat:
                return EmitTypeCheck(valueVal, typePat.MatchedType);

            case IDeclarationPatternOperation declPat:
            {
                var checkVal = EmitTypeCheck(valueVal, declPat.MatchedType);
                if (declPat.DeclaredSymbol is ILocalSymbol local)
                {
                    var localType = GetUdonType(local.Type);
                    var localId = _ctx.DeclareLocal(local.Name, localType);
                    _localVarIds[local] = localId;
                    // Only assign when type check succeeds — avoid invalid type COPY on mismatch
                    _builder.EmitIf(checkVal, b =>
                    {
                        EmitStoreField(localId, valueVal);
                    });
                }
                return checkVal;
            }
            case IDiscardPatternOperation:
                return Const(true, "SystemBoolean");

            case IRelationalPatternOperation relPat:
            {
                var constVal = VisitExpression(relPat.Value);
                var valType = GetUdonType(valueType);
                var opName = relPat.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => "__op_LessThan",
                    BinaryOperatorKind.LessThanOrEqual => "__op_LessThanOrEqual",
                    BinaryOperatorKind.GreaterThan => "__op_GreaterThan",
                    BinaryOperatorKind.GreaterThanOrEqual => "__op_GreaterThanOrEqual",
                    _ => throw new System.NotSupportedException(
                        $"Unsupported relational operator: {relPat.OperatorKind}")
                };
                return ExternCall(
                    ExternResolver.BuildMethodSignature(
                        valType, opName, new[] { valType, valType }, "SystemBoolean"),
                    new List<HExpr> { valueVal, constVal },
                    "SystemBoolean");
            }
            case IBinaryPatternOperation binPat:
            {
                var leftVal = EmitPatternCheckImpl(valueVal, valueType, binPat.LeftPattern);
                var rightVal = EmitPatternCheckImpl(valueVal, valueType, binPat.RightPattern);
                var opName = binPat.OperatorKind == BinaryOperatorKind.And
                    ? "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean"
                    : "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean";
                return ExternCall(opName, new List<HExpr> { leftVal, rightVal }, "SystemBoolean");
            }

            default:
                throw new System.NotSupportedException($"Unsupported pattern: {pattern.GetType().Name}");
        }
    }

    HExpr EmitTypeCheck(HExpr valueVal, ITypeSymbol targetType)
    {
        var typeConst = Const(GetUdonType(targetType), "SystemType");
        return ExternCall(
            "SystemType.__IsInstanceOfType__SystemObject__SystemBoolean",
            new List<HExpr> { typeConst, valueVal },
            "SystemBoolean");
    }

    // ── Switch expression ──

    HExpr VisitSwitchExpression(ISwitchExpressionOperation op)
    {
        var resultType = GetUdonType(op.Type);
        var resultField = _ctx.DeclareTemp(resultType);
        // Initialize result to default in case no arm matches (non-exhaustive)
        EmitStoreField(resultField, Const(
            EmitContext.ParseConstValue(resultType, GetDefaultConstValue(resultType)), resultType));
        var valueVal = VisitExpression(op.Value);

        foreach (var arm in op.Arms)
        {
            if (arm.Pattern is IDiscardPatternOperation)
            {
                // Default arm — always matches
                var armVal = VisitExpression(arm.Value);
                EmitStoreField(resultField, armVal);
            }
            else
            {
                var checkVal = EmitPatternCheckImpl(valueVal, op.Value.Type, arm.Pattern);

                if (arm.Guard != null)
                {
                    // Pattern match + guard: both must be true
                    _builder.EmitIf(checkVal, b =>
                    {
                        var guardVal = VisitExpression(arm.Guard);
                        _builder.EmitIf(guardVal, b2 =>
                        {
                            var armVal = VisitExpression(arm.Value);
                            EmitStoreField(resultField, armVal);
                        });
                    });
                }
                else
                {
                    _builder.EmitIf(checkVal, b =>
                    {
                        var armVal = VisitExpression(arm.Value);
                        EmitStoreField(resultField, armVal);
                    });
                }
            }
        }

        return LoadField(resultField, resultType);
    }

    // ── Conditional (ternary) expression ──

    HExpr VisitConditionalExpression(IConditionalOperation op)
    {
        var condVal = VisitExpression(op.Condition);
        var trueVal = VisitExpression(op.WhenTrue);
        var falseVal = VisitExpression(op.WhenFalse);
        var resultType = GetUdonType(op.Type);
        return Select(condVal, trueVal, falseVal, resultType);
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
