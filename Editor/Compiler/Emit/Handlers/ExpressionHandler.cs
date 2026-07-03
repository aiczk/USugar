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

    // ── Wave-12 r5 [W1]/[W2]: object-laundered variant delegate casts ──
    // A non-delegate-typed value (object / System.Delegate) cast or `as`-cast to a DELEGATE type
    // erases the source signature from the conversion node, so the [V2] delegate-to-delegate arm
    // never fires and a co/contravariant bundle flows into channels keyed by the DESTINATION
    // signature (VM-proven silent loss: covariant Func ref=2 vs -1, contravariant Action ref=7
    // vs 5). Compile-time cannot see through `object` in general, so the gate is EVIDENCE-BASED:
    // reject only when a statically visible producer of the operand carries a delegate whose sig
    // part diverges from the destination's — nested conversions, ternary/coalesce arms, the
    // operand LOCAL's writes (transitively through other object-typed locals), and, for a
    // PARAMETER operand (incl. a setter's `value`), the class-family-visible call-site arguments /
    // property-write values feeding it. Same-signature roundtrips (the pinned accepted boundary)
    // and unprovable sources (foreign-class callers, fields, array elements) keep flowing; if a
    // future wave reds a laundering shape through an uncovered source, extend the evidence
    // collectors, not the criterion.

    List<IOperation> _launderEvidenceBodies;

    /// <summary>Bodies whose delegate flows are visible to this program: the compiled class's own
    /// member bodies plus its user base chain's (inherited bodies emit into this program).</summary>
    IReadOnlyList<IOperation> ClassFamilyBodies()
    {
        if (_launderEvidenceBodies != null) return _launderEvidenceBodies;
        var list = new List<IOperation>();
        for (var t = _classSymbol; t != null && t.Name != "UdonSharpBehaviour"; t = t.BaseType)
        {
            if (t.DeclaringSyntaxReferences.IsEmpty
                || USugarCompilerHelper.IsFrameworkNamespace(t.ContainingNamespace)) break;
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet
                    or MethodKind.PropertySet or MethodKind.ExplicitInterfaceImplementation)) continue;
                var sr = m.DeclaringSyntaxReferences.Length > 0 ? m.DeclaringSyntaxReferences[0] : null;
                if (sr == null) continue;
                var syntax = sr.GetSyntax();
                var bodyOp = _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
                if (bodyOp != null) list.Add(bodyOp);
            }
        }
        return _launderEvidenceBodies = list;
    }

    static IOperation OperationRoot(IOperation op)
    {
        while (op.Parent != null) op = op.Parent;
        return op;
    }

    static void CollectLocalWrites(IOperation op, ILocalSymbol local, List<IOperation> writes)
    {
        if (op == null) return;
        switch (op)
        {
            case IVariableDeclaratorOperation vd when SymbolEqualityComparer.Default.Equals(vd.Symbol, local):
                if (vd.Initializer?.Value is { } init) writes.Add(init);
                break;
            case ISimpleAssignmentOperation sa when sa.Target is ILocalReferenceOperation t
                && SymbolEqualityComparer.Default.Equals(t.Local, local):
                writes.Add(sa.Value);
                break;
            // Wave-12 r8 [X11]/[X12]: a `??=` local write (`boxed ??= narrow;`) is neither an initializer
            // nor a simple assignment — add the null-coalescing RHS as a writer.
            case ICoalesceAssignmentOperation ca when ca.Target is ILocalReferenceOperation ct
                && SymbolEqualityComparer.Default.Equals(ct.Local, local):
                writes.Add(ca.Value);
                break;
            // Wave-12 r7 [X6]: a tuple-deconstruction declaration/assignment writes the local through
            // neither an initializer nor a simple assignment — pair the target tuple to its value tuple
            // positionally (recursing through nested tuples) and add the matching value element.
            case IDeconstructionAssignmentOperation da:
                MatchDeconstruction(da.Target, da.Value, local, writes);
                break;
        }
        foreach (var child in op.Children)
            CollectLocalWrites(child, local, writes);
    }

    static void MatchDeconstruction(IOperation target, IOperation value, ILocalSymbol local,
        List<IOperation> writes)
    {
        while (target is IConversionOperation tc) target = tc.Operand;
        while (value is IConversionOperation vc) value = vc.Operand;
        if (target is ITupleOperation tt && value is ITupleOperation vt
            && tt.Elements.Length == vt.Elements.Length)
        {
            for (int i = 0; i < tt.Elements.Length; i++)
                MatchDeconstruction(tt.Elements[i], vt.Elements[i], local, writes);
            return;
        }
        var te = target;
        if (te is IDeclarationExpressionOperation de) te = de.Expression;
        if (te is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local))
            writes.Add(value);
    }

    /// <summary>The local/field/parameter storage symbol at the root of a reference expression, or
    /// null for anything else (used to key array-element and array-reference writes).</summary>
    static ISymbol RootStorageSymbol(IOperation op)
    {
        while (op != null)
            switch (op)
            {
                case IConversionOperation c: op = c.Operand; break;
                case ILocalReferenceOperation l: return l.Local;
                case IFieldReferenceOperation f: return f.Field;
                case IParameterReferenceOperation p: return p.Parameter;
                default: return null;
            }
        return null;
    }

    static void CollectFieldWrites(IOperation op, IFieldSymbol field, List<IOperation> writes)
    {
        if (op == null) return;
        if (op is ISimpleAssignmentOperation sa && sa.Target is IFieldReferenceOperation t
            && SymbolEqualityComparer.Default.Equals(t.Field.OriginalDefinition, field.OriginalDefinition))
            writes.Add(sa.Value);
        // Wave-12 r8 [X13]: a `??=` FIELD write (`_stash ??= narrow;`) is invisible to the simple-
        // assignment check — add the null-coalescing RHS.
        else if (op is ICoalesceAssignmentOperation ca && ca.Target is IFieldReferenceOperation ct
            && SymbolEqualityComparer.Default.Equals(ct.Field.OriginalDefinition, field.OriginalDefinition))
            writes.Add(ca.Value);
        foreach (var child in op.Children)
            CollectFieldWrites(child, field, writes);
    }

    static void CollectArrayElementWrites(IOperation op, ISymbol arraySym, List<IOperation> writes)
    {
        if (op == null) return;
        switch (op)
        {
            case ISimpleAssignmentOperation sa when sa.Target is IArrayElementReferenceOperation t
                && SymbolEqualityComparer.Default.Equals(RootStorageSymbol(t.ArrayReference), arraySym):
                writes.Add(sa.Value);
                break;
            // Wave-12 r8 [X14]: a `??=` ARRAY-ELEMENT write (`arr[0] ??= narrow;`) is invisible to the
            // simple-assignment check — add the null-coalescing RHS.
            case ICoalesceAssignmentOperation ca when ca.Target is IArrayElementReferenceOperation ct
                && SymbolEqualityComparer.Default.Equals(RootStorageSymbol(ct.ArrayReference), arraySym):
                writes.Add(ca.Value);
                break;
            // Wave-12 r8 [X1]/[X6]: an array-creation-WITH-INITIALIZER element (`var arr = new object[]{
            // narrow };`) writes element 0 through the initializer, not an indexer assignment. Match the
            // creation to the array local via its declarator and add every initializer element.
            case IVariableDeclaratorOperation vd
                when SymbolEqualityComparer.Default.Equals(vd.Symbol, arraySym):
                AddArrayInitializerElements(vd.Initializer?.Value, writes);
                break;
            // Wave-12 r8 [X4]/[X5]: a user `params object[]` call site (`Boxed(narrow)`) desugars to a
            // synthesized array-creation passed as the params argument — its elements are invisible to
            // any declarator/indexer match. When the traced array symbol is the params parameter, add the
            // creation elements of every class-family call-site argument bound to it.
            case IArgumentOperation arg when arg.Parameter is { IsParams: true } pp
                && SymbolEqualityComparer.Default.Equals(pp.OriginalDefinition, arraySym.OriginalDefinition):
                AddArrayInitializerElements(arg.Value, writes);
                break;
        }
        foreach (var child in op.Children)
            CollectArrayElementWrites(child, arraySym, writes);
    }

    /// <summary>Adds each element value of an array-creation-with-initializer (unwrapping conversions
    /// around the creation) to <paramref name="writes"/>; a no-op for anything else.</summary>
    static void AddArrayInitializerElements(IOperation value, List<IOperation> writes)
    {
        while (value is IConversionOperation c) value = c.Operand;
        if (value is IArrayCreationOperation ac && ac.Initializer is { } init)
            foreach (var e in init.ElementValues) writes.Add(e);
    }

    static void CollectOutRefArgs(IOperation op, ILocalSymbol local, List<IArgumentOperation> args)
    {
        if (op == null) return;
        if (op is IArgumentOperation arg && arg.Parameter is { RefKind: RefKind.Out or RefKind.Ref })
        {
            var v = arg.Value;
            if (v is IDeclarationExpressionOperation de) v = de.Expression;
            if (v is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local))
                args.Add(arg);
        }
        foreach (var child in op.Children)
            CollectOutRefArgs(child, local, args);
    }

    static void CollectParamAssignments(IOperation op, IParameterSymbol param, List<IOperation> values)
    {
        if (op == null) return;
        if (op is ISimpleAssignmentOperation sa && sa.Target is IParameterReferenceOperation pr
            && SymbolEqualityComparer.Default.Equals(pr.Parameter, param))
            values.Add(sa.Value);
        foreach (var child in op.Children)
            CollectParamAssignments(child, param, values);
    }

    static void CollectParamEvidence(IOperation op, IMethodSymbol method, IParameterSymbol param,
        IPropertySymbol setterProp, List<IOperation> values)
    {
        if (op == null) return;
        if (setterProp != null)
        {
            if (op is ISimpleAssignmentOperation sa && sa.Target is IPropertyReferenceOperation pref
                && SymbolEqualityComparer.Default.Equals(pref.Property.OriginalDefinition, setterProp.OriginalDefinition))
                values.Add(sa.Value);
        }
        else if (op is IInvocationOperation inv
            && SymbolEqualityComparer.Default.Equals(inv.TargetMethod.OriginalDefinition, method.OriginalDefinition))
        {
            foreach (var a in inv.Arguments)
                if (a.Parameter != null && a.Parameter.Ordinal == param.Ordinal) { values.Add(a.Value); break; }
        }
        foreach (var child in op.Children)
            CollectParamEvidence(child, method, param, setterProp, values);
    }

    /// <summary>The first statically visible delegate-typed producer of <paramref name="val"/> whose
    /// sig part diverges from <paramref name="dstSig"/>, or null when every visible producer agrees
    /// (or nothing is visible).</summary>
    ITypeSymbol DivergingDelegateEvidence(IOperation val, string dstSig, HashSet<ISymbol> visited)
    {
        while (val is IConversionOperation c) val = c.Operand;
        if (val == null) return null;
        switch (val)
        {
            case IConditionalOperation cond:
                return DivergingDelegateEvidence(cond.WhenTrue, dstSig, visited)
                    ?? DivergingDelegateEvidence(cond.WhenFalse, dstSig, visited);
            case ICoalesceOperation coal:
                return DivergingDelegateEvidence(coal.Value, dstSig, visited)
                    ?? DivergingDelegateEvidence(coal.WhenNull, dstSig, visited);
            case ISwitchExpressionOperation swx:
                foreach (var arm in swx.Arms)
                    if (DivergingDelegateEvidence(arm.Value, dstSig, visited) is { } t) return t;
                return null;
            // Wave-12 r8 [X7]-[X10]: a null-conditional producer (`object boxed = other?.BoxedNarrow;`
            // or `other?.GetBoxed()`) wraps the real member access in an IConditionalAccessOperation the
            // top unwrap loop never strips — trace the .WhenNotNull member (property getter / call).
            case IConditionalAccessOperation cacc:
                return DivergingDelegateEvidence(cacc.WhenNotNull, dstSig, visited);
        }
        if (val.Type is INamedTypeSymbol dlg && dlg.DelegateInvokeMethod is { } invoke)
            return DelegateAbi.BuildSigPart(invoke, _ctx.TypeParamMap) != dstSig ? val.Type : null;
        if (val is ILocalReferenceOperation lr && lr.Local != null && visited.Add(lr.Local))
        {
            var root = OperationRoot(val);
            var writes = new List<IOperation>();
            CollectLocalWrites(root, lr.Local, writes);
            foreach (var w in writes)
                if (DivergingDelegateEvidence(w, dstSig, visited) is { } t) return t;
            // Wave-12 r7 [X5]/[X7]: the local written through an out/ref argument — the producing
            // value is whatever the callee assigns to its by-ref parameter, invisible at the call
            // site. Trace the callee body's assignments to that parameter.
            var refArgs = new List<IArgumentOperation>();
            CollectOutRefArgs(root, lr.Local, refArgs);
            foreach (var arg in refArgs)
                if (arg.Parameter is { } rp && rp.ContainingSymbol is IMethodSymbol rm
                    && visited.Add(rp) && MethodBody(rm) is { } rbody)
                {
                    var pvals = new List<IOperation>();
                    CollectParamAssignments(rbody, rp, pvals);
                    foreach (var v in pvals)
                        if (DivergingDelegateEvidence(v, dstSig, visited) is { } t) return t;
                }
        }
        if (val is IParameterReferenceOperation pr && pr.Parameter != null && visited.Add(pr.Parameter)
            && pr.Parameter.ContainingSymbol is IMethodSymbol pm)
        {
            var setterProp = pm.MethodKind == MethodKind.PropertySet
                             && pr.Parameter.Ordinal == pm.Parameters.Length - 1
                ? pm.AssociatedSymbol as IPropertySymbol : null;
            if (setterProp != null || pm.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet
                or MethodKind.PropertySet or MethodKind.LocalFunction)
            {
                var values = new List<IOperation>();
                foreach (var body in ClassFamilyBodies())
                    CollectParamEvidence(body, pm, pr.Parameter, setterProp, values);
                foreach (var v in values)
                    if (DivergingDelegateEvidence(v, dstSig, visited) is { } t) return t;
            }
        }
        // Wave-12 r7 [X2]: a get-only PROPERTY whose getter returns the object-boxed producer
        // (`object boxed = BoxedNarrow;`) — trace the visible getter's return expressions, exactly
        // like the method-call branch below. Auto-property / metadata getters have no visible body
        // and keep flowing.
        if (val is IPropertyReferenceOperation propRef && propRef.Property?.GetMethod is { } getter
            && visited.Add(getter.OriginalDefinition) && MethodBody(getter) is { } getBody)
        {
            var returns = new List<IOperation>();
            CollectReturns(getBody, returns);
            foreach (var r in returns)
                if (DivergingDelegateEvidence(r, dstSig, visited) is { } t) return t;
        }
        // Wave-12 r7 [X3]: a FIELD holding the object-boxed producer (`_stash = narrow; object boxed
        // = _stash;`) — trace class-family assignments to the field.
        if (val is IFieldReferenceOperation fref && fref.Field != null
            && visited.Add(fref.Field.OriginalDefinition))
        {
            var writes = new List<IOperation>();
            foreach (var body in ClassFamilyBodies())
                CollectFieldWrites(body, fref.Field, writes);
            foreach (var w in writes)
                if (DivergingDelegateEvidence(w, dstSig, visited) is { } t) return t;
        }
        // Wave-12 r7 [X4]: an ARRAY ELEMENT holding the object-boxed producer (`arr[0] = narrow;
        // object boxed = arr[0];`) — trace class-family element assignments to the same array storage
        // symbol. Index-insensitive: any diverging element write is evidence (a same-sig control keeps
        // flowing because every element write agrees).
        if (val is IArrayElementReferenceOperation aref
            && RootStorageSymbol(aref.ArrayReference) is { } arrSym && visited.Add(arrSym))
        {
            var writes = new List<IOperation>();
            foreach (var body in ClassFamilyBodies())
                CollectArrayElementWrites(body, arrSym, writes);
            foreach (var w in writes)
                if (DivergingDelegateEvidence(w, dstSig, visited) is { } t) return t;
        }
        // Wave-12 r6 [X5]: a same-class method CALL whose return value is the object-boxed producer
        // (`object boxed = Identity(narrow);`) — trace into the callee's return expressions. A return
        // that yields a parameter is picked up by the IParameterReferenceOperation branch above (which
        // maps it back to this and other class-family call-site arguments), so an identity-like helper
        // no longer opaquely defeats the reject. Only user methods with a visible body are followed;
        // framework calls and body-less methods yield nothing and keep flowing.
        if (val is IInvocationOperation inv && inv.TargetMethod != null
            && visited.Add(inv.TargetMethod.OriginalDefinition)
            && MethodBody(inv.TargetMethod) is { } invBody)
        {
            // Wave-12 r7 [X1]: a LOCAL FUNCTION target's body op IS the ILocalFunctionOperation node;
            // descend into its .Body so CollectReturns' nested-function guard doesn't bail on the very
            // body we came to inspect (an instance-method target's body op is not a local function, so
            // this is a no-op for the r6 case).
            var scanBody = invBody is ILocalFunctionOperation invLf ? (IOperation)invLf.Body : invBody;
            var returns = new List<IOperation>();
            CollectReturns(scanBody, returns);
            foreach (var r in returns)
                if (DivergingDelegateEvidence(r, dstSig, visited) is { } t) return t;
        }
        return null;
    }

    /// <summary>The bound body operation of <paramref name="method"/> when it is declared in source and
    /// visible to this compile, else null (framework / metadata-only methods).</summary>
    IOperation MethodBody(IMethodSymbol method)
    {
        var def = method.OriginalDefinition;
        var sr = def.DeclaringSyntaxReferences.Length > 0 ? def.DeclaringSyntaxReferences[0] : null;
        if (sr == null) return null;
        var syntax = sr.GetSyntax();
        return _compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
    }

    static void CollectReturns(IOperation op, List<IOperation> returns)
    {
        if (op == null) return;
        switch (op)
        {
            case IAnonymousFunctionOperation _:
            case ILocalFunctionOperation _:
                return; // a nested function's returns are its own, not the enclosing method's
            case IReturnOperation ret when ret.ReturnedValue != null:
                returns.Add(ret.ReturnedValue);
                break;
        }
        foreach (var child in op.Children)
            CollectReturns(child, returns);
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
        if ((conv.Type is INamedTypeSymbol dlgDst && dlgDst.DelegateInvokeMethod != null)
            || (conv.Operand.Type is INamedTypeSymbol dlgSrc && dlgSrc.DelegateInvokeMethod != null))
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
            if (conv.Type is INamedTypeSymbol vDst && vDst.DelegateInvokeMethod is { } vDstInvoke
                && conv.Operand.Type is INamedTypeSymbol vSrc && vSrc.DelegateInvokeMethod is { } vSrcInvoke
                && !SymbolEqualityComparer.Default.Equals(vDst, vSrc)
                && DelegateAbi.BuildSigPart(vDstInvoke, _ctx.TypeParamMap)
                   != DelegateAbi.BuildSigPart(vSrcInvoke, _ctx.TypeParamMap))
                throw new System.NotSupportedException(
                    $"Variant delegate conversion from '{vSrc.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + $"to '{vDst.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                    + "the delegate calling convention keys its argument/return channels by the exact "
                    + "signature, so a co/contravariant binding silently drops values across the "
                    + "dispatch. Use matching delegate type parameters.");

            // Wave-12 r5 [W1]/[W2]: a NON-delegate-typed operand (object / System.Delegate box) cast
            // to a delegate type — evidence-based reject when a statically visible producer of the
            // operand carries a diverging sig (see the collectors above). Same-sig roundtrips and
            // evidence-free casts keep the reference passthrough.
            if (conv.Type is INamedTypeSymbol lDst && lDst.DelegateInvokeMethod is { } lInvoke
                && !(conv.Operand.Type is INamedTypeSymbol opDlg && opDlg.DelegateInvokeMethod != null)
                && DivergingDelegateEvidence(conv.Operand,
                       DelegateAbi.BuildSigPart(lInvoke, _ctx.TypeParamMap),
                       new HashSet<ISymbol>(SymbolEqualityComparer.Default)) is { } launderSrc)
                throw new System.NotSupportedException(
                    $"Variant delegate conversion from '{launderSrc.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                    + $"to '{lDst.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported "
                    + $"(laundered through '{conv.Operand.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "object"}'): "
                    + "the delegate calling convention keys its argument/return channels by the exact "
                    + "signature, so a co/contravariant binding silently drops values across the "
                    + "dispatch. Use matching delegate type parameters.");
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
