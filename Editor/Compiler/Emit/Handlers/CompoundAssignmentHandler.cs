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
        var lv = PrepareLValue(op.Target);
        var leftVal = lv.Value;

        // B67/M4b parity (found by the M4b DiffFuzz sweep): `s += x` is string.Concat one surface over —
        // a user-enum operand must synthesize its name (this path Concat'd the raw number: CLR "xSpades"
        // vs VM "x2") and a v1-class operand must dispatch ToString like the binary form. Unwrap BEFORE
        // evaluating: the class operand arrives boxed and the erasure choke would loud-reject the
        // wrapped visit — but only through the value-preserving conversions (WjR3 A11: the full strip ate
        // the user's inline `(Suit)(k % 4)` cast and Concat'd the raw number). Plain operands fall
        // through untouched (byte-neutral for every prior shape).
        if (op.OperatorKind == BinaryOperatorKind.Add && GetStorageTypeName(op.Type) == "SystemString")
        {
            var vOp = UnwrapConcatOperand(op.Value);
            // Same choke as the binary-concat and interpolation surfaces: an ndim or object[]-emulated
            // value-type operand (user struct / tuple / anonymous type) would launder to "System.Object[]".
            ClassAbi.RejectImplicitToString(vOp.Type);
            if (ResolveType(vOp.Type) is INamedTypeSymbol vt
                && (TypeClassifier.IsUserClass(vt) || ExternResolver.IsUserEnum(vt)))
            {
                var converted = ConvertConcatOperand(VisitExpression(vOp), vOp);
                var concat = ExternCall("SystemString.__Concat__SystemObject_SystemObject__SystemString",
                    new List<CLeaf> { leftVal, converted }, StorageTypes.String);
                lv.Write(concat);
                return concat;
            }
        }

        var rightVal = VisitExpression(op.Value);

        // User-defined struct operator (s += t uses the struct's operator +): static method call, then write
        // back. The struct's Udon type is SystemObjectArray, so ResolveBinaryExtern would build a bogus extern.
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } cuOpM
            && cuOpM.ContainingType is INamedTypeSymbol cuOpCt && TypeClassifier.IsObjectArrayEmulated(cuOpCt))
        {
            var res = EmitCallToMethod(ResolveStructMember(cuOpM), new List<CLeaf> { leftVal, rightVal });
            lv.Write(res);
            return res;
        }

        // Nullable (lifted) compound assignment: x += v  →  x = lifted(x, v) (null-propagating).
        if (EmitPolicy.IsNullableT(op.Target.Type, out var tUnderlying))
        {
            var rNullable = EmitPolicy.IsNullableT(op.Value.Type, out var vUnderlying);
            var lifted = EmitLiftedBinaryCore(
                leftVal, true, tUnderlying,
                rightVal, rNullable, rNullable ? vUnderlying : op.Value.Type,
                op.OperatorKind, op.OperatorMethod, op.Type);
            lv.Write(lifted);
            return lifted;
        }

        var resultType = GetStorageTypeName(op.Type);

        // long/ulong/uint %= : no Udon op_Remainder extern; polyfill a - (a/b)*b (shared with the binary path).
        if (op.OperatorKind == BinaryOperatorKind.Remainder && RemainderNeedsPolyfill(resultType))
        {
            var rem = EmitRemainderViaDivision(leftVal, rightVal, resultType);
            lv.Write(rem);
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
        var leftType = GetStorageTypeName(op.Target.Type);
        if (ExternResolver.IsSmallIntOrChar(leftType))
            leftVal = PromoteToInt32(leftVal, leftType);
        var rightType = GetStorageTypeName(op.Value.Type);
        if (ExternResolver.IsSmallIntOrChar(rightType))
            rightVal = PromoteToInt32(rightVal, rightType);

        var sig = ExternResolver.ResolveBinaryExtern(
            op.OperatorKind, op.OperatorMethod,
            ResolveType(op.Target.Type), ResolveType(op.Value.Type), ResolveType(op.Type));
        CLeaf resultVal = ExternCall(sig, new List<CLeaf> { leftVal, rightVal }, new StorageType(opResultType));

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opResultType != resultType)
            resultVal = EmitNarrowingConvert(resultVal, opResultType, resultType);

        lv.Write(resultVal);
        return resultVal;
    }

    /// <summary>
    /// Multicast design (2026-07-03 §1.4/§7 A-M1): `d += h` / `d -= h` lowers to
    /// `d = __dlg_combine_{sig}(d, h)` / `d = __dlg_remove_{sig}(d, h)`. The helper is a per-class
    /// synthetic function (UasmEmitter.EmitMulticastCombineRemoveHelpers, sibling of
    /// DelegateBridgeEmitter) minted lazily via RegisterMulticastSig — a class with no `+=`/`-=`
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

        var lv = PrepareLValue(op.Target);
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

        var resultVal = _builder.InternalCall(helperName, new List<CLeaf> { leftVal, rightVal }, new StorageType(DelegateAbi.BundleType));
        lv.Write(resultVal);
        return resultVal;
    }

    /// <summary>
    /// `evt += h` / `evt -= h` (design §2.2, A-M2): Roslyn models event add/remove as
    /// IEventAssignmentOperation, distinct from ICompoundAssignmentOperation — same combine/remove
    /// helper lowering as a plain delegate field's `+=`/`-=` (§1.4), just against the event's backing
    /// storage (UasmEmitter.DeclareEvent) instead of an lvalue-captured target. A field-like event on
    /// another behaviour uses the delegate-field GetProgramVariable/combine/SetProgramVariable RMW
    /// protocol. Custom accessors remain calls and are handled separately above.
    /// </summary>
    CLeaf VisitEventAssignment(IEventAssignmentOperation op)
    {
        if (op.EventReference is not IEventReferenceOperation evtRef)
            throw new System.NotSupportedException("Unsupported event assignment target.");
        var evt = evtRef.Event;
        var storageName = evt.IsStatic
            ? StaticOwnerAbi.EventName(evt,
                ResolveType(evt.ContainingType) as INamedTypeSymbol ?? evt.ContainingType)
            : evt.Name;

        if (evt.ContainingType is INamedTypeSymbol { TypeKind: TypeKind.Interface } localInterface
            && _planner.InterfaceIsLocalUserClassOnly(localInterface))
        {
            var accessor = op.Adds ? evt.AddMethod : evt.RemoveMethod;
            if (accessor == null)
                throw new System.NotSupportedException($"Event '{evt.Name}' has no accessor.");
            var recvSlot = _builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
            EmitAssign(recvSlot, LoadInstanceRaw(evtRef.Instance));
            var handlerSlot = _builder.AllocScratch(GetStorageType(evt.Type));
            EmitAssign(handlerSlot, VisitExpression(op.HandlerValue));
            var targets = _ctx.VirtualDispatch.ResolveInterfaceTargets(localInterface, accessor);
            if (targets.Count == 0)
                throw new System.NotSupportedException(
                    $"Interface event '{localInterface.Name}.{evt.Name}' has no minted implementation.");
            if (targets.Count == 1)
            {
                EmitExprStmt(EmitCallToMethod(ResolveStructMember(targets[0].Impl),
                    new List<CLeaf> { SlotRef(recvSlot), SlotRef(handlerSlot) }, op.Syntax));
                return null;
            }
            var typeObj = AggregateAbi.ReadSlot(_builder, SlotRef(recvSlot), 0, StorageTypes.String);
            foreach (var target in targets)
            {
                var match = ExternCall(
                    "SystemString.__op_Equality__SystemString_SystemString__SystemBoolean",
                    new List<CLeaf> { typeObj, LoadField(target.TypeObjVar, StorageTypes.String) },
                    StorageTypes.Boolean);
                _builder.EmitIf(match, _ => EmitExprStmt(EmitCallToMethod(
                    ResolveStructMember(target.Impl),
                    new List<CLeaf> { SlotRef(recvSlot), SlotRef(handlerSlot) }, op.Syntax)), null);
            }
            return null;
        }

        // Custom-accessor events have no synthesized backing storage; invoke their add/remove body.
        if (evt.AddMethod == null || !evt.AddMethod.IsImplicitlyDeclared
            || evt.RemoveMethod == null || !evt.RemoveMethod.IsImplicitlyDeclared)
        {
            var accessor = op.Adds ? evt.AddMethod : evt.RemoveMethod;
            if (accessor == null)
                throw new System.NotSupportedException($"Event '{evt.Name}' has no accessor.");
            var objectArrayOwner = evt.ContainingType is INamedTypeSymbol owner
                && TypeClassifier.IsObjectArrayEmulated(owner);
            var crossBehaviourCustom = !evt.IsStatic
                && evtRef.Instance is not IInstanceReferenceOperation;
            CLeaf stagedReceiver = null;
            if (crossBehaviourCustom)
                stagedReceiver = objectArrayOwner
                    ? LoadInstanceRaw(evtRef.Instance)
                    : VisitExpression(evtRef.Instance);
            var emittedHandler = VisitEmittedValue(op.HandlerValue);
            var handlerValue = emittedHandler.Leaf;
            if (objectArrayOwner)
            {
                if (accessor.IsStatic)
                {
                    EmitExprStmt(EmitCallToMethod(ResolveStructMember(accessor),
                        new List<CLeaf> { handlerValue }, op.Syntax));
                    return null;
                }
                var receiver = evtRef.Instance is IInstanceReferenceOperation
                    ? (_ctx.Methods.CurrentStructReceiverParamId is { } receiverId
                        ? LoadField(receiverId, new StorageType(AggregateAbi.ArrayType))
                        : throw new System.NotSupportedException(
                            $"Custom event '{evt.Name}' has no class receiver in this context."))
                    : stagedReceiver;
                EmitExprStmt(EmitCallToMethod(ResolveStructMember(accessor),
                    new List<CLeaf> { receiver, handlerValue }, op.Syntax));
                return null;
            }
            if (crossBehaviourCustom)
            {
                RejectUnsafeCrossProgramEventHandler(evt, emittedHandler.Info);
                var (exportName, paramIds, _) = GetCalleeLayout(accessor);
                if (paramIds.Length != 1)
                    throw new System.InvalidOperationException(
                        $"Event accessor '{accessor.Name}' has {paramIds.Length} parameters; expected one.");
                CrossCall(stagedReceiver, exportName,
                    new List<(string, CLeaf)> { (paramIds[0], handlerValue) },
                    System.Array.Empty<ReturnSlot>(), StorageTypes.Void,
                    TryMarkReentrantCrossDispatch(op, accessor));
                return null;
            }
            EmitExprStmt(EmitCallToMethod(accessor, new List<CLeaf> { handlerValue }, op.Syntax));
            return null;
        }

        // Field-like cross-behaviour events share the delegate-field RMW transport.
        var delegateType = (INamedTypeSymbol)evt.Type;
        var invoke = delegateType.DelegateInvokeMethod;
        DelegateAbi.ValidateNoRefOutParams(invoke);
        var crossBehaviour = !evt.IsStatic
            && evtRef.Instance is not IInstanceReferenceOperation;
        CLeaf eventReceiver = null;
        CLeaf currentVal;
        if (crossBehaviour)
        {
            eventReceiver = VisitExpression(evtRef.Instance);
            currentVal = ExternCall(
                ExternResolver.EventReceiverGetProgramVariable,
                new List<CLeaf> { eventReceiver, Const(storageName, StorageTypes.String) },
                StorageTypes.Object);
        }
        else
        {
            currentVal = LoadField(storageName, new StorageType(DelegateAbi.BundleType));
        }
        var handler = VisitEmittedValue(op.HandlerValue);
        if (op.Adds)
            RejectUnsafeCrossProgramEventHandler(evt, handler.Info);
        var handlerVal = handler.Leaf;

        var sigPart = DelegateAbi.BuildSigPart(invoke, _ctx.Generics.TypeParamMap);
        RegisterMulticastSig(sigPart, invoke);

        var helperName = op.Adds
            ? DelegateAbi.MulticastCombineName(sigPart)
            : DelegateAbi.MulticastRemoveName(sigPart);

        var resultVal = _builder.InternalCall(helperName, new List<CLeaf> { currentVal, handlerVal }, new StorageType(DelegateAbi.BundleType));
        if (crossBehaviour)
            EmitExternVoid(
                ExternResolver.EventReceiverSetProgramVariable,
                new List<CLeaf> { eventReceiver, Const(storageName, StorageTypes.String), resultVal });
        else
            EmitStoreField(storageName, resultVal);
        return null; // event add/remove is a void-shaped statement expression
    }

    CLeaf PromoteToInt32(CLeaf value, string srcUdonType)
        => ExternCall($"SystemConvert.__ToInt32__{srcUdonType}__SystemInt32",
            new List<CLeaf> { value }, StorageTypes.Int32);

    CLeaf VisitIncrementDecrement(IIncrementOrDecrementOperation op)
    {
        // Capture lvalue sub-expressions once to avoid double evaluation
        var lv = PrepareLValue(op.Target);
        var targetVal = lv.Value;

        // User-defined struct operator ++/-- (a single-operand static method returning the new struct), then
        // write back. Postfix returns the captured OLD value; the built-in op_Addition path below would build
        // a bogus extern on the struct's SystemObjectArray type and use the wrong (value, 1) shape.
        if (op.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator } iuOpM
            && iuOpM.ContainingType is INamedTypeSymbol iuOpCt && TypeClassifier.IsObjectArrayEmulated(iuOpCt))
        {
            var res = EmitCallToMethod(ResolveStructMember(iuOpM), new List<CLeaf> { targetVal });
            lv.Write(res);
            return op.IsPostfix ? lv.Value : res;
        }

        // Nullable (lifted) increment/decrement: x++  →  x = lifted(x, 1) (null-propagating).
        if (EmitPolicy.IsNullableT(op.Type, out var incUnderlying))
        {
            var kind = op.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
            var lifted = EmitLiftedBinaryCore(
                targetVal, true, incUnderlying,
                Const(1, GetStorageType(incUnderlying)), false, incUnderlying,
                kind, null, op.Type);
            lv.Write(lifted);
            // Postfix returns the OLD value: targetVal (= lv.Value) is a single-assignment scratch leaf bound
            // before the write-back, which stores to the target's heap id and never touches this scratch.
            return op.IsPostfix ? targetVal : lifted;
        }

        var udonType = GetStorageTypeName(op.Type);

        // Promote small integers: Udon VM has no byte/sbyte/short/ushort operators
        var opType = udonType;
        if (ExternResolver.IsSmallIntOrChar(opType))
            opType = "SystemInt32";

        var oneConst = Const(1, new StorageType(opType));

        // Explicit operand promotion to match the int extern signature.
        if (ExternResolver.IsSmallIntOrChar(udonType))
            targetVal = PromoteToInt32(targetVal, udonType);

        var isIncrement = op.Kind == OperationKind.Increment;
        var externName = isIncrement ? "op_Addition" : "op_Subtraction";
        var sig = ExternResolver.BuildMethodSignature(
            opType, ExternResolver.GetOperatorExternName(externName),
            new[] { opType, opType }, opType);

        CLeaf resultVal = ExternCall(sig, new List<CLeaf> { targetVal, oneConst }, new StorageType(opType));

        // Narrow back to original type if promoted (C#-unchecked wrap, not checked Convert)
        if (opType != udonType)
            resultVal = EmitNarrowingConvert(resultVal, opType, udonType);

        lv.Write(resultVal);

        // Postfix returns the OLD un-promoted value. lv.Value is the single-assignment scratch leaf bound by
        // CaptureLValue (NOT `targetVal`, which PromoteToInt32 may have overwritten above); the write-back
        // stores resultVal to the target's heap id and never touches that scratch, so it still holds the old value.
        return op.IsPostfix ? lv.Value : resultVal;
    }
}
