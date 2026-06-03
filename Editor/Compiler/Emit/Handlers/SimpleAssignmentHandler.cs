using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Handles `a = b` simple assignments across all lvalue targets
/// (locals, fields, array elements, properties, cross-behaviour, delegates, struct fields).</summary>
public class SimpleAssignmentHandler : AssignmentHandlerBase, IExpressionHandler
{
    public SimpleAssignmentHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation op) => op is ISimpleAssignmentOperation;

    public CLeaf Handle(IOperation op)
        => op is ISimpleAssignmentOperation assign
            ? VisitAssignment(assign)
            : throw new System.NotSupportedException(op.GetType().Name);

    CLeaf VisitAssignment(ISimpleAssignmentOperation assign)
    {
        // Aggregate field/auto-property write: point.x = 5, result.Item1 = 42, v.X = 7 (struct auto-property).
        // Triggered by the containing type being aggregate, regardless of instance kind.
        if (TryGetAggregateMemberTarget(assign.Target, out var aggInstance, out var aggMemberName)
            && aggInstance.Type is INamedTypeSymbol aggContaining && EmitContext.IsAggregateType(aggContaining)
            && _ctx.GetAggregateLayout(aggContaining).TryGetIndex(aggMemberName, out var fieldIndex))
        {
            var srcVal = VisitExpression(assign.Value);
            var arrExpr = LoadInstanceRaw(aggInstance);
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CLeaf> { arrExpr, Const(fieldIndex, "SystemInt32"), srcVal });
            return srcVal;
        }

        // Computed (non-auto) struct property setter: p.Both = v → call the user setter with the receiver
        // object[] as synthetic param0 (mutates this-fields through the shared backing array). Auto-properties
        // are handled by the aggregate fast path above (their backing field is in the layout).
        if (assign.Target is IPropertyReferenceOperation { Property: { IsIndexer: false, SetMethod: { } aggSetter } } aggSetRef
            && aggSetRef.Instance?.Type is INamedTypeSymbol aggSetType && EmitContext.IsAggregateType(aggSetType)
            && _methodFunctions.ContainsKey(aggSetter.OriginalDefinition))
        {
            var srcVal = VisitExpression(assign.Value);
            EmitExprStmt(EmitCallToMethod(aggSetter.OriginalDefinition,
                new List<CLeaf> { LoadInstanceRaw(aggSetRef.Instance), srcVal }));
            return srcVal;
        }

        if (assign.Target is IArrayElementReferenceOperation arrayElem)
        {
            var arrayVal = VisitExpression(arrayElem.ArrayReference);
            var indexVal = VisitExpression(arrayElem.Indices[0]);
            var srcVal = VisitExpression(assign.Value);
            var arrSymbol = arrayElem.ArrayReference.Type as IArrayTypeSymbol;
            var arrayType = GetArrayType(arrSymbol);
            var elementType = GetArrayElemType(arrSymbol);
            EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<CLeaf> { arrayVal, indexVal, srcVal });
            return srcVal;
        }

        // Self-delegate field assignment: _callback = MyMethod / _callback = () => { } / _callback = null
        if (assign.Target is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } selfDlg
            && selfDlg.Field.Type is INamedTypeSymbol selfDlgType
            && selfDlgType.DelegateInvokeMethod != null
            && _delegateFields.Contains(selfDlg.Field.Name))
        {
            var fieldName = selfDlg.Field.Name;

            var bundle = new DelegateBundle(fieldName);

            // null assignment
            if (assign.Value.ConstantValue is { HasValue: true, Value: null })
            {
                EmitStoreField(bundle.Target, Const(null, "SystemObject"));
                EmitStoreField(bundle.Method, Const(null, "SystemString"));
                EmitStoreField(bundle.Addr, Const(0u, "SystemUInt32"));
                return Const(null, "SystemObject");
            }

            // delegate field copy: _a = _b
            if (assign.Value is IFieldReferenceOperation { Instance: IInstanceReferenceOperation } rhsDlg
                && _delegateFields.Contains(rhsDlg.Field.Name))
            {
                var srcBundle = new DelegateBundle(rhsDlg.Field.Name);
                EmitStoreField(bundle.Target, LoadField(srcBundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver"));
                EmitStoreField(bundle.Method, LoadField(srcBundle.Method, "SystemString"));
                EmitStoreField(bundle.Addr, LoadField(srcBundle.Addr, "SystemUInt32"));
                return LoadField(bundle.Target, "VRCUdonCommonInterfacesIUdonEventReceiver");
            }

            IDelegateCreationOperation dc = assign.Value as IDelegateCreationOperation;
            if (dc == null && assign.Value is IConversionOperation convSelf && convSelf.Operand is IDelegateCreationOperation dc1)
                dc = dc1;

            if (dc != null)
            {
                var (bridgeName, funcRef, thirdParty) = ResolveDelegateBridge(dc);
                var thisRef = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
                var target = thirdParty ?? thisRef;
                var addr = thirdParty != null ? (CLeaf)Const(0u, "SystemUInt32") : funcRef;

                RecordIfCapturingLambda(dc);

                EmitStoreField(bundle.Target, target);
                EmitStoreField(bundle.Method, Const(bridgeName, "SystemString"));
                EmitStoreField(bundle.Addr, addr);
                return target;
            }

            throw new System.NotSupportedException($"Delegate field '{fieldName}' can only be assigned a method group, lambda, or null.");
        }

        // cross-behaviour field write → SetProgramVariable
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } ubTarget && ExternResolver.IsUdonSharpBehaviour(ubTarget.Field.ContainingType))
        {
            // Cross-behaviour delegate field → SetProgramVariable for bundle
            if (ubTarget.Field.Type is INamedTypeSymbol dlgType && dlgType.DelegateInvokeMethod != null)
            {
                if (dlgType.DelegateInvokeMethod.ReturnType.IsTupleType)
                    throw new System.NotSupportedException($"Tuple-return delegate field '{ubTarget.Field.Name}' is not supported.");

                var instanceVal = VisitExpression(ubTarget.Instance);
                var fn = ubTarget.Field.Name;
                var bundle2 = new DelegateBundle(fn);

                // null assignment
                if (assign.Value.ConstantValue is { HasValue: true, Value: null })
                {
                    foreach (var (field, val) in new[] { (bundle2.Target, (CLeaf)Const(null, "SystemObject")), (bundle2.Method, Const(null, "SystemString")), (bundle2.Addr, Const(0u, "SystemUInt32")) })
                        EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                            new List<CLeaf> { instanceVal, Const(field, "SystemString"), val });
                    return Const(null, "SystemObject");
                }

                IDelegateCreationOperation dc = assign.Value as IDelegateCreationOperation;
                if (dc == null && assign.Value is IConversionOperation convCross && convCross.Operand is IDelegateCreationOperation dc2)
                    dc = dc2;

                if (dc != null)
                {
                    RecordIfCapturingLambda(dc);
                    var (bridgeName, _, thirdParty) = ResolveDelegateBridge(dc);
                    var thisRef = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
                    var delegateTarget = thirdParty ?? thisRef;

                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<CLeaf> { instanceVal, Const(bundle2.Target, "SystemString"), delegateTarget });
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<CLeaf> { instanceVal, Const(bundle2.Method, "SystemString"), Const(bridgeName, "SystemString") });
                    EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
                        new List<CLeaf> { instanceVal, Const(bundle2.Addr, "SystemString"), Const(0u, "SystemUInt32") });
                    return delegateTarget;
                }

                throw new System.NotSupportedException($"Delegate field '{fn}' can only be assigned a method group, lambda, or null.");
            }

            var srcVal = VisitExpression(assign.Value);
            var instanceVal2 = VisitExpression(ubTarget.Instance);
            var nameConst = Const(ubTarget.Field.Name, "SystemString");
            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal2, nameConst, srcVal });
            return srcVal;
        }

        if (assign.Target is IFieldReferenceOperation { Instance: not null } fieldTarget
            && fieldTarget.Field.ContainingType.IsValueType)
        {
            var srcVal = VisitExpression(assign.Value);
            var containingType = GetUdonType(fieldTarget.Field.ContainingType);
            var instanceVal = fieldTarget.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(containingType), containingType)
                : VisitExpression(fieldTarget.Instance);
            var valueType = GetUdonType(fieldTarget.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldTarget.Field.Name, valueType);
            EmitExternVoid(sig, new List<CLeaf> { instanceVal, srcVal });
            return srcVal;
        }

        if (assign.Target is IPropertyReferenceOperation propRef)
        {
            var srcVal = VisitExpression(assign.Value);
            var propContainingUdon = GetUdonType(propRef.Property.ContainingType);

            // User-defined indexer on this/base → internal setter call (index args followed by the value).
            if (propRef.Property.IsIndexer && propRef.Instance is IInstanceReferenceOperation
                && propRef.Property.SetMethod != null && _methodFunctions.ContainsKey(propRef.Property.SetMethod))
            {
                var setterArgs = new List<CLeaf>();
                foreach (var arg in propRef.Arguments) setterArgs.Add(VisitExpression(arg.Value));
                setterArgs.Add(srcVal);
                EmitExprStmt(EmitCallToMethod(propRef.Property.SetMethod, setterArgs));
                return srcVal;
            }

            // Static property setter (no instance) — e.g. Time.timeScale = 1.0f
            if (propRef.Instance == null)
            {
                var staticValType = GetUdonType(propRef.Property.Type);
                EmitExternVoid(ExternResolver.BuildPropertySetSignature(propContainingUdon, propRef.Property.Name, staticValType), new List<CLeaf> { srcVal });
                return srcVal;
            }

            // Behaviour/MonoBehaviour have no Udon externs; resolve to actual type
            if (propContainingUdon is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            {
                propContainingUdon = propRef.Instance is IInstanceReferenceOperation
                    ? GetUdonType(_classSymbol)
                    : GetUdonType(propRef.Instance.Type);
            }
            var instanceVal = propRef.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(propContainingUdon), propContainingUdon)
                : VisitExpression(propRef.Instance);
            var containingType = propContainingUdon;
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                var indexArgs = new List<CLeaf> { instanceVal };
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    indexArgs.Add(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                indexArgs.Add(srcVal);
                var indexParamStr = string.Join("_", indexTypes);
                EmitExternVoid($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid", indexArgs);
            }
            else switch (propRef.Instance)
            {
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod != null && _methodFunctions.TryGetValue(propRef.Property.SetMethod, out _):
                    // User-defined property setter on this → internal call
                    EmitExprStmt(EmitCallToMethod(propRef.Property.SetMethod, new List<CLeaf> { srcVal }));
                    break;
                case IInstanceReferenceOperation
                    when propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                         && ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType)
                         && propRef.Property.ContainingType.Name != "UdonSharpBehaviour":
                    // Auto-property set on this → direct variable assignment (user-defined classes only)
                    EmitStoreField(propRef.Property.Name, srcVal);
                    break;
                default:
                {
                    if (ExternResolver.IsUdonSharpBehaviour(propRef.Property.ContainingType) && propRef.Instance is not IInstanceReferenceOperation)
                    {
                        if (propRef.Property.Type is INamedTypeSymbol dlgPropType && dlgPropType.DelegateInvokeMethod != null)
                            throw new System.NotSupportedException("Delegate properties are not supported in v2.1. Use delegate fields instead.");

                        var isAutoSet = propRef.Property.SetMethod?.DeclaringSyntaxReferences.IsEmpty == true;
                        if (isAutoSet || propRef.Property.SetMethod == null)
                        {
                            // Auto-property or read-only: direct SetProgramVariable("PropertyName")
                            var nameConst = Const(propRef.Property.Name, "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, nameConst, srcVal });
                        }
                        else
                        {
                            // Non-auto property setter: call via SendCustomEvent
                            var (exportName, setParamIds, _) = GetCalleeLayout(propRef.Property.SetMethod);

                            // SetProgramVariable for the value parameter
                            var paramNameConst = Const(setParamIds[0], "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid", new List<CLeaf> { instanceVal, paramNameConst, srcVal });

                            // SendCustomEvent to invoke setter
                            var eventConst = Const(exportName, "SystemString");
                            EmitExternVoid("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", new List<CLeaf> { instanceVal, eventConst });
                        }
                    }
                    else
                    {
                        EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType), new List<CLeaf> { instanceVal, srcVal });
                    }

                    break;
                }
            }
            return srcVal;
        }

        // Non-this reference-type field assignment → extern field setter
        if (assign.Target is IFieldReferenceOperation { Instance: not null and not IInstanceReferenceOperation } refFieldTarget
            && !refFieldTarget.Field.ContainingType.IsValueType
            && !ExternResolver.IsUdonSharpBehaviour(refFieldTarget.Field.ContainingType))
        {
            var srcVal = VisitExpression(assign.Value);
            var instanceVal = VisitExpression(refFieldTarget.Instance);
            var containingType = GetUdonType(refFieldTarget.Field.ContainingType);
            var valueType = GetUdonType(refFieldTarget.Field.Type);
            EmitExternVoid(ExternResolver.BuildFieldSetSignature(containingType, refFieldTarget.Field.Name, valueType, isValueType: false), new List<CLeaf> { instanceVal, srcVal });
            return srcVal;
        }

        // Fallback: local variable or this.field. (Private/this delegate-field assignments no longer reach here —
        // private fields are bundled now, so the self-delegate bundle branch above intercepts and returns first.)

        // Delegate-local re-assignment with a lambda (e.g. `f = null; f = (n) => ... f(n-1) ...;` — the
        // required idiom for a recursive lambda). Hoist the lambda and record the var→method binding so the
        // delegate invocation can resolve, then mark self-recursion so EmitCallToMethod spills.
        if (assign.Target is ILocalReferenceOperation dlgLocal
            && dlgLocal.Local.Type.TypeKind == TypeKind.Delegate
            && UnwrapDelegateLambda(assign.Value, out var reassignLambda))
        {
            var hoisted = HoistLambdaToMethod(reassignLambda);
            _delegateVarMap[dlgLocal.Local] = hoisted;
            MarkLambdaSelfRecursion(reassignLambda, dlgLocal.Local, hoisted);
        }

        // VisitExpression clones aggregate locals/params automatically (Clone-on-read).
        var srcFallback = VisitExpression(assign.Value);
        var targetFieldName = GetAssignTargetFieldName(assign.Target);
        EmitStoreField(targetFieldName, srcFallback);
        // The assignment's VALUE is the stored value. Return a fresh read of the target rather than the
        // RHS expression tree: re-emitting the tree (when the assignment is used as an expression, e.g.
        // `G(n = n - 1)`) would re-evaluate it after the store already mutated its inputs. A dead read in
        // statement form is removed by DCE.
        var targetFieldType = _ctx.GetFieldType(targetFieldName);
        if (targetFieldType == null) return srcFallback;
        var loaded = LoadField(targetFieldName, targetFieldType);
        // When the assignment is USED AS A VALUE (e.g. chained `z = y = x`) and the target is an aggregate,
        // that value must be an independent COPY (struct value semantics) — otherwise z aliases y. (diff-fuzz w4)
        return assign.Parent is not IExpressionStatementOperation
               && assign.Target.Type is INamedTypeSymbol tAgg && EmitContext.IsAggregateType(tAgg)
            ? EmitDeepCloneAggregate(loaded, tAgg) : loaded;
    }

    void RecordIfCapturingLambda(IDelegateCreationOperation dc)
    {
        if (dc.Target is IAnonymousFunctionOperation lambda && _ctx.CaptureAnalyzer.HasCaptures(lambda))
            _ctx.RecordLambdaCaptures(lambda);
    }

    static bool UnwrapDelegateLambda(IOperation value, out IAnonymousFunctionOperation lambda)
    {
        lambda = null;
        var dc = value as IDelegateCreationOperation
                 ?? (value as IConversionOperation)?.Operand as IDelegateCreationOperation;
        if (dc?.Target is IAnonymousFunctionOperation l) { lambda = l; return true; }
        return false;
    }

    // If the lambda body has a NON-tail invocation of the delegate variable it is assigned to, it is
    // self-recursive in a way that clobbers its flat-heap frame: record a recursion-cycle self-edge so
    // EmitCallToMethod spills. Tail self-calls are left unmarked (no spill) so deep tail recursion is safe.
    void MarkLambdaSelfRecursion(IAnonymousFunctionOperation lambda, ILocalSymbol selfVar, IMethodSymbol hoisted)
    {
        if (lambda.Body == null || !EmitContext.HasNonTailDelegateSelfCall(lambda.Body, selfVar)) return;
        var key = hoisted.OriginalDefinition;
        _ctx.RecursiveCallees ??= new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        if (!_ctx.RecursiveCallees.TryGetValue(key, out var set))
            _ctx.RecursiveCallees[key] = set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        set.Add(key);
    }
}
