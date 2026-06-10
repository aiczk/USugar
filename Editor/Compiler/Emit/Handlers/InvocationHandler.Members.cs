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
        if (op.Instance != null && EmitContext.IsNullableT(op.Property.ContainingType, out var nblUnder))
        {
            var nv = VisitExpression(op.Instance);
            if (op.Property.Name == "HasValue") return EmitNullableHasValue(nv);
            // Value of a nullable AGGREGATE (e.g. (int,int)? / V?) copies the struct out (value semantics).
            if (op.Property.Name == "Value")
                return nblUnder is INamedTypeSymbol nblAgg && EmitContext.IsAggregateType(nblAgg)
                    ? EmitDeepCloneAggregate(nv, nblAgg) : nv;
        }

        // Auto-property on an aggregate (struct/tuple) → object[] element (the backing field's slot).
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggProp && EmitContext.IsAggregateType(aggProp)
            && _ctx.GetAggregateLayout(aggProp).TryGetIndex(op.Property.Name, out var aggPropIdx))
        {
            var arrExpr = LoadInstanceRaw(op.Instance);
            var getVal = ExternCall("SystemObjectArray.__Get__SystemInt32__SystemObject",
                new List<CLeaf> { arrExpr, Const(aggPropIdx, "SystemInt32") }, "SystemObject");
            // A struct-typed property returns a COPY (C# getters return by value; you cannot mutate through it).
            return op.Property.Type is INamedTypeSymbol propAgg && EmitContext.IsAggregateType(propAgg)
                ? EmitDeepCloneAggregate(getVal, propAgg) : getVal;
        }

        // Computed (non-auto) property on an aggregate (struct): no backing-field slot, so inline-call the
        // user getter with the receiver object[] as synthetic param0 (same convention as EmitStructInstanceCall).
        // The getter only reads, so the receiver is passed uncloned.
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggGet && EmitContext.IsAggregateType(aggGet)
            && op.Property.GetMethod is { } aggGetter && _methodFunctions.ContainsKey(aggGetter.OriginalDefinition))
        {
            var ret = EmitCallToMethod(aggGetter.OriginalDefinition,
                new List<CLeaf> { LoadInstanceRaw(op.Instance) });
            return op.Property.Type is INamedTypeSymbol getRetAgg && EmitContext.IsAggregateType(getRetAgg)
                ? EmitDeepCloneAggregate(ret, getRetAgg) : ret;
        }

        // this.gameObject / this.transform → __this_* variable (Udon VM resolves via "this" default)
        if (op.Instance is IInstanceReferenceOperation)
        {
            // User-defined property getter → internal call. A struct-typed getter result is COPIED (C#
            // getters return by value) — otherwise `read = this.Prop` aliases the backing field. (diff-fuzz w4)
            if (op.Property.GetMethod != null
                && _methodFunctions.ContainsKey(op.Property.GetMethod))
            {
                var gv = EmitCallToMethod(op.Property.GetMethod, new List<CLeaf>());
                return op.Property.Type is INamedTypeSymbol thisGetAgg && EmitContext.IsAggregateType(thisGetAgg)
                    ? EmitDeepCloneAggregate(gv, thisGetAgg) : gv;
            }

            // Auto-property on this class → direct backing-field access (user-defined classes only). A
            // struct-typed backing field is COPIED on read (value semantics), same as a struct field.
            if (op.Property.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true
                && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
                && op.Property.ContainingType.Name != "UdonSharpBehaviour")
            {
                var bv = LoadField(op.Property.Name, GetUdonType(op.Property.Type));
                return op.Property.Type is INamedTypeSymbol thisAutoAgg && EmitContext.IsAggregateType(thisAutoAgg)
                    ? EmitDeepCloneAggregate(bv, thisAutoAgg) : bv;
            }

            var propName = op.Property.Name;
            if (propName == "gameObject" || propName == "transform")
            {
                var propType = GetUdonType(op.Property.Type);
                return LoadField(_ctx.DeclareThisOnce(propType), propType);
            }
            // Other this.property → extern getter with this instance
            var thisType = GetUdonType(_classSymbol);
            var thisVal = LoadField(_ctx.DeclareThisOnce(thisType), thisType);
            var cType = GetUdonType(op.Property.ContainingType);
            // Behaviour/MonoBehaviour have no Udon externs; use the class's Udon type instead
            if (cType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
                cType = GetUdonType(_classSymbol);
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
            // Constant folding: static properties on foldable struct types (e.g., Vector3.zero)
            if (op.Property.IsStatic && ConstFoldableStructTypes.Contains(containingType))
            {
                var value = TryGetStaticPropertyValue(containingType, op.Property.Name);
                if (value != null)
                    return LoadField(_ctx.DeclareStructConst(returnType, value), returnType);
            }

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
            var isAuto = op.Property.GetMethod?.DeclaringSyntaxReferences.IsEmpty == true;

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
                var (getExportName, _, getRetId) = GetCalleeLayout(op.Property.GetMethod);
                var getReturns = getRetId != null
                    ? new[] { new ReturnSlot(getRetId, returnType) }
                    : System.Array.Empty<ReturnSlot>();
                return CrossCall(instanceVal, getExportName,
                    new List<(string, CLeaf)>(), getReturns, returnType);
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
            var ifaceInst = VisitExpression(op.Instance);
            return CrossCall(ifaceInst, LayoutPlanner.InterfaceDispatchName(ifaceGetter, ifaceGetterMl),
                new List<(string, CLeaf)>(), ifaceGetterMl.Returns.ToArray(), returnType);
        }

        // Other instance.property → extern getter
        var instVal = VisitExpression(op.Instance);
        // Array .Length → use SystemArray (not the concrete array type) to match UdonSharp
        if (op.Instance.Type is IArrayTypeSymbol && op.Property.Name != "Length")
            containingType = GetUdonType((IArrayTypeSymbol)op.Instance.Type);
        // Behaviour/MonoBehaviour have no Udon externs; use the instance type
        if (containingType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            containingType = GetUdonType(op.Instance.Type);
        var sig = ExternResolver.BuildPropertyGetSignature(containingType, op.Property.Name, returnType);
        return ExternCall(sig, new List<CLeaf> { instVal }, returnType);
    }

    // ── Indexer Get ──

    CLeaf VisitIndexerGet(IPropertyReferenceOperation op)
    {
        // User-defined indexer on this/base class → internal getter call (`this[i]` reads this-fields directly).
        if (op.Instance is IInstanceReferenceOperation
            && op.Property.GetMethod != null && _methodFunctions.ContainsKey(op.Property.GetMethod))
        {
            var args = new List<CLeaf>();
            foreach (var arg in op.Arguments) args.Add(VisitExpression(arg.Value));
            return EmitCallToMethod(op.Property.GetMethod, args);
        }

        // User-defined indexer on a user STRUCT instance (`s[i]`) → call the getter with the struct receiver
        // (object[]) as param0 plus the index args, like a struct computed property. Without this it falls to
        // a bogus SystemObjectArray.__get_Item extern the validator rejects. (diff-fuzz wave 4)
        if (op.Instance != null && op.Instance.Type is INamedTypeSymbol aggIdx && EmitContext.IsAggregateType(aggIdx)
            && op.Property.GetMethod is { } idxGetter && _methodFunctions.ContainsKey(idxGetter.OriginalDefinition))
        {
            var sargs = new List<CLeaf> { LoadInstanceRaw(op.Instance) };
            foreach (var arg in op.Arguments) sargs.Add(VisitExpression(arg.Value));
            var ret = EmitCallToMethod(idxGetter.OriginalDefinition, sargs);
            return op.Property.Type is INamedTypeSymbol idxRetAgg && EmitContext.IsAggregateType(idxRetAgg)
                ? EmitDeepCloneAggregate(ret, idxRetAgg) : ret;
        }

        var cType = GetUdonType(op.Property.ContainingType);
        var rType = GetUdonType(op.Property.Type);

        // string[i] → str.ToCharArray(i, 1)[0]
        // Udon VM has no string indexer; mirror UdonSharp's BoundStringAccessExpression
        if (cType == "SystemString")
        {
            CLeaf inst = op.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol))
                : VisitExpression(op.Instance);
            var indexVal = VisitExpression(op.Arguments[0].Value);
            var oneConst = Const(1, "SystemInt32");
            var charArr = ExternCall(
                "SystemString.__ToCharArray__SystemInt32_SystemInt32__SystemCharArray",
                new List<CLeaf> { inst, indexVal, oneConst },
                "SystemCharArray");
            var zeroConst = Const(0, "SystemInt32");
            return ExternCall(
                "SystemCharArray.__Get__SystemInt32__SystemChar",
                new List<CLeaf> { charArr, zeroConst },
                "SystemChar");
        }

        CLeaf instVal;
        if (op.Instance is IInstanceReferenceOperation)
            instVal = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
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
                    argVals.Add(VisitExpression(interpolation.Expression));
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
                "SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
                new List<CLeaf> { sizeConst },
                "SystemObjectArray");
            for (int i = 0; i < argVals.Count; i++)
            {
                var idxConst = Const(i, "SystemInt32");
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
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

    CLeaf VisitObjectCreation(IObjectCreationOperation op)
    {
        // §2.8 round-2: ctor arguments are call-boundary escapes too — a capturing lambda passed
        // to an erasing-typed (object / delegate-tuple / T=object) ctor param is loud.
        GuardCaptureEscapeArguments(op.Arguments);

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

        // Parameterless struct ctor. A user struct used AS A VALUE (e.g. `_field = new V()`, `Foo(new V())`)
        // must allocate + default-init a fresh object[]; the local-declaration path already does this, but
        // other contexts reach here. SDK value types fall through to the null placeholder.
        if (op.Arguments.Length == 0 && op.Type.IsValueType && op.Initializer == null)
            return op.Type is INamedTypeSymbol structTy && EmitContext.IsAggregateType(structTy)
                ? EmitNewAggregate(structTy)
                : Const(null, resultType);

        // Constant folding: struct ctor with all-constant args
        if (op.Type.IsValueType && op.Initializer == null && op.Arguments.Length > 0
            && op.Arguments.All(a => a.Value.ConstantValue.HasValue)
            && ConstFoldableStructTypes.Contains(resultType))
        {
            var value = TryConstructAtCompileTime(resultType, op.Arguments);
            if (value != null)
                return LoadField(_ctx.DeclareStructConst(resultType, value), resultType);
        }

        // User struct with a user-defined ctor, used AS A VALUE (e.g. an operator body `return new V(x,y)`):
        // allocate + default-init the object[] and run the registered ctor on it, like the local-declaration
        // path. The extern-ctor fallback below is only for SDK value types (Vector3, …) — for a user struct it
        // would emit a bogus SystemObjectArray.__ctor__<args>__ extern that the validator rejects. (diff-fuzz w3)
        if (op.Type.IsValueType && op.Arguments.Length > 0
            && op.Type is INamedTypeSymbol userStruct && EmitContext.IsUserStruct(userStruct)
            && op.Constructor != null && _methodFunctions.ContainsKey(op.Constructor))
        {
            var layout = _ctx.GetAggregateLayout(userStruct);
            var slot = _ctx.AllocTemp("SystemObjectArray");
            EmitAssign(slot, ExternCall("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray",
                new List<CLeaf> { Const(layout.Count, "SystemInt32") }, "SystemObjectArray"));
            EmitDefaultInitAggregate(SlotRef(slot), layout);
            var ctorArgs = new List<CLeaf> { SlotRef(slot) };
            foreach (var arg in op.Arguments)
                ctorArgs.Add(VisitExpression(arg.Value));
            EmitExprStmt(EmitCallToMethod(op.Constructor, ctorArgs));
            return SlotRef(slot);
        }

        CLeaf resultVal;
        if (op.Arguments.Length == 0 && op.Type.IsValueType)
        {
            // Struct with initializer but no ctor args: need a mutable temp
            var resultSlot = _ctx.AllocTemp(resultType);
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
                // §2.8 round-3 [C]: object-initializer member stores are escaping stores into the
                // value's backing storage — guard each member value like an array-initializer
                // element (this arm used to emit the member set with no guard and no taint,
                // VM-verified laundering through struct envelopes).
                GuardCaptureEscapeValue(assign.Value);
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
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            var valueType = GetUdonType(fieldRef.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef.Field.Name, valueType);
            EmitExternVoid(sig, new List<CLeaf> { instanceVal, valueVal });
        }
        else if (target is IPropertyReferenceOperation propRef)
        {
            var containingType = GetUdonType(propRef.Property.ContainingType);
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
