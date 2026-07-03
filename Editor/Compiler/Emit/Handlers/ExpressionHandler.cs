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

    public CLeaf Handle(IOperation expression) => expression switch
    {
        ILiteralOperation op => VisitLiteral(op),
        // Stage 2 §4.1: a captured local/param has NO flat storage — reads route through the owning
        // scope's env record (aggregate captures keep clone-on-read value semantics on the way out).
        ILocalReferenceOperation localRef when _ctx.TryGetEnvBinding(localRef.Local, out _)
            => ResolveType(localRef.Type) is INamedTypeSymbol eaggT && EmitContext.IsAggregateType(eaggT)
                   ? EmitDeepCloneAggregate(EnvEmit.Read(_builder, _ctx, localRef.Local, "SystemObjectArray"), eaggT)
                   : EnvEmit.Read(_builder, _ctx, localRef.Local, GetUdonType(localRef.Type)),
        ILocalReferenceOperation localRef => _localBindings.TryGetValue(localRef.Local, out var localBinding)
                                                 ? ResolveType(localRef.Type) is INamedTypeSymbol laggT && EmitContext.IsAggregateType(laggT)
                                                     ? EmitDeepCloneAggregate(LoadField(localBinding.Id, "SystemObjectArray"), laggT)
                                                     : LoadField(localBinding.Id, GetUdonType(localRef.Type))
                                                 : throw new InvalidOperationException($"Cannot resolve local variable '{localRef.Local.Name}' in method '{_currentMethod?.Name ?? "(none)"}'."),
        IFieldReferenceOperation op => VisitFieldReference(op),
        IParameterReferenceOperation paramRef when _ctx.TryGetEnvBinding(paramRef.Parameter, out _)
            => ResolveType(paramRef.Type) is INamedTypeSymbol epaggT && EmitContext.IsAggregateType(epaggT)
                   ? EmitDeepCloneAggregate(EnvEmit.Read(_builder, _ctx, paramRef.Parameter, "SystemObjectArray"), epaggT)
                   : EnvEmit.Read(_builder, _ctx, paramRef.Parameter, GetUdonType(paramRef.Type)),
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

    CLeaf VisitLiteral(ILiteralOperation lit)
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

    CLeaf VisitFieldReference(IFieldReferenceOperation fieldRef)
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
                new List<CLeaf>(),
                fldType);
        }
        // Delegate field read as a value: a plain SystemObjectArray load of the bundle reference (the
        // single-var ABI, design §2.1/§2.3 — the this.field arm below handles it; IsAggregateType's
        // delegate armor guarantees no clone).

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
                    new List<CLeaf> { arrExpr, Const(elemIndex, "SystemInt32") }, "SystemObject");
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
            var instanceVal = VisitExpression(fieldRef.Instance);
            var nameConst = Const(fieldRef.Field.Name, "SystemString");
            return ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<CLeaf> { instanceVal, nameConst },
                "SystemObject");
        }
        // other.field → extern getter (same pattern as VisitPropertyReference)
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            var instanceVal = VisitExpression(fieldRef.Instance);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
                new List<CLeaf> { instanceVal },
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

    // Wave-12 r4 [W1]/[W2]: true when converting src to dst re-types a DELEGATE somewhere inside an
    // array (covariance: Func<string>[] → Func<object>[]) or a tuple ((Func<string>,int) →
    // (Func<object>,int)) with a diverging __dlgc_ sig part — the exact channel-divergence criterion
    // of the [V2] delegate-value arm, which never sees these because the conversion node sits on the
    // ARRAY/TUPLE type. Recurses through nested arrays and tuple elements. An `object`(/[])
    // destination element is NOT a delegate, so object-laundering stays the accepted boundary.
    static bool ContainsVariantDelegateConversion(ITypeSymbol src, ITypeSymbol dst,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        if (src == null || dst == null) return false;
        if (src is IArrayTypeSymbol srcArr && dst is IArrayTypeSymbol dstArr)
            return ContainsVariantDelegateConversion(srcArr.ElementType, dstArr.ElementType, typeParamMap);
        if (src is INamedTypeSymbol srcTup && dst is INamedTypeSymbol dstTup
            && srcTup.IsTupleType && dstTup.IsTupleType)
        {
            var se = srcTup.TupleElements;
            var de = dstTup.TupleElements;
            for (int i = 0; i < se.Length && i < de.Length; i++)
                if (ContainsVariantDelegateConversion(se[i].Type, de[i].Type, typeParamMap))
                    return true;
            return false;
        }
        return src is INamedTypeSymbol srcDlg && srcDlg.DelegateInvokeMethod is { } srcInvoke
            && dst is INamedTypeSymbol dstDlg && dstDlg.DelegateInvokeMethod is { } dstInvoke
            && !SymbolEqualityComparer.Default.Equals(srcDlg, dstDlg)
            && DelegateAbi.BuildSigPart(srcInvoke, typeParamMap)
               != DelegateAbi.BuildSigPart(dstInvoke, typeParamMap);
    }

    CLeaf VisitConversion(IConversionOperation conv)
    {
        var srcVal = VisitExpression(conv.Operand);

        // Wave-12 r4 [W1]/[W2]: variance laundered through array covariance or a tuple conversion
        // diverges the __dlgc_ channels exactly like the direct delegate-value conversion the [V2]
        // arm below rejects (VM-proven lost return: ref=2 vs -1 on both shapes). Same loud reject,
        // same criterion, recursing through the aggregate structure; equal-sig element conversions
        // and delegate↔object flows are untouched.
        if ((conv.Type is IArrayTypeSymbol || (conv.Type as INamedTypeSymbol)?.IsTupleType == true)
            && ContainsVariantDelegateConversion(conv.Operand.Type, conv.Type, _ctx.TypeParamMap))
            throw new System.NotSupportedException(
                $"Variant delegate conversion from '{conv.Operand.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                + $"to '{conv.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                + "the delegate calling convention keys its argument/return channels by the exact "
                + "signature, so a co/contravariant element binding silently drops values across the "
                + "dispatch. Use matching delegate type parameters.");

        // Delegate-typed conversions are reference passthrough (design §2.3, fcd25): delegate → object is
        // box-free (the value already IS an object[] reference) and (Func<T>)objExpr cast-back keeps the
        // same bundle reference. No Convert extern may ever be emitted for a delegate source or target.
        // Resolve BOTH ends through the type-param map first: inside a generic body Roslyn shows a `(T)o`
        // cast's type as the un-substituted ITypeParameterSymbol T, so a raw `conv.Type is INamedTypeSymbol`
        // check misses the monomorphized delegate destination entirely (VM-proven silent lost return).
        var convDstType = ResolveType(conv.Type);
        var convSrcType = ResolveType(conv.Operand.Type);
        if ((convDstType is INamedTypeSymbol dlgDst && dlgDst.DelegateInvokeMethod != null)
            || (convSrcType is INamedTypeSymbol dlgSrc && dlgSrc.DelegateInvokeMethod != null))
        {
            // Wave-12 r2 [V2]: a VARIANT delegate-VALUE conversion (Func<string> value flowing into a
            // Func<object>-typed field/local/param/return via C# co/contravariance) diverges the
            // __dlgc_ convention keys — the callee's bridge writes the channel keyed by its OWN
            // signature while the dispatch site reads the channel keyed by the receiving STATIC
            // delegate type, so arguments/returns are silently dropped (VM-proven: NRE / lost return).
            // The 'variant delegate bindings' policy tier already rejects variant METHOD-GROUP
            // creations (DelegateAbi.ValidateDelegateBinding); this closes the delegate-to-delegate
            // hole the same loud way. Equal sig parts (identity or Udon-type-identical conversions)
            // keep the reference passthrough — their channels agree. Also load-bearing for §5.4's
            // sig-filter soundness (tracked pin SigFilterCoupledToVarianceReject).
            if (convDstType is INamedTypeSymbol vDst && vDst.DelegateInvokeMethod is { } vDstInvoke
                && convSrcType is INamedTypeSymbol vSrc && vSrc.DelegateInvokeMethod is { } vSrcInvoke
                && !SymbolEqualityComparer.Default.Equals(vDst, vSrc)
                && DelegateAbi.BuildSigPart(vDstInvoke, _ctx.TypeParamMap)
                   != DelegateAbi.BuildSigPart(vSrcInvoke, _ctx.TypeParamMap))
                throw new System.NotSupportedException(
                    $"Variant delegate conversion from '{vSrc.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + $"to '{vDst.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                    + "the delegate calling convention keys its argument/return channels by the exact "
                    + "signature, so a co/contravariant binding silently drops values across the "
                    + "dispatch. Use matching delegate type parameters.");

            // A NON-delegate-typed operand (object / System.Delegate box) cast to a delegate type: the
            // __dlgc_ channels are keyed by the STATIC destination signature, but the boxed delegate's
            // RUNTIME signature is unknown, so a variant box silently drops values across the dispatch.
            // CONSERVATIVE + BOUNDED: allow only when the operand, after stripping conversions on THIS
            // expression, is DIRECTLY a delegate-typed value whose sig part equals the destination (the
            // trivially-safe box-and-unbox-same-type roundtrip); reject everything whose boxed delegate
            // we cannot see statically. This replaces the wave-12 r5-r9 producer-walking evidence check
            // — which tried to PROVE divergence by tracing every AST shape that can produce/launder a
            // boxed delegate (an unbounded whack-a-mole: 33 channels found across 4 rounds, never
            // saturating). Over-rejecting the rare cross-statement box roundtrip is acceptable (design
            // §8-3: loud over-rejection, never a silent wrong value); the fix is to keep the delegate
            // typed instead of routing it through object.
            if (convDstType is INamedTypeSymbol lDst && lDst.DelegateInvokeMethod is { } lInvoke
                && !(convSrcType is INamedTypeSymbol opDlg && opDlg.DelegateInvokeMethod != null))
            {
                var stripped = conv.Operand;
                while (stripped is IConversionOperation strippedConv) stripped = strippedConv.Operand;
                // A null / default operand carries no delegate and no signature — `(Func<...>)null`
                // dispatches through the invoke-time target-null guard (LogError+skip), never diverging
                // a channel. Safe passthrough.
                var isNull = stripped is IDefaultValueOperation
                    || (stripped?.ConstantValue.HasValue == true && stripped.ConstantValue.Value == null);
                // A same-signature delegate boxed and unboxed within THIS expression is the trivially
                // safe roundtrip — its channels agree (resolve through the type-param map so a generic
                // operand whose spec is a same-sig delegate still qualifies).
                var safeRoundtrip = ResolveType(stripped?.Type) is INamedTypeSymbol sDlg && sDlg.DelegateInvokeMethod is { } sInvoke
                    && DelegateAbi.BuildSigPart(sInvoke, _ctx.TypeParamMap)
                       == DelegateAbi.BuildSigPart(lInvoke, _ctx.TypeParamMap);
                if (!isNull && !safeRoundtrip)
                    throw new System.NotSupportedException(
                        $"Cast from '{(convSrcType ?? conv.Operand.Type)?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "object"}' "
                        + $"to delegate type '{lDst.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                        + "the delegate calling convention keys its argument/return channels by the exact "
                        + "signature, and a delegate boxed to a non-delegate type carries no statically "
                        + "visible signature, so a variant boxed delegate would silently drop values across "
                        + "the dispatch. Keep the value typed as its delegate type instead of routing it "
                        + "through object.");
            }
            return srcVal;
        }

        // Lifted numeric Nullable<T> conversion (e.g. char?→int? inserted by Roslyn around small-int nullable
        // arithmetic, or an explicit (int?)byteNullable). Both sides are Nullable<numeric>. The plain
        // identity passthrough below would feed a boxed small-int to a SystemInt32 extern → InvalidCast, so
        // materialize a null-preserving Convert.To{Dst}(object): null stays null, otherwise re-box the
        // converted underlying. To{Dst}(SystemObject) tolerates either storage tag (the source nullable may
        // hold a boxed small-int or, for un-narrowed literals, a boxed int).
        // Resolve the destination underlying: Roslyn can lower a small-int nullable narrowing as an inner
        // `int? -> byte` conversion (nullable SOURCE, BARE byte dest) wrapped by an outer byte->byte?. Accept a
        // bare numeric dest too, so the narrow+rebox below still runs — otherwise the boxed int falls through to
        // the identity passthrough and a later `.Value`'s strict ToInt32(SystemByte) InvalidCasts on the boxed int.
        var liftedDstU = EmitContext.IsNullableT(conv.Type, out var dstNblU) ? dstNblU : conv.Type;
        if (conv.Conversion.IsNullable
            && EmitContext.IsNullableT(conv.Operand.Type, out var liftedSrcU)
            && ExternResolver.IsNumericType(liftedSrcU) && liftedDstU != null && ExternResolver.IsNumericType(liftedDstU)
            && !SymbolEqualityComparer.Default.Equals(liftedSrcU, liftedDstU)
            && ExternResolver.GetConvertMethodName(liftedDstU) is { } liftedDstMethod)
        {
            var dstU = GetUdonType(liftedDstU);
            // srcVal is already a single-assignment SystemObject leaf (the boxed nullable) under ANF — re-read
            // directly for the HasValue test and the conversion. resSlot below is an accumulator (kept).
            var resSlot = _ctx.AllocTemp("SystemObject");
            EmitAssign(resSlot, Const(null, "SystemObject"));
            // C# integer narrowing is UNCHECKED (wrap); Convert.To{Small} is CHECKED and throws. For an
            // integer→integer lifted conversion, promote the boxed source to int64 (tolerates any boxed integer
            // tag, never overflows) and wrap/reinterpret via EmitNarrowingConvert. Float-involved conversions
            // keep the plain null-preserving Convert.
            // char is integral for narrowing (EmitNarrowingConvert wraps it like C#'s unchecked cast) but
            // ExternResolver.IsIntegerType excludes it; treat char as integral here so a lifted int?→char?
            // narrowing WRAPS instead of taking the CHECKED Convert.ToChar branch (which throws > 65535).
            bool liftedIntToInt =
                (ExternResolver.IsIntegerType(liftedSrcU) || liftedSrcU.SpecialType == SpecialType.System_Char)
                && (ExternResolver.IsIntegerType(liftedDstU) || liftedDstU.SpecialType == SpecialType.System_Char);
            _builder.EmitIf(EmitNullableHasValue(srcVal), _ =>
            {
                CValue converted = liftedIntToInt
                    ? EmitNarrowingConvert(
                        ExternCall("SystemConvert.__ToInt64__SystemObject__SystemInt64",
                            new List<CLeaf> { srcVal }, "SystemInt64"),
                        "SystemInt64", dstU)
                    : ExternCall($"SystemConvert.__{liftedDstMethod}__SystemObject__{dstU}",
                        new List<CLeaf> { srcVal }, dstU);
                EmitAssign(resSlot, converted);
            });
            return SlotRef(resSlot);
        }

        // Lifted numeric conversion with a BARE source and a Nullable<numeric> dest (e.g. `(byte?)(intExpr)`):
        // the value is always present, so narrow numerically (C#-unchecked wrap) and let it box into the
        // nullable's SystemObject slot with the right tag, so a later `.Value`'s strict small-int extern reads it.
        if (conv.Conversion.IsNullable
            && conv.Operand.Type != null && ExternResolver.IsNumericType(conv.Operand.Type)
            && EmitContext.IsNullableT(conv.Type, out var bareDstU) && ExternResolver.IsNumericType(bareDstU)
            && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, bareDstU))
        {
            return EmitNarrowingConvert(srcVal, GetUdonType(conv.Operand.Type), GetUdonType(bareDstU));
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
                            new List<CLeaf> { srcVal },
                            "SystemDouble");
                    }

                    // Math.Truncate(double) or Math.Truncate(decimal)
                    srcVal = ExternCall(
                        $"SystemMath.__Truncate__{truncType}__{truncType}",
                        new List<CLeaf> { srcVal },
                        truncType);

                    // Convert truncated value → target integer type
                    var dstType = GetUdonType(conv.Type);
                    return ExternCall(
                        $"SystemConvert.__{methodName}__{truncType}__{dstType}",
                        new List<CLeaf> { srcVal },
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
            // A user STRUCT conversion operator is an emitted method, not an extern: route to it (its containing
            // type is SystemObjectArray-backed, so ResolveConversionExtern would build a non-existent extern).
            if (conv.OperatorMethod.ContainingType is INamedTypeSymbol convOpCt && EmitContext.IsUserStruct(convOpCt)
                && _methodFunctions.ContainsKey(conv.OperatorMethod.OriginalDefinition))
                return EmitCallToMethod(conv.OperatorMethod.OriginalDefinition, new List<CLeaf> { srcVal });

            var dstType = GetUdonType(conv.Type);
            return ExternCall(
                ExternResolver.ResolveConversionExtern(
                    conv.OperatorMethod, ResolveType(conv.Operand.Type), ResolveType(conv.Type)),
                new List<CLeaf> { srcVal },
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

            // Enum ↔ underlying is a pure re-typing between each side's effective underlying udon type (an enum
            // is STORED as its underlying type — see ExternResolver.GetUdonTypeName, so dstType for an enum
            // target is already its underlying). The former int→enum path indexed a per-enum lookup array, but
            // enumArr[v - min] == v — an identity over the underlying value — so it added nothing except a VM
            // fault on out-of-range casts ((E)999 is legal C# and must round-trip). A direct convert is correct
            // for every value: in-range, out-of-range, and byte/short/unsigned wrap. A same-width pair (int-
            // backed enum ↔ int) re-types through a scratch slot; a different-width pair (byte/short-backed enum
            // ↔ int, any enum ↔ long) needs a real numeric conversion (a bare COPY into a wider slot would store
            // e.g. a SystemByte into a SystemInt32 variable and fail verification).
            var srcUnderlying = conv.Operand.Type is INamedTypeSymbol srcEnum && srcEnum.TypeKind == TypeKind.Enum
                ? GetUdonType(srcEnum.EnumUnderlyingType)
                : GetUdonType(conv.Operand.Type);
            if (srcUnderlying != dstType)
                return EmitNarrowingConvert(srcVal, srcUnderlying, dstType);
            var tmpSlot = _ctx.AllocTemp(dstType);
            EmitAssign(tmpSlot, srcVal);
            return SlotRef(tmpSlot);
        }

        // Identity conversion: pass through
        return srcVal;
    }

    // ── Default Value ──

    CLeaf VisitDefaultValue(IDefaultValueOperation defaultVal)
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

    CLeaf VisitDeclarationExpression(IDeclarationExpressionOperation declExpr)
    {
        if (declExpr.Expression is not ILocalReferenceOperation localRef2)
            return VisitExpression(declExpr.Expression);

        var udonType = GetUdonType(localRef2.Type);
        // Stage 2 §4.1: a CAPTURED out-var/pattern declaration still needs an ADDRESSABLE heap slot
        // for the writer (env cells have no address) — declare a flat staging field as before, and
        // the consumer that populated it must sync it into the env cell (out-arg copy-back /
        // pattern-binding stores go through AssignToLValue / TryEmitEnvStore arms). Registering the
        // staging field in _localBindings is WRONG for captured symbols (reads would bypass the env),
        // so captured declarations get a staging slot WITHOUT a binding.
        if (_ctx.TryGetEnvBinding(localRef2.Local, out _))
        {
            var stagingId = _ctx.DeclareLocal(localRef2.Local.Name, udonType);
            return LoadField(stagingId, udonType);
        }
        var localId = _ctx.DeclareLocal(localRef2.Local.Name, udonType);
        _localBindings[localRef2.Local] = new EmitContext.LocalBinding(localId);
        return LoadField(localId, udonType);
    }

    // ── Delegate Creation ──

    // The ONLY producer of delegate values (design §2.2): builds the runtime object[4] bundle
    // { [0]=target, [1]=bridge export name, [2]=boxed-UInt32 funcaddr, [3]=env (Stage-1 null) }.
    // ResolveDelegateBridge hoists lambdas/local functions and registers their __dlg_ bridge via
    // PendingDelegateBridges (bundle[1] is the cross-path entry, so the bridge is always emitted).
    // Capture-escape registration is pre-emit analysis (§4.1) — nothing is marked here.
    CLeaf VisitDelegateCreation(IDelegateCreationOperation op)
    {
        var (bridgeName, funcRef, thirdParty, envLeaf) = ResolveDelegateBridge(op);
        DelegateAbi.ValidateDelegateBinding(op.Type as INamedTypeSymbol,
            (op.Target as IMethodReferenceOperation)?.Method, _ctx.TypeParamMap);

        var bundle = ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CLeaf> { Const(DelegateAbi.BundleSize, "SystemInt32") }, "SystemObjectArray");

        var thisType = GetUdonType(_classSymbol);
        var target = thirdParty ?? LoadField(_ctx.DeclareThisOnce(thisType), thisType);
        // Addr discipline (§1.3): the only sources for bundle[2] are the back-patched funcaddr const
        // (boxed UInt32) or Const(0u). A third-party method group's local funcaddr is meaningless in the
        // target program, so it carries 0u; a same-this target carries the REAL funcaddr — even when the
        // bundle is later handed cross-Behaviour (the invoke-side target-identity guard is the only gate).
        var addr = thirdParty != null ? (CLeaf)Const(0u, "SystemUInt32") : funcRef;

        EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            new List<CLeaf> { bundle, Const(DelegateAbi.Target, "SystemInt32"), target });
        EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            new List<CLeaf> { bundle, Const(DelegateAbi.Method, "SystemInt32"), Const(bridgeName, "SystemString") });
        EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            new List<CLeaf> { bundle, Const(DelegateAbi.Addr, "SystemInt32"), addr });
        // Stage 2 §3.7: bundle[3] carries the binding-scope env for a CAPTURING closure target, else
        // a null const (capture-free lambda / named method / base.M) = byte-identical to Stage 1.
        EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
            new List<CLeaf> { bundle, Const(DelegateAbi.Env, "SystemInt32"), envLeaf });

        return bundle;
    }

    // ── Tuple Literal ──

    CLeaf VisitTupleLiteral(ITupleOperation op)
    {
        // Create object[] and set each element
        var count = op.Elements.Length;
        var arrExpr = ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
            new List<CLeaf> { Const(count, "SystemInt32") }, "SystemObjectArray");

        for (int i = 0; i < count; i++)
        {
            var elemVal = VisitExpression(op.Elements[i]);
            EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                new List<CLeaf> { arrExpr, Const(i, "SystemInt32"), elemVal });
        }

        return arrExpr;
    }

}
