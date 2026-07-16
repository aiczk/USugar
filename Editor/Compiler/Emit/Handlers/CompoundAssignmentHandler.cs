using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a += b`, `a -= b`, `a++`, `++a`, etc.</summary>
public class CompoundAssignmentHandler : AssignmentHandlerBase, IExpressionHandler
{
    public CompoundAssignmentHandler(EmitContext ctx) : base(ctx) { }

    // IIncrementOrDecrementOperation spans BOTH Increment and Decrement — a single kind would drop `x--`.
    public OperationKind[] HandledKinds { get; } = new[]
    {
        OperationKind.CompoundAssignment, OperationKind.Increment, OperationKind.Decrement, OperationKind.EventAssignment,
    };

    public CLeaf Handle(IOperation op) => op switch
    {
        ICompoundAssignmentOperation compound => VisitCompoundAssignment(compound),
        IIncrementOrDecrementOperation incDec => VisitIncrementDecrement(incDec),
        IEventAssignmentOperation evtAssign => VisitEventAssignment(evtAssign),
        _ => throw new System.NotSupportedException(op.GetType().Name),
    };

    CLeaf VisitCompoundAssignment(ICompoundAssignmentOperation op)
    {
        // Multicast design (2026-07-03 §1.4/§7 A-M1): `+=`/`-=` on ANY delegate-typed target (field,
        // local, param, property, array element, …) lowers to the sig's synthetic combine/remove helper
        // instead of rejecting. Predicate unchanged from the former reject arm (widened per Stage-1 §5.2).
        if (op.Target.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null)
            return VisitDelegateCompoundAssignment(op, nt);

        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var leftVal = lv.Value;

        // B67/M4b parity (found by the M4b DiffFuzz sweep): `s += x` is string.Concat one surface over —
        // a user-enum operand must synthesize its name (this path Concat'd the raw number: CLR "xSpades"
        // vs VM "x2") and a v1-class operand must dispatch ToString like the binary form. Unwrap BEFORE
        // evaluating: the class operand arrives boxed and the erasure choke would loud-reject the
        // wrapped visit. Plain operands fall through untouched (byte-neutral for every prior shape).
        if (op.OperatorKind == BinaryOperatorKind.Add && GetUdonType(op.Type) == "SystemString")
        {
            var vOp = UnwrapConversions(op.Value);
            // Same choke as the binary-concat and interpolation surfaces: an ndim or object[]-emulated
            // value-type operand (user struct / tuple / anonymous type) would launder to "System.Object[]".
            ClassAbi.RejectImplicitToString(vOp.Type);
            if (ResolveType(vOp.Type) is INamedTypeSymbol vt
                && (EmitPolicy.IsUserClassType(vt) || ExternResolver.IsUserEnum(vt)))
            {
                var converted = ConvertConcatOperand(VisitExpression(vOp), vOp);
                var concat = ExternCall("SystemString.__Concat__SystemObject_SystemObject__SystemString",
                    new List<CLeaf> { leftVal, converted }, "SystemString");
                EmitWriteBack(op.Target, concat, lv);
                return concat;
            }
        }

        var rightVal = VisitExpression(op.Value);

        // User-defined struct operator (s += t uses the struct's operator +): static method call, then write
        // back. The struct's Udon type is SystemObjectArray, so ResolveBinaryExtern would build a bogus extern.
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } cuOpM
            && cuOpM.ContainingType is INamedTypeSymbol cuOpCt && EmitPolicy.IsObjectArrayEmulated(cuOpCt))
        {
            var res = EmitCallToMethod(ResolveStructMember(cuOpM), new List<CLeaf> { leftVal, rightVal });
            EmitWriteBack(op.Target, res, lv);
            return res;
        }
        ClassAbi.RejectUserOperator(op.OperatorMethod);

        // Nullable (lifted) compound assignment: x += v  →  x = lifted(x, v) (null-propagating).
        if (EmitPolicy.IsNullableT(op.Target.Type, out var tUnderlying))
        {
            var rNullable = EmitPolicy.IsNullableT(op.Value.Type, out var vUnderlying);
            var lifted = EmitLiftedBinaryCore(
                leftVal, true, tUnderlying,
                rightVal, rNullable, rNullable ? vUnderlying : op.Value.Type,
                op.OperatorKind, op.OperatorMethod, op.Type);
            EmitWriteBack(op.Target, lifted, lv);
            return lifted;
        }

        var resultType = GetUdonType(op.Type);

        // long/ulong/uint %= : no Udon op_Remainder extern; polyfill a - (a/b)*b (shared with the binary path).
        if (op.OperatorKind == BinaryOperatorKind.Remainder && RemainderNeedsPolyfill(resultType))
        {
            var rem = EmitRemainderViaDivision(leftVal, rightVal, resultType);
            EmitWriteBack(op.Target, rem, lv);
            return rem;
        }

        // Promote small integers for the operation temp.
        // Udon VM has no byte/sbyte/short/ushort operators — operations go through int.
        var opResultType = resultType;
        if (ExternResolver.IsSmallIntOrChar(resultType))
            opResultType = "SystemInt32";

        // Explicit operand promotion: byte slot pushed to int extern requires ToInt32 conversion
        // (matches ExpressionHandler.VisitConversion behaviour for byte+byte). Without this we
        // rely on Udon VM's implicit boxed-value coercion, which is fragile across SDK updates.
        var leftType = GetUdonType(op.Target.Type);
        if (ExternResolver.IsSmallIntOrChar(leftType))
            leftVal = PromoteToInt32(leftVal, leftType);
        var rightType = GetUdonType(op.Value.Type);
        if (ExternResolver.IsSmallIntOrChar(rightType))
            rightVal = PromoteToInt32(rightVal, rightType);

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        CLeaf resultVal = ExternCall(sig, new List<CLeaf> { leftVal, rightVal }, opResultType);

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opResultType != resultType)
            resultVal = EmitNarrowingConvert(resultVal, opResultType, resultType);

        EmitWriteBack(op.Target, resultVal, lv);
        return resultVal;
    }

    /// <summary>
    /// Multicast design (2026-07-03 §1.4/§7 A-M1): `d += h` / `d -= h` lowers to
    /// `d = __dlg_combine_{sig}(d, h)` / `d = __dlg_remove_{sig}(d, h)`. The helper is a per-class
    /// synthetic function (UasmEmitter.EmitMulticastCombineRemoveHelpers, sibling of
    /// EmitPendingDelegateBridges) minted lazily via RegisterMulticastSig — a class with no `+=`/`-=`
    /// on a delegate emits none of this (single-cast golden stays byte-identical, §6 gate).
    /// </summary>
    CLeaf VisitDelegateCompoundAssignment(ICompoundAssignmentOperation op, INamedTypeSymbol delegateType)
    {
        if (op.OperatorKind != BinaryOperatorKind.Add && op.OperatorKind != BinaryOperatorKind.Subtract)
            throw new System.NotSupportedException(
                $"Delegate compound operator '{op.OperatorKind}' is not supported.");

        var invoke = delegateType.DelegateInvokeMethod;
        // §3.4-1: re-validate ref/out at the lowering site, mirroring the dispatch-side re-validation —
        // a delegate value from a foreign source never passed creation-site validation.
        DelegateAbi.ValidateNoRefOutParams(invoke);

        var lv = CaptureLValue(op.Target);
        var leftVal = lv.Value;
        var right = VisitEmittedValue(op.Value);
        if (op.OperatorKind == BinaryOperatorKind.Add)
            RejectUnsafeCrossProgramDelegateWrite(op.Target, right.Info);
        var rightVal = right.Leaf;

        var sigPart = DelegateAbi.BuildSigPart(invoke, _ctx.Generics.TypeParamMap);
        RegisterMulticastSig(sigPart, invoke);

        var helperName = op.OperatorKind == BinaryOperatorKind.Add
            ? DelegateAbi.MulticastCombineName(sigPart)
            : DelegateAbi.MulticastRemoveName(sigPart);

        var resultVal = _builder.InternalCall(helperName, new List<CLeaf> { leftVal, rightVal }, DelegateAbi.BundleType);
        EmitWriteBack(op.Target, resultVal, lv);
        return resultVal;
    }

    /// <summary>
    /// `evt += h` / `evt -= h` (design §2.2, A-M2): Roslyn models event add/remove as
    /// IEventAssignmentOperation, distinct from ICompoundAssignmentOperation — same combine/remove
    /// helper lowering as a plain delegate field's `+=`/`-=` (§1.4), just against the event's backing
    /// storage (UasmEmitter.DeclareEvent) instead of an lvalue-captured target. Same-program only:
    /// cross-behaviour subscribe (`other.Foo += h`) is a LOUD reject (§2.2) — the add accessor would
    /// need to run ON the target program, which this compiler cannot combine into from here.
    /// </summary>
    CLeaf VisitEventAssignment(IEventAssignmentOperation op)
    {
        if (op.EventReference is not IEventReferenceOperation evtRef)
            throw new System.NotSupportedException("Unsupported event assignment target.");
        var evt = evtRef.Event;

        // R1 armor: custom-accessor events never get backing storage (UasmEmitter.DeclareEvent already
        // loud-rejects them at declaration) — if one somehow reached here, fail loud rather than write
        // to nonexistent storage.
        if (evt.AddMethod == null || !evt.AddMethod.IsImplicitlyDeclared
            || evt.RemoveMethod == null || !evt.RemoveMethod.IsImplicitlyDeclared)
            throw new System.NotSupportedException(
                $"Custom-accessor event '{evt.Name}' (add{{...}}/remove{{...}}) is not supported.");

        // §2.2 R2: cross-behaviour subscribe. A this-receiver is the ONLY supported shape.
        if (evtRef.Instance is not IInstanceReferenceOperation)
            throw new System.NotSupportedException(
                "cross-behaviour event subscription is not supported; combine into a delegate field the "
                + "target exposes, or subscribe from within the declaring behaviour.");

        var delegateType = (INamedTypeSymbol)evt.Type;
        var invoke = delegateType.DelegateInvokeMethod;
        DelegateAbi.ValidateNoRefOutParams(invoke);
        var currentVal = LoadField(evt.Name, DelegateAbi.BundleType);
        var handler = VisitEmittedValue(op.HandlerValue);
        if (op.Adds)
            RejectUnsafeCrossProgramEventHandler(evt, handler.Info);
        var handlerVal = handler.Leaf;

        var sigPart = DelegateAbi.BuildSigPart(invoke, _ctx.Generics.TypeParamMap);
        RegisterMulticastSig(sigPart, invoke);

        var helperName = op.Adds
            ? DelegateAbi.MulticastCombineName(sigPart)
            : DelegateAbi.MulticastRemoveName(sigPart);

        var resultVal = _builder.InternalCall(helperName, new List<CLeaf> { currentVal, handlerVal }, DelegateAbi.BundleType);
        EmitStoreField(evt.Name, resultVal);
        return null; // event add/remove is a void-shaped statement expression
    }

    CLeaf PromoteToInt32(CLeaf value, string srcUdonType)
        => ExternCall($"SystemConvert.__ToInt32__{srcUdonType}__SystemInt32",
            new List<CLeaf> { value }, "SystemInt32");

    CLeaf VisitIncrementDecrement(IIncrementOrDecrementOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = CaptureLValue(op.Target);
        var targetVal = lv.Value;

        // User-defined struct operator ++/-- (a single-operand static method returning the new struct), then
        // write back. Postfix returns the captured OLD value; the built-in op_Addition path below would build
        // a bogus extern on the struct's SystemObjectArray type and use the wrong (value, 1) shape.
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } iuOpM
            && iuOpM.ContainingType is INamedTypeSymbol iuOpCt && EmitPolicy.IsObjectArrayEmulated(iuOpCt))
        {
            var res = EmitCallToMethod(ResolveStructMember(iuOpM), new List<CLeaf> { targetVal });
            EmitWriteBack(op.Target, res, lv);
            return op.IsPostfix ? lv.Value : res;
        }
        ClassAbi.RejectUserOperator(op.OperatorMethod);

        // Nullable (lifted) increment/decrement: x++  →  x = lifted(x, 1) (null-propagating).
        if (EmitPolicy.IsNullableT(op.Type, out var incUnderlying))
        {
            var kind = op.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
            var lifted = EmitLiftedBinaryCore(
                targetVal, true, incUnderlying,
                Const(1, GetUdonType(incUnderlying)), false, incUnderlying,
                kind, null, op.Type);
            EmitWriteBack(op.Target, lifted, lv);
            // Postfix returns the OLD value: targetVal (= lv.Value) is a single-assignment scratch leaf bound
            // before the write-back, which stores to the target's heap id and never touches this scratch.
            return op.IsPostfix ? targetVal : lifted;
        }

        var udonType = GetUdonType(op.Type);

        // Promote small integers: Udon VM has no byte/sbyte/short/ushort operators
        var opType = udonType;
        if (ExternResolver.IsSmallIntOrChar(opType))
            opType = "SystemInt32";

        var oneConst = Const(1, opType);

        // Explicit operand promotion to match the int extern signature.
        if (ExternResolver.IsSmallIntOrChar(udonType))
            targetVal = PromoteToInt32(targetVal, udonType);

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        CLeaf resultVal = ExternCall(sig, new List<CLeaf> { targetVal, oneConst }, opType);

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opType != udonType)
            resultVal = EmitNarrowingConvert(resultVal, opType, udonType);

        EmitWriteBack(op.Target, resultVal, lv);

        // Postfix returns the OLD un-promoted value. lv.Value is the single-assignment scratch leaf bound by
        // CaptureLValue (NOT `targetVal`, which PromoteToInt32 may have overwritten above); the write-back
        // stores resultVal to the target's heap id and never touches that scratch, so it still holds the old value.
        return op.IsPostfix ? lv.Value : resultVal;
    }
}
