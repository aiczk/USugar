using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ExpressionHandler : HandlerBase, IExpressionHandler
{
    public ExpressionHandler(EmitContext ctx) : base(ctx) { }

    public OperationKind[] HandledKinds { get; } = new[]
    {
        OperationKind.Literal, OperationKind.LocalReference, OperationKind.FieldReference,
        OperationKind.EventReference, OperationKind.ParameterReference, OperationKind.InstanceReference,
        OperationKind.Conversion, OperationKind.DefaultValue, OperationKind.TypeOf, OperationKind.NameOf,
        OperationKind.DeclarationExpression, OperationKind.Discard, OperationKind.DelegateCreation, OperationKind.Tuple,
    };

    public CLeaf Handle(IOperation expression) => expression switch
    {
        ILiteralOperation op => VisitLiteral(op),
        // Stage 2 §4.1: a captured local/param has NO flat storage — reads route through the owning
        // scope's env record (aggregate captures keep clone-on-read value semantics on the way out).
        ILocalReferenceOperation localRef when _ctx.Closures.TryGetEnvBinding(localRef.Local, out _)
            => ResolveType(localRef.Type) is INamedTypeSymbol eaggT && TypeClassifier.IsAggregateValue(eaggT)
                   ? AggregateAbi.DeepClone(_builder, EnvEmit.Read(_builder, _ctx, localRef.Local, new StorageType(AggregateAbi.ArrayType)),
                       eaggT, _ctx.Aggregates.GetLayout)
                   : EnvEmit.Read(_builder, _ctx, localRef.Local, GetStorageType(localRef.Type)),
        ILocalReferenceOperation localRef => _localBindings.TryGetValue(localRef.Local, out var localBinding)
                                                 ? ResolveType(localRef.Type) is INamedTypeSymbol laggT && TypeClassifier.IsAggregateValue(laggT)
                                                     ? AggregateAbi.DeepClone(_builder, LoadField(localBinding.Id, new StorageType(AggregateAbi.ArrayType)),
                                                         laggT, _ctx.Aggregates.GetLayout)
                                                     : LoadField(localBinding.Id, GetStorageType(localRef.Type))
                                                 : throw new InvalidOperationException($"Cannot resolve local variable '{localRef.Local.Name}' in method '{_currentMethod?.Name ?? "(none)"}'."),
        IFieldReferenceOperation op => VisitFieldReference(op),
        IEventReferenceOperation op => VisitEventReference(op),
        IParameterReferenceOperation paramRef when _ctx.Closures.TryGetEnvBinding(paramRef.Parameter, out _)
            => ResolveType(paramRef.Type) is INamedTypeSymbol epaggT && TypeClassifier.IsAggregateValue(epaggT)
                   ? AggregateAbi.DeepClone(_builder, EnvEmit.Read(_builder, _ctx, paramRef.Parameter, new StorageType(AggregateAbi.ArrayType)),
                       epaggT, _ctx.Aggregates.GetLayout)
                   : EnvEmit.Read(_builder, _ctx, paramRef.Parameter, GetStorageType(paramRef.Type)),
        IParameterReferenceOperation paramRef => ResolveType(paramRef.Type) is INamedTypeSymbol paggT && TypeClassifier.IsAggregateValue(paggT)
                                                     ? AggregateAbi.DeepClone(_builder, LoadParam(paramRef.Parameter),
                                                         paggT, _ctx.Aggregates.GetLayout)
                                                     : LoadParam(paramRef.Parameter),
        // CW24 (closed-world audit): `this` read AS A VALUE (`return this` / `var c = this` / `M(this)`)
        // clones like every sibling value-read arm above — the receiver bundle is the caller's LIVE
        // storage (EmitStructInstanceCall passes it raw). A v1 CLASS receiver shares the same param0
        // convention but stays raw (CA-M1 reference semantics); receiver-position `this` never gets
        // here (LoadInstanceRaw has its own IInstanceReference arm).
        IInstanceReferenceOperation when _ctx.Methods.CurrentStructReceiverParamId is { } recvPid
            => _ctx.Methods.CurrentMethod?.ContainingType is INamedTypeSymbol thisStructT && TypeClassifier.IsUserStruct(thisStructT)
                   ? AggregateAbi.DeepClone(_builder, LoadField(recvPid, new StorageType(AggregateAbi.ArrayType)),
                       thisStructT, _ctx.Aggregates.GetLayout)
                   : LoadField(recvPid, new StorageType(AggregateAbi.ArrayType)),
        // Class receiver capture (design 2026-07-10 v2 §1.4, the SINGLE new resolution arm): inside
        // a hoisted closure hosted by a v1-class member, `this` is the receiver bundle in the env
        // chain (synthetic capture keyed by the member's OriginalDefinition). Every access shape —
        // field read/write/compound, instance call, property, indexer — funnels here through
        // LoadInstanceRaw's fallthrough, so no second arm exists anywhere.
        IInstanceReferenceOperation when LambdaCaptureAnalyzer.ReceiverCaptureKey(_ctx.Methods.CurrentMethod) is { } rcvKey
                                         && _ctx.Closures.TryGetEnvBinding(rcvKey, out _)
            => EnvEmit.Read(_builder, _ctx, rcvKey, new StorageType(AggregateAbi.ArrayType)),
        IInstanceReferenceOperation => LoadField(_ctx.Storage.DeclareThisOnce(GetStorageType(_classSymbol)), GetStorageType(_classSymbol)),
        IConversionOperation op => VisitConversion(op),
        IDefaultValueOperation op => VisitDefaultValue(op),
        ITypeOfOperation typeOf => EmitTypeofToken(typeOf),
        INameOfOperation nameOf => Const(nameOf.ConstantValue.Value.ToString(), StorageTypes.String),
        IDeclarationExpressionOperation op => VisitDeclarationExpression(op),
        IDiscardOperation discard => SlotRef(_ctx.Builder.AllocScratch(GetStorageType(discard.Type))),
        IDelegateCreationOperation op => VisitDelegateCreation(op),
        ITupleOperation op => VisitTupleLiteral(op),
        _ => throw new NotSupportedException(expression.GetType().Name),
    };

    // ── Literal ──

    CLeaf VisitLiteral(ILiteralOperation lit)
    {
        // null literal has no type
        if (lit.Type == null)
            return Const(null, StorageTypes.Object);
        var udonType = GetStorageTypeName(lit.Type);
        if (!lit.ConstantValue.HasValue)
            return Const(null, new StorageType(udonType));
        var value = lit.ConstantValue.Value;
        return Const(value, new StorageType(udonType));
    }

    // ── Field Reference ──

    CLeaf VisitFieldReference(IFieldReferenceOperation fieldRef)
    {
        // const fields (HasConstantValue) and static readonly with compile-time constant values
        if (fieldRef.Field.HasConstantValue)
        {
            var constType = GetStorageTypeName(fieldRef.Field.Type);
            var constVal = fieldRef.Field.ConstantValue;
            return Const(constVal, new StorageType(constType));
        }
        // static readonly with constant value at operation level (Roslyn may fold these)
        // static readonly field with a compile-time-constant initializer → fold to the value. A `static
        // readonly` field has no ConstantValue of its own (only `const` does), so evaluate the initializer
        // expression. Each program gets its own copy, which is observationally identical to a true shared
        // static because the value is immutable — so no singleton/shared storage is needed.
        if (fieldRef.Field.IsStatic && fieldRef.Field.IsReadOnly
            && (fieldRef.ConstantValue.HasValue || EmitPolicy.TryGetConstFieldInitializer(_compilation, fieldRef.Field, out _)))
        {
            var constType = GetStorageTypeName(fieldRef.Field.Type);
            var value = fieldRef.ConstantValue.HasValue ? fieldRef.ConstantValue.Value
                : (EmitPolicy.TryGetConstFieldInitializer(_compilation, fieldRef.Field, out var v) ? v : null);
            return Const(value, new StorageType(constType));
        }
        if (fieldRef.Field.IsStatic)
        {
            // Wave-14 crossfeature lens: a static field on a USER STRUCT (readonly or not; generic or
            // not) is NOT materialized anywhere — feature S's per-program static-readonly storage
            // (design S-M1) only walks the compiled UdonSharpBehaviour class's own hierarchy. Left
            // unguarded this fell through to the "Unity/System static field → extern getter" arm below
            // (meant for SDK statics like Vector3.zero), building a bogus property-get extern on the
            // struct's own SystemObjectArray Udon type (VM-proven: "Unknown extern:
            // SystemObjectArray.__get_Table__SystemObjectArray" for `StgSampler<T>.Table`). A struct's
            // static field is per-TYPE storage — for a GENERIC struct, per-CLOSED-instantiation
            // (StgSampler<int>.Table and StgSampler<string>.Table are C#-distinct) — which needs its own
            // materialization design (mirroring feature S but keyed by constructed struct type), not a
            // one-line patch. Reject loudly instead of emitting an assembler-crashing extern.
            if (fieldRef.Field.ContainingType is INamedTypeSymbol structFieldCt && TypeClassifier.IsUserStruct(structFieldCt))
                throw new NotSupportedException(
                    $"Static field '{fieldRef.Field.ContainingType.Name}.{fieldRef.Field.Name}' on a "
                    + "user-defined struct is not supported: static storage for a struct type (per closed "
                    + "instantiation, if generic) has no materialization mechanism yet. Move the data to "
                    + "a field on the UdonSharpBehaviour class instead.");
            ClassAbi.RejectStaticField(fieldRef.Field);
            if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
            {
                // Non-const, non-foldable `static readonly` (const/foldable already returned above) —
                // per-program instance materialization (design §3.1, Q-S1). Declared within this
                // program's own class-or-base hierarchy → UasmEmitter's static field walk gave it a
                // heap var (LoadField, same shape as a this-field read; aggregates clone-on-read).
                // Otherwise no storage for it exists in THIS program at all (§3.5, Q-S5) — loud.
                if (fieldRef.Field.IsReadOnly)
                {
                    if (IsDeclaredInOwnHierarchy(_classSymbol, fieldRef.Field.ContainingType))
                        return fieldRef.Field.Type is INamedTypeSymbol staticFieldAgg && TypeClassifier.IsAggregateValue(staticFieldAgg)
                            ? AggregateAbi.DeepClone(_builder, LoadField(fieldRef.Field.Name, new StorageType(AggregateAbi.ArrayType)),
                                staticFieldAgg, _ctx.Aggregates.GetLayout)
                            : LoadField(fieldRef.Field.Name, GetStorageType(fieldRef.Field.Type));

                    var crossMsg = $"cannot read a non-constant static readonly field "
                        + $"'{fieldRef.Field.ContainingType.Name}.{fieldRef.Field.Name}' from another behaviour; "
                        + "Udon programs have no shared static storage. Make it 'const' if it is compile-time "
                        + "constant, or expose an instance field.";
                    _diagnostics.Add(new EmitDiagnostic { Severity = "Error", Message = crossMsg });
                    throw new NotSupportedException(crossMsg);
                }
                // static MUTABLE field → compile error (Udon VM has no shared static storage). §3.7/R8:
                // message sharpened to make clear 'static readonly' IS supported (only mutable statics aren't).
                var mutableMsg = $"Static field '{fieldRef.Field.Name}' is not supported on UdonSharpBehaviour "
                    + "types: the Udon VM has no shared static storage. 'static readonly' IS supported (each "
                    + "behaviour instance materializes its own immutable copy) — use 'const' for a compile-time "
                    + "constant, 'static readonly' for immutable per-instance data, or convert to an instance field.";
                _diagnostics.Add(new EmitDiagnostic { Severity = "Error", Message = mutableMsg });
                throw new NotSupportedException(mutableMsg);
            }
            // Unity/System static field → extern getter
            var fldType = GetStorageTypeName(fieldRef.Field.Type);
            var containingType = GetStorageTypeName(fieldRef.Field.ContainingType);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
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
            && ResolveType(fieldRef.Instance.Type) is INamedTypeSymbol aggContaining
            && TypeClassifier.IsObjectArrayEmulated(aggContaining))
        {
            var layout = _ctx.Aggregates.GetLayout(aggContaining);
            if (layout.TryGetIndex(fieldRef.Field, out var elemIndex))
            {
                var arrExpr = LoadInstanceRaw(fieldRef.Instance);
                var getVal = AggregateAbi.ReadSlot(_builder, arrExpr, elemIndex, StorageTypes.Object);
                // A struct-typed element read AS A VALUE is copied (value semantics); scalar elements are immutable boxes.
                return fieldRef.Field.Type is INamedTypeSymbol elemAgg && TypeClassifier.IsAggregateValue(elemAgg)
                    ? AggregateAbi.DeepClone(_builder, getVal, elemAgg, _ctx.Aggregates.GetLayout) : getVal;
            }
            throw new System.NotSupportedException(
                $"Cannot access '{fieldRef.Field.Name}' on aggregate type '{aggContaining.Name}'.");
        }

        // this.field → direct variable name → LoadField (struct-typed field copied on value read)
        if (fieldRef.Instance is IInstanceReferenceOperation)
            return fieldRef.Field.Type is INamedTypeSymbol thisFieldAgg && TypeClassifier.IsAggregateValue(thisFieldAgg)
                ? AggregateAbi.DeepClone(_builder, LoadField(fieldRef.Field.Name, new StorageType(AggregateAbi.ArrayType)),
                    thisFieldAgg, _ctx.Aggregates.GetLayout)
                : LoadField(fieldRef.Field.Name, GetStorageType(fieldRef.Field.Type));
        // cross-behaviour field → GetProgramVariable
        if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            RejectProgramLocalCrossBehaviourFieldRead(fieldRef.Field);
            var instanceVal = VisitExpression(fieldRef.Instance);
            var nameConst = Const(fieldRef.Field.Name, StorageTypes.String);
            return ExternCall(
                ExternResolver.EventReceiverGetProgramVariable,
                new List<CLeaf> { instanceVal, nameConst },
                StorageTypes.Object);
        }
        // other.field → extern getter (same pattern as VisitPropertyReference)
        {
            var fldType = GetStorageTypeName(fieldRef.Field.Type);
            // B74 fold: an inherited field registers under the receiver's own static type, not its declaring
            // base — route through the shared owner funnel like the property-get/set sites.
            var containingType = GetStorageTypeName(ResolveExternOwnerType(fieldRef.Field.ContainingType, fieldRef.Instance?.Type, fieldRef.Field.Name));
            var instanceVal = VisitExpression(fieldRef.Instance);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
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
        if (eventRef.Instance is not IInstanceReferenceOperation)
            throw new NotSupportedException(
                $"Cannot reference event '{eventRef.Event.Name}' through a non-this receiver; only "
                + "`+=`/`-=` may target another behaviour's event (and cross-behaviour subscribe is "
                + "itself rejected — see the event add/remove diagnostic).");
        return LoadField(eventRef.Event.Name, new StorageType(DelegateAbi.BundleType));
    }

    // ── Conversion ──

    // True when fieldContainingType is _classSymbol or one of its user-defined base classes — i.e. a
    // static readonly field declared there is materialized as a heap var IN THIS PROGRAM (UasmEmitter's
    // static field walk covers the same hierarchy). Design §3.5, Q-S5: any OTHER class has no storage
    // for it here at all, regardless of accessibility.
    static bool IsDeclaredInOwnHierarchy(INamedTypeSymbol classSymbol, INamedTypeSymbol fieldContainingType)
    {
        for (var t = classSymbol; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, fieldContainingType))
                return true;
        return false;
    }

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
    static bool StructurallyContainsDelegate(ITypeSymbol t,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        HashSet<ITypeSymbol> visited)
    {
        t = ResolveTypeParam(t, typeParamMap);
        if (t == null) return false;
        if (t is INamedTypeSymbol n && n.DelegateInvokeMethod != null) return true;
        if (t is IArrayTypeSymbol a) return StructurallyContainsDelegate(a.ElementType, typeParamMap, visited);
        if (t is INamedTypeSymbol agg && TypeClassifier.IsAggregateValue(agg) && visited.Add(agg))
            foreach (var m in agg.GetMembers())
                if (m is IFieldSymbol f && !f.IsStatic
                    && StructurallyContainsDelegate(f.Type, typeParamMap, visited))
                    return true;
        return false;
    }

    static bool ContainsVariantDelegateConversion(ITypeSymbol src, ITypeSymbol dst,
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
            && DelegateAbi.BuildSigPart(srcInvoke, typeParamMap)
               != DelegateAbi.BuildSigPart(dstInvoke, typeParamMap);
    }

    CLeaf VisitConversion(IConversionOperation conv)
    {
        RejectChecked(conv.IsChecked);

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
        bool userClassConversion = conv.OperatorMethod is { MethodKind: MethodKind.Conversion }
            && conv.OperatorMethod.ContainingType is INamedTypeSymbol ucct && TypeClassifier.IsObjectArrayEmulated(ucct);
        if (!userClassConversion
            && ResolveType(conv.Operand.Type) is { } b82Src && ResolveType(conv.Type) is { } b82Dst)
            RejectProgramLocalErasure(conv, b82Src, b82Dst);

        var srcVal = VisitExpression(conv.Operand);

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
        var variantDst = ResolveType(conv.Type);
        if ((variantDst is IArrayTypeSymbol || (variantDst as INamedTypeSymbol)?.IsTupleType == true)
            && ContainsVariantDelegateConversion(conv.Operand.Type, conv.Type, _ctx.Generics.TypeParamMap))
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
            && StructurallyContainsDelegate(variantDst, _ctx.Generics.TypeParamMap, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
        {
            var stripped = conv.Operand;
            while (stripped is IConversionOperation strippedConv) stripped = strippedConv.Operand;
            var isNull = stripped is IDefaultValueOperation
                || (stripped?.ConstantValue.HasValue == true && stripped.ConstantValue.Value == null);
            var srcCarriesDelegate = StructurallyContainsDelegate(ResolveType(stripped?.Type),
                _ctx.Generics.TypeParamMap, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
            if (!isNull && !srcCarriesDelegate)
                throw new System.NotSupportedException(
                    $"Cast from '{(ResolveType(conv.Operand.Type) ?? conv.Operand.Type)?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "object"}' "
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
        var convDstType = ResolveType(conv.Type);
        var convSrcType = ResolveType(conv.Operand.Type);
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
            if (convDstType is INamedTypeSymbol vDst && vDst.DelegateInvokeMethod is { } vDstInvoke
                && convSrcType is INamedTypeSymbol vSrc && vSrc.DelegateInvokeMethod is { } vSrcInvoke
                && !SymbolEqualityComparer.Default.Equals(vDst, vSrc)
                && DelegateAbi.BuildSigPart(vDstInvoke, _ctx.Generics.TypeParamMap)
                   != DelegateAbi.BuildSigPart(vSrcInvoke, _ctx.Generics.TypeParamMap))
            {
                // The wrapper's INNER dispatch must speak srcVal's OWN native protocol — vSrc's Invoke
            // method (sig-T), never vDst's (sig-S): srcVal's DelegateAbi.Method names ITS OWN bridge (under
                // sig-T's conv-var protocol), so staging under sig-S would silently drop values.
                var wrapperName = RegisterWrapperSig(vDstInvoke, vSrcInvoke, _ctx.Generics.TypeParamMap);

                // A null delegate VALUE converts to null (C# semantics: converting null is null) — never
                // wrap it, or `o == null` and invoke-null-guard behavior would both silently diverge from
                // a plain unwrapped null. Guarded at RUNTIME (not just the statically-known-null case
                // below): srcVal may be a variable whose null-ness is unknown at compile time.
                var wrapResultSlot = _ctx.Builder.AllocScratch(new StorageType(DelegateAbi.BundleType));
                var srcNotNull = ExternCall("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
                    new List<CLeaf> { srcVal, Const(null, StorageTypes.Object) }, StorageTypes.Boolean);
                _builder.EmitIf(srcNotNull,
                    _ =>
                    {
                        var wThisType = GetStorageTypeName(_classSymbol);
                        var wrapperBundle = DelegateAbi.EmitBundleMint(_builder, () => LoadField(_ctx.Storage.DeclareThisOnce(new StorageType(wThisType)), new StorageType(wThisType)),
                            Const(wrapperName, StorageTypes.String), FuncRef(wrapperName), srcVal);
                        EmitAssign(wrapResultSlot, wrapperBundle);
                    },
                    _ => EmitAssign(wrapResultSlot, Const(null, new StorageType(DelegateAbi.BundleType))));
                return SlotRef(wrapResultSlot);
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
                // dispatches through the invoke-time target-null guard (LogError+skip), never diverging
                // a channel. Safe passthrough.
                var isNull = stripped is IDefaultValueOperation
                    || (stripped?.ConstantValue.HasValue == true && stripped.ConstantValue.Value == null);
                // A same-signature delegate boxed and unboxed within THIS expression is the trivially
                // safe roundtrip — its channels agree (resolve through the type-param map so a generic
                // operand whose spec is a same-sig delegate still qualifies).
                var safeRoundtrip = ResolveType(stripped?.Type) is INamedTypeSymbol sDlg && sDlg.DelegateInvokeMethod is { } sInvoke
                    && DelegateAbi.BuildSigPart(sInvoke, _ctx.Generics.TypeParamMap)
                       == DelegateAbi.BuildSigPart(lInvoke, _ctx.Generics.TypeParamMap);
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
        // CW18 (producer): a USER-enum side converts as its bare underlying numeric tag (a user enum is STORED
        // as that tag — GetUdonTypeName), so resolve enum facets before the numeric tests. Without this,
        // (E?)intExpr / (int?)enumNbl fell through BOTH arms to the identity passthrough and minted a
        // plain-int-tagged box inside a small-underlying E? — the drifted box whose strict accessor reads
        // HeapTypeMismatch-fault the VM. SDK enums keep their own registered Udon tag and stay on the
        // enum↔numeric arm below.
        static ITypeSymbol NumericFacet(ITypeSymbol t)
            => t is INamedTypeSymbol en && ExternResolver.IsUserEnum(en) ? en.EnumUnderlyingType : t;
        var liftedDstU = EmitPolicy.IsNullableT(conv.Type, out var dstNblU) ? dstNblU : conv.Type;
        var liftedDstN = liftedDstU == null ? null : NumericFacet(liftedDstU);
        if (conv.Conversion.IsNullable
            && EmitPolicy.IsNullableT(conv.Operand.Type, out var liftedSrcU)
            && NumericFacet(liftedSrcU) is { } liftedSrcN && ExternResolver.IsConvertibleNumericType(liftedSrcN)
            && liftedDstN != null && ExternResolver.IsConvertibleNumericType(liftedDstN)
            && !SymbolEqualityComparer.Default.Equals(liftedSrcN, liftedDstN)
            && ExternResolver.GetConvertMethodName(liftedDstN) is { } liftedDstMethod)
        {
            var dstU = GetStorageTypeName(liftedDstN);
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
            return NullableAbi.EmitLiftedNumericConversion(_builder, srcVal, new StorageType(dstU), liftedDstMethod,
                liftedIntToInt, EmitNarrowingConvert);
        }

        // Lifted numeric conversion with a BARE source and a Nullable<numeric> dest (e.g. `(byte?)(intExpr)`):
        // the value is always present, so narrow numerically (C#-unchecked wrap) and let it box into the
        // nullable's SystemObject slot with the right tag, so a later `.Value`'s strict small-int extern reads it.
        if (conv.Conversion.IsNullable
            && conv.Operand.Type != null && NumericFacet(conv.Operand.Type) is { } bareSrcN
            && ExternResolver.IsConvertibleNumericType(bareSrcN)
            && EmitPolicy.IsNullableT(conv.Type, out var bareDstU) && NumericFacet(bareDstU) is { } bareDstN
            && ExternResolver.IsConvertibleNumericType(bareDstN)
            && !SymbolEqualityComparer.Default.Equals(bareSrcN, bareDstN))
        {
            return EmitNarrowingConvert(srcVal, GetStorageTypeName(bareSrcN), GetStorageTypeName(bareDstN));
        }

        // CW20: a HARD cast from a reference-typed source (object / interface / ValueType) to
        // Nullable<T> is C#'s unboxing conversion — it needs a runtime type check of the box, and the
        // `as T?` twin loud-rejects through EmitTypeCheck for exactly this shape (a Nullable box is not
        // runtime-distinguishable). The identity passthrough below instead laundered ANY box into the
        // nullable slot: a mismatched box (C#: InvalidCastException) silently minted a drifted-tag
        // nullable that mis-compares on the tolerant lifted paths and HeapTypeMismatch-faults on the
        // strict accessors. Mirror the as-form's polarity: loud reject. A statically-null operand stays
        // a passthrough (C#: unboxing null into T? is legal and yields null), and a user conversion
        // operator is a real value conversion, not an unbox.
        if (conv.OperatorMethod == null
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
                            StorageTypes.Double);
                    }

                    // Math.Truncate(double) or Math.Truncate(decimal)
                    srcVal = ExternCall(
                        $"SystemMath.__Truncate__{truncType}__{truncType}",
                        new List<CLeaf> { srcVal },
                        new StorageType(truncType));

                    // Convert truncated value → target integer type
                    var dstType = GetStorageTypeName(conv.Type);
                    return ExternCall(
                        $"SystemConvert.__{methodName}__{truncType}__{dstType}",
                        new List<CLeaf> { srcVal },
                        new StorageType(dstType));
                }

                // Non-truncation numeric conversions. Integer→small-int narrowing uses C#-unchecked
                // wrap (EmitNarrowingConvert); widening/other falls back to the plain convert extern.
                var srcType = GetStorageTypeName(conv.Operand.Type);
                var dstType2 = GetStorageTypeName(conv.Type);
                return EmitNarrowingConvert(srcVal, srcType, dstType2);
            }
        }

        // User-defined implicit/explicit conversions (e.g. Vector2→Vector3)
        if (conv.OperatorMethod != null && conv.Operand.Type != null && conv.Type != null && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            // A user STRUCT conversion operator is an emitted method, not an extern: route to it (its containing
            // type is SystemObjectArray-backed, so ResolveConversionExtern would build a non-existent extern).
            if (conv.OperatorMethod.ContainingType is INamedTypeSymbol convOpCt && TypeClassifier.IsObjectArrayEmulated(convOpCt))
                return EmitCallToMethod(ResolveStructMember(conv.OperatorMethod), new List<CLeaf> { srcVal });
            ClassAbi.RejectUserOperator(conv.OperatorMethod);

            var dstType = GetStorageTypeName(conv.Type);
            return ExternCall(
                ExternResolver.ResolveConversionExtern(
                    conv.OperatorMethod, ResolveType(conv.Operand.Type), ResolveType(conv.Type)),
                new List<CLeaf> { srcVal },
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
            var dstType = GetStorageTypeName(conv.Type);
            // Prefer const: avoids COPY type-tag corruption
            var constVal = conv.ConstantValue.HasValue ? conv.ConstantValue
                         : conv.Operand.ConstantValue.HasValue ? conv.Operand.ConstantValue
                         : default;
            if (constVal.HasValue)
                return Const(constVal.Value, new StorageType(dstType));

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
                ? GetStorageTypeName(srcEnum.EnumUnderlyingType)
                : GetStorageTypeName(conv.Operand.Type);
            if (srcUnderlying != dstType)
                return EmitNarrowingConvert(srcVal, srcUnderlying, dstType);
            var tmpSlot = _ctx.Builder.AllocScratch(new StorageType(dstType));
            EmitAssign(tmpSlot, srcVal);
            return SlotRef(tmpSlot);
        }

        // CW2 (CA-v2b-1 design step 4, panel Q1 house deviation): an explicit reference downcast to a v1
        // user class — (Derived)baseVar, (T)objectVar — runs the same typeobj test as `is`/`as`. C# throws
        // InvalidCastException; Udon has no exceptions, so a mismatch is LogError + null, never the former
        // identity passthrough (a sibling-class bundle reinterprets field slots; a shorter base bundle
        // faults far from the cast). An upcast/identity conversion (destination assignable from the
        // resolved source) is an object[]-shared no-op and stays passthrough; a statically-null operand
        // casts to null with no check (C#: casting null never throws).
        if (ResolveType(conv.Type) is INamedTypeSymbol castDst && TypeClassifier.IsUserClass(castDst)
            && !(ResolveType(conv.Operand.Type) is INamedTypeSymbol castSrc && VirtualDispatch.IsAssignable(castSrc, castDst)))
        {
            var castOperand = conv.Operand;
            while (castOperand is IConversionOperation innerCast) castOperand = innerCast.Operand;
            var castsNull = castOperand is IDefaultValueOperation
                || (castOperand?.ConstantValue.HasValue == true && castOperand.ConstantValue.Value == null);
            if (!castsNull)
            {
                var castUdon = GetStorageTypeName(conv.Type);
                var castSlot = _ctx.Builder.AllocScratch(new StorageType(castUdon));
                var castOk = EmitTypeCheck(srcVal, conv.Type);
                _builder.EmitIf(castOk,
                    _ => EmitAssign(castSlot, srcVal),
                    _ =>
                    {
                        EmitExternVoid("UnityEngineDebug.__LogError__SystemObject__SystemVoid",
                            new List<CLeaf> { Const(
                                $"USugar: InvalidCastException — cast to '{castDst.Name}' on a value that is not a '{castDst.Name}' ({_classSymbol.Name}). Returning null.",
                                StorageTypes.String) });
                        EmitAssign(castSlot, Const(null, new StorageType(castUdon)));
                    });
                return SlotRef(castSlot);
            }
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
        ClassAbi.RejectTypeofToken(ResolveType(operand));
        bool stockObjectArray = operand is IArrayTypeSymbol arr
            && arr.Rank == 1
            && arr.ElementType.SpecialType == SpecialType.System_Object;
        if (!stockObjectArray
            && !ExternResolver.IsRuntimeDistinguishable(operand, _ctx.Generics.TypeParamMap)
            && !IsDirectComponentQueryArgument(typeOf))
            throw new NotSupportedException(
                $"typeof('{(ResolveType(operand) ?? operand).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') "
                + "is not supported here: Udon folds it and several distinct types onto one non-injective runtime "
                + "type tag, so once stored its System.Type token compares equal to every same-tag type (==, "
                + ".Equals, object-boxed, or stored-then-compared all silently lie). A collapse-set typeof may "
                + "only be passed directly to a component query (GetComponent(typeof(...))); pass typeof(...) "
                + "directly to the engine call, or use the generic GetComponent<T>() overload.");
        return ConstTypeToken(operand);
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
        var targetUdon = GetStorageTypeName(targetType);
        var resultSlot = _ctx.Builder.AllocScratch(new StorageType(targetUdon));
        var isCheck = EmitTypeCheck(srcVal, targetType);
        _builder.EmitIf(isCheck,
            _ => EmitAssign(resultSlot, srcVal),
            _ => EmitAssign(resultSlot, Const(null, new StorageType(targetUdon))));
        return SlotRef(resultSlot);
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
        if (ResolveType(defaultVal.Type) is INamedTypeSymbol aggDef && TypeClassifier.IsAggregateValue(aggDef))
            return AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout(aggDef),
                _ctx.Aggregates.GetLayout, GetStorageTypeName);

        var dvType = GetStorageTypeName(defaultVal.Type);
        if (!defaultVal.Type.IsValueType)
            return Const(null, new StorageType(dvType));

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
        return Const(defVal, new StorageType(dvType));
    }

    // ── Declaration Expression ──

    CLeaf VisitDeclarationExpression(IDeclarationExpressionOperation declExpr)
    {
        if (declExpr.Expression is not ILocalReferenceOperation localRef2)
            return VisitExpression(declExpr.Expression);

        var udonType = GetStorageTypeName(localRef2.Type);
        // Stage 2 §4.1: a CAPTURED out-var/pattern declaration still needs an ADDRESSABLE heap slot
        // for the writer (env cells have no address) — declare a flat staging field as before, and
        // the consumer that populated it must sync it into the env cell (out-arg copy-back /
        // pattern-binding stores go through AssignToLValue / TryEmitEnvStore arms). Registering the
        // staging field in _localBindings is WRONG for captured symbols (reads would bypass the env),
        // so captured declarations get a staging slot WITHOUT a binding.
        if (_ctx.Closures.TryGetEnvBinding(localRef2.Local, out _))
        {
            var stagingId = _ctx.Storage.DeclareLocal(localRef2.Local.Name, new StorageType(udonType));
            return LoadField(stagingId, new StorageType(udonType));
        }
        var localId = _ctx.Storage.DeclareLocal(localRef2.Local.Name, new StorageType(udonType));
        _localBindings[localRef2.Local] = new EmitContext.LocalBinding(localId);
        return LoadField(localId, new StorageType(udonType));
    }

    // ── Delegate Creation ──

    // The ONLY producer of delegate values (design §2.2): builds the tagged runtime delegate bundle
    // { Kind, Target, Method, Addr, Env }.
    // ResolveDelegateBridge hoists lambdas/local functions and registers their __dlg_ bridge via
    // PendingDelegateBridges (DelegateAbi.Method is the cross-path entry, so the bridge is always emitted).
    // Capture-escape registration is pre-emit analysis (§4.1) — nothing is marked here.
    CLeaf VisitDelegateCreation(IDelegateCreationOperation op)
    {
        var (bridgeName, funcRef, thirdParty, envLeaf) = ResolveDelegateBridge(op);
        // Stage 1.75 §2.2: a variant method-group binding was already resolved (adapter- or
        // wrapper-minted) by ResolveDelegateBridge above — recompute the same sig comparison to tell
        // ValidateDelegateBinding this mismatch is handled, not a reject (its throw stays armor for a
        // mismatch that somehow reaches it unresolved).
        var targetMethodForValidation = (op.Target as IMethodReferenceOperation)?.Method;
        bool varianceResolved = targetMethodForValidation != null
            && op.Type is INamedTypeSymbol vDelegateType && vDelegateType.DelegateInvokeMethod is { } vInvoke
            && DelegateAbi.BuildSigPart(vInvoke, _ctx.Generics.TypeParamMap)
               != DelegateAbi.BuildSigPart(targetMethodForValidation, _ctx.Generics.TypeParamMap);
        DelegateAbi.ValidateDelegateBinding(op.Type as INamedTypeSymbol,
            targetMethodForValidation, _ctx.Generics.TypeParamMap, varianceResolved);

        var thisType = GetStorageTypeName(_classSymbol);
        // Addr discipline (§1.3): the only sources for DelegateAbi.Addr are the back-patched funcaddr const
        // (boxed UInt32) or Const(0u). A third-party method group's local funcaddr is meaningless in the
        // target program, so it carries 0u; a same-this target carries the REAL funcaddr — even when the
        // bundle is later handed cross-Behaviour (the invoke-side target-identity guard is the only gate).
        var addr = thirdParty != null ? (CLeaf)Const(0u, StorageTypes.UInt32) : funcRef;

        // Stage 2 §3.7: DelegateAbi.Env carries the binding-scope env for a CAPTURING closure target, else
        // a null const (capture-free lambda / named method / base.M) = byte-identical to Stage 1.
        var bundle = DelegateAbi.EmitBundleMint(_builder, () => thirdParty ?? LoadField(_ctx.Storage.DeclareThisOnce(new StorageType(thisType)), new StorageType(thisType)),
            Const(bridgeName, StorageTypes.String), addr, envLeaf);

        return bundle;
    }

    // ── Tuple Literal ──

    CLeaf VisitTupleLiteral(ITupleOperation op)
    {
        return AggregateAbi.MintTupleLiteral(_builder, op, VisitExpression);
    }

}
