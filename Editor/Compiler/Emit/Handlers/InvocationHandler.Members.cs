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

    HExpr VisitPropertyReference(IPropertyReferenceOperation op)
    {
        // Indexer access: Type.__get_Item__IndexTypes__ReturnType
        if (op.Property.IsIndexer)
            return VisitIndexerGet(op);

        // this.gameObject / this.transform → __this_* variable (Udon VM resolves via "this" default)
        if (op.Instance is IInstanceReferenceOperation)
        {
            // User-defined property getter → internal call
            if (op.Property.GetMethod != null
                && _methodFunctions.TryGetValue(op.Property.GetMethod, out var getterFunc))
                return EmitCallToMethod(op.Property.GetMethod, new List<HExpr>());

            // Auto-property on this class → direct variable access
            if (op.Property.GetMethod?.IsImplicitlyDeclared == true
                && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType))
                return LoadField(op.Property.Name, GetUdonType(op.Property.Type));

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
                new List<HExpr> { thisVal },
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
                new List<HExpr>(),
                returnType);
        }

        // Cross-behaviour property get
        if (op.Instance != null && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
            && !(op.Instance is IInstanceReferenceOperation))
        {
            var instanceVal = VisitExpression(op.Instance);
            var isAuto = op.Property.GetMethod?.IsImplicitlyDeclared == true;

            if (isAuto)
            {
                // Auto-property: direct GetProgramVariable("PropertyName")
                var nameConst = Const(op.Property.Name, "SystemString");
                return ExternCall(
                    "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                    new List<HExpr> { instanceVal, nameConst },
                    returnType);
            }
            else
            {
                // Non-auto property: use HCrossBehaviourCall to keep SendCustomEvent
                // inside the expression tree (prevents side-effect leakage in HSelect)
                var (getExportName, _, getRetId) = GetCalleeLayout(op.Property.GetMethod);
                return new HCrossBehaviourCall(instanceVal, getExportName,
                    new List<(string, HExpr)>(), getRetId, returnType);
            }
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
        return ExternCall(sig, new List<HExpr> { instVal }, returnType);
    }

    // ── Indexer Get ──

    HExpr VisitIndexerGet(IPropertyReferenceOperation op)
    {
        var cType = GetUdonType(op.Property.ContainingType);
        var rType = GetUdonType(op.Property.Type);

        // string[i] → str.ToCharArray(i, 1)[0]
        // Udon VM has no string indexer; mirror UdonSharp's BoundStringAccessExpression
        if (cType == "SystemString")
        {
            HExpr inst = op.Instance is IInstanceReferenceOperation
                ? LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol))
                : VisitExpression(op.Instance);
            var indexVal = VisitExpression(op.Arguments[0].Value);
            var oneConst = Const(1, "SystemInt32");
            var charArr = ExternCall(
                "SystemString.__ToCharArray__SystemInt32_SystemInt32__SystemCharArray",
                new List<HExpr> { inst, indexVal, oneConst },
                "SystemCharArray");
            var zeroConst = Const(0, "SystemInt32");
            return ExternCall(
                "SystemCharArray.__Get__SystemInt32__SystemChar",
                new List<HExpr> { charArr, zeroConst },
                "SystemChar");
        }

        HExpr instVal;
        if (op.Instance is IInstanceReferenceOperation)
            instVal = LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol));
        else
            instVal = VisitExpression(op.Instance);

        var externArgs = new List<HExpr>();
        externArgs.Add(instVal);
        var idxTypes = new List<string>();
        foreach (var arg in op.Arguments)
        {
            externArgs.Add(VisitExpression(arg.Value));
            idxTypes.Add(GetUdonType(arg.Value.Type));
        }
        return ExternCall(
            $"{cType}.__get_Item__{string.Join("_", idxTypes)}__{rType}",
            externArgs,
            rType);
    }

    // ── Interpolated String ──

    HExpr VisitInterpolatedString(IInterpolatedStringOperation op)
    {
        var formatParts = new List<string>();
        var argVals = new List<HExpr>();
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
            var externArgs = new List<HExpr>();
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
                new List<HExpr> { sizeConst },
                "SystemObjectArray");
            for (int i = 0; i < argVals.Count; i++)
            {
                var idxConst = Const(i, "SystemInt32");
                EmitExternVoid("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid",
                    new List<HExpr> { arrVal, idxConst, argVals[i] });
            }
            return ExternCall(
                "SystemString.__Format__SystemString_SystemObjectArray__SystemString",
                new List<HExpr> { formatConst, arrVal },
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

    HExpr VisitObjectCreation(IObjectCreationOperation op)
    {
        var resultType = GetUdonType(op.Type);

        // Parameterless struct ctor → default initialization (no extern needed)
        if (op.Arguments.Length == 0 && op.Type.IsValueType && op.Initializer == null)
            return Const(null, resultType);

        // Constant folding: struct ctor with all-constant args
        if (op.Type.IsValueType && op.Initializer == null && op.Arguments.Length > 0
            && op.Arguments.All(a => a.Value.ConstantValue.HasValue)
            && ConstFoldableStructTypes.Contains(resultType))
        {
            var value = TryConstructAtCompileTime(resultType, op.Arguments);
            if (value != null)
                return LoadField(_ctx.DeclareStructConst(resultType, value), resultType);
        }

        HExpr resultVal;
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
            var argVals = new List<HExpr>();
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

    void EmitMemberSet(HExpr instanceVal, IOperation target, HExpr valueVal)
    {
        if (target is IFieldReferenceOperation fieldRef && fieldRef.Field.ContainingType.IsValueType)
        {
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            var valueType = GetUdonType(fieldRef.Field.Type);
            var sig = ExternResolver.BuildFieldSetSignature(containingType, fieldRef.Field.Name, valueType);
            EmitExternVoid(sig, new List<HExpr> { instanceVal, valueVal });
        }
        else if (target is IPropertyReferenceOperation propRef)
        {
            var containingType = GetUdonType(propRef.Property.ContainingType);
            var valueType = GetUdonType(propRef.Property.Type);
            if (propRef.Property.IsIndexer)
            {
                var externArgs = new List<HExpr>();
                externArgs.Add(instanceVal);
                var indexTypes = new List<string>();
                foreach (var arg in propRef.Arguments)
                {
                    externArgs.Add(VisitExpression(arg.Value));
                    indexTypes.Add(GetUdonType(arg.Value.Type));
                }
                externArgs.Add(valueVal);
                var indexParamStr = string.Join("_", indexTypes);
                EmitExternVoid($"{containingType}.__set_Item__{indexParamStr}_{valueType}__SystemVoid",
                    externArgs);
            }
            else
            {
                EmitExternVoid(ExternResolver.BuildPropertySetSignature(containingType, propRef.Property.Name, valueType),
                    new List<HExpr> { instanceVal, valueVal });
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
