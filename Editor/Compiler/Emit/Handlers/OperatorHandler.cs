using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class OperatorHandler : IExpressionHandler
{
    readonly LoweringServices _lowering;
    public OperatorHandler(LoweringServices lowering) => _lowering = lowering;

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
        var operatorMethod = op.OperatorMethod;
        if (operatorMethod != null)
            operatorMethod = _lowering.RequireBoundCallSite(
                op, CallableSiteKind.Operator, operatorMethod).Callable.Site.Target;

        LoweringServices.RejectChecked(op.IsChecked);

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
        if (operatorMethod is { MethodKind: MethodKind.UserDefinedOperator } binOpM
            && binOpM.ContainingType is INamedTypeSymbol binOpCt && TypeClassifier.IsObjectArrayEmulated(binOpCt))
        {
            var lhs = _lowering.VisitExpression(op.LeftOperand);
            var rhs = _lowering.VisitExpression(op.RightOperand);
            return _lowering.EmitCallToMethod(_lowering.ResolveStructMember(binOpM), new List<CLeaf> { lhs, rhs });
        }


        // ── User class (v1) reference equality: c1 == c2 / c == null → reference compare on the object[]
        // bundle (the bundle reference IS the identity; an unoverridden Equals/== is reference equality). ──
        if (ClassAbi.IsReferenceEquality(op.OperatorKind, op.LeftOperand.Type, op.RightOperand.Type))
            return ClassAbi.EmitReferenceEquality(_lowering.Builder, op.OperatorKind,
                _lowering.VisitExpression(op.LeftOperand), _lowering.VisitExpression(op.RightOperand));

        // ── Delegate null check / equality (design §2.5 — TYPE-routed, so fields, locals, params,
        // array elements, properties, and expression results all land here; the dispatch stays gated
        // on the delegate type because this linear handler scan is first-match) ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && (DelegateAbi.IsDelegateType(op.LeftOperand.Type) || DelegateAbi.IsDelegateType(op.RightOperand.Type)))
        {
            bool leftIsNull = IsNullLiteral(op.LeftOperand);
            bool rightIsNull = IsNullLiteral(op.RightOperand);
            bool isNotEquals = op.OperatorKind == BinaryOperatorKind.NotEquals;

            // d == null / d != null → reference null check on the BUNDLE itself (P4: delegate-null is
            // the bundle reference being null; [0] being null is a different condition).
            if (rightIsNull)
                return DelegateAbi.CompareToNull(_lowering.Builder, _lowering.VisitExpression(op.LeftOperand), isNotEquals);
            if (leftIsNull)
                return DelegateAbi.CompareToNull(_lowering.Builder, _lowering.VisitExpression(op.RightOperand), isNotEquals);

            // d1 == d2 → element-wise (target, method) value equality with null legs (fcd07).
            if (DelegateAbi.IsDelegateType(op.LeftOperand.Type) && DelegateAbi.IsDelegateType(op.RightOperand.Type))
                return DelegateAbi.CompareDelegates(_lowering.Builder,
                    _lowering.VisitExpression(op.LeftOperand), _lowering.VisitExpression(op.RightOperand), isNotEquals);
        }

        // wave-13 multishapes lens (2026-07-04): a PLAIN `d1 + d2` / `d1 - d2` on delegate-typed VALUES
        // (Roslyn IBinaryOperation) is C#'s Delegate.Combine/Remove operator overload -- the SAME
        // operation `d += h` (ICompoundAssignmentOperation, CompoundAssignmentHandler) and `evt += h`
        // (IEventAssignmentOperation) already lower to the combine/remove helper, but this operation
        // kind was never routed there and fell through to the generic numeric/extern binary-op path,
        // which emitted a non-existent "SystemObjectArray.__op_Addition__..." extern.
        if (op.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            && DelegateAbi.IsDelegateType(op.LeftOperand.Type) && DelegateAbi.IsDelegateType(op.RightOperand.Type))
        {
            var delegateType = (INamedTypeSymbol)op.Type;
            var invoke = delegateType.DelegateInvokeMethod;
            DelegateAbi.ValidateNoRefOutParams(invoke);

            var combineLeftVal = _lowering.VisitExpression(op.LeftOperand);
            var combineRightVal = _lowering.VisitExpression(op.RightOperand);

            var sigPart = DelegateAbi.BuildSigPart(
                invoke, _lowering.State.Session.Types, _lowering.State.Generics.TypeParamMap);
            _lowering.RegisterMulticastSig(sigPart, invoke,
                op.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine
                    : MulticastOperations.Remove);

            var helperName = op.OperatorKind == BinaryOperatorKind.Add
                ? DelegateAbi.MulticastCombineName(sigPart)
                : DelegateAbi.MulticastRemoveName(sigPart);
            return _lowering.Builder.InternalCall(helperName, new List<CLeaf> { combineLeftVal, combineRightVal }, new StorageType(DelegateAbi.BundleType));
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
                var nv = _lowering.VisitExpression(leftNullable ? op.LeftOperand : op.RightOperand);
                if (op.OperatorKind == BinaryOperatorKind.NotEquals)
                    return NullableAbi.HasValue(_lowering.Builder, nv); // != null  ⇔  HasValue
                return NullableAbi.IsNull(_lowering.Builder, nv);
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
            return EmitLiftedBinary(op, operatorMethod);
        }

        // ── Aggregate (tuple) structural equality ──
        if (op.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && TypeClassifier.IsAggregateValue(op.LeftOperand.Type)
            && op.LeftOperand.Type is INamedTypeSymbol aggType)
        {
            return EmitAggregateEquality(op, aggType);
        }

        // Constant folding: compile-time evaluable binary expressions
        if (op.ConstantValue.HasValue)
        {
            var constType = _lowering.GetStorageTypeName(op.Type);
            return _lowering.Const(EmitPolicy.ParseConstValue(constType, LoweringServices.ToInvariantString(op.ConstantValue.Value)), new StorageType(constType));
        }

        // B67: string concat with a user enum operand. C# boxes each operand to object for
        // string.Concat(object,object), so the enum operand arrives wrapped in a Suit→object conversion; look
        // THROUGH it — but only through the value-preserving conversions (WjR3 A11: the full strip ate the
        // user's inline `(Suit)(k % 4)` cast and Concat'd the raw number). Left as-is the enum would be
        // Concat-ToString'd to its underlying number, so convert that operand to the C#-correct name string
        // first and emit the object/object concat directly (routing it back through the generic path would
        // re-select the extern by the now-string operand types).
        if (op.OperatorKind == BinaryOperatorKind.Add && _lowering.GetStorageTypeName(op.Type) == "SystemString")
        {
            var lOp = LoweringServices.UnwrapConcatOperand(op.LeftOperand);
            var rOp = LoweringServices.UnwrapConcatOperand(op.RightOperand);
            // M4b: a v1 class operand stringifies through the object.ToString dispatch slot (same
            // lowering as an interpolation hole; the M3 sealed-only fast path dissolved into the
            // helper's devirt arm). Both operand VALUES evaluate first — C# order: Concat's operands
            // are fully evaluated before either ToString runs — then each class operand dispatches.
            var lCls = _lowering.ResolveType(lOp.Type) as INamedTypeSymbol;
            var rCls = _lowering.ResolveType(rOp.Type) as INamedTypeSymbol;
            bool lIsClass = lCls != null && TypeClassifier.IsUserClass(lCls);
            bool rIsClass = rCls != null && TypeClassifier.IsUserClass(rCls);
            if (lIsClass || rIsClass)
            {
                var l = _lowering.VisitExpression(lOp);
                var r = _lowering.VisitExpression(rOp);
                l = _lowering.ConvertConcatOperand(l, lOp);
                r = _lowering.ConvertConcatOperand(r, rOp);
                return _lowering.ExternCall(UdonAbi.StringConcatObjects,
                    new List<CLeaf> { l, r }, StorageTypes.String);
            }
            ClassAbi.RejectImplicitToString(lOp.Type);
            ClassAbi.RejectImplicitToString(rOp.Type);
            if (_lowering.IsFoldedEnum(_lowering.ResolveType(lOp.Type)) || _lowering.IsFoldedEnum(_lowering.ResolveType(rOp.Type)))
            {
                var l = _lowering.VisitExpression(lOp);
                l = _lowering.ConvertConcatOperand(l, lOp);
                var r = _lowering.VisitExpression(rOp);
                r = _lowering.ConvertConcatOperand(r, rOp);
                return _lowering.ExternCall(UdonAbi.StringConcatObjects,
                    new List<CLeaf> { l, r }, StorageTypes.String);
            }
        }

        var leftVal = _lowering.VisitExpression(op.LeftOperand);
        var rightVal = _lowering.VisitExpression(op.RightOperand);

        // Enum operands → convert to underlying type before comparison
        leftVal = _lowering.EmitEnumToUnderlying(leftVal, op.LeftOperand.Type);
        rightVal = _lowering.EmitEnumToUnderlying(rightVal, op.RightOperand.Type);

        var resultType = _lowering.GetStorageTypeName(op.Type);

        // long/ulong/uint % : Udon has no op_Remainder extern for these; lower to a - (a / b) * b via the
        // shared polyfill (uint included — it has Division/Multiplication/Subtraction but no Remainder).
        if (op.OperatorKind == BinaryOperatorKind.Remainder && LoweringServices.RemainderNeedsPolyfill(resultType))
            return _lowering.EmitRemainderViaDivision(leftVal, rightVal, resultType);

        // Udon has no byte/sbyte/short/ushort operators. C# promotes a plain small int to int before the op, but
        // a small-int-backed ENUM keeps its underlying width (enum|enum stays enum, etc.), so the result type is
        // small-int here only for such enums: compute in int32 and narrow back. (Comparisons yield bool, never a
        // small int, so they skip this.)
        if (ExternResolver.IsSmallIntOrChar(resultType))
        {
            var leftU = UnderlyingUdon(op.LeftOperand.Type);
            var rightU = UnderlyingUdon(op.RightOperand.Type);
            var li = leftU == "SystemInt32" ? leftVal : _lowering.EmitNarrowingConvert(leftVal, leftU, "SystemInt32");
            var ri = rightU == "SystemInt32" ? rightVal : _lowering.EmitNarrowingConvert(rightVal, rightU, "SystemInt32");
            var int32 = _lowering.Compilation.GetSpecialType(SpecialType.System_Int32);
            var raw = _lowering.ExternCall(
                ExternResolver.ResolveBuiltInBinaryExtern(op.OperatorKind,
                    _lowering.ResolveType(int32), _lowering.ResolveType(int32),
                    _lowering.ResolveType(int32), _lowering.GetStorageTypeName),
                new List<CLeaf> { li, ri }, StorageTypes.Int32);
            return _lowering.EmitNarrowingConvert(raw, "SystemInt32", resultType);
        }

        var sig = operatorMethod != null
            ? _lowering.State.BoundAbi.RequireOperator(operatorMethod, type => _lowering.GetStorageTypeName(type))
            : _lowering.State.BoundAbi.RequireExact(ExternResolver.ResolveBuiltInBinaryExtern(
                op.OperatorKind,
                _lowering.ResolveType(op.LeftOperand.Type),
                _lowering.ResolveType(op.RightOperand.Type),
                _lowering.ResolveType(op.Type), _lowering.GetStorageTypeName));

        // UnityEngineObject equality/inequality: cast operands to UnityEngineObject temps
        if (operatorMethod != null
            && _lowering.GetStorageTypeName(operatorMethod.ContainingType) == "UnityEngineObject"
            && (op.OperatorKind == BinaryOperatorKind.Equals
                || op.OperatorKind == BinaryOperatorKind.NotEquals))
        {
            var objLeftSlot = _lowering.State.Builder.AllocScratch(StorageTypes.UnityObject);
            _lowering.EmitAssign(objLeftSlot, leftVal);
            var objRightSlot = _lowering.State.Builder.AllocScratch(StorageTypes.UnityObject);
            _lowering.EmitAssign(objRightSlot, rightVal);
            return _lowering.ExternCall(sig, new List<CLeaf> { _lowering.SlotRef(objLeftSlot), _lowering.SlotRef(objRightSlot) }, new StorageType(resultType));
        }

        return _lowering.ExternCall(sig, new List<CLeaf> { leftVal, rightVal }, new StorageType(resultType));
    }

    // The effective Udon storage type of an operand: an enum is stored as (and operates on) its underlying type.
    string UnderlyingUdon(ITypeSymbol t) =>
        t is INamedTypeSymbol n && n.TypeKind == TypeKind.Enum
            ? _lowering.GetStorageTypeName(n.EnumUnderlyingType) : _lowering.GetStorageTypeName(t);

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
        if (_lowering.GetStorageTypeName(a) != _lowering.GetStorageTypeName(b))
            return;
        throw new System.NotSupportedException(
            $"typeof('{(_lowering.ResolveType(a) ?? a).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') "
            + $"==/!= typeof('{(_lowering.ResolveType(b) ?? b).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') "
            + "is unsound: these are distinct C# types but Udon folds both onto one runtime type tag "
            + $"('{_lowering.GetStorageTypeName(a)}'), so the comparison is silently true where C# says false.");
    }

    // The type operand of a typeof, seeing through an identity/boxing conversion wrapper; null if not a typeof.
    static ITypeSymbol AsTypeofOperand(IOperation op)
    {
        op = LoweringServices.UnwrapConversions(op);
        return op is ITypeOfOperation t ? t.TypeOperand : null;
    }

    // Nullable bool `&` / `|` with C# three-valued logic: a known false dominates `&` (false & null = false)
    // and a known true dominates `|` (true | null = true), regardless of the other operand being null.
    CLeaf EmitLiftedBoolLogic(IBinaryOperation op)
        => NullableAbi.EmitLiftedBoolLogic(_lowering.Builder,
            _lowering.VisitExpression(op.LeftOperand), _lowering.VisitExpression(op.RightOperand),
            op.OperatorKind);

    // Lifted binary operator on Nullable<T> (null propagation) — see LoweringServices.EmitLiftedBinaryCore.
    CLeaf EmitLiftedBinary(IBinaryOperation op, IMethodSymbol operatorMethod)
    {
        var leftNullable = EmitPolicy.IsNullableT(op.LeftOperand.Type, out var lu);
        var rightNullable = EmitPolicy.IsNullableT(op.RightOperand.Type, out var ru);
        var leftVal = _lowering.VisitExpression(op.LeftOperand);
        var rightVal = _lowering.VisitExpression(op.RightOperand);
        return _lowering.EmitLiftedBinaryCore(
            leftVal, leftNullable, leftNullable ? lu : op.LeftOperand.Type,
            rightVal, rightNullable, rightNullable ? ru : op.RightOperand.Type,
            op.OperatorKind, operatorMethod, op.Type);
    }

    CLeaf VisitConditionalAnd(IBinaryOperation op)
    {
        // a && b: evaluate b only when a is true (short-circuit).
        // VisitExpression on operands may emit Core IR statements (e.g. temp stores for
        // enum conversions, UnityEngineObject casts). Those statements must live inside
        // the conditional branch so they don't execute unconditionally.
        var leftVal = _lowering.VisitExpression(op.LeftOperand);
        var resultSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Boolean);
        _lowering.EmitAssign(resultSlot, _lowering.Const(false, StorageTypes.Boolean));
        _lowering.Builder.EmitIf(leftVal, _ =>
        {
            var rightVal = _lowering.VisitExpression(op.RightOperand);
            _lowering.EmitAssign(resultSlot, rightVal);
        });
        return _lowering.SlotRef(resultSlot);
    }

    CLeaf VisitConditionalOr(IBinaryOperation op)
    {
        // a || b: evaluate b only when a is false (short-circuit).
        var leftVal = _lowering.VisitExpression(op.LeftOperand);
        var resultSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Boolean);
        _lowering.EmitAssign(resultSlot, _lowering.Const(true, StorageTypes.Boolean));
        _lowering.Builder.EmitIf(leftVal, null, _ =>
        {
            var rightVal = _lowering.VisitExpression(op.RightOperand);
            _lowering.EmitAssign(resultSlot, rightVal);
        });
        return _lowering.SlotRef(resultSlot);
    }

    // ── Unary ──

    CLeaf VisitUnary(IUnaryOperation op)
    {
        var operatorMethod = op.OperatorMethod;
        if (operatorMethod != null)
            operatorMethod = _lowering.RequireBoundCallSite(
                op, CallableSiteKind.Operator, operatorMethod).Callable.Site.Target;

        LoweringServices.RejectChecked(op.IsChecked);

        // ── User-defined struct operator (ANY unary kind, incl. ~): static operator method call. MUST come
        // before the built-in ~ branch below — that branch builds an extern on the struct's SystemObjectArray
        // type and throws "Bitwise NOT not supported on SystemObjectArray". Only fires for a user operator
        // (a built-in lifted ~ has OperatorMethod null → falls through to the BitwiseNegation handling). ──
        if (operatorMethod is { MethodKind: MethodKind.UserDefinedOperator } unOpM
            && unOpM.ContainingType is INamedTypeSymbol unOpCt && TypeClassifier.IsObjectArrayEmulated(unOpCt))
        {
            var operand = _lowering.VisitExpression(op.Operand);
            return _lowering.EmitCallToMethod(_lowering.ResolveStructMember(unOpM), new List<CLeaf> { operand });
        }

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
            var constType = _lowering.GetStorageTypeName(op.Type);
            return _lowering.Const(EmitPolicy.ParseConstValue(constType, LoweringServices.ToInvariantString(op.ConstantValue.Value)), new StorageType(constType));
        }

        // Lifted unary on Nullable<T> (null propagation): null stays null, else apply the op to the unwrapped
        // value. The non-lifted path below would build an invalid extern on the Nullable operand type.
        if (op.IsLifted && EmitPolicy.IsNullableT(op.Type, out var unaryResU))
            return EmitLiftedUnary(op, unaryResU);

        var operandVal = _lowering.VisitExpression(op.Operand);
        var resultType = _lowering.GetStorageTypeName(op.Type);

        var sig = operatorMethod != null && !ExternResolver.IsNumericType(op.Operand.Type)
            ? _lowering.State.BoundAbi.RequireOperator(operatorMethod, type => _lowering.GetStorageTypeName(type))
            : _lowering.State.BoundAbi.RequireExact(BuildBuiltinUnaryKey(op));

        return _lowering.ExternCall(sig, new List<CLeaf> { operandVal }, new StorageType(resultType));
    }

    // Lifted unary minus / logical-not on Nullable<T>: null-preserving. A small-int operand is promoted to
    // the (int) result underlying for the SystemInt32 op; int/long/float/bool pass through. The unwrapped
    // result is re-boxed into the SystemObject nullable slot. (Lifted bitwise ~ is not covered here.)
    CLeaf EmitLiftedUnary(IUnaryOperation op, ITypeSymbol resUnderlying)
    {
        EmitPolicy.IsNullableT(op.Operand.Type, out var opUnderlying);
        var resU = _lowering.GetStorageTypeName(resUnderlying);

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
            return _lowering.EmitLiftedBinaryCore(
                _lowering.VisitExpression(op.Operand), true, opUnderlying,
                _lowering.Const(allBitsValue, new StorageType(resU)), false, resUnderlying,
                BinaryOperatorKind.ExclusiveOr, null, op.Type);
        }

        return NullableAbi.EmitLiftedUnary(_lowering.Builder, _lowering.VisitExpression(op.Operand),
            opUnderlying, resUnderlying, op.OperatorKind, _lowering.GetStorageTypeName,
            (boxed, underlying) => NullableAbi.PromoteBoxedToInt32(_lowering.Builder, boxed, underlying,
                _lowering.Compilation.GetSpecialType(SpecialType.System_Int32), _lowering.GetStorageTypeName));
    }

    CLeaf VisitBitwiseNot(IUnaryOperation op)
    {
        var operandVal = _lowering.VisitExpression(op.Operand);
        var operandType = _lowering.GetStorageTypeName(op.Operand.Type);
        var resultType = _lowering.GetStorageTypeName(op.Type);

        // An enum operand has SpecialType None; ~ operates on (and narrows back to) the underlying type, so key
        // off the underlying. operandType/resultType already resolve to it via GetStorageTypeName.
        var effSpecial = op.Operand.Type is INamedTypeSymbol enumOperand && enumOperand.TypeKind == TypeKind.Enum
            ? enumOperand.EnumUnderlyingType.SpecialType
            : op.Operand.Type.SpecialType;

        // Udon has no byte/sbyte/short/ushort operators. C# already promotes a plain small int to int before ~
        // (so op.Operand.Type is int there); only a small-int-BACKED ENUM reaches here as byte/short. Compute it
        // in int32 then narrow back to the underlying: ~x ≡ (T)((int)x ^ -1).
        if (effSpecial is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16)
        {
            var asInt = operandType == "SystemInt32" ? operandVal : _lowering.EmitNarrowingConvert(operandVal, operandType, "SystemInt32");
            var xored = _lowering.ExternCall(UdonAbiKey.Method("SystemInt32", "op_LogicalXor", new[] { "SystemInt32", "SystemInt32" }, "SystemInt32"),
                new List<CLeaf> { asInt, _lowering.Const(-1, StorageTypes.Int32) }, StorageTypes.Int32);
            return resultType == "SystemInt32" ? xored : _lowering.EmitNarrowingConvert(xored, "SystemInt32", resultType);
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
        var allBitsConst = _lowering.Const(allBitsValue, new StorageType(operandType));

        return _lowering.ExternCall(
            ExternResolver.ResolveBuiltInBinaryExtern(
                BinaryOperatorKind.ExclusiveOr,
                _lowering.ResolveType(op.Operand.Type), _lowering.ResolveType(op.Operand.Type),
                _lowering.ResolveType(op.Type), _lowering.GetStorageTypeName),
            new List<CLeaf> { operandVal, allBitsConst },
            new StorageType(resultType));
    }

    // ── Is-type / Is-pattern ──

    CLeaf VisitIsType(IIsTypeOperation op)
    {
        // A bare `x is T` (no binding) is exactly a type pattern without a variable — route it through the
        // single guarded EmitTypeCheck so NO reachable path emits IsInstanceOfType without the layer-2
        // distinguishability guard.
        return _lowering.EmitTypeCheck(_lowering.VisitExpression(op.ValueOperand), op.TypeOperand);
    }

    CLeaf VisitIsPattern(IIsPatternOperation op)
    {
        var valueVal = _lowering.VisitExpression(op.Value);
        return EmitPatternCheckImpl(valueVal, op.Value.Type, op.Pattern);
    }

    // ── Pattern matching (public — called from LoopHandler via LoweringState dispatch) ──

    public CLeaf EmitPatternCheckImpl(CLeaf valueVal, ITypeSymbol valueType, IPatternOperation pattern)
    {
        // Nullable<T> scrutinee (boxed object): `x is null` is an object null check; any other pattern
        // requires HasValue, then matches against the unboxed underlying value (Udon unboxes transparently).
        if (EmitPolicy.IsNullableT(valueType, out var patUnderlying))
            return NullableAbi.EmitPatternCheck(_lowering.Builder, valueVal, patUnderlying, pattern,
                EmitPatternCheckImpl);

        switch (pattern)
        {
            case IConstantPatternOperation constPat:
                // Shared with the nullable switch single-value clause (CW19) — enum-underlying
                // conversion, null → SystemObject equality, small-int/char two-sided int32 promotion.
                return _lowering.EmitConstantEquality(valueVal, valueType, _lowering.VisitExpression(constPat.Value),
                    constPat.Value.ConstantValue is { HasValue: true, Value: null });
            case INegatedPatternOperation negated:
            {
                var innerVal = EmitPatternCheckImpl(valueVal, valueType, negated.Pattern);
                return _lowering.ExternCall(
                    UdonAbi.BooleanNot,
                    new List<CLeaf> { innerVal },
                    StorageTypes.Boolean);
            }
            case ITypePatternOperation typePat:
                return _lowering.EmitTypeCheck(valueVal, typePat.MatchedType);

            case IDeclarationPatternOperation declPat:
            {
                // `var x` (no explicit MatchedType / MatchesNull) matches any value — no type check.
                var isVar = declPat.MatchedType == null || declPat.MatchesNull;
                var checkVal = isVar ? (CLeaf)_lowering.Const(true, StorageTypes.Boolean) : _lowering.EmitTypeCheck(valueVal, declPat.MatchedType);
                if (declPat.DeclaredSymbol is ILocalSymbol local)
                {
                    if (isVar)
                        BindPatternLocal(local, valueVal); // always matches → bind unconditionally
                    else
                        // Only assign when the type check succeeds — avoid invalid type COPY on mismatch
                        _lowering.Builder.EmitIf(checkVal, _ => BindPatternLocal(local, valueVal));
                }
                return checkVal;
            }
            case IDiscardPatternOperation:
                return _lowering.Const(true, StorageTypes.Boolean);

            case IRelationalPatternOperation relPat:
            {
                var constVal = _lowering.VisitExpression(relPat.Value);
                // Enum scrutinee → compare on the underlying type (mirrors the constant-pattern arm).
                var scrut = _lowering.EmitEnumToUnderlying(valueVal, valueType);
                constVal = _lowering.EmitEnumToUnderlying(constVal, valueType);
                var underlyingSym = valueType is INamedTypeSymbol relEnum && relEnum.TypeKind == TypeKind.Enum
                    ? relEnum.EnumUnderlyingType : valueType;
                var valType = _lowering.GetStorageTypeName(underlyingSym);
                if (ExternResolver.IsSmallIntOrChar(valType))
                {
                    // A nullable small-int/char (or small-underlying enum) scrutinee may be boxed as a plain int;
                    // promote both sides to int32 so the strict small-int extern's box-tag fetch cannot InvalidCast.
                    scrut = NullableAbi.PromoteBoxedToInt32(_lowering.Builder, scrut, underlyingSym,
                        _lowering.Compilation.GetSpecialType(SpecialType.System_Int32), _lowering.GetStorageTypeName).Value;
                    constVal = NullableAbi.PromoteBoxedToInt32(_lowering.Builder, constVal, underlyingSym,
                        _lowering.Compilation.GetSpecialType(SpecialType.System_Int32), _lowering.GetStorageTypeName).Value;
                    valType = "SystemInt32";
                }
                var opName = relPat.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => "op_LessThan",
                    BinaryOperatorKind.LessThanOrEqual => "op_LessThanOrEqual",
                    BinaryOperatorKind.GreaterThan => "op_GreaterThan",
                    BinaryOperatorKind.GreaterThanOrEqual => "op_GreaterThanOrEqual",
                    _ => throw new System.NotSupportedException(
                        $"Unsupported relational operator: {relPat.OperatorKind}")
                };
                return _lowering.ExternCall(
                    UdonAbiKey.Method(
                        valType, opName, new[] { valType, valType }, "SystemBoolean"),
                    new List<CLeaf> { scrut, constVal },
                    StorageTypes.Boolean);
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
                    var resultSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Boolean);
                    _lowering.EmitAssign(resultSlot, _lowering.Const(false, StorageTypes.Boolean));
                    var matched = EmitPatternCheckImpl(valueVal, valueType, binPat.LeftPattern);
                    _lowering.Builder.EmitIf(matched, b =>
                    {
                        var nt = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(narrowedType));
                        _lowering.EmitAssign(nt, valueVal);
                        _lowering.EmitAssign(resultSlot, EmitPatternCheckImpl(_lowering.SlotRef(nt), narrowedType, binPat.RightPattern));
                    });
                    return _lowering.SlotRef(resultSlot);
                }
                var leftVal = EmitPatternCheckImpl(valueVal, valueType, binPat.LeftPattern);
                var rightVal = EmitPatternCheckImpl(valueVal, valueType, binPat.RightPattern);
                var opName = binPat.OperatorKind == BinaryOperatorKind.And
                    ? UdonAbi.BooleanConditionalAnd
                    : UdonAbi.BooleanConditionalOr;
                return _lowering.ExternCall(opName, new List<CLeaf> { leftVal, rightVal }, StorageTypes.Boolean);
            }

            case IRecursivePatternOperation rec:
                return EmitRecursivePattern(valueVal, valueType, rec);

            default:
                throw new System.NotSupportedException($"Unsupported pattern: {pattern.GetType().Name}");
        }
    }

    CLeaf EmitRecursivePattern(CLeaf valueVal, ITypeSymbol valueType, IRecursivePatternOperation rec)
    {
        // One guarded lowering owns every recursive-pattern facet. Splitting positional and property
        // forms into competing switch arms made the first arm silently drop the second facet, the
        // matched-type/null guard, and the designator for `T(...) { P: ... } v`.
        var resultSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Boolean);
        _lowering.EmitAssign(resultSlot, _lowering.Const(false, StorageTypes.Boolean));

        CLeaf guard;
        if (rec.MatchedType != null && !SymbolEqualityComparer.Default.Equals(rec.MatchedType, valueType))
            guard = _lowering.EmitTypeCheck(valueVal, rec.MatchedType);
        else if (!valueType.IsValueType)
            guard = _lowering.ExternCall(
                UdonAbi.ObjectInequality,
                new List<CLeaf> { valueVal, _lowering.Const(null, StorageTypes.Object) }, StorageTypes.Boolean);
        else
            guard = _lowering.Const(true, StorageTypes.Boolean);

        _lowering.Builder.EmitIf(guard, _ =>
        {
            var matchType = rec.MatchedType ?? valueType;
            var valSlot = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(matchType));
            _lowering.EmitAssign(valSlot, valueVal);

            var acc = EmitRecursivePositionalChecks(_lowering.SlotRef(valSlot), matchType, rec);
            acc = EmitRecursivePropertyChecks(_lowering.SlotRef(valSlot), matchType, rec, acc);
            _lowering.EmitAssign(resultSlot, acc);

            // A recursive-pattern designator is assigned only when every positional/property facet
            // matched. Keeping the write under the final result also avoids publishing a stale
            // environment value from a failed pattern.
            if (rec.DeclaredSymbol is ILocalSymbol bound)
                _lowering.Builder.EmitIf(acc, __ => BindPatternLocal(bound, _lowering.SlotRef(valSlot)));
        });
        return _lowering.SlotRef(resultSlot);
    }

    CLeaf EmitRecursivePositionalChecks(CLeaf valueVal, ITypeSymbol matchType,
        IRecursivePatternOperation rec)
    {
        if (rec.DeconstructionSubpatterns.Length == 0)
            return _lowering.Const(true, StorageTypes.Boolean);

        // Tuple/user-struct positional patterns read their aggregate slots directly. Other supported
        // types call the registered user Deconstruct(out ...) method.
        if (matchType is not INamedTypeSymbol aggType || !TypeClassifier.IsAggregateValue(matchType))
        {
            var deconstruct = rec.DeconstructSymbol is not IMethodSymbol deconstructMethod
                ? null : _lowering.ResolveStructMember(_lowering.SubstituteMethodTypeArgs(deconstructMethod));
            if (deconstruct == null
                || deconstruct.Parameters.Length != rec.DeconstructionSubpatterns.Length
                || deconstruct.Parameters.Any(p => p.RefKind != RefKind.Out))
                throw new System.NotSupportedException(
                    "Positional pattern requires a supported user Deconstruct(out ...) method.");

            var args = new List<CLeaf> { valueVal };
            foreach (var parameter in deconstruct.Parameters)
                args.Add(_lowering.SlotRef(_lowering.Builder.AllocScratch(_lowering.GetStorageType(parameter.Type))));
            _lowering.EmitExprStmt(_lowering.EmitCallToMethod(deconstruct, args, rec.Syntax));
            if (!_lowering.MethodParamVarIds.TryGetValue(deconstruct, out var paramIds))
                throw new System.InvalidOperationException(
                    $"Deconstruct method '{deconstruct.ToDisplayString()}' was not registered.");

            CLeaf deconstructResult = _lowering.Const(true, StorageTypes.Boolean);
            for (int i = 0; i < rec.DeconstructionSubpatterns.Length; i++)
            {
                var elemType = deconstruct.Parameters[i].Type;
                var elem = _lowering.LoadField(paramIds[i], _lowering.GetStorageType(elemType));
                var subResult = EmitPatternCheckImpl(elem, elemType, rec.DeconstructionSubpatterns[i]);
                deconstructResult = CombinePatternChecks(deconstructResult, subResult);
            }
            return deconstructResult;
        }

        var layout = _lowering.State.Aggregates.GetLayout(aggType);
        if (rec.DeconstructionSubpatterns.Length != layout.Count)
            throw new System.NotSupportedException(
                $"Positional pattern element count ({rec.DeconstructionSubpatterns.Length}) "
                + $"does not match tuple arity ({layout.Count}).");

        CLeaf result = _lowering.Const(true, StorageTypes.Boolean);
        for (int i = 0; i < rec.DeconstructionSubpatterns.Length; i++)
        {
            var elemType = layout.Fields[i].Type;
            var elemRaw = AggregateAbi.ReadSlot(_lowering.Builder, valueVal, i, StorageTypes.Object);
            // Materialize into a typed temp (Udon COPY unboxes) so the sub-pattern compares with
            // the correct type tag.
            var elemSlot = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(elemType));
            _lowering.EmitAssign(elemSlot, elemRaw);
            var subResult = EmitPatternCheckImpl(_lowering.SlotRef(elemSlot), elemType,
                rec.DeconstructionSubpatterns[i]);
            result = CombinePatternChecks(result, subResult);
        }
        return result;
    }

    CLeaf EmitRecursivePropertyChecks(CLeaf valueVal, ITypeSymbol matchType,
        IRecursivePatternOperation rec, CLeaf acc)
    {
        var aggMatchType = matchType as INamedTypeSymbol;
        bool isAgg = aggMatchType != null && TypeClassifier.IsObjectArrayEmulated(aggMatchType);
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
            if (isAgg && TypeClassifier.IsUserClass(aggMatchType)
                && sub.Member is IPropertyReferenceOperation vSubRef
                && VirtualDispatch.FindAccessor(vSubRef.Property, getter: true) is { } vSubGetter
                && VirtualDispatch.IsVirtualCall(vSubGetter))
            {
                var dispatched = _lowering.EmitAccessorDispatch(vSubRef, aggMatchType, vSubGetter,
                    valueVal, new List<CLeaf>(), null);
                var vSubSlot = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(memberType));
                _lowering.EmitAssign(vSubSlot, dispatched);
                memberVal = _lowering.SlotRef(vSubSlot);
            }
            else if (isAgg
                     && _lowering.State.Aggregates.GetLayout(aggMatchType).TryGetIndex(memberName, out var aggMemberIdx))
            {
                var rawMember = AggregateAbi.ReadSlot(_lowering.Builder, valueVal, aggMemberIdx, StorageTypes.Object);
                var memberSlot = _lowering.State.Builder.AllocScratch(_lowering.GetStorageType(memberType));
                _lowering.EmitAssign(memberSlot, rawMember);
                memberVal = _lowering.SlotRef(memberSlot);
            }
            else if (isAgg && sub.Member is IPropertyReferenceOperation cpr
                     && cpr.Property.GetMethod is { } cgetter)
            {
                memberVal = _lowering.EmitCallToMethod(_lowering.ResolveStructMember(cgetter),
                    new List<CLeaf> { valueVal });
            }
            else
            {
                var memberOwner = _lowering.GetStorageTypeName(
                    _lowering.ResolveExternOwnerType(memberContainingType, matchType, memberName));
                memberVal = _lowering.ExternCall(
                    _lowering.State.BoundAbi.RequirePropertyGetter(
                        memberOwner, memberName, _lowering.GetStorageTypeName(memberType)),
                    new List<CLeaf> { valueVal }, _lowering.GetStorageType(memberType));
            }

            var subResult = EmitPatternCheckImpl(memberVal, memberType, sub.Pattern);
            acc = CombinePatternChecks(acc, subResult);
        }
        return acc;
    }

    CLeaf CombinePatternChecks(CLeaf left, CLeaf right) =>
        _lowering.ExternCall(
            UdonAbi.BooleanConditionalAnd,
            new List<CLeaf> { left, right }, StorageTypes.Boolean);

    void BindPatternLocal(ILocalSymbol local, CLeaf value)
    {
        // Stage 2 §4.1: captured pattern variable → env cell (its owning scope's env is live at
        // every point a condition/section hosting this pattern executes).
        if (_lowering.State.Closures.TryGetEnvBinding(local, out _))
        {
            EnvEmit.Write(_lowering.Builder, _lowering.State, local, value);
            return;
        }

        var localId = _lowering.State.Storage.DeclareLocal(local.Name, _lowering.GetStorageType(local.Type));
        _lowering.LocalBindings[local] = new LocalBinding(localId);
        _lowering.EmitStoreField(localId, value);
    }

    // ── Switch expression ──

    CLeaf VisitSwitchExpression(ISwitchExpressionOperation op)
    {
        var resultType = _lowering.GetStorageTypeName(op.Type);
        var resultSlot = _lowering.State.Builder.AllocScratch(new StorageType(resultType));
        // Initialize result to default in case no arm matches (non-exhaustive)
        _lowering.EmitAssign(resultSlot, _lowering.Const(
            EmitPolicy.ParseConstValue(resultType, GetDefaultConstValue(resultType)), new StorageType(resultType)));
        var valueVal = _lowering.VisitExpression(op.Value);

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
                var armVal = _lowering.VisitExpression(defArm.Value);
                _lowering.EmitAssign(resultSlot, armVal);
            };
        }
        else
        {
            // Non-exhaustive fallthrough: C# throws SwitchExpressionException, but Udon has no
            // exceptions. Match the null-invoke deviation (§8-8): loud LogError at runtime, then keep
            // the default(T) the result slot was seeded with — never a silent wrong value.
            tail = _ =>
                _lowering.EmitExternVoid(UdonAbi.DebugLogError,
                    new List<CLeaf> { _lowering.Const(
                        $"USugar: SwitchExpressionException — no arm matched in switch expression ({_lowering.ClassSymbol.Name})",
                        StorageTypes.String) });
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
                    _lowering.Builder.EmitIf(checkVal, __ =>
                    {
                        var guardVal = _lowering.VisitExpression(arm.Guard);
                        _lowering.Builder.EmitIf(guardVal, ___ =>
                        {
                            var armVal = _lowering.VisitExpression(arm.Value);
                            _lowering.EmitAssign(resultSlot, armVal);
                        }, elseBranch);
                    }, elseBranch);
                }
                else
                {
                    _lowering.Builder.EmitIf(checkVal, __ =>
                    {
                        var armVal = _lowering.VisitExpression(arm.Value);
                        _lowering.EmitAssign(resultSlot, armVal);
                    }, elseBranch);
                }
            };
        }

        // Emit the chain
        tail?.Invoke(null);

        return _lowering.SlotRef(resultSlot);
    }

    // ── Conditional (ternary) expression ──

    CLeaf VisitConditionalExpression(IConditionalOperation op)
    {
        // cond ? a : b: evaluate branches only on the taken path.
        var condVal = _lowering.VisitExpression(op.Condition);
        var resultType = _lowering.GetStorageTypeName(op.Type);
        var resultSlot = _lowering.State.Builder.AllocScratch(new StorageType(resultType));
        _lowering.Builder.EmitIf(condVal,
            _ => _lowering.EmitAssign(resultSlot, _lowering.VisitExpression(op.WhenTrue)),
            _ => _lowering.EmitAssign(resultSlot, _lowering.VisitExpression(op.WhenFalse)));
        return _lowering.SlotRef(resultSlot);
    }

    // ── Extern signature helpers ──

    static readonly Dictionary<UnaryOperatorKind, string> UnaryOpNames = new()
    {
        [UnaryOperatorKind.Minus] = "op_UnaryMinus",
        [UnaryOperatorKind.Not] = "op_UnaryNegation",
    };

    UdonAbiKey BuildBuiltinUnaryKey(IUnaryOperation op)
    {
        var operandType = _lowering.GetStorageTypeName(op.Operand.Type);
        var returnType = _lowering.GetStorageTypeName(op.Type);
        if (!UnaryOpNames.TryGetValue(op.OperatorKind, out var opName))
            throw new System.NotSupportedException(
                $"Unsupported unary operator: {op.OperatorKind} on type {_lowering.GetStorageTypeName(op.Operand.Type)}");
        // Decimal uses C# method name: op_UnaryNegation (not op_UnaryMinus)
        if (operandType == "SystemDecimal" && op.OperatorKind == UnaryOperatorKind.Minus)
            opName = "op_UnaryNegation";
        return UdonAbiKey.Method(operandType, opName,
            new[] { operandType }, returnType);
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
            _lowering.VisitExpression(op.LeftOperand), _lowering.VisitExpression(op.RightOperand), aggType,
            op.OperatorKind == BinaryOperatorKind.NotEquals);
    }

    // ── Aggregate (tuple) equality (via IBinaryOperation shape) ──

    CLeaf EmitAggregateEquality(IBinaryOperation op, INamedTypeSymbol aggType)
        => EmitTupleStructuralEquality(
            _lowering.VisitExpression(op.LeftOperand), _lowering.VisitExpression(op.RightOperand), aggType,
            op.OperatorKind == BinaryOperatorKind.NotEquals);

    CLeaf EmitTupleStructuralEquality(CValue leftArr, CValue rightArr, INamedTypeSymbol aggType, bool isNotEquals)
    {
        var result = EmitAggregateElementsEqual(leftArr, rightArr, aggType);
        if (isNotEquals)
            result = _lowering.ExternCall(UdonAbi.BooleanNot,
                new List<CLeaf> { result }, StorageTypes.Boolean);
        return result;
    }

    // Field-by-field structural equality of two object[]-backed tuples. A nested-tuple element recurses
    // (boxed object.Equals would otherwise do REFERENCE equality on the nested object[] and never match);
    // a scalar element uses SystemObject.__Equals (object.Equals = VALUE equality, NOT __op_Equality which
    // is reference equality). Caveat: float NaN compares equal under object.Equals.
    CLeaf EmitAggregateElementsEqual(CValue leftArr, CValue rightArr, INamedTypeSymbol aggType)
    {
        var layout = _lowering.State.Aggregates.GetLayout(aggType);
        var leftSlot = _lowering.State.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType)); _lowering.EmitAssign(leftSlot, leftArr);
        var rightSlot = _lowering.State.Builder.AllocScratch(new StorageType(AggregateAbi.ArrayType)); _lowering.EmitAssign(rightSlot, rightArr);

        CLeaf result = _lowering.Const(true, StorageTypes.Boolean);
        for (int i = 0; i < layout.Count; i++)
        {
            var leftElem = AggregateAbi.ReadSlot(_lowering.Builder, _lowering.SlotRef(leftSlot), i, StorageTypes.Object);
            var rightElem = AggregateAbi.ReadSlot(_lowering.Builder, _lowering.SlotRef(rightSlot), i, StorageTypes.Object);

            CLeaf elemEq = layout.Fields[i].Type is INamedTypeSymbol nested && nested.IsTupleType
                ? EmitAggregateElementsEqual(leftElem, rightElem, nested) // nested tuple → recurse
                : _lowering.ExternCall(UdonAbi.ObjectEquals,
                    new List<CLeaf> { leftElem, rightElem }, StorageTypes.Boolean);

            result = _lowering.ExternCall(UdonAbi.BooleanLogicalAnd,
                new List<CLeaf> { result, elemEq }, StorageTypes.Boolean);
        }
        return result;
    }

    // ── Delegate comparison helpers (design §2.5) ──
    // Delegate comparison helpers moved to DelegateAbi (wave-9
    // round-4 [X1]: the .Equals METHOD spelling of delegate equality reuses the same value
    // comparison from InvocationHandler — one knowledge source).

    bool IsNullLiteral(IOperation op)
    {
        var unwrapped = op;
        while (unwrapped is IConversionOperation conv) unwrapped = conv.Operand;
        return unwrapped is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
    }

}
