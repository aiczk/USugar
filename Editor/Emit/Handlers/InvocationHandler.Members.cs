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

    string VisitPropertyReference(IPropertyReferenceOperation op)
    {
        // Indexer access: Type.__get_Item__IndexTypes__ReturnType
        if (op.Property.IsIndexer)
            return VisitIndexerGet(op);

        // this.gameObject / this.transform → __this_* variable (Udon VM resolves via "this" default)
        if (op.Instance is IInstanceReferenceOperation)
        {
            // User-defined property getter → JUMP call
            if (op.Property.GetMethod != null
                && _ctx.MethodLabels.TryGetValue(op.Property.GetMethod, out var getterLabel))
                return EmitCallByLabel(op.Property.GetMethod, getterLabel);

            // Auto-property on this class → direct variable access
            if (op.Property.GetMethod?.IsImplicitlyDeclared == true
                && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType))
                return op.Property.Name;

            var propName = op.Property.Name;
            if (propName == "gameObject" || propName == "transform")
            {
                var propType = GetUdonType(op.Property.Type);
                return _ctx.Vars.DeclareThisOnce(propType);
            }
            // Other this.property → extern getter with this instance
            var thisType = GetUdonType(_ctx.ClassSymbol);
            var thisId = _ctx.Vars.DeclareThisOnce(thisType);
            var cType = GetUdonType(op.Property.ContainingType);
            // Behaviour/MonoBehaviour have no Udon externs; use the class's Udon type instead
            if (cType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
                cType = GetUdonType(_ctx.ClassSymbol);
            var rType = GetUdonType(op.Property.Type);
            var tid = ConsumeTargetHintOrTemp(rType);
            _ctx.Module.AddPush(thisId);
            _ctx.Module.AddPush(tid);
            AddExternChecked(ExternResolver.BuildPropertyGetSignature(cType, propName, rType));
            return tid;
        }

        var containingType = GetUdonType(op.Property.ContainingType);
        var returnType = GetUdonType(op.Property.Type);

        // Static property: no instance push
        if (op.Instance == null)
        {
            // Constant folding: static properties on foldable struct types (e.g., Vector3.zero)
            if (op.Property.IsStatic && ConstFoldableStructTypes.Contains(containingType))
            {
                var value = TryGetStaticPropertyValue(containingType, op.Property.Name);
                if (value != null)
                    return _ctx.Vars.DeclareStructConst(returnType, value);
            }

            var tempId = ConsumeTargetHintOrTemp(returnType);
            _ctx.Module.AddPush(tempId);
            AddExternChecked(ExternResolver.BuildPropertyGetSignature(containingType, op.Property.Name, returnType));
            return tempId;
        }

        // Cross-behaviour property get
        if (op.Instance != null && ExternResolver.IsUdonSharpBehaviour(op.Property.ContainingType)
            && !(op.Instance is IInstanceReferenceOperation))
        {
            var savedHint2 = _ctx.TargetHint;
            _ctx.TargetHint = null;
            var instanceId2 = VisitExpression(op.Instance);
            _ctx.TargetHint = savedHint2;
            var isAuto = op.Property.GetMethod?.IsImplicitlyDeclared == true;

            if (isAuto)
            {
                // Auto-property: direct GetProgramVariable("PropertyName")
                var nameConst = _ctx.Vars.DeclareConst("SystemString", op.Property.Name);
                var tempId = ConsumeTargetHintOrTemp(returnType);
                _ctx.Module.AddPush(instanceId2);
                _ctx.Module.AddPush(nameConst);
                _ctx.Module.AddPush(tempId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
                return tempId;
            }
            else
            {
                // Non-auto property: call getter via SendCustomEvent, then read return value
                var (getExportName, _, getRetId) = GetCalleeLayout(op.Property.GetMethod);

                // SendCustomEvent to invoke getter
                var eventConst = _ctx.Vars.DeclareConst("SystemString", getExportName);
                _ctx.Module.AddPush(instanceId2);
                _ctx.Module.AddPush(eventConst);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid");

                // GetProgramVariable for return value
                var retNameConst = _ctx.Vars.DeclareConst("SystemString", getRetId);
                var tempId = ConsumeTargetHintOrTemp(returnType);
                _ctx.Module.AddPush(instanceId2);
                _ctx.Module.AddPush(retNameConst);
                _ctx.Module.AddPush(tempId);
                AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
                return tempId;
            }
        }

        // Other instance.property → extern getter
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;
        var instanceId = VisitExpression(op.Instance);
        _ctx.TargetHint = savedHint;
        // Array .Length → use SystemArray (not the concrete array type) to match UdonSharp
        if (op.Instance.Type is IArrayTypeSymbol && op.Property.Name != "Length")
            containingType = GetUdonType((IArrayTypeSymbol)op.Instance.Type);
        // Behaviour/MonoBehaviour have no Udon externs; use the instance type
        if (containingType is "UnityEngineBehaviour" or "UnityEngineMonoBehaviour")
            containingType = GetUdonType(op.Instance.Type);
        var sig = ExternResolver.BuildPropertyGetSignature(containingType, op.Property.Name, returnType);
        var resultId = ConsumeTargetHintOrTemp(returnType);
        _ctx.Module.AddPush(instanceId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked(sig);
        return resultId;
    }

    // ── Indexer Get ──

    string VisitIndexerGet(IPropertyReferenceOperation op)
    {
        var cType = GetUdonType(op.Property.ContainingType);
        var rType = GetUdonType(op.Property.Type);

        // string[i] → str.ToCharArray(i, 1)[0]
        // Udon VM has no string indexer; mirror UdonSharp's BoundStringAccessExpression
        if (cType == "SystemString")
        {
            string inst = op.Instance is IInstanceReferenceOperation
                ? _ctx.Vars.DeclareThisOnce(GetUdonType(_ctx.ClassSymbol))
                : VisitExpression(op.Instance);
            var indexId = VisitExpression(op.Arguments[0].Value);
            var oneId = _ctx.Vars.DeclareConst("SystemInt32", "1");
            var charArr = _ctx.Vars.DeclareTemp("SystemCharArray");
            _ctx.Module.AddPush(inst);
            _ctx.Module.AddPush(indexId);
            _ctx.Module.AddPush(oneId);
            _ctx.Module.AddPush(charArr);
            AddExternChecked("SystemString.__ToCharArray__SystemInt32_SystemInt32__SystemCharArray");
            var zeroId = _ctx.Vars.DeclareConst("SystemInt32", "0");
            var result = _ctx.Vars.DeclareTemp("SystemChar");
            _ctx.Module.AddPush(charArr);
            _ctx.Module.AddPush(zeroId);
            _ctx.Module.AddPush(result);
            AddExternChecked("SystemCharArray.__Get__SystemInt32__SystemChar");
            return result;
        }

        var resultId = _ctx.Vars.DeclareTemp(rType);
        string instId;
        if (op.Instance is IInstanceReferenceOperation)
            instId = _ctx.Vars.DeclareThisOnce(GetUdonType(_ctx.ClassSymbol));
        else
            instId = VisitExpression(op.Instance);
        _ctx.Module.AddPush(instId);
        var idxTypes = new List<string>();
        foreach (var arg in op.Arguments)
        {
            _ctx.Module.AddPush(VisitExpression(arg.Value));
            idxTypes.Add(GetUdonType(arg.Value.Type));
        }
        _ctx.Module.AddPush(resultId);
        AddExternChecked($"{cType}.__get_Item__{string.Join("_", idxTypes)}__{rType}");
        return resultId;
    }

    // ── Interpolated String ──

    string VisitInterpolatedString(IInterpolatedStringOperation op)
    {
        var formatParts = new List<string>();
        var argIds = new List<string>();
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
                    argIds.Add(VisitExpression(interpolation.Expression));
                    argIndex++;
                    break;
            }
        }

        var formatStr = string.Join("", formatParts);
        var formatConstId = _ctx.Vars.DeclareConst("SystemString", formatStr);
        var resultId = _ctx.Vars.DeclareTemp("SystemString");

        if (argIds.Count == 0)
        {
            // No interpolation: just return the literal
            return formatConstId;
        }

        if (argIds.Count <= 3)
        {
            _ctx.Module.AddPush(formatConstId);
            foreach (var argId in argIds)
                _ctx.Module.AddPush(argId);
            _ctx.Module.AddPush(resultId);
            var argTypes = string.Join("_", argIds.Select(_ => "SystemObject"));
            AddExternChecked($"SystemString.__Format__SystemString_{argTypes}__SystemString");
        }
        else
        {
            // 4+ args: pack into SystemObjectArray, use Format(string, object[])
            var sizeConst = _ctx.Vars.DeclareConst("SystemInt32", argIds.Count.ToString());
            var arrId = _ctx.Vars.DeclareTemp("SystemObjectArray");
            _ctx.Module.AddPush(sizeConst);
            _ctx.Module.AddPush(arrId);
            AddExternChecked("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray");
            for (int i = 0; i < argIds.Count; i++)
            {
                var idxConst = _ctx.Vars.DeclareConst("SystemInt32", i.ToString());
                _ctx.Module.AddPush(arrId);
                _ctx.Module.AddPush(idxConst);
                _ctx.Module.AddPush(argIds[i]);
                AddExternChecked("SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid");
            }
            _ctx.Module.AddPush(formatConstId);
            _ctx.Module.AddPush(arrId);
            _ctx.Module.AddPush(resultId);
            AddExternChecked("SystemString.__Format__SystemString_SystemObjectArray__SystemString");
        }

        return resultId;
    }

    // ── Object Creation ──

    static readonly HashSet<string> ConstFoldableStructTypes = new()
    {
        "UnityEngineVector2", "UnityEngineVector3", "UnityEngineVector4",
        "UnityEngineQuaternion", "UnityEngineColor", "UnityEngineColor32",
        "UnityEngineMatrix4x4", "UnityEngineRect",
    };

    string VisitObjectCreation(IObjectCreationOperation op)
    {
        var resultType = GetUdonType(op.Type);

        // Parameterless struct ctor → default initialization (no extern needed)
        if (op.Arguments.Length == 0 && op.Type.IsValueType && op.Initializer == null)
            return _ctx.Vars.DeclareConst(resultType, "null");

        // Constant folding: struct ctor with all-constant args
        if (op.Type.IsValueType && op.Initializer == null && op.Arguments.Length > 0
            && op.Arguments.All(a => a.Value.ConstantValue.HasValue)
            && ConstFoldableStructTypes.Contains(resultType))
        {
            var value = TryConstructAtCompileTime(resultType, op.Arguments);
            if (value != null)
                return _ctx.Vars.DeclareStructConst(resultType, value);
        }

        string resultId;
        if (op.Arguments.Length == 0 && op.Type.IsValueType)
        {
            // Struct with initializer but no ctor args: need a mutable temp
            resultId = _ctx.Vars.DeclareTemp(resultType);
            var defaultVal = _ctx.Vars.DeclareConst(resultType, "null");
            _ctx.Module.AddCopy(defaultVal, resultId);
        }
        else
        {
            resultId = _ctx.Vars.DeclareTemp(resultType);
            // Evaluate all args first, then PUSH contiguously (avoid interleaving)
            var argIds = new string[op.Arguments.Length];
            for (int i = 0; i < op.Arguments.Length; i++)
                argIds[i] = VisitExpression(op.Arguments[i].Value);
            foreach (var argId in argIds)
                _ctx.Module.AddPush(argId);
            _ctx.Module.AddPush(resultId);
            var paramTypes = op.Arguments.Select(a => GetUdonType(a.Value.Type)).ToArray();
            var paramPart = string.Join("_", paramTypes);
            AddExternChecked($"{resultType}.__ctor__{paramPart}__{resultType}");
        }

        // Object initializer: new T { Prop = val, ... }
        if (op.Initializer != null)
        {
            foreach (var init in op.Initializer.Initializers)
            {
                if (init is not ISimpleAssignmentOperation assign) continue;
                var valueId = VisitExpression(assign.Value);
                EmitMemberSet(resultId, assign.Target, valueId);
            }
        }

        return resultId;
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
            var argValues = args.Select(a => a.Value.ConstantValue.Value).ToArray();

            // Try direct match with Roslyn constant types
            var argTypes = argValues.Select(v => v?.GetType() ?? typeof(object)).ToArray();
            var ctor = clrType.GetConstructor(argTypes);
            if (ctor != null) return ctor.Invoke(argValues);

            // Fallback: convert to actual constructor parameter types
            foreach (var c in clrType.GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length != argValues.Length) continue;
                try
                {
                    var converted = new object[argValues.Length];
                    for (int i = 0; i < argValues.Length; i++)
                        converted[i] = Convert.ChangeType(argValues[i], ps[i].ParameterType);
                    return c.Invoke(converted);
                }
                catch { }
            }
            return null;
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
