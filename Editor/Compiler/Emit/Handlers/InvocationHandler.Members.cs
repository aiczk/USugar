using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler
{
    // ── Property Reference ──

    CLeaf VisitPropertyReference(IPropertyReferenceOperation op)
    {
        // Indexer access: Type.__get_Item__IndexTypes__ReturnType
        if (op.Property.IsIndexer)
            return VisitIndexerGet(op);

        // Nullable<T> (boxed-object emulation): HasValue → null check; Value → the boxed value itself
        // (Udon unboxes transparently when the result is used as the underlying type).
        if (op.Instance != null && EmitPolicy.IsNullableT(op.Property.ContainingType, out var nblUnder))
        {
            var nv = VisitExpression(op.Instance);
            if (op.Property.Name == "HasValue") return NullableAbi.HasValue(_builder, nv);
            // Value of a nullable AGGREGATE (e.g. (int,int)? / V?) copies the struct out (value semantics).
            if (op.Property.Name == "Value")
                return nblUnder is INamedTypeSymbol nblAgg && EmitPolicy.IsAggregateType(nblAgg)
                    ? AggregateAbi.DeepClone(_builder, nv, nblAgg, _ctx.Aggregates.GetLayout) : nv;
        }

        // Auto-property on an aggregate (struct/tuple) OR v1 class → object[] element (the backing field's
        // slot). The clone at the return stays IsAggregateType, so a class-typed property returns by reference.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggProp && EmitPolicy.IsObjectArrayEmulated(aggProp)
            && _ctx.Aggregates.GetLayout(aggProp).TryGetIndex(op.Property.Name, out var aggPropIdx))
        {
            var arrExpr = LoadInstanceRaw(op.Instance);
            var getVal = AggregateAbi.ReadSlot(_builder, arrExpr, aggPropIdx, "SystemObject");
            // A struct-typed property returns a COPY (C# getters return by value; you cannot mutate through it).
            return op.Property.Type is INamedTypeSymbol propAgg && EmitPolicy.IsAggregateType(propAgg)
                ? AggregateAbi.DeepClone(_builder, getVal, propAgg, _ctx.Aggregates.GetLayout) : getVal;
        }

        // Computed (non-auto) property on an aggregate (struct) OR v1 class: no backing-field slot, so
        // inline-call the user getter with the receiver object[] as synthetic param0 (same convention as
        // EmitStructInstanceCall). The getter only reads, so the receiver is passed uncloned. The return
        // clone stays IsAggregateType, so a class-typed getter result is returned by reference.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggGet && EmitPolicy.IsObjectArrayEmulated(aggGet)
            && op.Property.GetMethod is { } aggGetterRaw)
        {
            var ret = EmitCallToMethod(ResolveStructMember(aggGetterRaw),
                new List<CLeaf> { LoadInstanceRaw(op.Instance) });
            return op.Property.Type is INamedTypeSymbol getRetAgg && EmitPolicy.IsAggregateType(getRetAgg)
                ? AggregateAbi.DeepClone(_builder, ret, getRetAgg, _ctx.Aggregates.GetLayout) : ret;
        }

        // this.gameObject / this.transform → __this_* variable (Udon VM resolves via "this" default)
        if (op.Instance is IInstanceReferenceOperation)
        {
            // Virtual dispatch through `this` (round 7): a read inside an inherited base body binds the
            // BASE accessor — resolve to the chain-leaf override; `base.P` keeps the static binding.
            var thisProp = ResolveDispatchProperty(op);

            // User-defined property getter → internal call. A struct-typed getter result is COPIED (C#
            // getters return by value) — otherwise `read = this.Prop` aliases the backing field. (diff-fuzz w4)
            if (thisProp.GetMethod != null
                && _methodFunctions.ContainsKey(thisProp.GetMethod))
            {
                var gv = EmitCallToMethod(thisProp.GetMethod, new List<CLeaf>());
                return thisProp.Type is INamedTypeSymbol thisGetAgg && EmitPolicy.IsAggregateType(thisGetAgg)
                    ? AggregateAbi.DeepClone(_builder, gv, thisGetAgg, _ctx.Aggregates.GetLayout) : gv;
            }

            // Auto-property on this class → direct backing-field access (user-defined classes only). A
            // struct-typed backing field is COPIED on read (value semantics), same as a struct field.
            if (thisProp.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                && ExternResolver.IsUdonSharpBehaviour(thisProp.ContainingType)
                && thisProp.ContainingType.Name != "UdonSharpBehaviour")
            {
                var bv = LoadField(thisProp.Name, GetUdonType(thisProp.Type));
                return thisProp.Type is INamedTypeSymbol thisAutoAgg && EmitPolicy.IsAggregateType(thisAutoAgg)
                    ? AggregateAbi.DeepClone(_builder, bv, thisAutoAgg, _ctx.Aggregates.GetLayout) : bv;
            }

            var propName = op.Property.Name;
            if (propName == "gameObject" || propName == "transform")
            {
                var propType = GetUdonType(op.Property.Type);
                return LoadField(_ctx.Storage.DeclareThisOnce(propType), propType);
            }
            // Other this.property → extern getter with this instance
            var thisType = GetUdonType(_classSymbol);
            var thisVal = LoadField(_ctx.Storage.DeclareThisOnce(thisType), thisType);
            var cType = GetUdonType(ResolveExternOwnerType(op.Property.ContainingType, op.Instance?.Type, op.Property.Name));
            var rType = GetUdonType(op.Property.Type);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(cType, propName, rType),
                new List<CLeaf> { thisVal },
                rType);
        }

        var containingType = GetUdonType(op.Property.ContainingType);
        var returnType = GetUdonType(op.Property.Type);

        // Static property: no instance
        if (op.Instance == null)
        {
            ClassAbi.RejectStaticProperty(op.Property);

            // B47 (wave-14 r6): a STATIC COMPUTED property on a user struct/class (StaticPropHelper<T>.Doubled)
            // is a foreign-static accessor CALL, not a real extern — inline-call its getter (the B46
            // static-method pattern, one node kind over). Without this the fall-through emits a bogus
            // SystemObjectArray.__get_Doubled__ extern. The getter is pre-registered when its call site was
            // reachable with a CLOSED containing type (collection layer); a closed spec first seen at a
            // generic call site inside a struct/generic body is registered on demand here (mirrors the
            // foreign-static-on-generic method arm). Auto/BCL/const-foldable statics are excluded: the
            // const-fold arm below owns the BCL foldables, and IsUserComputedStaticProperty gates out autos.
            if (op.Property.IsStatic && op.Property.GetMethod is { } sPropGetter
                && IsForeignStatic(sPropGetter) && IsUserComputedStaticProperty(op.Property))
            {
                // ResolveStructMember substitutes the containing type's type args from the current map
                // (SP<T>.get_Doubled → SP<int>.get_Doubled inside a Box<int> spec) and registers the closed
                // spec on demand; an already-closed getter (a class-body call site) is returned unchanged
                // and was pre-registered by the collection layer.
                var sgv = EmitCallToMethod(ResolveStructMember(sPropGetter), new List<CLeaf>());
                return op.Property.Type is INamedTypeSymbol sgAgg && EmitPolicy.IsAggregateType(sgAgg)
                    ? AggregateAbi.DeepClone(_builder, sgv, sgAgg, _ctx.Aggregates.GetLayout) : sgv;
            }

            // Constant folding: static properties on foldable struct types (e.g., Vector3.zero)
            if (op.Property.IsStatic && ConstFoldableStructTypes.Contains(containingType))
            {
                var value = TryGetStaticPropertyValue(containingType, op.Property.Name);
                if (value != null)
                    return LoadField(_ctx.Storage.DeclareStructConst(returnType, value), returnType);
            }

            // Armor: a user-struct static property reaching here (the B47 on-demand arm above did not
            // register its accessor) would mint a bogus SystemObjectArray.__get_<Name>__ extern — fail
            // with a diagnosis instead (collector-scope drift, roadmap B47 family).
            GuardUserStructMemberReachedExtern(op.Property.ContainingType, op.Property.Name);

            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, op.Property.Name, returnType),
                new List<CLeaf>(),
                returnType);
        }

        // Cross-behaviour property get
        if (op.Instance != null && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
            && !(op.Instance is IInstanceReferenceOperation))
        {
            var instanceVal = VisitExpression(op.Instance);
            // Wave-12 [V2]: non-public autos read the declared backing symbol directly (their
            // accessors are never exported); see IsNonPublicAutoCrossProperty.
            var isAuto = IsNonPublicAutoCrossProperty(op.Property.GetMethod, op.Property);

            if (isAuto)
            {
                // Auto-property: direct GetProgramVariable("PropertyName")
                var nameConst = Const(op.Property.Name, "SystemString");
                return ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<CLeaf> { instanceVal, nameConst },
                    returnType);
            }
            else
            {
                // Non-auto property getter: a single-return cross-behaviour call. CrossCall binds it to a
                // scratch slot at this point (A-normal form), so the SendCustomEvent fires exactly once in
                // program order — inside the branch block when this getter is a ternary arm.
                RejectNonPublicCrossAccessor(op.Property.GetMethod, op.Property); // wave-12 [V2]
                var (getExportName, _, getRetId) = GetCalleeLayout(op.Property.GetMethod);
                var getReturns = getRetId != null
                    ? new[] { new ReturnSlot(getRetId, returnType) }
                    : System.Array.Empty<ReturnSlot>();
                return CrossCall(instanceVal, getExportName,
                    new List<(string, CLeaf)>(), getReturns, returnType,
                    TryMarkReentrantCrossDispatch(op, op.Property.GetMethod)); // wave-12 r2 [V1]
            }
        }

        // Interface property get → dispatch the getter through its interface bridge (SendCustomEvent),
        // like an interface method call. Without this, GetUdonType(interface) yields IUdonEventReceiver and
        // the fall-through emits a non-existent __get_Value extern on it.
        if (op.Property.GetMethod is { } ifaceGetter
            && op.Property.ContainingType.TypeKind == TypeKind.Interface
            && op.Property.ContainingType.SpecialType == SpecialType.None
            && !IsResolvedConcreteNonBehaviour(op.Instance.Type)
            && _planner.GetLayout(op.Property.ContainingType).Methods.TryGetValue(ifaceGetter, out var ifaceGetterMl))
        {
            GuardInterfaceHasBehaviourImplementor(op.Property.ContainingType, op.Property.Name);
            var ifaceInst = VisitExpression(op.Instance);
            return CrossCall(ifaceInst, LayoutPlanner.InterfaceDispatchName(ifaceGetter, ifaceGetterMl),
                new List<(string, CLeaf)>(), ifaceGetterMl.Returns.ToArray(), returnType,
                TryMarkReentrantCrossDispatch(op, ifaceGetter)); // wave-12 r2 [V1]
        }

        // N-dim array (design 2026-07-04 §2/N-R4): Rank>1 array VALUE is an object[] bundle whose Udon
        // type tag (SystemObjectArray) happens to have REAL, valid Rank/Length externs registered — MUST
        // intercept before the generic extern-getter path below, or ".Rank"/".Length" would silently
        // read the bundle wrapper's own shape (rank always 1, length always 1+r) instead of the logical
        // array's. Length reads the FLAT BACKING's length (§2); Rank is a compile-time constant.
        if (op.Instance != null && NdimArrayAbi.IsNdimArray(op.Instance.Type))
        {
            var ndimPropType = (IArrayTypeSymbol)op.Instance.Type;
            if (!NdimArrayAbi.TryGetProperty(op.Property.Name, out var propertyKind))
            {
                NdimArrayAbi.RejectMember(op.Property.Name);
                return null; // unreachable
            }
            switch (propertyKind)
            {
                case NdimArrayAbi.PropertyKind.Length: return EmitNdimLength(VisitExpression(op.Instance), ndimPropType);
                case NdimArrayAbi.PropertyKind.Rank: return EmitNdimRank(ndimPropType);
                default: throw new System.InvalidOperationException($"Unknown N-dim array property kind: {propertyKind}");
            }
        }

        // Other instance.property → extern getter
        var instVal = VisitExpression(op.Instance);
        // Array .Length → use SystemArray (not the concrete array type) to match UdonSharp
        if (op.Instance.Type is IArrayTypeSymbol && op.Property.Name != "Length")
            containingType = GetUdonType((IArrayTypeSymbol)op.Instance.Type);
        // An inherited member (incl. Behaviour/MonoBehaviour/BCL-base) registers under the RECEIVER's
        // static type, not the declaring base — resolve through the shared owner choke point (B55/B59).
        // Array receivers keep the fixup above (SystemArray for .Length, element-typed otherwise).
        else if (op.Instance.Type is not IArrayTypeSymbol)
            containingType = GetUdonType(ResolveExternOwnerType(op.Property.ContainingType, op.Instance.Type, op.Property.Name));
        var sig = ExternResolver.BuildPropertyGetSignature(containingType, op.Property.Name, returnType);
        return ExternCall(sig, new List<CLeaf> { instVal }, returnType);
    }

    // ── Indexer Get ──

    CLeaf VisitIndexerGet(IPropertyReferenceOperation op)
    {
        // User-defined indexer on this/base class → internal getter call (`this[i]` reads this-fields
        // directly). ResolveDispatchProperty (round 7): `this[i]` inside an inherited base body binds
        // the BASE indexer — dispatch the chain-leaf override; `base[i]` keeps the static binding.
        // B48: a struct's `this[i]` (implicit-`this` receiver whose type is a user struct) must NOT take
        // this class-receiver arm — the struct getter is a CFunction expecting the receiver object[] as
        // param0, and calling it with only the index args slots the index into the receiver slot
        // (HeapTypeMismatch 'Int32' as 'Object[]' inside the getter's field read). It falls through to
        // the struct arm below, which passes LoadInstanceRaw(this) = the receiver param — mirroring how
        // a struct's `this.Method()` self-call routes through EmitStructInstanceCall (struct-first).
        if (op.Instance is IInstanceReferenceOperation
            && !EmitPolicy.IsAggregateType(op.Property.ContainingType)
            && ResolveDispatchProperty(op) is { GetMethod: { } idxDispatchGetter }
            && _methodFunctions.ContainsKey(idxDispatchGetter))
        {
            // Wave-9 round-4: index args slotted by parameter ordinal (named/reordered index args
            // bind by name) — the textual foreach bound `this[col: 2, row: 1]` positionally.
            return EmitCallToMethod(idxDispatchGetter, EvaluateIndexerArgs(op));
        }

        // User-defined indexer on a user STRUCT or v1-class instance (`s[i]`) → call the getter with the
        // receiver (object[]) as param0 plus the index args, like a computed property. Without this it falls
        // to a bogus SystemObjectArray.__get_Item extern the validator rejects. (diff-fuzz wave 4) The return
        // clone stays IsAggregateType, so a class-typed indexer result is returned by reference.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggIdx && EmitPolicy.IsObjectArrayEmulated(aggIdx)
            && op.Property.GetMethod is { } idxGetterRaw)
        {
            var sargs = new List<CLeaf> { LoadInstanceRaw(op.Instance) };
            sargs.AddRange(EvaluateIndexerArgs(op)); // wave-9 round-4: named index args bind by ordinal
            var ret = EmitCallToMethod(ResolveStructMember(idxGetterRaw), sargs);
            return op.Property.Type is INamedTypeSymbol idxRetAgg && EmitPolicy.IsAggregateType(idxRetAgg)
                ? AggregateAbi.DeepClone(_builder, ret, idxRetAgg, _ctx.Aggregates.GetLayout) : ret;
        }

        // Wave-9 round-2 [W6]: user indexer read through a VARIABLE receiver (own-typed copy /
        // base-typed / another behaviour) → cross-program getter dispatch (see EmitCrossIndexerCall).
        // Pre-fix this fell through to the extern arm below and emitted a nonexistent
        // IUdonEventReceiver.__get_Item the validator/assembler crashes on.
        if (IsVariableReceiverBehaviourIndexer(op) && op.Property.GetMethod is { } recvIdxGetter)
        {
            var recvVal = VisitExpression(op.Instance);
            return EmitCrossIndexerCall(recvIdxGetter, recvVal, EvaluateIndexerArgs(op),
                TryMarkReentrantCrossDispatch(op, recvIdxGetter)); // wave-12 r2 [V1]
        }

        // Wave-9 round-4 [X4]/[X9]: user indexer read through an INTERFACE-typed receiver → dispatch
        // the getter through its interface bridge, like an interface method/property. The [W6] gate
        // tests IsUdonSharpBehaviour(ContainingType), which is the INTERFACE here, so this fell
        // through to extern resolution and emitted a nonexistent IUdonEventReceiver.__get_Item the
        // validator crashes on (loud crash on legal C#).
        if (TryGetInterfaceAccessorLayout(op, op.Property.GetMethod, out var ifaceIdxGetMl))
        {
            var ifaceIdxInst = VisitExpression(op.Instance);
            return EmitInterfaceAccessorCall(op.Property.GetMethod, ifaceIdxGetMl, ifaceIdxInst,
                EvaluateIndexerArgs(op),
                TryMarkReentrantCrossDispatch(op, op.Property.GetMethod)); // wave-12 r2 [V1]
        }

        var cType = GetUdonType(ResolveExternOwnerType(op.Property.ContainingType, op.Instance?.Type, op.Property.Name));
        var rType = GetUdonType(op.Property.Type);

        // string[i] → str.ToCharArray(i, 1)[0]
        // Udon VM has no string indexer; mirror UdonSharp's BoundStringAccessExpression
        if (cType == "SystemString")
        {
            CLeaf inst = op.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.Storage.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol))
                : VisitExpression(op.Instance);
            var indexVal = VisitExpression(op.Arguments[0].Value);
            var oneConst = Const(1, "SystemInt32");
            var charArr = ExternCall(
                "SystemString.__ToCharArray__SystemInt32_SystemInt32__SystemCharArray",
                new List<CLeaf> { inst, indexVal, oneConst },
                "SystemCharArray");
            var zeroConst = Const(0, "SystemInt32");
            return ExternCall(
                ExternResolver.BuildArrayGetSignature("SystemCharArray", "SystemChar"),
                new List<CLeaf> { charArr, zeroConst },
                "SystemChar");
        }

        CLeaf instVal;
        if (op.Instance is IInstanceReferenceOperation)
            instVal = LoadField(_ctx.Storage.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
        else
            instVal = VisitExpression(op.Instance);

        var externArgs = new List<CLeaf>();
        externArgs.Add(instVal);
        var idxTypes = new List<string>();
        foreach (var arg in op.Arguments)
        {
            externArgs.Add(VisitExpression(arg.Value));
            idxTypes.Add(GetUdonType(arg.Value.Type));
        }
        // Use the indexer's metadata name, not a hardcoded "Item": most indexers are "Item", but a type with
        // [IndexerName(...)] differs (e.g. StringBuilder's indexer is "Chars" → __get_Chars__, not __get_Item__).
        return ExternCall(
            $"{cType}.__get_{op.Property.MetadataName}__{string.Join("_", idxTypes)}__{rType}",
            externArgs,
            rType);
    }

    // ── Interpolated String ──

    CLeaf VisitInterpolatedString(IInterpolatedStringOperation op)
    {
        var formatParts = new List<string>();
        var argVals = new List<CLeaf>();
        int argIndex = 0;

        foreach (var part in op.Parts)
        {
            switch (part)
            {
                case IInterpolatedStringTextOperation text:
                    if (text.Text is ILiteralOperation lit && lit.ConstantValue.HasValue)
                        formatParts.Add(lit.ConstantValue.Value?.ToString() ?? "");
                    break;
                case IInterpolationOperation interpolation:
                    var placeholder = new System.Text.StringBuilder();
                    placeholder.Append('{');
                    placeholder.Append(argIndex);
                    if (interpolation.Alignment != null)
                    {
                        var alignVal = interpolation.Alignment.ConstantValue;
                        if (alignVal.HasValue)
                        {
                            placeholder.Append(',');
                            placeholder.Append(alignVal.Value);
                        }
                    }
                    if (interpolation.FormatString != null)
                    {
                        var fmtVal = interpolation.FormatString.ConstantValue;
                        if (fmtVal.HasValue)
                        {
                            placeholder.Append(':');
                            placeholder.Append(fmtVal.Value);
                        }
                    }
                    placeholder.Append('}');
                    formatParts.Add(placeholder.ToString());
                    // CA-v2 M3: a v1 class with a SEALED user ToString override stringifies through it.
                    var interpTs = ClassAbi.TryGetUserToString(interpolation.Expression.Type);
                    if (interpTs != null)
                    {
                        var recv = VisitExpression(interpolation.Expression);
                        argVals.Add(EmitCallToMethod(ResolveStructMember(interpTs), new List<CLeaf> { recv }));
                        argIndex++;
                        break;
                    }
                    // B67: a user enum in an interpolation hole would be boxed and Format-ToString'd to its
                    // underlying number — pre-convert it to the C#-correct name string instead.
                    ClassAbi.RejectImplicitToString(interpolation.Expression.Type);
                    var interpVal = VisitExpression(interpolation.Expression);
                    argVals.Add(TryEmitEnumToString(interpVal, interpolation.Expression.Type) ?? interpVal);
                    argIndex++;
                    break;
            }
        }

        var formatStr = string.Join("", formatParts);
        var formatConst = Const(formatStr, "SystemString");

        if (argVals.Count == 0)
        {
            // No interpolation: just return the literal
            return formatConst;
        }

        if (argVals.Count <= 3)
        {
            var externArgs = new List<CLeaf>();
            externArgs.Add(formatConst);
            externArgs.AddRange(argVals);
            var argTypes = string.Join("_", argVals.Select(_ => "SystemObject"));
            return ExternCall(
                $"SystemString.__Format__SystemString_{argTypes}__SystemString",
                externArgs,
                "SystemString");
        }
        else
        {
            // 4+ args: pack into SystemObjectArray, use Format(string, object[])
            var sizeConst = Const(argVals.Count, "SystemInt32");
            var arrVal = ExternCall(
                ExternResolver.BuildArrayCtorSignature("SystemObjectArray"),
                new List<CLeaf> { sizeConst },
                "SystemObjectArray");
            for (int i = 0; i < argVals.Count; i++)
            {
                var idxConst = Const(i, "SystemInt32");
                EmitExternVoid(ExternResolver.BuildArraySetSignature("SystemObjectArray", "SystemObject"),
                    new List<CLeaf> { arrVal, idxConst, argVals[i] });
            }
            return ExternCall(
                "SystemString.__Format__SystemString_SystemObjectArray__SystemString",
                new List<CLeaf> { formatConst, arrVal },
                "SystemString");
        }
    }

    // ── Object Creation ──

    static readonly HashSet<string> ConstFoldableStructTypes = new()
    {
        "UnityEngineVector2", "UnityEngineVector3", "UnityEngineVector4",
        "UnityEngineQuaternion", "UnityEngineColor", "UnityEngineColor32",
        "UnityEngineMatrix4x4", "UnityEngineRect",
    };

    /// <summary>Class ABI v1 (CA-M1): THE single mint sequence for a supported user class — a
    /// reference-semantics object[1+F] bundle (slot 0 reserved for the future type-object reference). Kept in
    /// one place (the DelegateAbi.EmitBundleMint analogue) so the future slot-0 flip is a one-line change here.
    /// Order (C# semantics): allocate object[SlotCount] → default-init fields (slot 0 stays the ctor's null) →
    /// run instance field initializers in declaration order → run the ctor body (ctor-as-CFunction, receiver =
    /// param0) → apply any object-initializer. NO defensive copies — the same bundle reference flows through.</summary>
    /// <summary>CA-v2 M1 ctor prologue (charter #6): before a v1 class ctor's body runs, either
    /// redirect to a `: this(...)` target (suppressing own field inits) or run own field inits then the
    /// base ctor (explicit `: base(...)` function, or the implicit base chain). Emitted from EmitMethod
    /// which owns the ctor function; this handler owns EmitCallToMethod/field-init helpers.</summary>
    // True if any instance field / auto-property of THIS class tier declares an initializer (drives the
    // ctor-prologue zero-work fast path — a plain single class with no initializers stays byte-identical).
    static bool ClassHasFieldInitializers(INamedTypeSymbol classTy)
    {
        foreach (var member in classTy.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
            var holder = f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol prop ? (ISymbol)prop : f;
            var syntax = holder.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            bool hasInit = syntax switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd => vd.Initializer != null,
                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd => pd.Initializer != null,
                _ => false,
            };
            if (hasInit) return true;
        }
        return false;
    }

    public void EmitClassCtorPrologue(IMethodSymbol ctor, IConstructorBodyOperation body, string receiverParamId)
    {
        var classTy = (INamedTypeSymbol)ctor.ContainingType;

        // Roslyn wraps `: base(...)` / `: this(...)` as an IExpressionStatementOperation over the
        // ctor IInvocationOperation.
        var initInv = body.Initializer switch
        {
            IInvocationOperation d => d,
            IExpressionStatementOperation { Operation: IInvocationOperation w } => w,
            _ => null,
        };

        // Zero-work fast path (byte-identical to pre-M1 for a plain single class): nothing to do unless
        // this class has field initializers, a user-class base chain, or an EXPLICIT this/base call.
        bool baseIsUserClass = classTy.BaseType is INamedTypeSymbol bt0 && EmitPolicy.IsUserClassType(bt0);
        bool explicitChainCall = initInv != null && !initInv.TargetMethod.IsImplicitlyDeclared;
        if (!ClassHasFieldInitializers(classTy) && !baseIsUserClass && !explicitChainCall)
            return;

        var inst = LoadField(receiverParamId, AggregateAbi.ArrayType);
        if (initInv is { } init
            && init.TargetMethod is { MethodKind: MethodKind.Constructor } target)
        {
            bool isThisChain = SymbolEqualityComparer.Default.Equals(target.ContainingType, classTy);
            if (!isThisChain)
                ClassAbi.EmitInstanceFieldInitializers(_builder, _compilation, inst, classTy,
                    _ctx.Aggregates.GetLayout(classTy), VisitExpression);
            if (target.IsImplicitlyDeclared)
            {
                if (target.ContainingType is INamedTypeSymbol implBase && EmitPolicy.IsUserClassType(implBase))
                    ClassAbi.EmitImplicitCtorChain(_builder, _compilation, inst, implBase,
                        _ctx.Aggregates.GetLayout, VisitExpression, CallBaseCtor);
            }
            else
            {
                var chainArgs = new List<CLeaf> { inst };
                foreach (var a in init.Arguments) chainArgs.Add(VisitExpression(a.Value));
                EmitExprStmt(EmitCallToMethod(ResolveStructMember(target), chainArgs));
            }
            return;
        }

        // No initializer node = implicit `: base()`: own field inits then the implicit base chain.
        ClassAbi.EmitInstanceFieldInitializers(_builder, _compilation, inst, classTy,
            _ctx.Aggregates.GetLayout(classTy), VisitExpression);
        if (classTy.BaseType is INamedTypeSymbol cbt && EmitPolicy.IsUserClassType(cbt))
            ClassAbi.EmitImplicitCtorChain(_builder, _compilation, inst, cbt,
                _ctx.Aggregates.GetLayout, VisitExpression, CallBaseCtor);
    }

    /// <summary>CA-v2b-2: emit a direct call to an explicit parameterless base ctor from an implicit derived
    /// ctor chain — the base ctor runs its own field inits, base chain, and body (needed for a base ctor with
    /// side effects, e.g. a virtual call under charter #6).</summary>
    void CallBaseCtor(IMethodSymbol ctorSym, CLeaf inst)
        => EmitExprStmt(EmitCallToMethod(ResolveStructMember(ctorSym), new List<CLeaf> { inst }));

    CLeaf EmitClassInstanceMint(IObjectCreationOperation op, INamedTypeSymbol classTy)
    {
        var layout = _ctx.Aggregates.GetLayout(classTy);
        return ClassAbi.EmitMint(
            _builder, _compilation, classTy, layout, VisitExpression,
            instance => AggregateAbi.DefaultInitialize(_builder, instance, layout, _ctx.Aggregates.GetLayout, GetUdonType),
            instance =>
            {
                // CA-v2 M1: an explicit ctor runs the full chain (field inits + base call + body) inside
                // its own function; a class with no explicit ctor runs the implicit chain (field inits
                // derived->base) inline here.
                if (op.Constructor == null || op.Constructor.IsImplicitlyDeclared)
                {
                    ClassAbi.EmitImplicitCtorChain(_builder, _compilation, instance, classTy,
                        _ctx.Aggregates.GetLayout, VisitExpression, CallBaseCtor);
                    return;
                }
                var ctorArgs = new List<CLeaf> { instance };
                foreach (var arg in op.Arguments) ctorArgs.Add(VisitExpression(arg.Value));
                EmitExprStmt(EmitCallToMethod(ResolveStructMember(op.Constructor), ctorArgs));
            },
            instance => AggregateAbi.EmitObjectInitializer(_builder, instance, layout, op.Initializer, VisitExpression),
            TypeObjWrite(classTy));
    }

    /// <summary>CA-v2b-1: the bundle[0]=typeobj write action for a minted concrete class (null if the
    /// class has no typeobj — never minted, so no is-test can observe it).</summary>
    System.Action<CLeaf> TypeObjWrite(INamedTypeSymbol classTy)
    {
        var tv = _ctx.ClassTypes.TryGetTypeObjVar(classTy);
        return tv == null ? null
            : inst => AggregateAbi.WriteSlot(_builder, inst, 0, LoadField(tv, AggregateAbi.ArrayType));
    }

    /// <summary>`new T()` (kind-level census gap, 2026-07-11): monomorphization has substituted T to a
    /// concrete type in the type-param map, so this mints exactly as `new ConcreteType()` would — a v1
    /// class bundle (implicit parameterless ctor chain), a default-initialized struct aggregate, or a
    /// primitive/SDK default. `new()` is always parameterless; only an object initializer can follow.</summary>
    CLeaf VisitTypeParameterObjectCreation(ITypeParameterObjectCreationOperation op)
    {
        var concrete = ResolveType(op.Type);
        var udon = GetUdonType(concrete);
        if (concrete is INamedTypeSymbol classTy && EmitPolicy.IsUserClassType(classTy))
        {
            var layout = _ctx.Aggregates.GetLayout(classTy);
            return ClassAbi.EmitMint(_builder, _compilation, classTy, layout, VisitExpression,
                inst => AggregateAbi.DefaultInitialize(_builder, inst, layout, _ctx.Aggregates.GetLayout, GetUdonType),
                inst => ClassAbi.EmitImplicitCtorChain(_builder, _compilation, inst, classTy,
                    _ctx.Aggregates.GetLayout, VisitExpression, CallBaseCtor),
                inst => AggregateAbi.EmitObjectInitializer(_builder, inst, layout, op.Initializer, VisitExpression),
                TypeObjWrite(classTy));
        }
        if (concrete is INamedTypeSymbol structTy && EmitPolicy.IsAggregateType(structTy))
        {
            var inst = AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout(structTy),
                _ctx.Aggregates.GetLayout, GetUdonType);
            if (op.Initializer != null)
                AggregateAbi.EmitObjectInitializer(_builder, inst, _ctx.Aggregates.GetLayout(structTy),
                    op.Initializer, VisitExpression);
            return inst;
        }
        // Primitive / SDK value type: `new T()` is the type's default value.
        return DefaultConst(udon);
    }

    /// <summary>`new { X = a, Y = b }` (kind-level census gap, 2026-07-11): an anonymous type is an
    /// immutable value-shaped aggregate — allocate the object[] and write each initializer value to its
    /// property's slot (declaration order = layout order). Member reads route through the ordinary
    /// aggregate property path once IsAggregateType admits anonymous types.</summary>
    CLeaf VisitAnonymousObjectCreation(IAnonymousObjectCreationOperation op)
    {
        var anonTy = (INamedTypeSymbol)op.Type;
        var layout = _ctx.Aggregates.GetLayout(anonTy);
        var inst = AggregateAbi.Allocate(_builder, layout.SlotCount);
        foreach (var init in op.Initializers)
        {
            if (init is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation propRef } sa
                && layout.TryGetIndex(propRef.Property.Name, out var idx))
                AggregateAbi.WriteSlot(_builder, inst, idx, VisitExpression(sa.Value));
        }
        return inst;
    }

    CLeaf VisitObjectCreation(IObjectCreationOperation op)
    {
        var resultType = GetUdonType(op.Type);

        // UdonSharpBehaviour subclasses cannot be instantiated at runtime —
        // Udon VM has no heap allocation for user-defined types.
        // Emit a diagnostic error instead of generating invalid UASM.
        if (!op.Type.IsValueType
            && op.Type is INamedTypeSymbol namedCtor
            && ExternResolver.IsUdonSharpBehaviour(namedCtor))
        {
            var loc = op.Syntax.GetLocation();
            var lineSpan = loc.GetLineSpan();
            _diagnostics.Add(new EmitDiagnostic
            {
                Severity = "Error",
                Message = $"Cannot instantiate user-defined type '{op.Type.Name}' with 'new'. "
                        + "Udon VM does not support runtime object allocation for user-defined types. "
                        + "UdonSharpBehaviour instances must be placed in the scene.",
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Character = lineSpan.StartLinePosition.Character + 1,
            });
            return Const(null, resultType);
        }

        // Class ABI v1 (CA-M1): a supported user class mints via the single ClassAbi bundle sequence. An
        // unsupported class (record / non-Object base / extern-backed foreign) already threw at the resultType
        // GetUdonType above (B79); nothing unsupported lands here.
        if (op.Type is INamedTypeSymbol classTy && EmitPolicy.IsUserClassType(classTy))
            return EmitClassInstanceMint(op, classTy);

        // Parameterless struct ctor. A user struct used AS A VALUE (e.g. `_field = new V()`, `Foo(new V())`)
        // must allocate + default-init a fresh object[]; the local-declaration path already does this, but
        // other contexts reach here. SDK value types fall through to the null placeholder.
        if (op.Arguments.Length == 0 && op.Type.IsValueType && op.Initializer == null)
            return op.Type is INamedTypeSymbol structTy && EmitPolicy.IsAggregateType(structTy)
                ? AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout(structTy),
                    _ctx.Aggregates.GetLayout, GetUdonType)
                : Const(null, resultType);

        // Constant folding: struct ctor with all-constant args
        if (op.Type.IsValueType && op.Initializer == null && op.Arguments.Length > 0
            && op.Arguments.All(a => a.Value.ConstantValue.HasValue)
            && ConstFoldableStructTypes.Contains(resultType))
        {
            var value = TryConstructAtCompileTime(resultType, op.Arguments);
            if (value != null)
                return LoadField(_ctx.Storage.DeclareStructConst(resultType, value), resultType);
        }

        // User struct with a user-defined ctor, used AS A VALUE (e.g. an operator body `return new V(x,y)`):
        // allocate + default-init the object[] and run the registered ctor on it, like the local-declaration
        // path. The SDK extern-ctor path below is only for SDK value types (Vector3, …) — for a user struct it
        // would emit a bogus SystemObjectArray.__ctor__<args>__ extern that the validator rejects. (diff-fuzz w3)
        if (op.Type.IsValueType && op.Arguments.Length > 0
            && op.Type is INamedTypeSymbol userStruct && EmitPolicy.IsUserStruct(userStruct)
            && op.Constructor != null)
        {
            var layout = _ctx.Aggregates.GetLayout(userStruct);
            var slot = _ctx.Builder.AllocScratch(AggregateAbi.ArrayType);
            EmitAssign(slot, AggregateAbi.Allocate(_builder, layout.Count));
            AggregateAbi.DefaultInitialize(_builder, SlotRef(slot), layout, _ctx.Aggregates.GetLayout, GetUdonType);
            var ctorArgs = new List<CLeaf> { SlotRef(slot) };
            foreach (var arg in op.Arguments)
                ctorArgs.Add(VisitExpression(arg.Value));
            EmitExprStmt(EmitCallToMethod(ResolveStructMember(op.Constructor), ctorArgs));
            // ctor + object-initializer combo (`new V(1,2) { Y = 3 }`): apply the initializer AFTER the
            // ctor runs, same order C# gives the fields (roadmap B41 (d)).
            AggregateAbi.EmitObjectInitializer(_builder, SlotRef(slot), layout, op.Initializer, VisitExpression);
            return SlotRef(slot);
        }

        // User struct / tuple object initializer with NO ctor args (`new V { X = 1 }`): allocate +
        // default-init the object[] like the local-declaration path, then apply the initializer via
        // layout-INDEX writes. The SDK extern initializer path below assumes a native per-field setter extern (SDK
        // value types like Vector3), which object[]-emulated aggregates don't have (roadmap B41).
        if (op.Arguments.Length == 0 && op.Type.IsValueType && op.Initializer != null
            && op.Type is INamedTypeSymbol aggInitType && EmitPolicy.IsAggregateType(aggInitType))
        {
            var layout = _ctx.Aggregates.GetLayout(aggInitType);
            var aggVal = AggregateAbi.MintDefault(_builder, layout, _ctx.Aggregates.GetLayout, GetUdonType);
            AggregateAbi.EmitObjectInitializer(_builder, aggVal, layout, op.Initializer, VisitExpression);
            return aggVal;
        }

        CLeaf resultVal;
        if (op.Arguments.Length == 0 && op.Type.IsValueType)
        {
            // Struct with initializer but no ctor args: need a mutable temp
            var resultSlot = _ctx.Builder.AllocScratch(resultType);
            EmitAssign(resultSlot, Const(null, resultType));
            resultVal = SlotRef(resultSlot);
        }
        else
        {
            // Evaluate all args first
            var argVals = new List<CLeaf>();
            for (int i = 0; i < op.Arguments.Length; i++)
                argVals.Add(VisitExpression(op.Arguments[i].Value));
            var paramTypes = op.Arguments.Select(a => GetUdonType(a.Value.Type)).ToArray();
            var paramPart = string.Join("_", paramTypes);
            resultVal = ExternCall(
                $"{resultType}.__ctor__{paramPart}__{resultType}",
                argVals,
                resultType);
        }

        // Object initializer: new T { Prop = val, ... }
        if (op.Initializer != null)
        {
            foreach (var init in op.Initializer.Initializers)
            {
                if (init is not ISimpleAssignmentOperation assign) continue;
                var valueVal = VisitExpression(assign.Value);
                EmitMemberSet(resultVal, assign.Target, valueVal);
            }
        }

        return resultVal;
    }

    void EmitMemberSet(CLeaf instanceVal, IOperation target, CLeaf valueVal)
    {
        if (target is IFieldReferenceOperation fieldRef && fieldRef.Field.ContainingType.IsValueType)
        {
            var containingType = GetUdonType(ResolveExternOwnerType(fieldRef.Field.ContainingType, fieldRef.Instance?.Type, fieldRef.Field.Name));
            var valueType = GetUdonType(fieldRef.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef.Field.Name, valueType);
            EmitExternVoid(sig, new List<CLeaf> { instanceVal, valueVal });
        }
        else if (target is IPropertyReferenceOperation propRef)
        {
            var containingType = GetUdonType(ResolveExternOwnerType(propRef.Property.ContainingType, propRef.Instance?.Type, propRef.Property.Name));
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                var externArgs = new List<CLeaf>();
                externArgs.Add(instanceVal);
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    externArgs.Add(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                externArgs.Add(valueVal);
                var indexParamStr = string.Join("_", indexTypes);
                // Indexer metadata name, not a hardcoded "Item" ([IndexerName] e.g. StringBuilder → "Chars").
                EmitExternVoid($"{containingType}.__set_{propRef.Property.MetadataName}__{indexParamStr}_{valueType}__SystemVoid",
                    externArgs);
            }
            else
            {
                EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType),
                    new List<CLeaf> { instanceVal, valueVal });
            }
        }
        else if (target is IFieldReferenceOperation fieldRef2)
        {
            // Non-struct field assignment (class fields via SetProgramVariable or direct)
            EmitStoreField(fieldRef2.Field.Name, valueVal);
        }
    }

    // ── Constant Folding Helpers ──

    static readonly Dictionary<string, string> UdonToClrTypeName = new()
    {
        ["UnityEngineVector2"] = "UnityEngine.Vector2, UnityEngine.CoreModule",
        ["UnityEngineVector3"] = "UnityEngine.Vector3, UnityEngine.CoreModule",
        ["UnityEngineVector4"] = "UnityEngine.Vector4, UnityEngine.CoreModule",
        ["UnityEngineQuaternion"] = "UnityEngine.Quaternion, UnityEngine.CoreModule",
        ["UnityEngineColor"] = "UnityEngine.Color, UnityEngine.CoreModule",
        ["UnityEngineColor32"] = "UnityEngine.Color32, UnityEngine.CoreModule",
        ["UnityEngineMatrix4x4"] = "UnityEngine.Matrix4x4, UnityEngine.CoreModule",
        ["UnityEngineRect"] = "UnityEngine.Rect, UnityEngine.CoreModule",
    };

    static Type ResolveClrType(string udonType)
    {
        if (!UdonToClrTypeName.TryGetValue(udonType, out var typeName))
            return null;
        return Type.GetType(typeName);
    }

    static object TryConstructAtCompileTime(string udonType, ImmutableArray<IArgumentOperation> args)
    {
        try
        {
            var clrType = ResolveClrType(udonType);
            if (clrType == null) return null;
            var ctorArgs = args.Select(a => Convert.ChangeType(
                a.Value.ConstantValue.Value, typeof(float))).ToArray();
            var ctorArgTypes = ctorArgs.Select(a => a.GetType()).ToArray();
            var ctor = clrType.GetConstructor(ctorArgTypes);
            return ctor?.Invoke(ctorArgs);
        }
        catch { return null; }
    }

    // B47: a user-defined COMPUTED (non-auto) static property — its getter has a real body to inline-call,
    // vs an auto-property whose getter reads a backing field. Mirrors UasmEmitter.IsComputedProperty (no
    // field on the containing type is associated with the property).
    static bool IsUserComputedStaticProperty(IPropertySymbol prop)
        => !prop.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));

    static object TryGetStaticPropertyValue(string udonType, string propertyName)
    {
        try
        {
            var clrType = ResolveClrType(udonType);
            if (clrType == null) return null;
            var prop = clrType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null);
        }
        catch { return null; }
    }

}
