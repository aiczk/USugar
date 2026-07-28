using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

internal sealed class ExpressionHandler
{
    readonly LoweringServices _lowering;
    public ExpressionHandler(LoweringServices lowering) => _lowering = lowering;

    public CLeaf Handle(IOperation expression) => expression switch
    {
        ILiteralOperation op => VisitLiteral(op),
        // Stage 2 §4.1: a captured local/param has NO flat storage — reads route through the owning
        // scope's env record (aggregate captures keep clone-on-read value semantics on the way out).
        ILocalReferenceOperation localRef when _lowering.State.TryGetEnvBinding(localRef.Local, out _)
            => _lowering.ResolveType(localRef.Type) is INamedTypeSymbol eaggT && _lowering.IsAggregateValue(eaggT)
                   ? AggregateAbi.DeepClone(_lowering.Builder, EnvEmit.Read(_lowering.Builder, _lowering.State, localRef.Local, new StorageType(AggregateAbi.ArrayType)),
                       eaggT, _lowering.State.Aggregates.GetLayout)
                   : EnvEmit.Read(_lowering.Builder, _lowering.State, localRef.Local, _lowering.GetStorageType(localRef.Type)),
        ILocalReferenceOperation localRef => _lowering.LocalBindings.TryGetValue(localRef.Local, out var localBinding)
                                                 ? _lowering.ResolveType(localRef.Type) is INamedTypeSymbol laggT && _lowering.IsAggregateValue(laggT)
                                                     ? AggregateAbi.DeepClone(_lowering.Builder, _lowering.LoadField(localBinding.Id, new StorageType(AggregateAbi.ArrayType)),
                                                         laggT, _lowering.State.Aggregates.GetLayout)
                                                     : _lowering.LoadField(localBinding.Id, _lowering.GetStorageType(localRef.Type))
                                                 : throw new InvalidOperationException($"Cannot resolve local variable '{localRef.Local.Name}' in method '{_lowering.CurrentMethod?.Name ?? "(none)"}'."),
        IFieldReferenceOperation op => VisitFieldReference(op),
        IEventReferenceOperation op => VisitEventReference(op),
        IParameterReferenceOperation paramRef when _lowering.State.TryGetEnvBinding(paramRef.Parameter, out _)
            => _lowering.ResolveType(paramRef.Type) is INamedTypeSymbol epaggT && _lowering.IsAggregateValue(epaggT)
                   ? AggregateAbi.DeepClone(_lowering.Builder, EnvEmit.Read(_lowering.Builder, _lowering.State, paramRef.Parameter, new StorageType(AggregateAbi.ArrayType)),
                       epaggT, _lowering.State.Aggregates.GetLayout)
                   : EnvEmit.Read(_lowering.Builder, _lowering.State, paramRef.Parameter, _lowering.GetStorageType(paramRef.Type)),
        IParameterReferenceOperation paramRef => _lowering.ResolveType(paramRef.Type) is INamedTypeSymbol paggT && _lowering.IsAggregateValue(paggT)
                                                     ? AggregateAbi.DeepClone(_lowering.Builder, _lowering.LoadParam(paramRef.Parameter),
                                                         paggT, _lowering.State.Aggregates.GetLayout)
                                                     : _lowering.LoadParam(paramRef.Parameter),
        // CW24 (closed-world audit): `this` read AS A VALUE (`return this` / `var c = this` / `M(this)`)
        // clones like every sibling value-read arm above — the receiver bundle is the caller's LIVE
        // storage (EmitStructInstanceCall passes it raw). A v1 CLASS receiver shares the same param0
        // convention but stays raw (CA-M1 reference semantics); receiver-position `this` never gets
        // here (LoadInstanceRaw has its own IInstanceReference arm).
        IInstanceReferenceOperation when _lowering.State.Methods.CurrentStructReceiverParamId is { } recvPid
            => _lowering.State.Methods.CurrentMethod?.ContainingType is INamedTypeSymbol thisStructT && _lowering.IsUserStruct(thisStructT)
                   ? AggregateAbi.DeepClone(_lowering.Builder, _lowering.LoadField(recvPid, new StorageType(AggregateAbi.ArrayType)),
                       thisStructT, _lowering.State.Aggregates.GetLayout)
                   : _lowering.LoadField(recvPid, new StorageType(AggregateAbi.ArrayType)),
        // Class receiver capture (design 2026-07-10 v2 §1.4, the SINGLE new resolution arm): inside
        // a hoisted closure hosted by a v1-class member, `this` is the receiver bundle in the env
        // chain (synthetic capture keyed by the member's OriginalDefinition). Every access shape —
        // field read/write/compound, instance call, property, indexer — funnels here through
        // LoadInstanceRaw's fallthrough, so no second arm exists anywhere.
        IInstanceReferenceOperation when LambdaCaptureAnalyzer.ReceiverCaptureKey(_lowering.State.Methods.CurrentMethod) is { } rcvKey
                                         && _lowering.State.TryGetEnvBinding(rcvKey, out _)
            => EnvEmit.Read(_lowering.Builder, _lowering.State, rcvKey, new StorageType(AggregateAbi.ArrayType)),
        IInstanceReferenceOperation => _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(_lowering.GetStorageType(_lowering.ClassSymbol)), _lowering.GetStorageType(_lowering.ClassSymbol)),
        IConversionOperation op => VisitConversion(op),
        IDefaultValueOperation op => VisitDefaultValue(op),
        ITypeOfOperation typeOf => EmitTypeofToken(typeOf),
        ISizeOfOperation sizeOf => EmitSizeOf(sizeOf),
        INameOfOperation nameOf => _lowering.Const(nameOf.ConstantValue.Value.ToString(), StorageTypes.String),
        IDeclarationExpressionOperation op => VisitDeclarationExpression(op),
        IDiscardOperation discard => _lowering.SlotRef(_lowering.State.Builder.AllocScratch(_lowering.GetStorageType(discard.Type))),
        IDelegateCreationOperation op => VisitDelegateCreation(op),
        ITupleOperation op => VisitTupleLiteral(op),
        _ => throw new NotSupportedException(expression.GetType().Name),
    };

    CLeaf EmitSizeOf(ISizeOfOperation sizeOf)
    {
        if (sizeOf.ConstantValue is { HasValue: true, Value: int bytes })
            return _lowering.Const(bytes, StorageTypes.Int32);
        throw new NotSupportedException(
            $"sizeof({sizeOf.TypeOperand.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}) "
            + "is not a compile-time constant supported by USugar. Only C# built-in unmanaged types "
            + "with a language-defined size are supported.");
    }

    // ── Literal ──

    CLeaf VisitLiteral(ILiteralOperation lit)
    {
        // null literal has no type
        if (lit.Type == null)
            return _lowering.Const(null, StorageTypes.Object);
        var udonType = _lowering.GetStorageTypeName(lit.Type);
        if (!lit.ConstantValue.HasValue)
            return _lowering.Const(null, new StorageType(udonType));
        var value = lit.ConstantValue.Value;
        return _lowering.Const(value, new StorageType(udonType));
    }

    // ── Field Reference ──

    CLeaf VisitFieldReference(IFieldReferenceOperation fieldRef)
    {
        // const fields (HasConstantValue) and static readonly with compile-time constant values
        if (fieldRef.Field.HasConstantValue)
        {
            var constType = _lowering.GetStorageTypeName(fieldRef.Field.Type);
            var constVal = fieldRef.Field.ConstantValue;
            return _lowering.Const(constVal, new StorageType(constType));
        }
        // static readonly with constant value at operation level (Roslyn may fold these)
        // static readonly field with a compile-time-constant initializer → fold to the value. A `static
        // readonly` field has no ConstantValue of its own (only `const` does), so evaluate the initializer
        // expression. Each program gets its own copy, which is observationally identical to a true shared
        // static because the value is immutable — so no singleton/shared storage is needed.
        if (fieldRef.Field.IsStatic && fieldRef.Field.IsReadOnly
            && (fieldRef.ConstantValue.HasValue
                || _lowering.State.Program.Constants.TryGet(
                    fieldRef.Field, out _)))
        {
            var constType = _lowering.GetStorageTypeName(fieldRef.Field.Type);
            var value = fieldRef.ConstantValue.HasValue ? fieldRef.ConstantValue.Value
                : (_lowering.State.Program.Constants.TryGet(
                    fieldRef.Field, out var v) ? v : null);
            return _lowering.Const(value, new StorageType(constType));
        }
        if (fieldRef.Field.IsStatic)
        {
            if (fieldRef.Field.DeclaringSyntaxReferences.Length > 0
                && !USugarCompilerHelper.IsFrameworkNamespace(
                    fieldRef.Field.ContainingNamespace))
                throw ClassAbiPolicy.UnsupportedStaticStorage(
                    fieldRef.Field);
            if (fieldRef.Field.ContainingType is INamedTypeSymbol structFieldCt && _lowering.IsUserStruct(structFieldCt))
                throw ClassAbiPolicy.UnsupportedStaticStorage(
                    fieldRef.Field);
            if (_lowering.IsUserClass(
                    fieldRef.Field.ContainingType))
                throw ClassAbiPolicy.UnsupportedStaticStorage(
                    fieldRef.Field);
            if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
                throw ClassAbiPolicy.UnsupportedStaticStorage(
                    fieldRef.Field);
            // Unity/System static field → extern getter
            var fldType = _lowering.GetStorageTypeName(fieldRef.Field.Type);
            var containingType = _lowering.GetStorageTypeName(fieldRef.Field.ContainingType);
            return _lowering.ExternCall(
                _lowering.RequireBoundAbi(
                    fieldRef, BoundAbiRole.FieldGet),
                new List<CLeaf>(),
                new StorageType(fldType));
        }
        // Delegate field read as a value: a plain SystemObjectArray load of the bundle reference (the
        // single-var ABI, design §2.1/§2.3 — the this.field arm below handles it; IsAggregateValue's
        // delegate armor guarantees no clone).

        // Aggregate / v1-class field access: result.Item1, point.x, node.Val → object[] slot indexing.
        // Triggered by the containing type being object[]-emulated, regardless of instance kind (the clone at
        // the element read below stays IsAggregateValue so a class-typed element is returned by reference).
        // ResolveType: a receiver typed as a type parameter (T c = new T(); c.v) resolves to the
        // concrete aggregate through the monomorphization map (new T() member-access, 2026-07-11).
        if (fieldRef.Instance != null
            && _lowering.ResolveType(fieldRef.Instance.Type) is INamedTypeSymbol aggContaining
            && _lowering.IsObjectArrayEmulated(aggContaining))
        {
            var layout = _lowering.State.Aggregates.GetLayout(aggContaining);
            if (layout.TryGetIndex(fieldRef.Field, out var elemIndex))
            {
                var arrExpr = _lowering.LoadInstanceRaw(fieldRef.Instance);
                var getVal = AggregateAbi.ReadSlot(_lowering.Builder, arrExpr, elemIndex, StorageTypes.Object);
                // A struct-typed element read AS A VALUE is copied (value semantics); scalar elements are immutable boxes.
                var elemAggregate = _lowering.ResolveAggregateValueType(
                    fieldRef.Field.Type);
                return elemAggregate != null
                    ? AggregateAbi.DeepClone(
                        _lowering.Builder,
                        getVal,
                        elemAggregate,
                        _lowering.State.Aggregates.GetLayout)
                    : getVal;
            }
            throw new System.NotSupportedException(
                $"Cannot access '{fieldRef.Field.Name}' on aggregate type '{aggContaining.Name}'.");
        }

        // this.field → direct variable name → LoadField (struct-typed field copied on value read)
        if (fieldRef.Instance is IInstanceReferenceOperation)
        {
            var fieldAggregate = _lowering.ResolveAggregateValueType(
                fieldRef.Field.Type);
            return fieldAggregate != null
                ? AggregateAbi.DeepClone(
                    _lowering.Builder,
                    _lowering.LoadField(
                        _lowering.State.SourceStorageName(fieldRef.Field),
                        new StorageType(AggregateAbi.ArrayType)),
                    fieldAggregate,
                    _lowering.State.Aggregates.GetLayout)
                : _lowering.LoadField(
                    _lowering.State.SourceStorageName(fieldRef.Field),
                    _lowering.GetStorageType(fieldRef.Field.Type));
        }
        // cross-behaviour field → GetProgramVariable
        if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            _lowering.RejectProgramLocalCrossBehaviourFieldRead(fieldRef.Field);
            var instanceVal = _lowering.VisitExpression(fieldRef.Instance);
            var value = _lowering.LoadProgramVariable(
                instanceVal,
                fieldRef.Field.Name,
                _lowering.GetStorageType(fieldRef.Field.Type));
            return _lowering.MaterializeCrossProgramValue(
                value, fieldRef.Field.Type);
        }
        // other.field → extern getter (same pattern as VisitPropertyReference)
        {
            var fldType = _lowering.GetStorageTypeName(fieldRef.Field.Type);
            // B74 fold: an inherited field registers under the receiver's own static type, not its declaring
            // base — route through the shared owner funnel like the property-get/set sites.
            var containingType = _lowering.GetStorageTypeName(_lowering.ResolveExternOwnerType(fieldRef.Field.ContainingType, fieldRef.Instance?.Type, fieldRef.Field.Name));
            var instanceVal = _lowering.VisitExpression(fieldRef.Instance);
            return _lowering.ExternCall(
                _lowering.RequireBoundAbi(
                    fieldRef, BoundAbiRole.FieldGet),
                new List<CLeaf> { instanceVal },
                new StorageType(fldType));
        }
    }

    // ── Event Reference ──

    /// <summary>
    /// Field-like event value read (design §2.3, A-M2): within the declaring class (or a class that
    /// inherits it), `Foo` used as a plain value/invoke receiver resolves to its backing multicast
    /// delegate field — same SystemObjectArray load a this-field read uses (UasmEmitter.DeclareEvent
    /// materialized the storage under the event's bare name). `+=`/`-=` never reach here (they are
    /// IEventAssignmentOperation, handled by CompoundAssignmentHandler.VisitEventAssignment).
    /// Defensive reject: C# only allows a non-this-receiver event reference via `+=`/`-=`; reading or
    /// invoking `other.Foo` directly is a Roslyn compile error that should never reach an IOperation
    /// tree — this is armor for a future registration gap (§8-3), not a reachable user-facing path.
    /// </summary>
    CLeaf VisitEventReference(IEventReferenceOperation eventRef)
    {
        if (eventRef.Event.IsStatic)
            throw ClassAbiPolicy.UnsupportedStaticStorage(
                eventRef.Event);
        if (eventRef.Instance is not IInstanceReferenceOperation)
            throw new NotSupportedException(
                $"Cannot reference event '{eventRef.Event.Name}' through a non-this receiver; only "
                + "`+=`/`-=` may target another behaviour's event (and cross-behaviour subscribe is "
                + "itself rejected — see the event add/remove diagnostic).");
        return _lowering.LoadField(eventRef.Event.Name, new StorageType(DelegateAbi.BundleType));
    }

    // ── Conversion ──

    // Wave-12 r4 [W1]/[W2]: true when converting src to dst re-types a DELEGATE somewhere inside an
    // array (covariance: Func<string>[] → Func<object>[]) or a tuple ((Func<string>,int) →
    // (Func<object>,int)) with a diverging __dlgc_ sig part — the exact channel-divergence criterion
    // of the [V2] delegate-value arm, which never sees these because the conversion node sits on the
    // ARRAY/TUPLE type. Recurses through nested arrays and tuple elements. An `object`(/[])
    // destination element is NOT a delegate, so object-laundering stays the accepted boundary.
    static ITypeSymbol ResolveTypeParam(ITypeSymbol t,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
        => t is ITypeParameterSymbol tp && typeParamMap != null && typeParamMap.TryGetValue(tp, out var r) ? r : t;

    // A delegate proper, or one reachable through an array element / tuple element / user-struct field —
    // the "delegate-carrying" shape. Resolves each level through the type-param map so a monomorphized
    // generic T (e.g. Func<object>[]) is classified as the concrete type it becomes. Deliberately NOT
    // unified onto EmitPolicy's shared Contains* descent (2026-07-17 walker unification): the source-side
    // consumer below has ACCEPT polarity (srcCarriesDelegate=true ALLOWS the cast), so adopting the wider
    // generic-argument/delegate-signature descent would flip rejects to accepts — this walker must stay
    // exactly as narrow as the reject rule it implements.
    bool StructurallyContainsDelegate(ITypeSymbol t,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        HashSet<ITypeSymbol> visited)
    {
        t = ResolveTypeParam(t, typeParamMap);
        if (t == null) return false;
        if (t is INamedTypeSymbol n && n.DelegateInvokeMethod != null) return true;
        if (t is IArrayTypeSymbol a) return StructurallyContainsDelegate(a.ElementType, typeParamMap, visited);
        if (t is INamedTypeSymbol agg && _lowering.IsAggregateValue(agg) && visited.Add(agg))
            foreach (var m in agg.GetMembers())
                if (m is IFieldSymbol f && !f.IsStatic
                    && StructurallyContainsDelegate(f.Type, typeParamMap, visited))
                    return true;
        return false;
    }

    bool ContainsVariantDelegateConversion(ITypeSymbol src, ITypeSymbol dst,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        // Resolve each end through the type-param map at EVERY recursion level: a generic method whose
        // element/tuple type is a bare T (T=Func<object>) shows the raw type parameter here, so the
        // structural array/tuple/delegate tests below would miss it without substitution.
        src = ResolveTypeParam(src, typeParamMap);
        dst = ResolveTypeParam(dst, typeParamMap);
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
            && DelegateAbi.BuildSigPart(
                   srcInvoke, _lowering.State.Types, typeParamMap)
               != DelegateAbi.BuildSigPart(
                   dstInvoke, _lowering.State.Types, typeParamMap);
    }

    CLeaf EmitNumericConversion(CLeaf sourceValue, ITypeSymbol sourceType, ITypeSymbol destinationType)
    {
        var methodName = ExternResolver.GetConvertMethodName(destinationType);
        if (methodName == null)
            return null;

        // C# truncates float→integer; System.Convert rounds. Match UdonSharp by truncating in the
        // source domain before the final conversion, including when object erasure hid this pair.
        if (ExternResolver.IsFloatType(sourceType) && ExternResolver.IsIntegerType(destinationType))
        {
            var isDecimal = sourceType.SpecialType == SpecialType.System_Decimal;
            var truncType = isDecimal ? "SystemDecimal" : "SystemDouble";

            if (!isDecimal && sourceType.SpecialType == SpecialType.System_Single)
            {
                sourceValue = _lowering.ExternCall(
                    UdonAbiKey.Method("SystemConvert", "ToDouble", new[] { "SystemSingle" }, "SystemDouble"),
                    new List<CLeaf> { sourceValue },
                    StorageTypes.Double);
            }

            sourceValue = _lowering.ExternCall(
                UdonAbiKey.Method("SystemMath", "Truncate",
                    new[] { truncType }, truncType),
                new List<CLeaf> { sourceValue },
                new StorageType(truncType));

            var destinationStorage = _lowering.GetStorageTypeName(destinationType);
            return _lowering.ExternCall(
                UdonAbiKey.Method("SystemConvert", methodName,
                    new[] { truncType }, destinationStorage),
                new List<CLeaf> { sourceValue },
                new StorageType(destinationStorage));
        }

        return _lowering.EmitNarrowingConvert(
            sourceValue,
            _lowering.GetStorageTypeName(sourceType),
            _lowering.GetStorageTypeName(destinationType));
    }

    bool TryEmitClosedObjectCast(IConversionOperation conv, LoweredValue source,
        ITypeSymbol closedDestination, out CLeaf result)
    {
        result = null;
        var plan = _lowering.RequireBoundConversion(conv);
        if (plan.Kind == ClosedConversionKind.None)
            return false;

        // Object erasure changes the semantic type without changing the carried value. LoweredValue
        // preserves that carrier explicitly, so the closed conversion never has to inspect syntax.
        var srcVal = source.Leaf;
        var effectiveSource = plan.SourceType;
        var destinationStorage = _lowering.GetStorageType(closedDestination);
        if (plan.Kind == ClosedConversionKind.Identity)
        {
            result = srcVal;
            return true;
        }

        // The outer object cast hides a real operator on the carried source (DataToken→int/string
        // in YamaStream). Reclassify the CLOSED pair and emit that operator exactly as a direct cast.
        if (plan.Kind == ClosedConversionKind.UserOperator)
        {
            var conversionMethod = plan.OperatorMethod;
            if (conversionMethod.ContainingType is INamedTypeSymbol conversionOwner
                && _lowering.IsObjectArrayEmulated(conversionOwner))
            {
                result = _lowering.EmitCallToMethod(
                    _lowering.RequireRegisteredCallable(
                        conversionMethod),
                    new List<CLeaf> { srcVal });
                return true;
            }

            var conversionExtern = _lowering.RequireBoundAbi(
                conv, BoundAbiRole.Conversion);
            result = _lowering.ExternCall(
                conversionExtern,
                new List<CLeaf> {
                    _lowering.AdaptExternArgument(srcVal, conversionMethod.Parameters[0].Type)
                },
                destinationStorage);
            return true;
        }

        // A specialization can also reveal an enum conversion hidden by `(T)(object)value`.
        // User enums use their underlying integer storage; SDK enum→numeric follows UdonSharp's
        // object-based Convert path because the registered enum has its own heap tag.
        if (plan.Kind == ClosedConversionKind.Enumeration)
        {
            var sourceEnum = effectiveSource as INamedTypeSymbol;
            if (sourceEnum?.TypeKind != TypeKind.Enum)
                sourceEnum = null;
            var destinationEnum = closedDestination as INamedTypeSymbol;
            if (destinationEnum?.TypeKind != TypeKind.Enum)
                destinationEnum = null;

            if (sourceEnum != null
                && !_lowering.IsFoldedEnum(sourceEnum)
                && ExternResolver.IsConvertibleNumericType(closedDestination)
                && ExternResolver.GetConvertMethodName(closedDestination) is { } enumConvert)
            {
                result = _lowering.ExternCall(
                    UdonAbiKey.Method("SystemConvert", enumConvert,
                        new[] { "SystemObject" }, destinationStorage.Name),
                    new List<CLeaf> { srcVal },
                    destinationStorage);
                return true;
            }

            var numericSource = sourceEnum?.EnumUnderlyingType ?? effectiveSource;
            var numericDestination = destinationEnum?.EnumUnderlyingType ?? closedDestination;
            if ((destinationEnum == null || _lowering.IsFoldedEnum(destinationEnum))
                && ExternResolver.IsConvertibleNumericType(numericSource)
                && ExternResolver.IsConvertibleNumericType(numericDestination))
            {
            result = EmitNumericConversion(srcVal, numericSource, numericDestination);
            if (result.Type != destinationStorage)
                result = _lowering.RepresentationCast(
                    result, destinationStorage, RepresentationCastKind.EnumRepresentation);
            return true;
            }
        }

        // Once T is closed, an object-sourced numeric unbox uses Convert.ToT(object), while a numeric
        // value hidden behind the object erasure keeps C# unchecked numeric semantics.
        if (plan.Kind == ClosedConversionKind.ObjectNumeric
            && ExternResolver.GetConvertMethodName(closedDestination) is { } objectConvert)
        {
            result = _lowering.ExternCall(
                UdonAbiKey.Method("SystemConvert", objectConvert,
                    new[] { "SystemObject" }, destinationStorage.Name),
                new List<CLeaf> { srcVal }, destinationStorage);
            return true;
        }
        if (plan.Kind == ClosedConversionKind.Numeric)
        {
            result = EmitNumericConversion(srcVal, effectiveSource, closedDestination);
            return true;
        }

        // UdonSharp's final CastValue fallback is a raw COPY. Represent its destination type explicitly
        // instead of laundering the source leaf and weakening every verifier assignment/return check.
        result = _lowering.RepresentationCast(
            srcVal, destinationStorage, RepresentationCastKind.ClosedGenericObjectCast);
        return true;
    }

    CLeaf VisitConversion(IConversionOperation conv)
    {
        var operatorMethod = conv.OperatorMethod;
        if (operatorMethod != null)
            operatorMethod = _lowering.RequireBoundCallSite(
                conv, CallableSiteKind.Conversion).Callable.Site.Target;

        LoweringServices.RejectChecked(conv.IsChecked);

        var resolvedConversionSource =
            _lowering.ResolveType(conv.Operand.Type);
        var resolvedConversionDestination =
            _lowering.ResolveType(conv.Type);
        if (!conv.IsImplicit
            && operatorMethod == null
            && EmitPolicy.IsNullableT(
                resolvedConversionSource, out _)
            && resolvedConversionDestination is { IsValueType: true }
            && !EmitPolicy.IsNullableT(
                resolvedConversionDestination, out _))
            throw new NotSupportedException(
                $"Explicit conversion from nullable type "
                + $"'{resolvedConversionSource.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                + $"to non-nullable type "
                + $"'{resolvedConversionDestination.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                + "is not supported: a null value requires "
                + "InvalidOperationException, but Udon has no exception "
                + "semantics. Use pattern matching, `??`, or "
                + "GetValueOrDefault() to define the null case.");

        // B82 (wave-16, ruling Option A): reject a user-level conversion that ERASES a v1 class to a non-class
        // type (object / object[] / any object-erased type). A class value is a program-local object[] bundle
        // with NO runtime type identity, so once erased to object it launders past every §2-1 boundary check
        // that keys on the declared type (cross-call arg, object[] field element, string.Format arg,
        // (object[])(object)foo forge). Unlike a delegate (which carries provenance and gets dispatch-time
        // target checks, so delegate→object stays a passthrough), a class MUST be contained at the erasure.
        // The sole exemption is the equality/Equals operand position (§reference-equality lowering, E2 shape);
        // an in-program `object o = classInstance` is the documented over-rejection (E1 shape). Closure-env
        // capture stores the ref via a compiler-emitted EnvEmit.Write, not a user conversion — it never lands
        // here (the F1 execution-locality pin stays green).
        // CA-v2 M3: a USER conversion operator on a v1 class consumes the class value through the
        // operator (C# forbids user conversions to object/a base type, so the destination is always a
        // concrete non-erasing type) — it is a real value conversion, not a laundering erasure.
        bool userClassConversion = operatorMethod is
            { MethodKind: MethodKind.Conversion }
            && operatorMethod.ContainingType is INamedTypeSymbol owner
            && _lowering.IsObjectArrayEmulated(owner);
        if (!userClassConversion
            && _lowering.ResolveType(conv.Operand.Type) is { } source
            && _lowering.ResolveType(conv.Type) is { } destination)
            _lowering.RejectProgramLocalErasure(
                conv, source, destination);

        var sourceValue = _lowering.VisitLoweredExpression(conv.Operand);
        var srcVal = sourceValue.Leaf;

        // Boxing a user struct copies its current value into a distinct box.
        // The carrier is already an object[], so a raw pass-through would
        // alias the original struct storage across the boxing boundary.
        if (operatorMethod == null
            && resolvedConversionDestination?.SpecialType
                == SpecialType.System_Object)
        {
            if (resolvedConversionSource
                    is INamedTypeSymbol boxedAggregate
                && _lowering.IsAggregateValue(boxedAggregate))
                srcVal = AggregateAbi.DeepClone(
                    _lowering.Builder, srcVal, boxedAggregate,
                    _lowering.State.Aggregates.GetLayout);
            else if (EmitPolicy.IsNullableT(
                         resolvedConversionSource,
                         out var boxedUnderlying)
                     && _lowering.ResolveType(boxedUnderlying)
                         is INamedTypeSymbol boxedNullableAggregate
                     && _lowering.IsAggregateValue(
                         boxedNullableAggregate))
            {
                var boxedResult =
                    _lowering.State.Builder.AllocScratch(
                        StorageTypes.Object);
                _lowering.Builder.EmitIf(
                    NullableAbi.HasValue(
                        _lowering.Builder, srcVal),
                    _ => _lowering.EmitAssign(
                        boxedResult,
                        AggregateAbi.DeepClone(
                            _lowering.Builder, srcVal,
                            boxedNullableAggregate,
                            _lowering.State.Aggregates
                                .GetLayout)),
                    _ => _lowering.EmitAssign(
                        boxedResult,
                        _lowering.Const(
                            null, StorageTypes.Object)));
                srcVal = _lowering.SlotRef(boxedResult);
            }
        }

        // B62: `o as T` — mirror the `is` machinery through the same runtime-type-test choke point:
        // (o is T) ? o : null. A non-distinguishable (collapse-set) target hits EmitTypeCheck's loud
        // reject, exactly like `is`; a failing cast nulls the slot instead of passing the value through
        // untyped (which faulted the VM on next use — no IsTryCast handling existed before).
        if (conv.IsTryCast)
            return EmitTryCast(srcVal, conv.Type);

        // Wave-12 r4 [W1]/[W2]: variance laundered through array covariance or a tuple conversion
        // diverges the __dlgc_ channels exactly like the direct delegate-value conversion the [V2]
        // arm below rejects (VM-proven lost return: ref=2 vs -1 on both shapes). Same loud reject,
        // same criterion, recursing through the aggregate structure; equal-sig element conversions
        // and delegate↔object flows are untouched.
        // Resolve conv.Type through the type-param map BEFORE the structural gate: a generic T
        // monomorphizing to Func<object>[] shows the raw type parameter on conv.Type, so a bare
        // `conv.Type is IArrayTypeSymbol` test would miss it (mirrors the scalar arm's ResolveType).
        var variantDst = _lowering.ResolveType(conv.Type);
        if ((variantDst is IArrayTypeSymbol || (variantDst as INamedTypeSymbol)?.IsTupleType == true)
            && ContainsVariantDelegateConversion(
                conv.Operand.Type, conv.Type,
                _lowering.State.TypeParamMap))
            throw new System.NotSupportedException(
                $"Variant delegate conversion from '{conv.Operand.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                + $"to '{conv.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                + "the delegate calling convention keys its argument/return channels by the exact "
                + "signature, so a co/contravariant element binding silently drops values across the "
                + "dispatch. Use matching delegate type parameters.");

        // Object-laundering of a delegate-CARRYING array/tuple — the aggregate analogue of the scalar
        // object→delegate bounded reject below. A generic `CastIt<Func<object>[]>(object o)` monomorphizes
        // the `(T)o` cast's destination to Func<object>[]; its SOURCE is object, so neither the variant arm
        // above (source carries no visible delegate) nor the scalar delegate arm below (destination is an
        // array, not a named delegate) fires, and a bundle built for a DIFFERENT signature is reinterpreted,
        // silently dropping values across the dispatch (VM-proven: r11 array/two-hop, ref=8 vs -1). Mirror
        // the scalar bounded rule: allow only null/default or an operand that statically carries the SAME
        // delegate structure (a same-sig source is already vetted by the variant arm; what remains dangerous
        // is a source carrying NO visible delegate — object / object[]). Keep the value typed as its
        // delegate-carrying type instead of routing it through object.
        if ((variantDst is IArrayTypeSymbol || (variantDst as INamedTypeSymbol)?.IsTupleType == true)
            && StructurallyContainsDelegate(
                variantDst, _lowering.State.TypeParamMap,
                new HashSet<ITypeSymbol>(
                    SymbolEqualityComparer.Default)))
        {
            var stripped = conv.Operand;
            while (stripped is IConversionOperation strippedConv) stripped = strippedConv.Operand;
            var isNull = stripped is IDefaultValueOperation
                || (stripped?.ConstantValue.HasValue == true && stripped.ConstantValue.Value == null);
            var srcCarriesDelegate = StructurallyContainsDelegate(_lowering.ResolveType(stripped?.Type),
                _lowering.State.TypeParamMap,
                new HashSet<ITypeSymbol>(
                    SymbolEqualityComparer.Default));
            if (!isNull && !srcCarriesDelegate)
                throw new System.NotSupportedException(
                    $"Cast from '{(_lowering.ResolveType(conv.Operand.Type) ?? conv.Operand.Type)?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "object"}' "
                    + $"to delegate-carrying type '{variantDst.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                    + "the delegate calling convention keys its argument/return channels by the exact "
                    + "signature, and a delegate boxed inside a non-delegate type carries no statically "
                    + "visible signature, so a variant boxed delegate would silently drop values across the "
                    + "dispatch. Keep the value typed as its delegate-carrying type instead of routing it "
                    + "through object.");
        }

        // Delegate-typed conversions are reference passthrough (design §2.3, fcd25): delegate → object is
        // box-free (the value already IS an object[] reference) and (Func<T>)objExpr cast-back keeps the
        // same bundle reference. No Convert extern may ever be emitted for a delegate source or target.
        // Resolve BOTH ends through the type-param map first: inside a generic body Roslyn shows a `(T)o`
        // cast's type as the un-substituted ITypeParameterSymbol T, so a raw `conv.Type is INamedTypeSymbol`
        // check misses the monomorphized delegate destination entirely (VM-proven silent lost return).
        var convDstType = _lowering.ResolveType(conv.Type);
        var convSrcType = _lowering.ResolveType(conv.Operand.Type);
        if ((convDstType is INamedTypeSymbol dlgDst && dlgDst.DelegateInvokeMethod != null)
            || (convSrcType is INamedTypeSymbol dlgSrc && dlgSrc.DelegateInvokeMethod != null))
        {
            // Wave-12 r2 [V2] → Stage 1.75 §2.3 (B-2): a VARIANT delegate-VALUE conversion (Func<string>
            // value flowing into a Func<object>-typed field/local/param/return via C# co/contravariance)
            // diverges the __dlgc_ convention keys — the callee's bridge writes the channel keyed by its
            // OWN signature while the dispatch site reads the channel keyed by the receiving STATIC
            // delegate type, so arguments/returns would be silently dropped if passed through unchanged.
            // Mint a sig-S wrapper-with-payload bundle around the existing value instead: the wrapper's
            // unified dispatch fires the INNER bundle through the fan-out's one-element form, so self/
            // cross routing is handled generically regardless of the inner bundle's actual target.
            // Equal sig parts (identity or Udon-type-identical conversions) keep the reference
            // passthrough below — their channels already agree, no wrapper needed. Also load-bearing for
            // §5.4's sig-filter soundness (SigFilterCoupledToVarianceReject — now the widened-not-
            // rejected form, since the wrapper's own dispatch site is unconditionally Reentrant like the
            // fan-out, sidestepping the sig-filter question entirely for this arm).
            var wrapperName = _lowering
                .RequireBoundConversion(conv)
                .DelegateWrapperName;
            if (wrapperName != null)
            {
                // A null delegate VALUE converts to null (C# semantics: converting null is null) — never
                // wrap it, or `o == null` and invoke-null-guard behavior would both silently diverge from
                // a plain unwrapped null. Guarded at RUNTIME (not just the statically-known-null case
                // below): srcVal may be a variable whose null-ness is unknown at compile time.
                var wrapResultSlot = _lowering.State.Builder.AllocScratch(new StorageType(DelegateAbi.BundleType));
                var srcNotNull = _lowering.ExternCall(UdonAbi.ObjectInequality,
                    new List<CLeaf> { srcVal, _lowering.Const(null, StorageTypes.Object) }, StorageTypes.Boolean);
                _lowering.Builder.EmitIf(srcNotNull,
                    _ =>
                    {
                        var wThisType = _lowering.GetStorageTypeName(_lowering.ClassSymbol);
                        var wrapperBundle = DelegateAbi.EmitBundleMint(
                            _lowering.Builder,
                            _lowering.Const(
                                BundleAbi.RuntimeTypeId(convDstType),
                                StorageTypes.String),
                            () => _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(new StorageType(wThisType)), new StorageType(wThisType)),
                            _lowering.Const(wrapperName, StorageTypes.String), _lowering.FuncRef(wrapperName), srcVal);
                        _lowering.EmitAssign(wrapResultSlot, wrapperBundle);
                    },
                    _ => _lowering.EmitAssign(wrapResultSlot, _lowering.Const(null, new StorageType(DelegateAbi.BundleType))));
                return _lowering.SlotRef(wrapResultSlot);
            }

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
                // is a safe cast passthrough. If later invoked normally, the real carrier read follows
                // the stock Udon VM fault path; conditional invocation still short-circuits.
                var isNull = stripped is IDefaultValueOperation
                    || (stripped?.ConstantValue.HasValue == true && stripped.ConstantValue.Value == null);
                // A same-signature delegate boxed and unboxed within THIS expression is the trivially
                // safe roundtrip — its channels agree (resolve through the type-param map so a generic
                // operand whose spec is a same-sig delegate still qualifies).
                var safeRoundtrip = _lowering.ResolveType(stripped?.Type) is INamedTypeSymbol sDlg && sDlg.DelegateInvokeMethod is { } sInvoke
                    && DelegateAbi.BuildSigPart(
                           sInvoke, _lowering.State.Types,
                           _lowering.State.TypeParamMap)
                       == DelegateAbi.BuildSigPart(
                           lInvoke, _lowering.State.Types,
                           _lowering.State.TypeParamMap);
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

        // Closed generic/object-erasure cast. This must run after the delegate protocol guards above
        // and before the raw conversion arms below, whose Roslyn symbols still expose `object` and T.
        if (TryEmitClosedObjectCast(
                conv, sourceValue, convDstType, out var closedObjectCast))
            return closedObjectCast;

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
        // CW18 (producer): a USER-enum side converts as its bare underlying numeric tag (a user enum is STORED
        // as that tag — GetUdonTypeName), so resolve enum facets before the numeric tests. Without this,
        // (E?)intExpr / (int?)enumNbl fell through BOTH arms to the identity passthrough and minted a
        // plain-int-tagged box inside a small-underlying E? — the drifted box whose strict accessor reads
        // HeapTypeMismatch-fault the VM. SDK enums keep their own registered Udon tag and stay on the
        // enum↔numeric arm below.
        ITypeSymbol NumericFacet(ITypeSymbol t)
            => t is INamedTypeSymbol en && _lowering.IsFoldedEnum(en) ? en.EnumUnderlyingType : t;
        var liftedDstU = EmitPolicy.IsNullableT(conv.Type, out var dstNblU) ? dstNblU : conv.Type;
        var liftedDstN = liftedDstU == null ? null : NumericFacet(liftedDstU);
        if (conv.Conversion.IsNullable
            && EmitPolicy.IsNullableT(conv.Operand.Type, out var liftedSrcU)
            && NumericFacet(liftedSrcU) is { } liftedSrcN && ExternResolver.IsConvertibleNumericType(liftedSrcN)
            && liftedDstN != null && ExternResolver.IsConvertibleNumericType(liftedDstN)
            && !SymbolEqualityComparer.Default.Equals(liftedSrcN, liftedDstN)
            && ExternResolver.GetConvertMethodName(liftedDstN) is { } liftedDstMethod)
        {
            var dstU = _lowering.GetStorageTypeName(liftedDstN);
            // C# integer narrowing is UNCHECKED (wrap); Convert.To{Small} is CHECKED and throws. For an
            // integer→integer lifted conversion, promote the boxed source to int64 (tolerates any boxed integer
            // tag, never overflows) and wrap/reinterpret via EmitNarrowingConvert. Float-involved conversions
            // keep the plain null-preserving Convert.
            // char is integral for narrowing (EmitNarrowingConvert wraps it like C#'s unchecked cast) but
            // ExternResolver.IsIntegerType excludes it; treat char as integral here so a lifted int?→char?
            // narrowing WRAPS instead of taking the CHECKED Convert.ToChar branch (which throws > 65535).
            bool liftedIntToInt =
                (ExternResolver.IsIntegerType(liftedSrcN) || liftedSrcN.SpecialType == SpecialType.System_Char)
                && (ExternResolver.IsIntegerType(liftedDstN) || liftedDstN.SpecialType == SpecialType.System_Char);
            return NullableAbi.EmitLiftedNumericConversion(_lowering.Builder, srcVal, new StorageType(dstU), liftedDstMethod,
                liftedIntToInt, _lowering.EmitNarrowingConvert);
        }

        // Lifted numeric conversion with a BARE source and a Nullable<numeric> dest (e.g. `(byte?)(intExpr)`):
        // the value is always present, so narrow numerically (C#-unchecked wrap) and let it box into the
        // nullable's SystemObject slot with the right tag, so total consumers such as
        // GetValueOrDefault() and `??` read the correct underlying representation.
        if (conv.Conversion.IsNullable
            && conv.Operand.Type != null && NumericFacet(conv.Operand.Type) is { } bareSrcN
            && ExternResolver.IsConvertibleNumericType(bareSrcN)
            && EmitPolicy.IsNullableT(conv.Type, out var bareDstU) && NumericFacet(bareDstU) is { } bareDstN
            && ExternResolver.IsConvertibleNumericType(bareDstN)
            && !SymbolEqualityComparer.Default.Equals(bareSrcN, bareDstN))
        {
            return _lowering.EmitNarrowingConvert(srcVal, _lowering.GetStorageTypeName(bareSrcN), _lowering.GetStorageTypeName(bareDstN));
        }

        // CW20: a HARD cast from a reference-typed source (object / interface / ValueType) to
        // Nullable<T> is C#'s unboxing conversion — it needs a runtime type check of the box, and the
        // `as T?` twin loud-rejects through EmitTypeCheck for exactly this shape (a Nullable box is not
        // runtime-distinguishable). The identity passthrough below instead laundered ANY box into the
        // nullable slot: a mismatched box (C#: InvalidCastException) silently minted a drifted-tag
        // nullable that mis-compares on the tolerant lifted paths and HeapTypeMismatch-faults on later
        // typed consumers. Mirror the as-form's polarity: loud reject. A statically-null operand stays
        // a passthrough (C#: unboxing null into T? is legal and yields null), and a user conversion
        // operator is a real value conversion, not an unbox.
        if (operatorMethod == null
            && EmitPolicy.IsNullableT(convDstType, out _)
            && convSrcType is { IsValueType: false })
        {
            var unboxOperand = conv.Operand;
            while (unboxOperand is IConversionOperation innerUnbox) unboxOperand = innerUnbox.Operand;
            var unboxesNull = unboxOperand is IDefaultValueOperation
                || (unboxOperand?.ConstantValue.HasValue == true && unboxOperand.ConstantValue.Value == null);
            if (!unboxesNull)
                throw new System.NotSupportedException(
                    $"Cast from '{convSrcType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + $"to '{convDstType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                    + "unboxing into a nullable needs a runtime type check, but Udon collapses the box onto a "
                    + "non-injective runtime type tag (the same reason the `as` form rejects), so a mismatched "
                    + "box (C#: InvalidCastException) would pass through silently and mis-compare or fault at a "
                    + "later use. Keep the value typed as its nullable type instead of routing it through "
                    + $"'{convSrcType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'.");
        }

        // Numeric conversions (int→float, decimal↔numeric, etc.) via System.Convert
        if (conv.Operand.Type != null && conv.Type != null
            && ExternResolver.IsConvertibleNumericType(conv.Operand.Type)
            && ExternResolver.IsConvertibleNumericType(conv.Type)
            && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            return EmitNumericConversion(srcVal, conv.Operand.Type, conv.Type);
        }

        // User-defined implicit/explicit conversions (e.g. Vector2→Vector3)
        if (operatorMethod != null && conv.Operand.Type != null && conv.Type != null && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            // A user STRUCT conversion operator is an emitted method, not an extern: route to it (its containing
            // type is SystemObjectArray-backed, so it has no SDK conversion extern).
            if (operatorMethod.ContainingType is INamedTypeSymbol convOpCt && _lowering.IsObjectArrayEmulated(convOpCt))
                return _lowering.EmitCallToMethod(
                    _lowering.RequireRegisteredCallable(operatorMethod),
                    new List<CLeaf> { srcVal });

            var dstType = _lowering.GetStorageTypeName(conv.Type);
            var conversionExtern = _lowering.RequireBoundAbi(
                conv, BoundAbiRole.Conversion);
            return _lowering.ExternCall(
                conversionExtern,
                new List<CLeaf> {
                    _lowering.AdaptExternArgument(srcVal, operatorMethod.Parameters[0].Type)
                },
                new StorageType(dstType));
        }

        // Enum ↔ numeric conversions (int→enum, enum→int, and B72: enum→float/double/decimal). This arm is a
        // conversion between an enum and a numeric type. It must NOT fire when the other side is `object`
        // (enum→object BOXING) — that mints a nonexistent SystemConvert.__ToObject__ and must fall through to
        // the identity pass-through below (the underlying value is already a heap object in Udon's object[]
        // model). B61 restricted this to exclude boxing; B72 widened it back to floating/decimal targets,
        // which are genuine conversions (a raw COPY into a float/decimal slot is a silent mistype).
        if (conv.Operand.Type != null && conv.Type != null
                                      && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type)
                                      && (conv.Operand.Type.TypeKind == TypeKind.Enum || conv.Type.TypeKind == TypeKind.Enum)
                                      && IsEnumOrNumeric(conv.Operand.Type) && IsEnumOrNumeric(conv.Type))
        {
            var dstType = _lowering.GetStorageTypeName(conv.Type);
            // Prefer const: avoids COPY type-tag corruption
            var constVal = conv.ConstantValue.HasValue ? conv.ConstantValue
                         : conv.Operand.ConstantValue.HasValue ? conv.Operand.ConstantValue
                         : default;
            if (constVal.HasValue)
                return _lowering.Const(constVal.Value, new StorageType(dstType));

            // Enum ↔ underlying is a pure re-typing between each side's effective underlying udon type (an enum
            // is STORED as its underlying type — see UdonTypeSystem.Describe, so dstType for an enum
            // target is already its underlying). The former int→enum path indexed a per-enum lookup array, but
            // enumArr[v - min] == v — an identity over the underlying value — so it added nothing except a VM
            // fault on out-of-range casts ((E)999 is legal C# and must round-trip). A direct convert is correct
            // for every value: in-range, out-of-range, and byte/short/unsigned wrap. A same-width pair (int-
            // backed enum ↔ int) re-types through a scratch slot; a different-width pair (byte/short-backed enum
            // ↔ int, any enum ↔ long) needs a real numeric conversion (a bare COPY into a wider slot would store
            // e.g. a SystemByte into a SystemInt32 variable and fail verification).
            var srcUnderlying = conv.Operand.Type is INamedTypeSymbol srcEnum && srcEnum.TypeKind == TypeKind.Enum
                ? _lowering.GetStorageTypeName(srcEnum.EnumUnderlyingType)
                : _lowering.GetStorageTypeName(conv.Operand.Type);
            if (srcUnderlying != dstType)
                return _lowering.EmitNarrowingConvert(srcVal, srcUnderlying, dstType);
            var tmpSlot = _lowering.State.Builder.AllocScratch(new StorageType(dstType));
            _lowering.EmitAssign(tmpSlot, srcVal);
            return _lowering.SlotRef(tmpSlot);
        }

        // A potentially-failing hard cast into a compiler-owned bundle needs
        // InvalidCastException. Udon cannot implement that contract, so keep
        // only the statically total cases: identity, class upcast, and null
        // into a class reference. `is`, `as`, and declaration patterns remain
        // the supported runtime-checked forms.
        var castDestination = _lowering.ResolveType(conv.Type);
        var castSource = _lowering.ResolveType(conv.Operand.Type);
        var destinationShape = _lowering.State.Types.SourceShape(
            conv.Type, _lowering.State.TypeParamMap);
        var staticallyCompatibleBundle =
            SymbolEqualityComparer.Default.Equals(
                castSource, castDestination)
            || castSource is INamedTypeSymbol castSourceClass
               && castDestination is INamedTypeSymbol
                   castDestinationClass
               && _lowering.IsUserClass(castSourceClass)
               && _lowering.IsUserClass(castDestinationClass)
               && VirtualDispatch.IsAssignable(
                   castSourceClass, castDestinationClass);
        if (destinationShape.IsBundle
            && !staticallyCompatibleBundle)
        {
            var castOperand = conv.Operand;
            while (castOperand is IConversionOperation innerCast) castOperand = innerCast.Operand;
            var castsNull = castOperand is IDefaultValueOperation
                || (castOperand?.ConstantValue.HasValue == true && castOperand.ConstantValue.Value == null);
            if (!castsNull
                || destinationShape.Bundle is not
                    RuntimeBundleKind.Class
                    and not RuntimeBundleKind.ReferenceAggregate)
                throw new NotSupportedException(
                    $"Cast from "
                    + $"'{castSource.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + $"to compiler bundle "
                    + $"'{castDestination.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + "is not supported: a failed hard cast requires "
                    + "InvalidCastException, but Udon has no exception "
                    + "semantics. Use `is`, `as`, or a declaration pattern "
                    + "to handle the mismatch explicitly.");
        }

        // Identity conversion: pass through
        return srcVal;
    }

    // B63 (mint-site reject, immediate-use-only): a typeof() on a NON-distinguishable type bakes a SystemType
    // constant whose Udon runtime tag is non-injective — many distinct CLR types share one tag (every
    // UdonSharpBehaviour-derived type + UdonBehaviour + user interfaces → IUdonEventReceiver, an enum → its
    // underlying, every delegate/struct/tuple/array-of-those → SystemObjectArray). Once that constant is a
    // storable heap value, ANY equality path silently lies (`typeof(A)==typeof(B)`, `ta==tb` after a store,
    // `object o=typeof(A); o==p`, `object.Equals`, `.Equals`, or a comparison in a callee it was passed to) —
    // an unbounded surface no comparison-site check can close. So the collapse-set token may exist ONLY
    // transiently as a direct argument to a component-query engine call (GetComponent/GetComponents family),
    // where the extern consumes it immediately and it never becomes a comparable value; every other parent
    // (assignment/local init, ==/!=, field/array store, user-method argument, return) is a loud reject at this
    // single mint choke point. A token that cannot be stored cannot be laundered into a later comparison.
    // Distinguishable types (primitives, arrays with distinct element tags, uniquely-tagged SDK/native types)
    // keep an honest token and are fully unrestricted.
    CLeaf EmitTypeofToken(ITypeOfOperation typeOf)
    {
        var operand = typeOf.TypeOperand;
        // object[]'s fold to SystemObjectArray IS UdonSharp's intended representation (a jagged/object array
        // genuinely IS object[] at the VM level — GetType()==typeof(object[]) is documented, SDK-Compat-pinned
        // behaviour), so typeof(object[]) is unrestricted. B76: the exemption MUST gate on the ARRAY's own
        // runtime tag, not its element's. Component[] has a runtime-distinguishable ELEMENT (UnityEngine.
        // Component) yet the array itself folds to UnityEngineComponentArray — the SAME collapse tag a
        // UdonSharpBehaviour[] / user interface[] carries — so an element-axis test let typeof(Component[])
        // escape and lie (myBehaviourArray.GetType()==typeof(Component[]) silently true, the B63 vector). The
        // distinguishable arrays (int[]/Camera[]) pass IsRuntimeDistinguishable(operand) below on their own
        // unique tag; the one folding array that stays legal is object[], carved out explicitly.
        ClassAbi.RejectTypeofToken(_lowering.ResolveType(operand));
        bool stockObjectArray = operand is IArrayTypeSymbol arr
            && arr.Rank == 1
            && arr.ElementType.SpecialType == SpecialType.System_Object;
        if (!stockObjectArray
            && !_lowering.State.Types.IsRuntimeDistinguishable(
                operand, _lowering.State.TypeParamMap)
            && !IsDirectComponentQueryArgument(typeOf))
            throw new NotSupportedException(
                $"typeof('{(_lowering.ResolveType(operand) ?? operand).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') "
                + "is not supported here: Udon folds it and several distinct types onto one non-injective runtime "
                + "type tag, so once stored its System.Type token compares equal to every same-tag type (==, "
                + ".Equals, object-boxed, or stored-then-compared all silently lie). A collapse-set typeof may "
                + "only be passed directly to a component query (GetComponent(typeof(...))); pass typeof(...) "
                + "directly to the engine call, or use the generic GetComponent<T>() overload.");
        return _lowering.ConstTypeToken(operand);
    }

    // Immediate-use gate for a collapse-set typeof: is this typeof (through any conversion wrapper) the direct
    // argument of a GetComponent-family engine call? Those externs consume the token in place and never return
    // it, so it can never be stored and later compared. A user-defined method that merely starts with
    // "GetComponent" is excluded by the receiver-type check (must be UnityEngine.Component/GameObject).
    static bool IsDirectComponentQueryArgument(ITypeOfOperation typeOf)
    {
        IOperation node = typeOf;
        while (node.Parent is IConversionOperation conv) node = conv;
        if (node.Parent is IArgumentOperation arg && arg.Parent is IInvocationOperation inv)
        {
            var ct = inv.TargetMethod.ContainingType;
            return inv.TargetMethod.Name.StartsWith("GetComponent")
                && ct != null && (ct.Name == "Component" || ct.Name == "GameObject")
                && ct.ContainingNamespace?.Name == "UnityEngine";
        }
        return false;
    }

    // `o as T` ≡ (o is T) ? (T)o : null, reusing the shared is-machinery: EmitTypeCheck enforces the
    // distinguishability choke point (collapse-set target rejects loudly), then the else branch nulls the
    // slot so a failed cast yields null rather than passing the value through untyped (B62).
    CLeaf EmitTryCast(CLeaf srcVal, ITypeSymbol targetType)
    {
        var targetUdon = _lowering.GetStorageTypeName(targetType);
        var resultSlot = _lowering.State.Builder.AllocScratch(new StorageType(targetUdon));
        var isCheck = _lowering.EmitTypeCheck(srcVal, targetType);
        _lowering.Builder.EmitIf(isCheck,
            _ => _lowering.EmitAssign(resultSlot, srcVal),
            _ => _lowering.EmitAssign(resultSlot, _lowering.Const(null, new StorageType(targetUdon))));
        return _lowering.SlotRef(resultSlot);
    }

    // An enum or a NUMERIC type (integral, char, OR floating/decimal) — the sides the enum↔numeric conversion
    // arm may involve. Excludes object/reference targets so enum→object BOXING still falls through to the
    // identity pass-through (B61). B72: a FLOATING/DECIMAL target must be included — `(double)enumVal` is a
    // real numeric conversion (SystemConvert.ToDouble), not a re-typing; the former integral-only guard let it
    // fall through to identity, COPY'ing the raw underlying int into a %SystemDouble slot with no convert
    // (silent-wrong, invisible to both extern-name gates).
    static bool IsEnumOrNumeric(ITypeSymbol t) =>
        t.TypeKind == TypeKind.Enum || ExternResolver.IsConvertibleNumericType(t);

    // ── Default Value ──

    CLeaf VisitDefaultValue(IDefaultValueOperation defaultVal)
    {
        // Aggregate default → a ZERO-INITIALIZED object[] (fields set to their defaults), not null, so field
        // access on the default does not NRE. ResolveType is required for `default(T)` inside a generic method
        // where T is a struct type arg: defaultVal.Type is then the open type parameter, which a directly-named
        // INamedTypeSymbol check would miss — leaving the default as null and crashing on the first field read.
        if (_lowering.ResolveType(defaultVal.Type) is INamedTypeSymbol aggDef && _lowering.IsAggregateValue(aggDef))
            return AggregateAbi.MintDefault(_lowering.Builder, _lowering.State.Aggregates.GetLayout(aggDef),
                _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName);

        var dvType = _lowering.GetStorageTypeName(defaultVal.Type);
        if (!defaultVal.Type.IsValueType)
            return _lowering.Const(null, new StorageType(dvType));

        return _lowering.Const(
            AggregateAbi.DefaultScalarValue(defaultVal.Type), new StorageType(dvType));
    }

    // ── Declaration Expression ──

    CLeaf VisitDeclarationExpression(IDeclarationExpressionOperation declExpr)
    {
        if (declExpr.Expression is not ILocalReferenceOperation localRef2)
            return _lowering.VisitExpression(declExpr.Expression);

        var udonType = _lowering.GetStorageTypeName(localRef2.Type);
        // Stage 2 §4.1: a CAPTURED out-var/pattern declaration still needs an ADDRESSABLE heap slot
        // for the writer (env cells have no address) — declare a flat staging field as before, and
        // the consumer that populated it must sync it into the env cell (out-arg copy-back /
        // pattern-binding stores go through AssignToLValue / TryEmitEnvStore arms). Registering the
        // staging field in _localBindings is WRONG for captured symbols (reads would bypass the env),
        // so captured declarations get a staging slot WITHOUT a binding.
        if (_lowering.State.TryGetEnvBinding(localRef2.Local, out _))
        {
            var stagingId = _lowering.State.Storage.DeclareLocal(localRef2.Local.Name, new StorageType(udonType));
            return _lowering.LoadField(stagingId, new StorageType(udonType));
        }
        var localId = _lowering.State.Storage.DeclareLocal(localRef2.Local.Name, new StorageType(udonType));
        _lowering.LocalBindings[localRef2.Local] = new LocalBinding(localId);
        return _lowering.LoadField(localId, new StorageType(udonType));
    }

    // ── Delegate Creation ──

    // The ONLY producer of delegate values (design §2.2): builds the tagged runtime delegate bundle
    // { Kind, Target, Method, Addr, Env }.
    // ResolveDelegateBridge hoists lambdas/local functions and registers their __dlg_ bridge via
    // PendingDelegateBridges (DelegateAbi.Method is the cross-path entry, so the bridge is always emitted).
    // Capture-escape registration is pre-emit analysis (§4.1) — nothing is marked here.
    CLeaf VisitDelegateCreation(IDelegateCreationOperation op)
    {
        var binding = _lowering.ResolveDelegateBridge(op);
        var bridgeName = binding.Plan.BridgeName;
        var funcRef = binding.FunctionReference;
        var thirdParty = binding.TargetInstance;
        var envLeaf = binding.Environment;
        var delegateInvoke = (op.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
        if (delegateInvoke != null
            && DelegateAbi.IsProgramLocalSignature(
                delegateInvoke,
                _lowering.State.Types,
                _lowering.State.TypeParamMap)
            && thirdParty != null)
            throw new System.NotSupportedException(
                "A delegate with a user-class parameter or return type cannot bind a cross-program "
                + "target. Keep the target in this behaviour so the class bundle remains program-local.");
        // The finalized bridge kind is the proof that signature variance was
        // handled. Emission does not reconstruct that decision from source
        // symbols after binding has been frozen.
        DelegateAbi.ValidateDelegateBinding(op.Type as INamedTypeSymbol,
            binding.Plan, _lowering.State.Types,
            _lowering.State.TypeParamMap);

        var thisType = _lowering.GetStorageTypeName(_lowering.ClassSymbol);
        // Addr discipline (§1.3): the only sources for DelegateAbi.Addr are the back-patched funcaddr const
        // (boxed UInt32) or Const(0u). A third-party method group's local funcaddr is meaningless in the
        // target program, so it carries 0u; a same-this target carries the REAL funcaddr — even when the
        // bundle is later handed cross-Behaviour (the invoke-side target-identity guard is the only gate).
        var addr = thirdParty != null ? (CLeaf)_lowering.Const(0u, StorageTypes.UInt32) : funcRef;

        // Stage 2 §3.7: DelegateAbi.Env carries the binding-scope env for a CAPTURING closure target, else
        // a null const (capture-free lambda / named method / base.M) = byte-identical to Stage 1.
        var bundle = DelegateAbi.EmitBundleMint(
            _lowering.Builder,
            _lowering.Const(
                BundleAbi.RuntimeTypeId(
                    _lowering.ResolveType(op.Type)),
                StorageTypes.String),
            () => thirdParty ?? _lowering.LoadField(_lowering.State.Storage.DeclareThisOnce(new StorageType(thisType)), new StorageType(thisType)),
            _lowering.Const(bridgeName, StorageTypes.String), addr, envLeaf);

        return bundle;
    }

    // ── Tuple Literal ──

    CLeaf VisitTupleLiteral(ITupleOperation op)
    {
        return AggregateAbi.MintTupleLiteral(
            _lowering.Builder, op,
            _lowering.State.Aggregates.GetLayout(
                (INamedTypeSymbol)_lowering.ResolveType(op.Type)),
            _lowering.VisitExpression);
    }

}
