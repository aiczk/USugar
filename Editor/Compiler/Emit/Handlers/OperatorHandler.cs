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
            or ISwitchExpressionOperation
            or ITupleBinaryOperation;

    public CValue Handle(IOperation expression) => expression switch
    {
        ITupleBinaryOperation op => VisitTupleBinary(op),
        IBinaryOperation op => VisitBinary(op),
        IUnaryOperation op => VisitUnary(op),
        IConditionalOperation op => VisitConditionalExpression(op),
        IIsTypeOperation op => VisitIsType(op),
        IIsPatternOperation op => VisitIsPattern(op),
        ISwitchExpressionOperation op => VisitSwitchExpression(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── Binary ──

    CValue VisitBinary(IBinaryOperation op)
    {
        // Short-circuit evaluation for && and ||
        if (op.OperatorKind == BinaryOperatorKind.ConditionalAnd)
            return VisitConditionalAnd(op);
        if (op.OperatorKind == BinaryOperatorKind.ConditionalOr)
            return VisitConditionalOr(op);

        // ── User-defined struct operator: v1 + v2 → static operator method call ──
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } binOpM
            && binOpM.ContainingType is INamedTypeSymbol binOpCt && EmitContext.IsUserStruct(binOpCt)
            && _methodFunctions.ContainsKey(binOpM.OriginalDefinition))
        {
            var lhs = VisitExpression(op.LeftOperand);
            var rhs = VisitExpression(op.RightOperand);
            return EmitCallToMethod(binOpM.OriginalDefinition, new List<CValue> { lhs, rhs });
        }

        // ── Delegate field null check / comparison ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var leftField = TryGetDelegateFieldName(op.LeftOperand);
            var rightField = TryGetDelegateFieldName(op.RightOperand);
            bool leftIsNull = IsNullLiteral(op.LeftOperand);
            bool rightIsNull = IsNullLiteral(op.RightOperand);
            bool isNotEquals = op.OperatorKind == BinaryOperatorKind.NotEquals;

            // delegate == null / delegate != null
            if (leftField != null && rightIsNull)
                return CompareDelegateToNull(new DelegateBundle(leftField).Target, isNotEquals);
            if (rightField != null && leftIsNull)
                return CompareDelegateToNull(new DelegateBundle(rightField).Target, isNotEquals);

            // delegate == delegate
            if (leftField != null && rightField != null)
                return CompareDelegates(leftField, rightField, isNotEquals);
        }

        // ── Nullable (boxed object) compared to null literal → object reference null check ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            bool leftNullable = EmitContext.IsNullableT(op.LeftOperand.Type, out _);
            bool rightNullable = EmitContext.IsNullableT(op.RightOperand.Type, out _);
            bool leftNull = IsNullLiteral(op.LeftOperand);
            bool rightNull = IsNullLiteral(op.RightOperand);
            if ((leftNullable && rightNull) || (rightNullable && leftNull))
            {
                var nv = VisitExpression(leftNullable ? op.LeftOperand : op.RightOperand);
                if (op.OperatorKind == BinaryOperatorKind.NotEquals)
                    return EmitNullableHasValue(nv); // != null  ⇔  HasValue
                return ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
                    new List<CValue> { nv, Const(null, "SystemObject") }, "SystemBoolean");
            }
        }

        // ── Nullable bool & / | : C# three-valued logic (false & null = false, true | null = true) ──
        if (op.IsLifted && (op.OperatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or)
            && EmitContext.IsNullableT(op.Type, out var boolUnder) && boolUnder.SpecialType == SpecialType.System_Boolean)
        {
            return EmitLiftedBoolLogic(op);
        }

        // ── Lifted operator on Nullable<T> (null propagation) ──
        if (op.IsLifted
            && (EmitContext.IsNullableT(op.LeftOperand.Type, out _) || EmitContext.IsNullableT(op.RightOperand.Type, out _)))
        {
            return EmitLiftedBinary(op);
        }

        // ── Aggregate (tuple) structural equality ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && EmitContext.IsAggregateType(op.LeftOperand.Type)
            && op.LeftOperand.Type is INamedTypeSymbol aggType)
        {
            return EmitAggregateEquality(op, aggType);
        }

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

        // long % long / ulong % ulong: Udon has no SystemInt64/SystemUInt64 op_Remainder extern;
        // lower to a - (a / b) * b via the shared polyfill.
        if (op.OperatorKind == BinaryOperatorKind.Remainder && Is64BitInt(resultType))
            return EmitInt64Remainder(leftVal, rightVal, resultType);

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
            return ExternCall(sig, new List<CValue> { SlotRef(objLeftSlot), SlotRef(objRightSlot) }, resultType);
        }

        return ExternCall(sig, new List<CValue> { leftVal, rightVal }, resultType);
    }

    // Nullable bool `&` / `|` with C# three-valued logic: a known false dominates `&` (false & null = false)
    // and a known true dominates `|` (true | null = true), regardless of the other operand being null.
    CValue EmitLiftedBoolLogic(IBinaryOperation op)
    {
        var aSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(aSlot, VisitExpression(op.LeftOperand));
        var bSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(bSlot, VisitExpression(op.RightOperand));

        void IfBool(int slot, bool wantTrue, System.Action<CoreBuilder> body)
        {
            CValue boolCond = wantTrue
                ? SlotRef(slot) // boxed bool used directly (Udon unboxes for the branch test)
                : ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CValue> { SlotRef(slot) }, "SystemBoolean");
            _builder.EmitIf(EmitNullableHasValue(SlotRef(slot)), _ => _builder.EmitIf(boolCond, body));
        }

        var rSlot = _ctx.AllocTemp("SystemObject");
        EmitAssign(rSlot, Const(null, "SystemObject"));
        bool isAnd = op.OperatorKind == BinaryOperatorKind.And;
        // dominating value: false for &, true for |
        IfBool(aSlot, !isAnd, _ => EmitAssign(rSlot, Const(!isAnd, "SystemBoolean")));
        IfBool(bSlot, !isAnd, _ => EmitAssign(rSlot, Const(!isAnd, "SystemBoolean")));
        // both the non-dominating value (both true for &, both false for |) → the non-dominating result
        IfBool(aSlot, isAnd, _ => IfBool(bSlot, isAnd, __ => EmitAssign(rSlot, Const(isAnd, "SystemBoolean"))));
        return SlotRef(rSlot);
    }

    // Lifted binary operator on Nullable<T> (null propagation) — see HandlerBase.EmitLiftedBinaryCore.
    CValue EmitLiftedBinary(IBinaryOperation op)
    {
        var leftNullable = EmitContext.IsNullableT(op.LeftOperand.Type, out var lu);
        var rightNullable = EmitContext.IsNullableT(op.RightOperand.Type, out var ru);
        var leftVal = VisitExpression(op.LeftOperand);
        var rightVal = VisitExpression(op.RightOperand);
        return EmitLiftedBinaryCore(
            leftVal, leftNullable, leftNullable ? lu : op.LeftOperand.Type,
            rightVal, rightNullable, rightNullable ? ru : op.RightOperand.Type,
            op.OperatorKind, op.OperatorMethod, op.Type);
    }

    CValue VisitConditionalAnd(IBinaryOperation op)
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

    CValue VisitConditionalOr(IBinaryOperation op)
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

    CValue VisitUnary(IUnaryOperation op)
    {
        // Bitwise NOT (~): Udon VM has no unary complement extern → synthesize as XOR with all-bits-set
        if (op.OperatorKind == UnaryOperatorKind.BitwiseNegation)
            return VisitBitwiseNot(op);

        // ── User-defined struct operator: -v → static operator method call ──
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } unOpM
            && unOpM.ContainingType is INamedTypeSymbol unOpCt && EmitContext.IsUserStruct(unOpCt)
            && _methodFunctions.ContainsKey(unOpM.OriginalDefinition))
        {
            var operand = VisitExpression(op.Operand);
            return EmitCallToMethod(unOpM.OriginalDefinition, new List<CValue> { operand });
        }

        // Constant folding: compile-time evaluable unary expressions (e.g., -5)
        if (op.ConstantValue.HasValue)
        {
            var constType = GetUdonType(op.Type);
            return Const(EmitContext.ParseConstValue(constType, ToInvariantString(op.ConstantValue.Value)), constType);
        }

        // Lifted unary on Nullable<T> (null propagation): null stays null, else apply the op to the unwrapped
        // value. The non-lifted path below would build an invalid extern on the Nullable operand type.
        if (op.IsLifted && EmitContext.IsNullableT(op.Type, out var unaryResU))
            return EmitLiftedUnary(op, unaryResU);

        var operandVal = VisitExpression(op.Operand);
        var resultType = GetUdonType(op.Type);

        string sig;
        if (op.OperatorMethod != null && !ExternResolver.IsNumericType(op.Operand.Type))
            sig = BuildExternSignature(op.OperatorMethod);
        else
            sig = BuildBuiltinUnarySignature(op);

        return ExternCall(sig, new List<CValue> { operandVal }, resultType);
    }

    // Lifted unary minus / logical-not on Nullable<T>: null-preserving. A small-int operand is promoted to
    // the (int) result underlying for the SystemInt32 op; int/long/float/bool pass through. The unwrapped
    // result is re-boxed into the SystemObject nullable slot. (Lifted bitwise ~ is not covered here.)
    CValue EmitLiftedUnary(IUnaryOperation op, ITypeSymbol resUnderlying)
    {
        EmitContext.IsNullableT(op.Operand.Type, out var opUnderlying);
        var resU = GetUdonType(resUnderlying);
        var opName = op.OperatorKind switch
        {
            UnaryOperatorKind.Not => "op_UnaryNegation",
            UnaryOperatorKind.Minus => resU == "SystemDecimal" ? "op_UnaryNegation" : "op_UnaryMinus",
            _ => throw new System.NotSupportedException($"Unsupported lifted unary operator: {op.OperatorKind}")
        };

        var nSlot = _ctx.AllocTemp("SystemObject");
        EmitAssign(nSlot, VisitExpression(op.Operand));
        var resSlot = _ctx.AllocTemp("SystemObject");
        EmitAssign(resSlot, Const(null, "SystemObject"));
        _builder.EmitIf(EmitNullableHasValue(SlotRef(nSlot)), _ =>
        {
            var v = PromoteBoxedToInt32(SlotRef(nSlot), opUnderlying, out var _eff);
            var computed = ExternCall(
                ExternResolver.BuildMethodSignature(resU, $"__{opName}", new[] { resU }, resU),
                new List<CValue> { v }, resU);
            EmitAssign(resSlot, computed); // re-box into the nullable's SystemObject slot
        });
        return SlotRef(resSlot);
    }

    CValue VisitBitwiseNot(IUnaryOperation op)
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
            new List<CValue> { operandVal, allBitsConst },
            resultType);
    }

    // ── Is-type / Is-pattern ──

    CValue VisitIsType(IIsTypeOperation op)
    {
        var valueVal = VisitExpression(op.ValueOperand);
        var typeConst = Const(GetUdonType(op.TypeOperand), "SystemType");
        return ExternCall(
            "SystemType.__IsInstanceOfType__SystemObject__SystemBoolean",
            new List<CValue> { typeConst, valueVal },
            "SystemBoolean");
    }

    CValue VisitIsPattern(IIsPatternOperation op)
    {
        var valueVal = VisitExpression(op.Value);
        return EmitPatternCheckImpl(valueVal, op.Value.Type, op.Pattern);
    }

    // ── Pattern matching (public — called from LoopHandler via EmitContext dispatch) ──

    public CValue EmitPatternCheckImpl(CValue valueVal, ITypeSymbol valueType, IPatternOperation pattern)
    {
        // Nullable<T> scrutinee (boxed object): `x is null` is an object null check; any other pattern
        // requires HasValue, then matches against the unboxed underlying value (Udon unboxes transparently).
        if (EmitContext.IsNullableT(valueType, out var patUnderlying))
        {
            var nSlot = _ctx.AllocTemp("SystemObject");
            EmitAssign(nSlot, valueVal);
            if (pattern is IConstantPatternOperation cpn && cpn.Value.ConstantValue is { HasValue: true, Value: null })
                return ExternCall("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
                    new List<CValue> { SlotRef(nSlot), Const(null, "SystemObject") }, "SystemBoolean");
            var matchSlot = _ctx.AllocTemp("SystemBoolean");
            EmitAssign(matchSlot, Const(false, "SystemBoolean"));
            _builder.EmitIf(EmitNullableHasValue(SlotRef(nSlot)), _ =>
                EmitAssign(matchSlot, EmitPatternCheckImpl(SlotRef(nSlot), patUnderlying, pattern)));
            return SlotRef(matchSlot);
        }

        switch (pattern)
        {
            case IConstantPatternOperation constPat:
            {
                var constVal = VisitExpression(constPat.Value);
                // Enum comparison → convert operands and use underlying type
                var convertedValueVal = EmitEnumToUnderlying(valueVal, valueType);
                constVal = EmitEnumToUnderlying(constVal, valueType);
                var underlyingSym = valueType is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum
                    ? named.EnumUnderlyingType : valueType;
                var eqType = GetUdonType(underlyingSym);
                if (constPat.Value.ConstantValue is { HasValue: true, Value: null })
                    eqType = "SystemObject"; // null comparisons use SystemObject equality
                else if (SmallIntOrChar(eqType))
                {
                    // A nullable small-int/char (or small-underlying enum) scrutinee may carry a boxed plain int
                    // rather than the strict small-int tag; promote both sides to int32 (ToInt32(SystemObject)
                    // tolerates any boxed numeric) and compare with the int32 extern, like the binary path.
                    convertedValueVal = PromoteBoxedToInt32(convertedValueVal, underlyingSym, out _);
                    constVal = PromoteBoxedToInt32(constVal, underlyingSym, out _);
                    eqType = "SystemInt32";
                }
                return ExternCall(
                    ExternResolver.BuildMethodSignature(
                        eqType, "__op_Equality", new[] { eqType, eqType }, "SystemBoolean"),
                    new List<CValue> { convertedValueVal, constVal },
                    "SystemBoolean");
            }
            case INegatedPatternOperation negated:
            {
                var innerVal = EmitPatternCheckImpl(valueVal, valueType, negated.Pattern);
                return ExternCall(
                    "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CValue> { innerVal },
                    "SystemBoolean");
            }
            case ITypePatternOperation typePat:
                return EmitTypeCheck(valueVal, typePat.MatchedType);

            case IDeclarationPatternOperation declPat:
            {
                // `var x` (no explicit MatchedType / MatchesNull) matches any value — no type check.
                var isVar = declPat.MatchedType == null || declPat.MatchesNull;
                var checkVal = isVar ? (CValue)Const(true, "SystemBoolean") : EmitTypeCheck(valueVal, declPat.MatchedType);
                if (declPat.DeclaredSymbol is ILocalSymbol local)
                {
                    var localType = GetUdonType(local.Type);
                    var localId = _ctx.DeclareLocal(local.Name, localType);
                    _localBindings[local] = new EmitContext.LocalBinding(localId);
                    if (isVar)
                        EmitStoreField(localId, valueVal); // always matches → bind unconditionally
                    else
                        // Only assign when the type check succeeds — avoid invalid type COPY on mismatch
                        _builder.EmitIf(checkVal, b => EmitStoreField(localId, valueVal));
                }
                return checkVal;
            }
            case IDiscardPatternOperation:
                return Const(true, "SystemBoolean");

            case IRelationalPatternOperation relPat:
            {
                var constVal = VisitExpression(relPat.Value);
                // Enum scrutinee → compare on the underlying type (mirrors the constant-pattern arm).
                var scrut = EmitEnumToUnderlying(valueVal, valueType);
                constVal = EmitEnumToUnderlying(constVal, valueType);
                var underlyingSym = valueType is INamedTypeSymbol relEnum && relEnum.TypeKind == TypeKind.Enum
                    ? relEnum.EnumUnderlyingType : valueType;
                var valType = GetUdonType(underlyingSym);
                if (SmallIntOrChar(valType))
                {
                    // A nullable small-int/char (or small-underlying enum) scrutinee may be boxed as a plain int;
                    // promote both sides to int32 so the strict small-int extern's box-tag fetch cannot InvalidCast.
                    scrut = PromoteBoxedToInt32(scrut, underlyingSym, out _);
                    constVal = PromoteBoxedToInt32(constVal, underlyingSym, out _);
                    valType = "SystemInt32";
                }
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
                    new List<CValue> { scrut, constVal },
                    "SystemBoolean");
            }
            case IBinaryPatternOperation binPat:
            {
                var leftVal = EmitPatternCheckImpl(valueVal, valueType, binPat.LeftPattern);
                var rightVal = EmitPatternCheckImpl(valueVal, valueType, binPat.RightPattern);
                var opName = binPat.OperatorKind == BinaryOperatorKind.And
                    ? "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean"
                    : "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean";
                return ExternCall(opName, new List<CValue> { leftVal, rightVal }, "SystemBoolean");
            }

            case IRecursivePatternOperation rec when rec.DeconstructionSubpatterns.Length > 0:
            {
                // Positional/deconstruction pattern — tuple-typed only (reuse the aggregate object[]
                // machinery; user-defined Deconstruct is out of scope).
                if (valueType is not INamedTypeSymbol aggType || !EmitContext.IsAggregateType(valueType))
                    throw new System.NotSupportedException(
                        "Positional pattern is only supported on tuple types (user Deconstruct is not supported).");
                var layout = _ctx.GetAggregateLayout(aggType);
                if (rec.DeconstructionSubpatterns.Length != layout.Count)
                    throw new System.NotSupportedException(
                        $"Positional pattern element count ({rec.DeconstructionSubpatterns.Length}) "
                        + $"does not match tuple arity ({layout.Count}).");

                var aggSlot = _ctx.AllocTemp("SystemObjectArray");
                EmitAssign(aggSlot, valueVal);

                CValue result = Const(true, "SystemBoolean");
                for (int i = 0; i < rec.DeconstructionSubpatterns.Length; i++)
                {
                    var elemType = layout.Fields[i].Type;
                    var elemRaw = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                        new List<CValue> { SlotRef(aggSlot), Const(i, "SystemInt32") }, "SystemObject");
                    // Materialize into a typed temp (Udon COPY unboxes) so the sub-pattern compares
                    // with the correct type tag, exactly as tuple deconstruction extracts elements.
                    var elemSlot = _ctx.AllocTemp(GetUdonType(elemType));
                    EmitAssign(elemSlot, elemRaw);
                    var subResult = EmitPatternCheckImpl(SlotRef(elemSlot), elemType, rec.DeconstructionSubpatterns[i]);
                    result = ExternCall(
                        "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<CValue> { result, subResult }, "SystemBoolean");
                }
                return result;
            }

            case IRecursivePatternOperation rec when rec.PropertySubpatterns.Length > 0:
            {
                // Property pattern  x is [Type] { P: subpat, ... }
                //   ≡  x passes the (optional) type/null guard  &&  x.P is subpat  &&  ...
                // Member reads are emitted INSIDE the guard's then-block so a null or type-mismatched
                // receiver short-circuits to false without dereferencing (extern AND does not short-circuit).
                var resultSlot = _ctx.AllocTemp("SystemBoolean");
                EmitAssign(resultSlot, Const(false, "SystemBoolean"));

                CValue guard;
                if (rec.MatchedType != null && !SymbolEqualityComparer.Default.Equals(rec.MatchedType, valueType))
                    guard = EmitTypeCheck(valueVal, rec.MatchedType);
                else if (!valueType.IsValueType)
                    guard = ExternCall(
                        "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                        new List<CValue> { valueVal, Const(null, "SystemObject") }, "SystemBoolean");
                else
                    guard = Const(true, "SystemBoolean");

                _builder.EmitIf(guard, _ =>
                {
                    var matchType = rec.MatchedType ?? valueType;
                    var valSlot = _ctx.AllocTemp(GetUdonType(matchType));
                    EmitAssign(valSlot, valueVal);

                    if (rec.DeclaredSymbol is ILocalSymbol bound)
                    {
                        var boundId = _ctx.DeclareLocal(bound.Name, GetUdonType(bound.Type));
                        _localBindings[bound] = new EmitContext.LocalBinding(boundId);
                        EmitStoreField(boundId, SlotRef(valSlot));
                    }

                    CValue acc = Const(true, "SystemBoolean");
                    // For a user struct / tuple scrutinee, members live as object[] indices — there is no Udon
                    // property getter (SystemObjectArray.__get_X does not exist) — so read via the layout __Get.
                    var aggMatchType = matchType as INamedTypeSymbol;
                    bool isAgg = aggMatchType != null && EmitContext.IsAggregateType(aggMatchType);
                    foreach (var sub in rec.PropertySubpatterns)
                    {
                        ITypeSymbol memberType;
                        string memberName, memberOwner;
                        switch (sub.Member)
                        {
                            case IPropertyReferenceOperation pr:
                                memberType = pr.Property.Type; memberName = pr.Property.Name;
                                memberOwner = GetUdonType(pr.Property.ContainingType); break;
                            case IFieldReferenceOperation fr:
                                memberType = fr.Field.Type; memberName = fr.Field.Name;
                                memberOwner = GetUdonType(fr.Field.ContainingType); break;
                            default:
                                throw new System.NotSupportedException(
                                    $"Property pattern member '{sub.Member?.GetType().Name}' is not supported "
                                    + "(only System/Unity properties and fields).");
                        }
                        CValue memberVal;
                        if (isAgg && _ctx.GetAggregateLayout(aggMatchType).TryGetIndex(memberName, out var aggMemberIdx))
                        {
                            // Aggregate member: read the boxed object[] slot, then materialize into a typed temp
                            // (Udon COPY unboxes) so the sub-pattern compares with the correct type tag.
                            var rawMember = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                                new List<CValue> { SlotRef(valSlot), Const(aggMemberIdx, "SystemInt32") }, "SystemObject");
                            var memberSlot = _ctx.AllocTemp(GetUdonType(memberType));
                            EmitAssign(memberSlot, rawMember);
                            memberVal = SlotRef(memberSlot);
                        }
                        else
                        {
                            memberVal = ExternCall(
                                ExternResolver.BuildPropertyGetSignature(memberOwner, memberName, GetUdonType(memberType)),
                                new List<CValue> { SlotRef(valSlot) }, GetUdonType(memberType));
                        }
                        var subResult = EmitPatternCheckImpl(memberVal, memberType, sub.Pattern);
                        acc = ExternCall(
                            "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                            new List<CValue> { acc, subResult }, "SystemBoolean");
                    }
                    EmitAssign(resultSlot, acc);
                });
                return SlotRef(resultSlot);
            }

            default:
                throw new System.NotSupportedException($"Unsupported pattern: {pattern.GetType().Name}");
        }
    }

    CValue EmitTypeCheck(CValue valueVal, ITypeSymbol targetType)
    {
        var typeConst = Const(GetUdonType(targetType), "SystemType");
        return ExternCall(
            "SystemType.__IsInstanceOfType__SystemObject__SystemBoolean",
            new List<CValue> { typeConst, valueVal },
            "SystemBoolean");
    }

    // ── Switch expression ──

    CValue VisitSwitchExpression(ISwitchExpressionOperation op)
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
        System.Action<CoreBuilder> tail = null;
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

    CValue VisitConditionalExpression(IConditionalOperation op)
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

    // ── Tuple binary (== / !=) ──

    CValue VisitTupleBinary(ITupleBinaryOperation op)
    {
        if (op.LeftOperand.Type is not INamedTypeSymbol aggType || !aggType.IsTupleType)
            throw new System.NotSupportedException(
                $"Tuple binary operation on non-tuple type: {op.LeftOperand.Type}");
        return EmitTupleStructuralEquality(
            VisitExpression(op.LeftOperand), VisitExpression(op.RightOperand), aggType,
            op.OperatorKind == BinaryOperatorKind.NotEquals);
    }

    // ── Aggregate (tuple) equality (via IBinaryOperation — fallback) ──

    CValue EmitAggregateEquality(IBinaryOperation op, INamedTypeSymbol aggType)
        => EmitTupleStructuralEquality(
            VisitExpression(op.LeftOperand), VisitExpression(op.RightOperand), aggType,
            op.OperatorKind == BinaryOperatorKind.NotEquals);

    CValue EmitTupleStructuralEquality(CValue leftArr, CValue rightArr, INamedTypeSymbol aggType, bool isNotEquals)
    {
        var result = EmitAggregateElementsEqual(leftArr, rightArr, aggType);
        if (isNotEquals)
            result = ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                new List<CValue> { result }, "SystemBoolean");
        return result;
    }

    // Field-by-field structural equality of two object[]-backed tuples. A nested-tuple element recurses
    // (boxed object.Equals would otherwise do REFERENCE equality on the nested object[] and never match);
    // a scalar element uses SystemObject.__Equals (object.Equals = VALUE equality, NOT __op_Equality which
    // is reference equality). Caveat: float NaN compares equal under object.Equals.
    CValue EmitAggregateElementsEqual(CValue leftArr, CValue rightArr, INamedTypeSymbol aggType)
    {
        var layout = _ctx.GetAggregateLayout(aggType);
        var leftSlot = _ctx.AllocTemp("SystemObjectArray"); EmitAssign(leftSlot, leftArr);
        var rightSlot = _ctx.AllocTemp("SystemObjectArray"); EmitAssign(rightSlot, rightArr);

        CValue result = Const(true, "SystemBoolean");
        for (int i = 0; i < layout.Count; i++)
        {
            var leftElem = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CValue> { SlotRef(leftSlot), Const(i, "SystemInt32") }, "SystemObject");
            var rightElem = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CValue> { SlotRef(rightSlot), Const(i, "SystemInt32") }, "SystemObject");

            CValue elemEq = layout.Fields[i].Type is INamedTypeSymbol nested && nested.IsTupleType
                ? EmitAggregateElementsEqual(leftElem, rightElem, nested) // nested tuple → recurse
                : ExternCall("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
                    new List<CValue> { leftElem, rightElem }, "SystemBoolean");

            result = ExternCall("SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                new List<CValue> { result, elemEq }, "SystemBoolean");
        }
        return result;
    }

    // ── Delegate comparison helpers ──

    /// <summary>Try to extract delegate field name from an operation, unwrapping conversions.</summary>
    string TryGetDelegateFieldName(IOperation op)
    {
        var unwrapped = op;
        while (unwrapped is IConversionOperation conv) unwrapped = conv.Operand;
        if (unwrapped is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fr
            && fr.Field.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null
            && _delegateFields.Contains(fr.Field.Name))
            return fr.Field.Name;
        return null;
    }

    bool IsNullLiteral(IOperation op)
    {
        var unwrapped = op;
        while (unwrapped is IConversionOperation conv) unwrapped = conv.Operand;
        return unwrapped is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
    }

    CValue CompareDelegateToNull(string targetFieldName, bool isNotEquals)
    {
        var targetVal = LoadField(targetFieldName, "VRCUdonCommonInterfacesIUdonEventReceiver");
        var nullVal = Const(null, "SystemObject");
        var sig = isNotEquals
            ? "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean"
            : "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean";
        return ExternCall(sig, new List<CValue> { targetVal, nullVal }, "SystemBoolean");
    }

    CValue CompareDelegates(string leftField, string rightField, bool isNotEquals)
    {
        var leftBundle = new DelegateBundle(leftField);
        var rightBundle = new DelegateBundle(rightField);
        var leftTarget = LoadField(leftBundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver");
        var rightTarget = LoadField(rightBundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver");
        // Addr is intentionally excluded: it is derived from Method and only used
        // for same-behaviour JUMP_INDIRECT optimization. Including it would cause
        // false negatives when two delegates point to the same method.
        var targetEq = ExternCall(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            new List<CValue> { leftTarget, rightTarget }, "SystemBoolean");

        var leftMethod = LoadField(leftBundle.Method, "SystemString");
        var rightMethod = LoadField(rightBundle.Method, "SystemString");
        var methodEq = ExternCall(
            "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
            new List<CValue> { leftMethod, rightMethod }, "SystemBoolean");

        var result = ExternCall(
            "SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
            new List<CValue> { targetEq, methodEq }, "SystemBoolean");

        if (isNotEquals)
            result = ExternCall(
                "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                new List<CValue> { result }, "SystemBoolean");

        return result;
    }
}
