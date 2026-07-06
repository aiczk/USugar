using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class OperatorHandler : HandlerBase, IExpressionHandler
{
    public OperatorHandler(EmitContext ctx) : base(ctx) { }

    // OperationKind.Conditional also appears in the statement table (StatementHandler) — the ternary lives here.
    public OperationKind[] HandledKinds { get; } = new[]
    {
        OperationKind.BinaryOperator, OperationKind.UnaryOperator, OperationKind.Conditional, OperationKind.IsType,
        OperationKind.IsPattern, OperationKind.SwitchExpression, OperationKind.TupleBinaryOperator,
    };

    public CLeaf Handle(IOperation expression) => expression switch
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

    CLeaf VisitBinary(IBinaryOperation op)
    {
        RejectChecked(op.IsChecked);

        // Short-circuit evaluation for && and ||
        if (op.OperatorKind == BinaryOperatorKind.ConditionalAnd)
            return VisitConditionalAnd(op);
        if (op.OperatorKind == BinaryOperatorKind.ConditionalOr)
            return VisitConditionalOr(op);

        // ── B63 redundant armor: typeof(A)==typeof(B) on a collapse-set operand. The mint-site
        // immediate-use gate (EmitTypeofToken) already rejects a collapse-set typeof outside a component-query
        // argument, so a direct both-typeof compare never reaches here; this is defence-in-depth. ──
        RejectTypeofTokenEquality(op);

        // ── User-defined struct operator: v1 + v2 → static operator method call ──
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } binOpM
            && binOpM.ContainingType is INamedTypeSymbol binOpCt && EmitPolicy.IsUserStruct(binOpCt))
        {
            var lhs = VisitExpression(op.LeftOperand);
            var rhs = VisitExpression(op.RightOperand);
            return EmitCallToMethod(ResolveStructMember(binOpM), new List<CLeaf> { lhs, rhs });
        }

        // CA-M1: a v1 class defines NO user operators (design §2 "ユーザー演算子なし") — reject a user-defined
        // binary operator loudly BEFORE the reference-equality arm below would silently ignore a user `==`.
        ClassAbi.RejectUserOperator(op.OperatorMethod);

        // ── User class (v1) reference equality: c1 == c2 / c == null → reference compare on the object[]
        // bundle (the bundle reference IS the identity; an unoverridden Equals/== is reference equality). ──
        if (ClassAbi.IsReferenceEquality(op.OperatorKind, op.LeftOperand.Type, op.RightOperand.Type))
            return ClassAbi.EmitReferenceEquality(_builder, op.OperatorKind,
                VisitExpression(op.LeftOperand), VisitExpression(op.RightOperand));

        // ── Delegate null check / equality (design §2.5 — TYPE-routed, so fields, locals, params,
        // array elements, properties, and expression results all land here; the dispatch stays gated
        // on the delegate type because this linear handler scan is first-match) ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && (IsDelegateTyped(op.LeftOperand.Type) || IsDelegateTyped(op.RightOperand.Type)))
        {
            bool leftIsNull = IsNullLiteral(op.LeftOperand);
            bool rightIsNull = IsNullLiteral(op.RightOperand);
            bool isNotEquals = op.OperatorKind == BinaryOperatorKind.NotEquals;

            // d == null / d != null → reference null check on the BUNDLE itself (P4: delegate-null is
            // the bundle reference being null; [0] being null is a different condition).
            if (rightIsNull)
                return CompareDelegateToNull(VisitExpression(op.LeftOperand), isNotEquals);
            if (leftIsNull)
                return CompareDelegateToNull(VisitExpression(op.RightOperand), isNotEquals);

            // d1 == d2 → element-wise (target, method) value equality with null legs (fcd07).
            if (IsDelegateTyped(op.LeftOperand.Type) && IsDelegateTyped(op.RightOperand.Type))
                return CompareDelegates(VisitExpression(op.LeftOperand), VisitExpression(op.RightOperand), isNotEquals);
        }

        // wave-13 multishapes lens (2026-07-04): a PLAIN `d1 + d2` / `d1 - d2` on delegate-typed VALUES
        // (Roslyn IBinaryOperation) is C#'s Delegate.Combine/Remove operator overload -- the SAME
        // operation `d += h` (ICompoundAssignmentOperation, CompoundAssignmentHandler) and `evt += h`
        // (IEventAssignmentOperation) already lower to the combine/remove helper, but this operation
        // kind was never routed there and fell through to the generic numeric/extern binary-op path,
        // which emitted a non-existent "SystemObjectArray.__op_Addition__..." extern.
        if (op.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            && IsDelegateTyped(op.LeftOperand.Type) && IsDelegateTyped(op.RightOperand.Type))
        {
            var delegateType = (INamedTypeSymbol)op.Type;
            var invoke = delegateType.DelegateInvokeMethod;
            DelegateAbi.ValidateNoRefOutParams(invoke);

            var combineLeftVal = VisitExpression(op.LeftOperand);
            var combineRightVal = VisitExpression(op.RightOperand);

            var sigPart = DelegateAbi.BuildSigPart(invoke, _ctx.TypeParamMap);
            RegisterMulticastSig(sigPart, invoke);

            var helperName = op.OperatorKind == BinaryOperatorKind.Add
                ? DelegateAbi.MulticastCombineName(sigPart)
                : DelegateAbi.MulticastRemoveName(sigPart);
            return _builder.InternalCall(helperName, new List<CLeaf> { combineLeftVal, combineRightVal }, "SystemObjectArray");
        }

        // ── Nullable (boxed object) compared to null literal → object reference null check ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            bool leftNullable = EmitPolicy.IsNullableT(op.LeftOperand.Type, out _);
            bool rightNullable = EmitPolicy.IsNullableT(op.RightOperand.Type, out _);
            bool leftNull = IsNullLiteral(op.LeftOperand);
            bool rightNull = IsNullLiteral(op.RightOperand);
            if ((leftNullable && rightNull) || (rightNullable && leftNull))
            {
                var nv = VisitExpression(leftNullable ? op.LeftOperand : op.RightOperand);
                if (op.OperatorKind == BinaryOperatorKind.NotEquals)
                    return EmitNullableHasValue(nv); // != null  ⇔  HasValue
                return NullableAbi.IsNull(_builder, nv);
            }
        }

        // ── Nullable bool & / | : C# three-valued logic (false & null = false, true | null = true) ──
        if (op.IsLifted && (op.OperatorKind is BinaryOperatorKind.And or BinaryOperatorKind.Or)
            && EmitPolicy.IsNullableT(op.Type, out var boolUnder) && boolUnder.SpecialType == SpecialType.System_Boolean)
        {
            return EmitLiftedBoolLogic(op);
        }

        // ── Lifted operator on Nullable<T> (null propagation) ──
        if (op.IsLifted
            && (EmitPolicy.IsNullableT(op.LeftOperand.Type, out _) || EmitPolicy.IsNullableT(op.RightOperand.Type, out _)))
        {
            return EmitLiftedBinary(op);
        }

        // ── Aggregate (tuple) structural equality ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && EmitPolicy.IsAggregateType(op.LeftOperand.Type)
            && op.LeftOperand.Type is INamedTypeSymbol aggType)
        {
            return EmitAggregateEquality(op, aggType);
        }

        // Constant folding: compile-time evaluable binary expressions
        if (op.ConstantValue.HasValue)
        {
            var constType = GetUdonType(op.Type);
            return Const(EmitPolicy.ParseConstValue(constType, ToInvariantString(op.ConstantValue.Value)), constType);
        }

        // B67: string concat with a user enum operand. C# boxes each operand to object for
        // string.Concat(object,object), so the enum operand arrives wrapped in a Suit→object conversion; look
        // THROUGH it. Left as-is the enum would be Concat-ToString'd to its underlying number, so convert that
        // operand to the C#-correct name string first and emit the object/object concat directly (routing it
        // back through the generic path would re-select the extern by the now-string operand types).
        if (op.OperatorKind == BinaryOperatorKind.Add && GetUdonType(op.Type) == "SystemString")
        {
            var lOp = UnwrapConversions(op.LeftOperand);
            var rOp = UnwrapConversions(op.RightOperand);
            ClassAbi.RejectImplicitToString(lOp.Type);
            ClassAbi.RejectImplicitToString(rOp.Type);
            if (ExternResolver.IsUserEnum(ResolveType(lOp.Type)) || ExternResolver.IsUserEnum(ResolveType(rOp.Type)))
            {
                var l = VisitExpression(lOp);
                l = TryEmitEnumToString(l, lOp.Type) ?? l;
                var r = VisitExpression(rOp);
                r = TryEmitEnumToString(r, rOp.Type) ?? r;
                return ExternCall("SystemString.__Concat__SystemObject_SystemObject__SystemString",
                    new List<CLeaf> { l, r }, "SystemString");
            }
        }

        var leftVal = VisitExpression(op.LeftOperand);
        var rightVal = VisitExpression(op.RightOperand);

        // Enum operands → convert to underlying type before comparison
        leftVal = EmitEnumToUnderlying(leftVal, op.LeftOperand.Type);
        rightVal = EmitEnumToUnderlying(rightVal, op.RightOperand.Type);

        var resultType = GetUdonType(op.Type);

        // long/ulong/uint % : Udon has no op_Remainder extern for these; lower to a - (a / b) * b via the
        // shared polyfill (uint included — it has Division/Multiplication/Subtraction but no Remainder).
        if (op.OperatorKind == BinaryOperatorKind.Remainder && RemainderNeedsPolyfill(resultType))
            return EmitRemainderViaDivision(leftVal, rightVal, resultType);

        // Udon has no byte/sbyte/short/ushort operators. C# promotes a plain small int to int before the op, but
        // a small-int-backed ENUM keeps its underlying width (enum|enum stays enum, etc.), so the result type is
        // small-int here only for such enums: compute in int32 and narrow back. (Comparisons yield bool, never a
        // small int, so they skip this.)
        if (ExternResolver.IsSmallIntOrChar(resultType))
        {
            var leftU = UnderlyingUdon(op.LeftOperand.Type);
            var rightU = UnderlyingUdon(op.RightOperand.Type);
            var li = leftU == "SystemInt32" ? leftVal : EmitNarrowingConvert(leftVal, leftU, "SystemInt32");
            var ri = rightU == "SystemInt32" ? rightVal : EmitNarrowingConvert(rightVal, rightU, "SystemInt32");
            var int32 = _compilation.GetSpecialType(SpecialType.System_Int32);
            var raw = ExternCall(
                ExternResolver.ResolveBinaryExtern(op.OperatorKind, op.OperatorMethod,
                    ResolveType(int32), ResolveType(int32), ResolveType(int32)),
                new List<CLeaf> { li, ri }, "SystemInt32");
            return EmitNarrowingConvert(raw, "SystemInt32", resultType);
        }

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
            return ExternCall(sig, new List<CLeaf> { SlotRef(objLeftSlot), SlotRef(objRightSlot) }, resultType);
        }

        return ExternCall(sig, new List<CLeaf> { leftVal, rightVal }, resultType);
    }

    // The effective Udon storage type of an operand: an enum is stored as (and operates on) its underlying type.
    string UnderlyingUdon(ITypeSymbol t) =>
        t is INamedTypeSymbol n && n.TypeKind == TypeKind.Enum
            ? GetUdonType(n.EnumUnderlyingType) : GetUdonType(t);

    // B63 redundant armor: reject a direct `typeof(A) ==/!= typeof(B)` where A,B are distinct C# types that
    // fold onto one Udon tag (the mint-site immediate-use gate already catches this — kept as defence-in-depth).
    void RejectTypeofTokenEquality(IBinaryOperation op)
    {
        if (op.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
            return;
        var a = AsTypeofOperand(op.LeftOperand);
        var b = AsTypeofOperand(op.RightOperand);
        if (a == null || b == null || SymbolEqualityComparer.Default.Equals(a, b))
            return;
        if (GetUdonType(a) != GetUdonType(b))
            return;
        throw new System.NotSupportedException(
            $"typeof('{(ResolveType(a) ?? a).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') "
            + $"==/!= typeof('{(ResolveType(b) ?? b).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') "
            + "is unsound: these are distinct C# types but Udon folds both onto one runtime type tag "
            + $"('{GetUdonType(a)}'), so the comparison is silently true where C# says false.");
    }

    // The type operand of a typeof, seeing through an identity/boxing conversion wrapper; null if not a typeof.
    static ITypeSymbol AsTypeofOperand(IOperation op)
    {
        op = UnwrapConversions(op);
        return op is ITypeOfOperation t ? t.TypeOperand : null;
    }

    // Nullable bool `&` / `|` with C# three-valued logic: a known false dominates `&` (false & null = false)
    // and a known true dominates `|` (true | null = true), regardless of the other operand being null.
    CLeaf EmitLiftedBoolLogic(IBinaryOperation op)
    {
        var aSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(aSlot, VisitExpression(op.LeftOperand));
        var bSlot = _ctx.AllocTemp("SystemObject"); EmitAssign(bSlot, VisitExpression(op.RightOperand));

        void IfBool(int slot, bool wantTrue, System.Action<CoreBuilder> body)
        {
            CLeaf boolCond = wantTrue
                ? SlotRef(slot) // boxed bool used directly (Udon unboxes for the branch test)
                : ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CLeaf> { SlotRef(slot) }, "SystemBoolean");
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
    CLeaf EmitLiftedBinary(IBinaryOperation op)
    {
        var leftNullable = EmitPolicy.IsNullableT(op.LeftOperand.Type, out var lu);
        var rightNullable = EmitPolicy.IsNullableT(op.RightOperand.Type, out var ru);
        var leftVal = VisitExpression(op.LeftOperand);
        var rightVal = VisitExpression(op.RightOperand);
        return EmitLiftedBinaryCore(
            leftVal, leftNullable, leftNullable ? lu : op.LeftOperand.Type,
            rightVal, rightNullable, rightNullable ? ru : op.RightOperand.Type,
            op.OperatorKind, op.OperatorMethod, op.Type);
    }

    CLeaf VisitConditionalAnd(IBinaryOperation op)
    {
        // a && b: evaluate b only when a is true (short-circuit).
        // VisitExpression on operands may emit Core IR statements (e.g. temp stores for
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

    CLeaf VisitConditionalOr(IBinaryOperation op)
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

    CLeaf VisitUnary(IUnaryOperation op)
    {
        RejectChecked(op.IsChecked);

        // ── User-defined struct operator (ANY unary kind, incl. ~): static operator method call. MUST come
        // before the built-in ~ branch below — that branch builds an extern on the struct's SystemObjectArray
        // type and throws "Bitwise NOT not supported on SystemObjectArray". Only fires for a user operator
        // (a built-in lifted ~ has OperatorMethod null → falls through to the BitwiseNegation handling). ──
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } unOpM
            && unOpM.ContainingType is INamedTypeSymbol unOpCt && EmitPolicy.IsUserStruct(unOpCt))
        {
            var operand = VisitExpression(op.Operand);
            return EmitCallToMethod(ResolveStructMember(unOpM), new List<CLeaf> { operand });
        }
        ClassAbi.RejectUserOperator(op.OperatorMethod);

        // Bitwise NOT (~): Udon VM has no unary complement extern → synthesize as XOR with all-bits-set
        if (op.OperatorKind == UnaryOperatorKind.BitwiseNegation)
        {
            // Lifted ~ on Nullable<T> null-propagates; route it through EmitLiftedUnary before the non-lifted
            // path, whose extern would be built on the Nullable operand type (SystemObject) and throw.
            if (op.IsLifted && EmitPolicy.IsNullableT(op.Type, out var bnResU))
                return EmitLiftedUnary(op, bnResU);
            return VisitBitwiseNot(op);
        }

        // Constant folding: compile-time evaluable unary expressions (e.g., -5)
        if (op.ConstantValue.HasValue)
        {
            var constType = GetUdonType(op.Type);
            return Const(EmitPolicy.ParseConstValue(constType, ToInvariantString(op.ConstantValue.Value)), constType);
        }

        // Lifted unary on Nullable<T> (null propagation): null stays null, else apply the op to the unwrapped
        // value. The non-lifted path below would build an invalid extern on the Nullable operand type.
        if (op.IsLifted && EmitPolicy.IsNullableT(op.Type, out var unaryResU))
            return EmitLiftedUnary(op, unaryResU);

        var operandVal = VisitExpression(op.Operand);
        var resultType = GetUdonType(op.Type);

        string sig;
        if (op.OperatorMethod != null && !ExternResolver.IsNumericType(op.Operand.Type))
            sig = BuildExternSignature(op.OperatorMethod);
        else
            sig = BuildBuiltinUnarySignature(op);

        return ExternCall(sig, new List<CLeaf> { operandVal }, resultType);
    }

    // Lifted unary minus / logical-not on Nullable<T>: null-preserving. A small-int operand is promoted to
    // the (int) result underlying for the SystemInt32 op; int/long/float/bool pass through. The unwrapped
    // result is re-boxed into the SystemObject nullable slot. (Lifted bitwise ~ is not covered here.)
    CLeaf EmitLiftedUnary(IUnaryOperation op, ITypeSymbol resUnderlying)
    {
        EmitPolicy.IsNullableT(op.Operand.Type, out var opUnderlying);
        var resU = GetUdonType(resUnderlying);

        // Lifted bitwise NOT: ~x ≡ x ^ allBits — reuse the lifted-binary machinery (promotion / narrowing /
        // null-propagation). ~ promotes a small int to int, so allBits is built in the RESULT underlying domain.
        if (op.OperatorKind == UnaryOperatorKind.BitwiseNegation)
        {
            object allBitsValue = resUnderlying.SpecialType switch
            {
                SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Int64
                    or SpecialType.System_SByte => EmitPolicy.ParseConstValue(resU, "-1"),
                SpecialType.System_UInt32 => uint.MaxValue,
                SpecialType.System_UInt64 => ulong.MaxValue,
                SpecialType.System_UInt16 => ushort.MaxValue,
                SpecialType.System_Byte => byte.MaxValue,
                _ => throw new System.NotSupportedException($"Lifted bitwise NOT (~) is not supported on {resU}")
            };
            return EmitLiftedBinaryCore(
                VisitExpression(op.Operand), true, opUnderlying,
                Const(allBitsValue, resU), false, resUnderlying,
                BinaryOperatorKind.ExclusiveOr, null, op.Type);
        }

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
                new List<CLeaf> { v }, resU);
            EmitAssign(resSlot, computed); // re-box into the nullable's SystemObject slot
        });
        return SlotRef(resSlot);
    }

    CLeaf VisitBitwiseNot(IUnaryOperation op)
    {
        var operandVal = VisitExpression(op.Operand);
        var operandType = GetUdonType(op.Operand.Type);
        var resultType = GetUdonType(op.Type);

        // An enum operand has SpecialType None; ~ operates on (and narrows back to) the underlying type, so key
        // off the underlying. operandType/resultType already resolve to it via GetUdonType.
        var effSpecial = op.Operand.Type is INamedTypeSymbol enumOperand && enumOperand.TypeKind == TypeKind.Enum
            ? enumOperand.EnumUnderlyingType.SpecialType
            : op.Operand.Type.SpecialType;

        // Udon has no byte/sbyte/short/ushort operators. C# already promotes a plain small int to int before ~
        // (so op.Operand.Type is int there); only a small-int-BACKED ENUM reaches here as byte/short. Compute it
        // in int32 then narrow back to the underlying: ~x ≡ (T)((int)x ^ -1).
        if (effSpecial is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16)
        {
            var asInt = operandType == "SystemInt32" ? operandVal : EmitNarrowingConvert(operandVal, operandType, "SystemInt32");
            var xored = ExternCall("SystemInt32.__op_LogicalXor__SystemInt32_SystemInt32__SystemInt32",
                new List<CLeaf> { asInt, Const(-1, "SystemInt32") }, "SystemInt32");
            return resultType == "SystemInt32" ? xored : EmitNarrowingConvert(xored, "SystemInt32", resultType);
        }

        // int/uint/long/ulong have native ops: ~x ≡ x ^ allBits (signed: -1 = all bits set, unsigned: MaxValue).
        object allBitsValue = effSpecial switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int64 => EmitPolicy.ParseConstValue(operandType, "-1"),
            SpecialType.System_UInt32 => uint.MaxValue,
            SpecialType.System_UInt64 => ulong.MaxValue,
            _ => throw new System.NotSupportedException(
                $"Bitwise NOT (~) is not supported on type {operandType}")
        };
        var allBitsConst = Const(allBitsValue, operandType);

        return ExternCall(
            ExternResolver.ResolveBinaryExtern(
                BinaryOperatorKind.ExclusiveOr, null,
                ResolveType(op.Operand.Type), ResolveType(op.Operand.Type), ResolveType(op.Type)),
            new List<CLeaf> { operandVal, allBitsConst },
            resultType);
    }

    // ── Is-type / Is-pattern ──

    CLeaf VisitIsType(IIsTypeOperation op)
    {
        // A bare `x is T` (no binding) is exactly a type pattern without a variable — route it through the
        // single guarded EmitTypeCheck so NO reachable path emits IsInstanceOfType without the layer-2
        // distinguishability guard.
        return EmitTypeCheck(VisitExpression(op.ValueOperand), op.TypeOperand);
    }

    CLeaf VisitIsPattern(IIsPatternOperation op)
    {
        var valueVal = VisitExpression(op.Value);
        return EmitPatternCheckImpl(valueVal, op.Value.Type, op.Pattern);
    }

    // ── Pattern matching (public — called from LoopHandler via EmitContext dispatch) ──

    public CLeaf EmitPatternCheckImpl(CLeaf valueVal, ITypeSymbol valueType, IPatternOperation pattern)
    {
        // Nullable<T> scrutinee (boxed object): `x is null` is an object null check; any other pattern
        // requires HasValue, then matches against the unboxed underlying value (Udon unboxes transparently).
        if (EmitPolicy.IsNullableT(valueType, out var patUnderlying))
        {
            var nSlot = _ctx.AllocTemp("SystemObject");
            EmitAssign(nSlot, valueVal);
            if (pattern is IConstantPatternOperation cpn && cpn.Value.ConstantValue is { HasValue: true, Value: null })
                return NullableAbi.IsNull(_builder, SlotRef(nSlot));
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
                else if (ExternResolver.IsSmallIntOrChar(eqType))
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
                    new List<CLeaf> { convertedValueVal, constVal },
                    "SystemBoolean");
            }
            case INegatedPatternOperation negated:
            {
                var innerVal = EmitPatternCheckImpl(valueVal, valueType, negated.Pattern);
                return ExternCall(
                    "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                    new List<CLeaf> { innerVal },
                    "SystemBoolean");
            }
            case ITypePatternOperation typePat:
                return EmitTypeCheck(valueVal, typePat.MatchedType);

            case IDeclarationPatternOperation declPat:
            {
                // `var x` (no explicit MatchedType / MatchesNull) matches any value — no type check.
                var isVar = declPat.MatchedType == null || declPat.MatchesNull;
                var checkVal = isVar ? (CLeaf)Const(true, "SystemBoolean") : EmitTypeCheck(valueVal, declPat.MatchedType);
                if (declPat.DeclaredSymbol is ILocalSymbol local)
                {
                    // Stage 2 §4.1: captured pattern variable → env cell (its owning scope's env is
                    // live at every point a condition/section hosting this pattern executes).
                    if (_ctx.TryGetEnvBinding(local, out _))
                    {
                        if (isVar)
                            EnvEmit.Write(_builder, _ctx, local, valueVal);
                        else
                            _builder.EmitIf(checkVal, b => EnvEmit.Write(_builder, _ctx, local, valueVal));
                        return checkVal;
                    }
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
                if (ExternResolver.IsSmallIntOrChar(valType))
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
                    new List<CLeaf> { scrut, constVal },
                    "SystemBoolean");
            }
            case IBinaryPatternOperation binPat:
            {
                // `A and B` where A narrows the type (e.g. `obj is int n and > 0`): B's input is the type A
                // narrowed to, and B must run ONLY when A matched — otherwise B compares/unboxes a value of the
                // wrong type (compile-time: the scrutinee type has no relational extern, e.g. SystemObject.__op_
                // GreaterThan; runtime: an InvalidCast). Short-circuit through the matched value re-typed to A's
                // NarrowedType (Udon COPY unboxes). A non-narrowing `and` (e.g. `>= 0 and < 100` on int) and any
                // `or` keep the plain extern combine — both sides are safe to evaluate on the same value.
                if (binPat.OperatorKind == BinaryOperatorKind.And
                    && binPat.LeftPattern.NarrowedType is { } narrowedType
                    && !SymbolEqualityComparer.Default.Equals(narrowedType, valueType))
                {
                    var resultSlot = _ctx.AllocTemp("SystemBoolean");
                    EmitAssign(resultSlot, Const(false, "SystemBoolean"));
                    var matched = EmitPatternCheckImpl(valueVal, valueType, binPat.LeftPattern);
                    _builder.EmitIf(matched, b =>
                    {
                        var nt = _ctx.AllocTemp(GetUdonType(narrowedType));
                        EmitAssign(nt, valueVal);
                        EmitAssign(resultSlot, EmitPatternCheckImpl(SlotRef(nt), narrowedType, binPat.RightPattern));
                    });
                    return SlotRef(resultSlot);
                }
                var leftVal = EmitPatternCheckImpl(valueVal, valueType, binPat.LeftPattern);
                var rightVal = EmitPatternCheckImpl(valueVal, valueType, binPat.RightPattern);
                var opName = binPat.OperatorKind == BinaryOperatorKind.And
                    ? "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean"
                    : "SystemBoolean.__op_ConditionalOr__SystemBoolean_SystemBoolean__SystemBoolean";
                return ExternCall(opName, new List<CLeaf> { leftVal, rightVal }, "SystemBoolean");
            }

            case IRecursivePatternOperation rec when rec.DeconstructionSubpatterns.Length > 0:
            {
                // Positional/deconstruction pattern — tuple-typed only (reuse the aggregate object[]
                // machinery; user-defined Deconstruct is out of scope).
                if (valueType is not INamedTypeSymbol aggType || !EmitPolicy.IsAggregateType(valueType))
                    throw new System.NotSupportedException(
                        "Positional pattern is only supported on tuple types (user Deconstruct is not supported).");
                var layout = _ctx.GetAggregateLayout(aggType);
                if (rec.DeconstructionSubpatterns.Length != layout.Count)
                    throw new System.NotSupportedException(
                        $"Positional pattern element count ({rec.DeconstructionSubpatterns.Length}) "
                        + $"does not match tuple arity ({layout.Count}).");

                var aggSlot = _ctx.AllocTemp("SystemObjectArray");
                EmitAssign(aggSlot, valueVal);

                CLeaf result = Const(true, "SystemBoolean");
                for (int i = 0; i < rec.DeconstructionSubpatterns.Length; i++)
                {
                    var elemType = layout.Fields[i].Type;
                    var elemRaw = AggregateAbi.ReadSlot(_builder, SlotRef(aggSlot), i, "SystemObject");
                    // Materialize into a typed temp (Udon COPY unboxes) so the sub-pattern compares
                    // with the correct type tag, exactly as tuple deconstruction extracts elements.
                    var elemSlot = _ctx.AllocTemp(GetUdonType(elemType));
                    EmitAssign(elemSlot, elemRaw);
                    var subResult = EmitPatternCheckImpl(SlotRef(elemSlot), elemType, rec.DeconstructionSubpatterns[i]);
                    result = ExternCall(
                        "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                        new List<CLeaf> { result, subResult }, "SystemBoolean");
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

                CLeaf guard;
                if (rec.MatchedType != null && !SymbolEqualityComparer.Default.Equals(rec.MatchedType, valueType))
                    guard = EmitTypeCheck(valueVal, rec.MatchedType);
                else if (!valueType.IsValueType)
                    guard = ExternCall(
                        "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                        new List<CLeaf> { valueVal, Const(null, "SystemObject") }, "SystemBoolean");
                else
                    guard = Const(true, "SystemBoolean");

                _builder.EmitIf(guard, _ =>
                {
                    var matchType = rec.MatchedType ?? valueType;
                    var valSlot = _ctx.AllocTemp(GetUdonType(matchType));
                    EmitAssign(valSlot, valueVal);

                    if (rec.DeclaredSymbol is ILocalSymbol bound)
                    {
                        // Stage 2 §4.1: captured recursive-pattern binding → env cell.
                        // (named out var: the enclosing EmitIf lambda's parameter is `_`.)
                        if (_ctx.TryGetEnvBinding(bound, out var envbUnused))
                        {
                            EnvEmit.Write(_builder, _ctx, bound, SlotRef(valSlot));
                        }
                        else
                        {
                            var boundId = _ctx.DeclareLocal(bound.Name, GetUdonType(bound.Type));
                            _localBindings[bound] = new EmitContext.LocalBinding(boundId);
                            EmitStoreField(boundId, SlotRef(valSlot));
                        }
                    }

                    CLeaf acc = Const(true, "SystemBoolean");
                    // For a user struct / tuple scrutinee, members live as object[] indices — there is no Udon
                    // property getter (SystemObjectArray.__get_X does not exist) — so read via the layout __Get.
                    var aggMatchType = matchType as INamedTypeSymbol;
                    // CA-M1: a v1 class scrutinee is also object[]-backed (no Udon property getter), so its
                    // subpattern members read via the layout __Get / computed-getter arms too. The receiver
                    // was null-guarded above (line ~689, reference-type guard), so the slot read is safe.
                    bool isAgg = aggMatchType != null && EmitPolicy.IsObjectArrayEmulated(aggMatchType);
                    foreach (var sub in rec.PropertySubpatterns)
                    {
                        ITypeSymbol memberType, memberContainingType;
                        string memberName;
                        switch (sub.Member)
                        {
                            case IPropertyReferenceOperation pr:
                                memberType = pr.Property.Type; memberName = pr.Property.Name;
                                memberContainingType = pr.Property.ContainingType; break;
                            case IFieldReferenceOperation fr:
                                memberType = fr.Field.Type; memberName = fr.Field.Name;
                                memberContainingType = fr.Field.ContainingType; break;
                            default:
                                throw new System.NotSupportedException(
                                    $"Property pattern member '{sub.Member?.GetType().Name}' is not supported "
                                    + "(only System/Unity properties and fields).");
                        }
                        CLeaf memberVal;
                        if (isAgg && _ctx.GetAggregateLayout(aggMatchType).TryGetIndex(memberName, out var aggMemberIdx))
                        {
                            // Aggregate member: read the boxed object[] slot, then materialize into a typed temp
                            // (Udon COPY unboxes) so the sub-pattern compares with the correct type tag.
                            var rawMember = AggregateAbi.ReadSlot(_builder, SlotRef(valSlot), aggMemberIdx, "SystemObject");
                            var memberSlot = _ctx.AllocTemp(GetUdonType(memberType));
                            EmitAssign(memberSlot, rawMember);
                            memberVal = SlotRef(memberSlot);
                        }
                        else if (isAgg && sub.Member is IPropertyReferenceOperation cpr
                            && EmitPolicy.IsObjectArrayEmulated(aggMatchType) && cpr.Property.GetMethod is { } cgetter)
                        {
                            // Computed user-struct property (no object[] storage slot): JUMP to its registered
                            // getter with the struct as the receiver, not a non-existent SystemObjectArray.__get_X
                            // extern. The getter is collected in CollectStructMethodsInOperation's subpattern case.
                            memberVal = EmitCallToMethod(ResolveStructMember(cgetter), new List<CLeaf> { SlotRef(valSlot) });
                        }
                        else
                        {
                            // B74 (owner funnel's 7th site): an INHERITED member in a property subpattern
                            // (`c is { enabled: true }` where enabled is declared on the abstract Behaviour)
                            // must resolve its extern owner to the SCRUTINEE's own static type, not the
                            // declaring base — else it mints an unknown UnityEngineBehaviour.__get_enabled__.
                            var memberOwner = GetUdonType(ResolveExternOwnerType(memberContainingType, matchType, memberName));
                            memberVal = ExternCall(
                                ExternResolver.BuildPropertyGetSignature(memberOwner, memberName, GetUdonType(memberType)),
                                new List<CLeaf> { SlotRef(valSlot) }, GetUdonType(memberType));
                        }
                        var subResult = EmitPatternCheckImpl(memberVal, memberType, sub.Pattern);
                        acc = ExternCall(
                            "SystemBoolean.__op_ConditionalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                            new List<CLeaf> { acc, subResult }, "SystemBoolean");
                    }
                    EmitAssign(resultSlot, acc);
                });
                return SlotRef(resultSlot);
            }

            default:
                throw new System.NotSupportedException($"Unsupported pattern: {pattern.GetType().Name}");
        }
    }

    // ── Switch expression ──

    CLeaf VisitSwitchExpression(ISwitchExpressionOperation op)
    {
        var resultType = GetUdonType(op.Type);
        var resultSlot = _ctx.AllocTemp(resultType);
        // Initialize result to default in case no arm matches (non-exhaustive)
        EmitAssign(resultSlot, Const(
            EmitPolicy.ParseConstValue(resultType, GetDefaultConstValue(resultType)), resultType));
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
        else
        {
            // Non-exhaustive fallthrough: C# throws SwitchExpressionException, but Udon has no
            // exceptions. Match the null-invoke deviation (§8-8): loud LogError at runtime, then keep
            // the default(T) the result slot was seeded with — never a silent wrong value.
            tail = _ =>
                EmitExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                    new List<CLeaf> { Const(
                        $"USugar: SwitchExpressionException — no arm matched in switch expression ({_classSymbol.Name})",
                        "SystemString") });
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

    CLeaf VisitConditionalExpression(IConditionalOperation op)
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
        _ => "0"
    };

    // ── Tuple binary (== / !=) ──

    CLeaf VisitTupleBinary(ITupleBinaryOperation op)
    {
        if (op.LeftOperand.Type is not INamedTypeSymbol aggType || !aggType.IsTupleType)
            throw new System.NotSupportedException(
                $"Tuple binary operation on non-tuple type: {op.LeftOperand.Type}");
        return EmitTupleStructuralEquality(
            VisitExpression(op.LeftOperand), VisitExpression(op.RightOperand), aggType,
            op.OperatorKind == BinaryOperatorKind.NotEquals);
    }

    // ── Aggregate (tuple) equality (via IBinaryOperation — fallback) ──

    CLeaf EmitAggregateEquality(IBinaryOperation op, INamedTypeSymbol aggType)
        => EmitTupleStructuralEquality(
            VisitExpression(op.LeftOperand), VisitExpression(op.RightOperand), aggType,
            op.OperatorKind == BinaryOperatorKind.NotEquals);

    CLeaf EmitTupleStructuralEquality(CValue leftArr, CValue rightArr, INamedTypeSymbol aggType, bool isNotEquals)
    {
        var result = EmitAggregateElementsEqual(leftArr, rightArr, aggType);
        if (isNotEquals)
            result = ExternCall("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
                new List<CLeaf> { result }, "SystemBoolean");
        return result;
    }

    // Field-by-field structural equality of two object[]-backed tuples. A nested-tuple element recurses
    // (boxed object.Equals would otherwise do REFERENCE equality on the nested object[] and never match);
    // a scalar element uses SystemObject.__Equals (object.Equals = VALUE equality, NOT __op_Equality which
    // is reference equality). Caveat: float NaN compares equal under object.Equals.
    CLeaf EmitAggregateElementsEqual(CValue leftArr, CValue rightArr, INamedTypeSymbol aggType)
    {
        var layout = _ctx.GetAggregateLayout(aggType);
        var leftSlot = _ctx.AllocTemp("SystemObjectArray"); EmitAssign(leftSlot, leftArr);
        var rightSlot = _ctx.AllocTemp("SystemObjectArray"); EmitAssign(rightSlot, rightArr);

        CLeaf result = Const(true, "SystemBoolean");
        for (int i = 0; i < layout.Count; i++)
        {
            var leftElem = AggregateAbi.ReadSlot(_builder, SlotRef(leftSlot), i, "SystemObject");
            var rightElem = AggregateAbi.ReadSlot(_builder, SlotRef(rightSlot), i, "SystemObject");

            CLeaf elemEq = layout.Fields[i].Type is INamedTypeSymbol nested && nested.IsTupleType
                ? EmitAggregateElementsEqual(leftElem, rightElem, nested) // nested tuple → recurse
                : ExternCall("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
                    new List<CLeaf> { leftElem, rightElem }, "SystemBoolean");

            result = ExternCall("SystemBoolean.__op_LogicalAnd__SystemBoolean_SystemBoolean__SystemBoolean",
                new List<CLeaf> { result, elemEq }, "SystemBoolean");
        }
        return result;
    }

    // ── Delegate comparison helpers (design §2.5) ──
    // IsDelegateTyped / CompareDelegateToNull / CompareDelegates moved to HandlerBase (wave-9
    // round-4 [X1]: the .Equals METHOD spelling of delegate equality reuses the same value
    // comparison from InvocationHandler — one knowledge source).

    bool IsNullLiteral(IOperation op)
    {
        var unwrapped = op;
        while (unwrapped is IConversionOperation conv) unwrapped = conv.Operand;
        return unwrapped is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
    }

}
