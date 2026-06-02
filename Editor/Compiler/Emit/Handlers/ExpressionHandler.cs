using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ExpressionHandler : HandlerBase, IExpressionHandler
{
    public ExpressionHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is ILiteralOperation
            or ILocalReferenceOperation
            or IFieldReferenceOperation
            or IParameterReferenceOperation
            or IInstanceReferenceOperation
            or IConversionOperation
            or IDefaultValueOperation
            or ITypeOfOperation
            or INameOfOperation
            or IDeclarationExpressionOperation
            or IDiscardOperation
            or IDelegateCreationOperation
            or ITupleOperation;

    public CValue Handle(IOperation expression) => expression switch
    {
        ILiteralOperation op => VisitLiteral(op),
        ILocalReferenceOperation localRef => _localBindings.TryGetValue(localRef.Local, out var localBinding)
                                                 ? ResolveType(localRef.Type) is INamedTypeSymbol laggT && EmitContext.IsAggregateType(laggT)
                                                     ? EmitDeepCloneAggregate(LoadField(localBinding.Id, "SystemObjectArray"), laggT)
                                                     : LoadField(localBinding.Id, GetUdonType(localRef.Type))
                                                 : throw new InvalidOperationException($"Cannot resolve local variable '{localRef.Local.Name}' in method '{_currentMethod?.Name ?? "(none)"}'."),
        IFieldReferenceOperation op => VisitFieldReference(op),
        IParameterReferenceOperation paramRef => ResolveType(paramRef.Type) is INamedTypeSymbol paggT && EmitContext.IsAggregateType(paggT)
                                                     ? EmitDeepCloneAggregate(LoadParam(paramRef.Parameter), paggT)
                                                     : LoadParam(paramRef.Parameter),
        IInstanceReferenceOperation when _ctx.CurrentStructReceiverParamId != null
            => LoadField(_ctx.CurrentStructReceiverParamId, "SystemObjectArray"),
        IInstanceReferenceOperation => LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol)),
        IConversionOperation op => VisitConversion(op),
        IDefaultValueOperation op => VisitDefaultValue(op),
        ITypeOfOperation typeOf => Const(GetUdonType(typeOf.TypeOperand), "SystemType"),
        INameOfOperation nameOf => Const(nameOf.ConstantValue.Value.ToString(), "SystemString"),
        IDeclarationExpressionOperation op => VisitDeclarationExpression(op),
        IDiscardOperation discard => SlotRef(_ctx.AllocTemp(GetUdonType(discard.Type))),
        IDelegateCreationOperation op => VisitDelegateCreation(op),
        ITupleOperation op => VisitTupleLiteral(op),
        _ => throw new NotSupportedException(expression.GetType().Name),
    };

    // ── Literal ──

    CValue VisitLiteral(ILiteralOperation lit)
    {
        // null literal has no type
        if (lit.Type == null)
            return Const(null, "SystemObject");
        var udonType = GetUdonType(lit.Type);
        if (!lit.ConstantValue.HasValue)
            return Const(null, udonType);
        var value = lit.ConstantValue.Value;
        return Const(value, udonType);
    }

    // ── Field Reference ──

    CValue VisitFieldReference(IFieldReferenceOperation fieldRef)
    {
        // const fields (HasConstantValue) and static readonly with compile-time constant values
        if (fieldRef.Field.HasConstantValue)
        {
            var constType = GetUdonType(fieldRef.Field.Type);
            var constVal = fieldRef.Field.ConstantValue;
            return Const(constVal, constType);
        }
        // static readonly with constant value at operation level (Roslyn may fold these)
        // static readonly field with a compile-time-constant initializer → fold to the value. A `static
        // readonly` field has no ConstantValue of its own (only `const` does), so evaluate the initializer
        // expression. Each program gets its own copy, which is observationally identical to a true shared
        // static because the value is immutable — so no singleton/shared storage is needed.
        if (fieldRef.Field.IsStatic && fieldRef.Field.IsReadOnly
            && (fieldRef.ConstantValue.HasValue || TryGetConstInitializer(fieldRef.Field, out _)))
        {
            var constType = GetUdonType(fieldRef.Field.Type);
            var value = fieldRef.ConstantValue.HasValue ? fieldRef.ConstantValue.Value
                : (TryGetConstInitializer(fieldRef.Field, out var v) ? v : null);
            return Const(value, constType);
        }
        if (fieldRef.Field.IsStatic)
        {
            // UdonSharpBehaviour static field → compile error (Udon VM has no shared static storage)
            if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
            {
                var qualifier = fieldRef.Field.IsReadOnly ? "'static readonly'" : "Static";
                _diagnostics.Add(new EmitDiagnostic
                {
                    Severity = "Error",
                    Message = $"{qualifier} field '{fieldRef.Field.Name}' is not supported on UdonSharpBehaviour types. " +
                        "Udon VM has no static variable support. Use 'const' for compile-time constants or convert to an instance field."
                });
                throw new NotSupportedException("Static fields are not supported on UdonSharpBehaviour types. " + $"Use 'const' for compile-time constants or convert '{fieldRef.Field.Name}' to an instance field.");
            }
            // Unity/System static field → extern getter
            var fldType = GetUdonType(fieldRef.Field.Type);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
                new List<CValue>(),
                fldType);
        }
        // Delegate field read as value is not supported — the original field has been expanded
        // to a DelegateBundle (Target/Method/Addr). Only invocation, null-check, ?.Invoke(), and
        // comparison are supported (handled by their respective handlers before reaching here).
        if (fieldRef.Instance is IInstanceReferenceOperation
            && fieldRef.Field.Type is INamedTypeSymbol dlgType
            && dlgType.DelegateInvokeMethod != null
            && _delegateFields.Contains(fieldRef.Field.Name))
        {
            throw new NotSupportedException(
                $"Delegate field '{fieldRef.Field.Name}' cannot be used as a value (e.g., in variable assignment, parameter passing, or return). " +
                "Only direct invocation (_callback()), null check (_callback != null), ?.Invoke(), and comparison (_a == _b) are supported.");
        }

        // Aggregate field access: result.Item1, point.x, pair.Item1 → object[] indexing
        // Triggered by the containing type being aggregate, regardless of instance kind
        if (fieldRef.Instance != null
            && fieldRef.Instance.Type is INamedTypeSymbol aggContaining
            && EmitContext.IsAggregateType(aggContaining))
        {
            var layout = _ctx.GetAggregateLayout(aggContaining);
            if (layout.TryGetIndex(fieldRef.Field, out var elemIndex))
            {
                var arrExpr = LoadInstanceRaw(fieldRef.Instance);
                var getVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<CValue> { arrExpr, Const(elemIndex, "SystemInt32") }, "SystemObject");
                // A struct-typed element read AS A VALUE is copied (value semantics); scalar elements are immutable boxes.
                return fieldRef.Field.Type is INamedTypeSymbol elemAgg && EmitContext.IsAggregateType(elemAgg)
                    ? EmitDeepCloneAggregate(getVal, elemAgg) : getVal;
            }
            throw new System.NotSupportedException(
                $"Cannot access '{fieldRef.Field.Name}' on aggregate type '{aggContaining.Name}'.");
        }

        // this.field → direct variable name → LoadField (struct-typed field copied on value read)
        if (fieldRef.Instance is IInstanceReferenceOperation)
            return fieldRef.Field.Type is INamedTypeSymbol thisFieldAgg && EmitContext.IsAggregateType(thisFieldAgg)
                ? EmitDeepCloneAggregate(LoadField(fieldRef.Field.Name, "SystemObjectArray"), thisFieldAgg)
                : LoadField(fieldRef.Field.Name, GetUdonType(fieldRef.Field.Type));
        // cross-behaviour field → GetProgramVariable
        if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var instanceVal = VisitExpression(fieldRef.Instance);
            var nameConst = Const(fieldRef.Field.Name, "SystemString");
            return ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<CValue> { instanceVal, nameConst },
                "SystemObject");
        }
        // other.field → extern getter (same pattern as VisitPropertyReference)
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            var instanceVal = VisitExpression(fieldRef.Instance);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
                new List<CValue> { instanceVal },
                fldType);
        }
    }

    // ── Conversion ──

    // Evaluate a field's initializer to a compile-time constant (primitives/enums). Used to fold a
    // `static readonly` field, whose own ConstantValue is unset, when its initializer is constant.
    bool TryGetConstInitializer(IFieldSymbol field, out object value)
    {
        value = null;
        var refs = field.DeclaringSyntaxReferences;
        if (refs.Length > 0 && refs[0].GetSyntax()
            is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax { Initializer: { } init })
        {
            var cv = _compilation.GetSemanticModel(init.SyntaxTree).GetConstantValue(init.Value);
            if (cv.HasValue) { value = cv.Value; return true; }
        }
        return false;
    }

    CValue VisitConversion(IConversionOperation conv)
    {
        var srcVal = VisitExpression(conv.Operand);

        // Lifted numeric Nullable<T> conversion (e.g. char?→int? inserted by Roslyn around small-int nullable
        // arithmetic, or an explicit (int?)byteNullable). Both sides are Nullable<numeric>. The plain
        // identity passthrough below would feed a boxed small-int to a SystemInt32 extern → InvalidCast, so
        // materialize a null-preserving Convert.To{Dst}(object): null stays null, otherwise re-box the
        // converted underlying. To{Dst}(SystemObject) tolerates either storage tag (the source nullable may
        // hold a boxed small-int or, for un-narrowed literals, a boxed int).
        if (EmitContext.IsNullableT(conv.Operand.Type, out var liftedSrcU)
            && EmitContext.IsNullableT(conv.Type, out var liftedDstU)
            && ExternResolver.IsNumericType(liftedSrcU) && ExternResolver.IsNumericType(liftedDstU)
            && !SymbolEqualityComparer.Default.Equals(liftedSrcU, liftedDstU)
            && ExternResolver.GetConvertMethodName(liftedDstU) is { } liftedDstMethod)
        {
            var dstU = GetUdonType(liftedDstU);
            var srcSlot = _ctx.AllocTemp("SystemObject");
            EmitAssign(srcSlot, srcVal);
            var resSlot = _ctx.AllocTemp("SystemObject");
            EmitAssign(resSlot, Const(null, "SystemObject"));
            _builder.EmitIf(EmitNullableHasValue(SlotRef(srcSlot)), _ =>
                EmitAssign(resSlot, ExternCall($"SystemConvert.__{liftedDstMethod}__SystemObject__{dstU}",
                    new List<CValue> { SlotRef(srcSlot) }, dstU)));
            return SlotRef(resSlot);
        }

        // Numeric conversions (int→float, etc.) via System.Convert
        if (conv.Operand.Type != null && conv.Type != null
            && ExternResolver.IsNumericType(conv.Operand.Type)
            && ExternResolver.IsNumericType(conv.Type)
            && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            var methodName = ExternResolver.GetConvertMethodName(conv.Type);
            if (methodName != null)
            {
                // C# truncates float→int; SystemConvert rounds. Insert Math.Truncate first.
                if (ExternResolver.IsFloatType(conv.Operand.Type) && ExternResolver.IsIntegerType(conv.Type))
                {
                    var isDecimal = conv.Operand.Type.SpecialType == SpecialType.System_Decimal;
                    var truncType = isDecimal ? "SystemDecimal" : "SystemDouble";

                    if (!isDecimal && conv.Operand.Type.SpecialType == SpecialType.System_Single)
                    {
                        // float → double promotion
                        srcVal = ExternCall(
                            "SystemConvert.__ToDouble__SystemSingle__SystemDouble",
                            new List<CValue> { srcVal },
                            "SystemDouble");
                    }

                    // Math.Truncate(double) or Math.Truncate(decimal)
                    srcVal = ExternCall(
                        $"SystemMath.__Truncate__{truncType}__{truncType}",
                        new List<CValue> { srcVal },
                        truncType);

                    // Convert truncated value → target integer type
                    var dstType = GetUdonType(conv.Type);
                    return ExternCall(
                        $"SystemConvert.__{methodName}__{truncType}__{dstType}",
                        new List<CValue> { srcVal },
                        dstType);
                }

                // Non-truncation numeric conversions. Integer→small-int narrowing uses C#-unchecked
                // wrap (EmitNarrowingConvert); widening/other falls back to the plain convert extern.
                var srcType = GetUdonType(conv.Operand.Type);
                var dstType2 = GetUdonType(conv.Type);
                return EmitNarrowingConvert(srcVal, srcType, dstType2);
            }
        }

        // User-defined implicit/explicit conversions (e.g. Vector2→Vector3)
        if (conv.OperatorMethod != null && conv.Operand.Type != null && conv.Type != null && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            var dstType = GetUdonType(conv.Type);
            return ExternCall(
                ExternResolver.ResolveConversionExtern(
                    conv.OperatorMethod, ResolveType(conv.Operand.Type), ResolveType(conv.Type)),
                new List<CValue> { srcVal },
                dstType);
        }

        // Enum ↔ underlying type conversions (int→enum, enum→int)
        if (conv.Operand.Type != null && conv.Type != null
                                      && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type)
                                      && (conv.Operand.Type.TypeKind == TypeKind.Enum || conv.Type.TypeKind == TypeKind.Enum))
        {
            var dstType = GetUdonType(conv.Type);
            // Prefer const: avoids COPY type-tag corruption
            var constVal = conv.ConstantValue.HasValue ? conv.ConstantValue
                         : conv.Operand.ConstantValue.HasValue ? conv.Operand.ConstantValue
                         : default;
            if (constVal.HasValue)
                return Const(constVal.Value, dstType);

            // Runtime int→enum: use object[] array lookup to preserve type tags
            if (conv.Type.TypeKind == TypeKind.Enum && conv.Type is INamedTypeSymbol enumTarget)
            {
                var info = _ctx.GetOrCreateEnumArray(enumTarget);
                var indexVal = info.MinOffset == 0
                    ? srcVal
                    : ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
                        new List<CValue> { srcVal, Const((int)info.MinOffset, "SystemInt32") }, "SystemInt32");
                return ExternCall(
                    "SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<CValue> { LoadField(info.ArrayId, "SystemObjectArray"), indexVal },
                    "SystemObject");
            }

            // enum→int: store/load through a scratch slot to re-type
            var tmpSlot = _ctx.AllocTemp(dstType);
            EmitAssign(tmpSlot, srcVal);
            return SlotRef(tmpSlot);
        }

        // Identity conversion: pass through
        return srcVal;
    }

    // ── Default Value ──

    CValue VisitDefaultValue(IDefaultValueOperation defaultVal)
    {
        // Aggregate default → a ZERO-INITIALIZED object[] (fields set to their defaults), not null, so field
        // access on the default does not NRE. ResolveType is required for `default(T)` inside a generic method
        // where T is a struct type arg: defaultVal.Type is then the open type parameter, which a directly-named
        // INamedTypeSymbol check would miss — leaving the default as null and crashing on the first field read.
        if (ResolveType(defaultVal.Type) is INamedTypeSymbol aggDef && EmitContext.IsAggregateType(aggDef))
            return EmitNewAggregate(aggDef);

        var dvType = GetUdonType(defaultVal.Type);
        if (!defaultVal.Type.IsValueType)
            return Const(null, dvType);

        var defVal = defaultVal.Type.SpecialType switch
        {
            SpecialType.System_Boolean => (object)false,
            SpecialType.System_Int32 => (object)0,
            SpecialType.System_Byte => (object)(byte)0,
            SpecialType.System_SByte => (object)(sbyte)0,
            SpecialType.System_Int16 => (object)(short)0,
            SpecialType.System_UInt16 => (object)(ushort)0,
            SpecialType.System_UInt32 => (object)0u,
            SpecialType.System_Int64 => (object)0L,
            SpecialType.System_UInt64 => (object)0UL,
            SpecialType.System_Single => (object)0f,
            SpecialType.System_Double => (object)0d,
            SpecialType.System_Char => (object)'\0',
            _ => null, // struct types (Vector3, etc.) — assembler uses default
        };
        return Const(defVal, dvType);
    }

    // ── Declaration Expression ──

    CValue VisitDeclarationExpression(IDeclarationExpressionOperation declExpr)
    {
        if (declExpr.Expression is not ILocalReferenceOperation localRef2)
            return VisitExpression(declExpr.Expression);

        var udonType = GetUdonType(localRef2.Type);
        var localId = _ctx.DeclareLocal(localRef2.Local.Name, udonType);
        _localBindings[localRef2.Local] = new EmitContext.LocalBinding(localId);
        return LoadField(localId, udonType);
    }

    // ── Delegate Creation ──

    CValue VisitDelegateCreation(IDelegateCreationOperation op)
    {
        switch (op.Target)
        {
            case IAnonymousFunctionOperation lambda:
            {
                var hoisted = HoistLambdaToMethod(lambda);
                return FuncRef(_methodFunctions[hoisted].Name);
            }
            case IMethodReferenceOperation methodRef
                when _methodFunctions.TryGetValue(methodRef.Method, out var func):
                return FuncRef(func.Name);
            default:
                throw new NotSupportedException($"Unsupported delegate target: {op.Target.GetType().Name}");
        }
    }

    // ── Tuple Literal ──

    CValue VisitTupleLiteral(ITupleOperation op)
    {
        // Create object[] and set each element
        var count = op.Elements.Length;
        var arrExpr = ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CValue> { Const(count, "SystemInt32") }, "SystemObjectArray");
        var tmpSlot = _ctx.AllocTemp("SystemObjectArray");
        EmitAssign(tmpSlot, arrExpr);

        for (int i = 0; i < count; i++)
        {
            var elemVal = VisitExpression(op.Elements[i]);
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CValue> { SlotRef(tmpSlot), Const(i, "SystemInt32"), elemVal });
        }

        return SlotRef(tmpSlot);
    }

}
