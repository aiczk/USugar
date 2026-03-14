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
            var objLeftSlot = _ctx.AllocTemp("UnityEngineObject");
            EmitAssign(objLeftSlot, leftVal);
            var objRightSlot = _ctx.AllocTemp("UnityEngineObject");
            EmitAssign(objRightSlot, rightVal);
            return ExternCall(sig, new List<HExpr> { SlotRef(objLeftSlot), SlotRef(objRightSlot) }, resultType);
        }

        return ExternCall(sig, new List<HExpr> { leftVal, rightVal }, resultType);
    }

    HExpr VisitConditionalAnd(IBinaryOperation op)
    {
        // a && b: evaluate b only when a is true (short-circuit).
        // VisitExpression on operands may emit HIR statements (e.g. temp stores for
        // enum conversions, UnityEngineObject casts). Those statements must live inside
        // the conditional branch so they don't execute unconditionally.
        var leftVal = VisitExpression(op.LeftOperand);
        var resultSlot = _ctx.AllocTemp("SystemBoolean");
        EmitAssign(resultSlot, Const(false, "SystemBoolean"));
        _builder.EmitIf(leftVal, _ =>
        {
            var rightVal = VisitExpression(op.RightOperand);
            EmitAssign(resultSlot, rightVal);
        });
        return SlotRef(resultSlot);
    }

    HExpr VisitConditionalOr(IBinaryOperation op)
    {
        // a || b: evaluate b only when a is false (short-circuit).
        var leftVal = VisitExpression(op.LeftOperand);
        var resultSlot = _ctx.AllocTemp("SystemBoolean");
        EmitAssign(resultSlot, Const(true, "SystemBoolean"));
        _builder.EmitIf(leftVal, null, _ =>
        {
            var rightVal = VisitExpression(op.RightOperand);
            EmitAssign(resultSlot, rightVal);
        });
        return SlotRef(resultSlot);
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
        var resultSlot = _ctx.AllocTemp(resultType);
        // Initialize result to default in case no arm matches (non-exhaustive)
        EmitAssign(resultSlot, Const(
            EmitContext.ParseConstValue(resultType, GetDefaultConstValue(resultType)), resultType));
        var valueVal = VisitExpression(op.Value);

        // Separate default arm from pattern arms to build proper if/else-if/else chain
        var patternArms = new List<ISwitchExpressionArmOperation>();
        ISwitchExpressionArmOperation defaultArm = null;
        foreach (var arm in op.Arms)
        {
            if (arm.Pattern is IDiscardPatternOperation)
                defaultArm = arm;
            else
                patternArms.Add(arm);
        }

        // Build nested if/else-if/else chain from inside out (reverse order)
        // Each level: if (check) { result = armVal } else { <next level> }
        // The innermost else is the default arm (if any).
        System.Action<HirBuilder> tail = null;
        if (defaultArm != null)
        {
            var defArm = defaultArm;
            tail = _ =>
            {
                var armVal = VisitExpression(defArm.Value);
                EmitAssign(resultSlot, armVal);
            };
        }

        for (int i = patternArms.Count - 1; i >= 0; i--)
        {
            var arm = patternArms[i];
            var elseBranch = tail;
            tail = _ =>
            {
                var checkVal = EmitPatternCheckImpl(valueVal, op.Value.Type, arm.Pattern);

                if (arm.Guard != null)
                {
                    // Pattern match + guard: combine with &&
                    _builder.EmitIf(checkVal, __ =>
                    {
                        var guardVal = VisitExpression(arm.Guard);
                        _builder.EmitIf(guardVal, ___ =>
                        {
                            var armVal = VisitExpression(arm.Value);
                            EmitAssign(resultSlot, armVal);
                        }, elseBranch);
                    }, elseBranch);
                }
                else
                {
                    _builder.EmitIf(checkVal, __ =>
                    {
                        var armVal = VisitExpression(arm.Value);
                        EmitAssign(resultSlot, armVal);
                    }, elseBranch);
                }
            };
        }

        // Emit the chain
        tail?.Invoke(null);

        return SlotRef(resultSlot);
    }

    // ── Conditional (ternary) expression ──

    HExpr VisitConditionalExpression(IConditionalOperation op)
    {
        // cond ? a : b: evaluate branches only on the taken path.
        var condVal = VisitExpression(op.Condition);
        var resultType = GetUdonType(op.Type);
        var resultSlot = _ctx.AllocTemp(resultType);
        _builder.EmitIf(condVal,
            _ => EmitAssign(resultSlot, VisitExpression(op.WhenTrue)),
            _ => EmitAssign(resultSlot, VisitExpression(op.WhenFalse)));
        return SlotRef(resultSlot);
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
