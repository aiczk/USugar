using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public static class ExternResolver
{
    // Optional extern existence check (set by test harness or Unity editor).
    // Used to resolve ambiguous containing types for conversion operators.
    static Func<string, bool> _isExternValid;
    public static Func<string, bool> IsExternValid
    {
        get => Volatile.Read(ref _isExternValid);
        set => Volatile.Write(ref _isExternValid, value);
    }

    // Rank>1 array type (int[,], …) has no Udon representation — the runtime only knows single-rank
    // System*Array externs. Lowering one silently dropped dimensions 2+; loud-reject at the single
    // type-lowering choke point so creation, element read/write, and field/param/local all reject.
    internal const string MultidimArrayMessage =
        "Multi-dimensional arrays (int[,]) are not supported: use a jagged array (int[][]) or a flat array with manual indexing";

    /// <summary>A user-authored reference type (class Foo {...}, record Foo): TypeKind.Class, source-defined
    /// in this compilation, not an SDK/Unity/System stand-in, and not a UdonSharpBehaviour. Distinct from a
    /// genuine foreign/SDK class (VRCUrl, DataList, …), which routes through the SAME extern-name-based
    /// method/ctor dispatch but has REAL registered externs — that distinction can only be checked at a
    /// specific call site (via IsExternValid against the exact candidate name), not from the type alone, so
    /// this predicate only narrows the shape; the caller decides whether a matching extern exists.</summary>
    public static bool IsPlainUserClass(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.TypeKind != TypeKind.Class) return false;
        if (named.SpecialType != SpecialType.None) return false; // string, object, … — natively supported
        if (named.DeclaringSyntaxReferences.IsEmpty) return false; // compiled assembly (SDK/Unity/System)
        if (IsSdkNamespace(named.ContainingNamespace)) return false;
        if (IsUdonSharpBehaviour(named)) return false;
        return true;
    }

    static readonly Dictionary<string, string> UdonTypeRemap = new()
    {
        ["UdonSharpUdonSharpBehaviour"] = "VRCUdonCommonInterfacesIUdonEventReceiver",
        ["VRCUdonUdonBehaviour"] = "VRCUdonCommonInterfacesIUdonEventReceiver",
        ["VRCSDKBaseVRC_AvatarPedestal"] = "VRCSDK3ComponentsVRCAvatarPedestal",
    };

    public static string RemapUdonType(string sanitizedType)
    {
        return UdonTypeRemap.TryGetValue(sanitizedType, out var remapped) ? remapped : sanitizedType;
    }

    public static bool IsUdonSharpBehaviour(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType) return false;
        var t = namedType;
        while (t != null)
        {
            if (t.Name == "UdonSharpBehaviour") return true;
            t = t.BaseType;
        }
        return false;
    }

    public static string GetUdonTypeName(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        if (type is ITypeParameterSymbol tp && typeParamMap != null
            && typeParamMap.TryGetValue(tp, out var resolved))
            return GetUdonTypeName(resolved, typeParamMap);

        if (type is IArrayTypeSymbol arrayType)
        {
            if (arrayType.Rank > 1) throw new System.NotSupportedException(MultidimArrayMessage);
            // Substitute a type-parameter element through the map BEFORE classifying. A generic method's
            // T[] param with T=<user struct> must be seen as struct[] (→ SystemObjectArray), like the
            // non-generic path. Without this, the aggregate check below runs on the raw type parameter
            // (not aggregate), then "Array" is appended to the substituted element name (already degraded
            // to SystemObjectArray) → an invalid SystemObjectArrayArray that Udon cannot resolve.
            var elementType = arrayType.ElementType;
            if (elementType is ITypeParameterSymbol etp && typeParamMap != null
                && typeParamMap.TryGetValue(etp, out var resolvedElem))
                elementType = resolvedElem;

            if (elementType is IArrayTypeSymbol)
                return "SystemObjectArray";
            // Delegate-element array (Func<T>[], …) → object[] of boxed bundle references, same shape as
            // aggregate arrays (element extern type SystemObject). MUST be an explicit case here — the
            // element recursion would otherwise produce a bogus SystemObjectArrayArray (design §1.2).
            if (elementType is INamedTypeSymbol elemDlg && elemDlg.DelegateInvokeMethod != null)
                return "SystemObjectArray";
            // struct[] / tuple[] → object[] of boxed object[] elements (no SystemObjectArrayArray in Udon).
            if (EmitContext.IsAggregateType(elementType))
                return "SystemObjectArray";
            var elemTypeName = GetUdonTypeName(elementType, typeParamMap);
            if (elemTypeName == "VRCUdonCommonInterfacesIUdonEventReceiver")
                return "UnityEngineComponentArray";
            return RemapUdonType(elemTypeName) + "Array";
        }

        // Delegate type → SystemObjectArray: the runtime value is a reference to the {target, method, addr,
        // env} object[] bundle (first-class delegate ABI, design §1.2). Must precede the constructed-generic
        // branch below, which would otherwise fabricate fake names like SystemFuncSystemInt32SystemInt32.
        if (type is INamedTypeSymbol dlgWithMap && dlgWithMap.DelegateInvokeMethod != null)
            return "SystemObjectArray";

        // Constructed generic carrying type-param args (e.g. a delegate parameter Func<T,int> of a generic
        // method): the no-map overload's generic branch would resolve the args WITHOUT the map, leaving a
        // literal "T" in the name → an invalid extern type. Recurse the args WITH the map here. Nullable /
        // aggregate / enum generics are intentionally left to the no-map overload, which ignores their type
        // args (→ SystemObject / SystemObjectArray / underlying) and so needs no substitution.
        if (typeParamMap != null && type is INamedTypeSymbol named && named.IsGenericType
            && named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            && type.TypeKind != TypeKind.Enum
            && !EmitContext.IsAggregateType(type))
        {
            var def = named.ConstructedFrom;
            var ns = def.ContainingNamespace?.ToDisplayString();
            var baseName = SanitizeTypeName(string.IsNullOrEmpty(ns) ? def.Name : $"{ns}.{def.Name}");
            foreach (var arg in named.TypeArguments)
                baseName += GetUdonTypeName(arg, typeParamMap);
            return RemapUdonType(baseName);
        }

        return GetUdonTypeName(type);
    }

    public static bool IsUdonSharpBehaviour(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        if (type is ITypeParameterSymbol tp2 && typeParamMap != null
            && typeParamMap.TryGetValue(tp2, out var resolved2))
            return IsUdonSharpBehaviour(resolved2);
        return IsUdonSharpBehaviour(type);
    }

    // Layer-2 choke point: can a RUNTIME type test (SystemType.__IsInstanceOfType) against `target`
    // honestly discriminate it? GetUdonTypeName is NON-INJECTIVE — it folds many distinct CLR types onto
    // one Udon runtime tag, so a type test against a folded type matches ANY same-tag value and silently
    // takes the wrong branch. Return FALSE (test cannot be honest → the caller MUST reject) for every type
    // in a collapse set; TRUE only for a type whose Udon tag uniquely identifies it. This is the single
    // predicate every runtime-type-test site routes through, so the unsoundness is closed by construction
    // rather than by per-node guards. Resolve `target` through the type-param map first (a generic T
    // monomorphizing to e.g. Func<object>[] must be classified as the concrete type it becomes).
    public static bool IsRuntimeDistinguishable(ITypeSymbol target,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        while (target is ITypeParameterSymbol tp && typeParamMap != null
            && typeParamMap.TryGetValue(tp, out var resolved))
            target = resolved;

        if (target == null) return false;

        // Bare object: IsInstanceOfType(typeof(object), v) == `v is object` exactly for every value —
        // the one universally answerable test, even though its tag "SystemObject" is otherwise shared.
        if (target.SpecialType == SpecialType.System_Object) return true;

        // Nullable<T> → boxed SystemObject: a runtime test cannot see through the box (any box matches).
        if (target is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return false;

        // Delegate / user struct / tuple: all fold to SystemObjectArray, sharing that tag with each other,
        // with arrays of those, and with object[] itself. (IsAggregateType deliberately excludes delegates,
        // so the delegate is tested on its own.)
        if (target is INamedTypeSymbol dlg && dlg.DelegateInvokeMethod != null) return false;
        if (EmitContext.IsAggregateType(target)) return false;

        // User-defined enum → its underlying int tag (indistinguishable from int or a sibling enum). SDK
        // enums keep a uniquely registered tag, so they stay distinguishable — mirror GetUdonTypeName's
        // exact enum classification (source syntax refs AND non-SDK namespace).
        if (target.TypeKind == TypeKind.Enum
            && !target.DeclaringSyntaxReferences.IsEmpty
            && !IsSdkNamespace(target.ContainingNamespace))
            return false;

        // The collapsing runtime tags themselves: SystemObjectArray (delegate/struct/tuple/array-of-those +
        // object[]), IUdonEventReceiver (UdonSharpBehaviour + every derived type + UdonBehaviour + every
        // user-defined interface), ComponentArray (arrays of the IUdonEventReceiver set).
        var tag = GetUdonTypeName(target, typeParamMap);
        if (tag == "SystemObjectArray"
            || tag == "VRCUdonCommonInterfacesIUdonEventReceiver"
            || tag == "UnityEngineComponentArray")
            return false;

        return true;
    }

    public static string GetUdonTypeName(ITypeSymbol type)
    {
        // Array types
        if (type is IArrayTypeSymbol arrayType)
        {
            if (arrayType.Rank > 1) throw new System.NotSupportedException(MultidimArrayMessage);
            if (arrayType.ElementType is IArrayTypeSymbol)
                return "SystemObjectArray";
            // Delegate-element array (Func<T>[], …) → object[] of boxed bundle references, same shape as
            // aggregate arrays (element extern type SystemObject). MUST be an explicit case — the element
            // recursion would otherwise produce a bogus SystemObjectArrayArray (design §1.2).
            if (arrayType.ElementType is INamedTypeSymbol arrElemDlg && arrElemDlg.DelegateInvokeMethod != null)
                return "SystemObjectArray";
            // struct[] / tuple[] → object[] whose elements are the boxed per-element object[]. Udon has no
            // SystemObjectArrayArray (object[][]) externs, so a nested-array element type cannot be used;
            // a plain object[] holds the object[] elements as boxed objects.
            if (EmitContext.IsAggregateType(arrayType.ElementType))
                return "SystemObjectArray";
            // All types that resolve to IUdonEventReceiver use ComponentArray at runtime:
            // UdonSharpBehaviour[], derived[], UdonBehaviour[], user-interface[]
            var elemTypeName = GetUdonTypeName(arrayType.ElementType);
            if (elemTypeName == "VRCUdonCommonInterfacesIUdonEventReceiver")
                return "UnityEngineComponentArray";
            return RemapUdonType(elemTypeName) + "Array";
        }

        // Delegate type → SystemObjectArray: a delegate value is a reference to the {target, method, addr,
        // env} object[] bundle (first-class delegate ABI, design §1.2). Must precede the generic branch,
        // which would otherwise fabricate fake names like SystemFuncSystemInt32SystemInt32 / SystemAction.
        if (type is INamedTypeSymbol dlgNamed && dlgNamed.DelegateInvokeMethod != null)
            return "SystemObjectArray";

        // UdonSharpBehaviour derivatives (not UdonSharpBehaviour itself) → IUdonEventReceiver
        if (type.Name != "UdonSharpBehaviour" && IsUdonSharpBehaviour(type))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

        // User-defined interfaces → IUdonEventReceiver (runtime is always UdonBehaviour)
        if (type.TypeKind == TypeKind.Interface && type.SpecialType == SpecialType.None
            && type.ContainingNamespace?.ToDisplayString() is not ("System" or "System.Collections" or "System.Collections.Generic"))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

        // Nullable<T> → SystemObject: Udon has no Nullable type, so a nullable value is emulated as a
        // boxed object that is either null or holds the (boxed) underlying value. HasValue is a null check,
        // Value is the unboxed object. Lifted operators propagate null explicitly.
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return "SystemObject";

        // Aggregate types (tuples, user-defined structs) → SystemObjectArray (object[] emulation)
        if (EmitContext.IsAggregateType(type))
            return "SystemObjectArray";

        // User-defined enums → underlying type (Udon has no type registration for user enums).
        // SDK enums (no syntax references) are registered in Udon's type system and keep their name.
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType
            && !type.DeclaringSyntaxReferences.IsEmpty
            && !IsSdkNamespace(type.ContainingNamespace))
            return GetUdonTypeName(enumType.EnumUnderlyingType);

        // Generic types: recursively process type arguments
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var def = named.ConstructedFrom;
            var ns = def.ContainingNamespace?.ToDisplayString();
            var baseName = string.IsNullOrEmpty(ns) ? def.Name : $"{ns}.{def.Name}";
            baseName = SanitizeTypeName(baseName);
            foreach (var arg in named.TypeArguments)
                baseName += GetUdonTypeName(arg);
            return RemapUdonType(baseName);
        }

        // Non-generic fallback
        var full = type.SpecialType != SpecialType.None
            ? GetSpecialTypeName(type.SpecialType)
            : type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return RemapUdonType(SanitizeTypeName(full));
    }

    static string GetSpecialTypeName(SpecialType st) => st switch
    {
        SpecialType.System_Boolean => "System.Boolean",
        SpecialType.System_Byte => "System.Byte",
        SpecialType.System_SByte => "System.SByte",
        SpecialType.System_Int16 => "System.Int16",
        SpecialType.System_UInt16 => "System.UInt16",
        SpecialType.System_Int32 => "System.Int32",
        SpecialType.System_UInt32 => "System.UInt32",
        SpecialType.System_Int64 => "System.Int64",
        SpecialType.System_UInt64 => "System.UInt64",
        SpecialType.System_Single => "System.Single",
        SpecialType.System_Double => "System.Double",
        SpecialType.System_String => "System.String",
        SpecialType.System_Object => "System.Object",
        SpecialType.System_Void => "System.Void",
        SpecialType.System_Char => "System.Char",
        SpecialType.System_Decimal => "System.Decimal",
        SpecialType.System_Array => "System.Array",
        SpecialType.System_DateTime => "System.DateTime",
        SpecialType.System_IntPtr => "System.IntPtr",
        SpecialType.System_UIntPtr => "System.UIntPtr",
        SpecialType.System_Enum => "System.Enum",
        SpecialType.System_ValueType => "System.ValueType",
        SpecialType.System_Delegate => "System.Delegate",
        SpecialType.System_MulticastDelegate => "System.MulticastDelegate",
        SpecialType.System_Collections_IEnumerable => "System.Collections.IEnumerable",
        SpecialType.System_IDisposable => "System.IDisposable",
        SpecialType.System_Nullable_T => "System.Nullable",
        _ => throw new System.NotSupportedException($"Unsupported SpecialType: {st}")
    };

    // SDK namespaces whose enums are registered in Udon's type system.
    // In tests, these enums are source-defined stubs (DeclaringSyntaxReferences non-empty),
    // so namespace check is needed in addition to the syntax-reference check.
    static bool IsSdkNamespace(INamespaceSymbol ns)
    {
        var s = ns?.ToDisplayString();
        return s != null && (s.StartsWith("UnityEngine") || s.StartsWith("VRC")
            || s.StartsWith("TMPro") || s.StartsWith("System"));
    }

    public static string SanitizeTypeName(string fullName)
    {
        if (fullName.EndsWith("[]"))
            return SanitizeTypeName(fullName.Substring(0, fullName.Length - 2)) + "Array";
        return fullName.Replace(".", "").Replace("+", "").Replace(",", "").Replace(" ", "")
                       .Replace("<", "").Replace(">", "").Replace("?", "");
    }

    public static string BuildMethodSignature(string containingType, string methodName, string[] paramTypes, string returnType)
    {
        var sanitizedType = RemapExternType(SanitizeTypeName(containingType));
        var sanitizedParams = string.Join("_", paramTypes.Select(SanitizeTypeName));
        var sanitizedReturn = SanitizeTypeName(returnType);
        var paramPart = paramTypes.Length > 0 ? $"__{sanitizedParams}" : "";
        return $"{sanitizedType}.{methodName}{paramPart}__{sanitizedReturn}";
    }

    static string RemapExternType(string sanitizedType) => sanitizedType switch
    {
        "VRCUdonUdonBehaviour" => "VRCUdonCommonInterfacesIUdonEventReceiver",
        _ => sanitizedType
    };

    public static string BuildPropertyGetSignature(string containingType, string propertyName, string returnType)
    {
        return $"{RemapExternType(SanitizeTypeName(containingType))}.__get_{propertyName}__{SanitizeTypeName(returnType)}";
    }

    public static string BuildPropertySetSignature(string containingType, string propertyName, string valueType)
    {
        return $"{RemapExternType(SanitizeTypeName(containingType))}.__set_{propertyName}__{SanitizeTypeName(valueType)}__SystemVoid";
    }

    public static string BuildFieldSetSignature(string containingType, string fieldName, string valueType, bool isValueType = true)
    {
        var sanitized = SanitizeTypeName(containingType);
        var prefix = isValueType ? sanitized : RemapExternType(sanitized);
        var suffix = isValueType ? "" : "__SystemVoid";
        return $"{prefix}.__set_{fieldName}__{SanitizeTypeName(valueType)}{suffix}";
    }

    public static string BuildConvertSignature(string fromType, string toType)
    {
        // e.g. SystemConvert.__ToByte__SystemInt32__SystemByte
        var shortName = toType.StartsWith("System") ? toType.Substring(6) : toType;
        return $"SystemConvert.__To{shortName}__{fromType}__{toType}";
    }

    public static string BuildArrayGetSignature(string arrayType, string elemType)
    {
        return $"{arrayType}.__Get__SystemInt32__{elemType}";
    }

    public static string BuildArraySetSignature(string arrayType, string elemType)
    {
        return $"{arrayType}.__Set__SystemInt32_{elemType}__SystemVoid";
    }

    public static string BuildArrayCtorSignature(string arrayType)
    {
        return $"{arrayType}.__ctor__SystemInt32__{arrayType}";
    }

    public static string GetArrayAccessorType(IArrayTypeSymbol arrayType)
    {
        return GetUdonTypeName(arrayType);
    }

    public static string GetArrayElementAccessorType(IArrayTypeSymbol arrayType)
    {
        // Derive element type from array type name: "FooArray" → "Foo"
        var arrTypeName = GetArrayAccessorType(arrayType);
        return arrTypeName.Substring(0, arrTypeName.Length - "Array".Length);
    }

    public static string GetOperatorExternName(string csharpOperatorName)
    {
        return $"__{csharpOperatorName}";
    }

    static readonly HashSet<SpecialType> NumericSpecialTypes = new()
    {
        SpecialType.System_Byte, SpecialType.System_SByte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Single, SpecialType.System_Double,
        SpecialType.System_Char,
    };

    public static bool IsNumericType(ITypeSymbol type)
        => type != null && NumericSpecialTypes.Contains(type.SpecialType);

    static readonly HashSet<SpecialType> FloatSpecialTypes = new()
    {
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal
    };

    static readonly HashSet<SpecialType> IntegerSpecialTypes = new()
    {
        SpecialType.System_Byte, SpecialType.System_SByte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64,
    };

    public static bool IsFloatType(ITypeSymbol type)
        => type != null && FloatSpecialTypes.Contains(type.SpecialType);

    public static bool IsIntegerType(ITypeSymbol type)
        => type != null && IntegerSpecialTypes.Contains(type.SpecialType);

    static readonly HashSet<string> SyncableUdonTypes = new()
    {
        "SystemBoolean", "SystemByte", "SystemSByte",
        "SystemInt16", "SystemUInt16", "SystemInt32", "SystemUInt32",
        "SystemInt64", "SystemUInt64", "SystemSingle", "SystemDouble",
        "SystemChar", "SystemString",
        "UnityEngineVector2", "UnityEngineVector3", "UnityEngineVector4",
        "UnityEngineQuaternion", "UnityEngineColor", "UnityEngineColor32",
        "VRCSDKBaseVRCUrl",
    };

    public static bool IsSyncableType(string udonType)
    {
        if (SyncableUdonTypes.Contains(udonType)) return true;
        if (udonType.EndsWith("Array"))
            return SyncableUdonTypes.Contains(udonType.Substring(0, udonType.Length - 5));
        return false;
    }

    static readonly Dictionary<SpecialType, string> ConvertMethodNames = new()
    {
        [SpecialType.System_Byte] = "ToByte",
        [SpecialType.System_SByte] = "ToSByte",
        [SpecialType.System_Int16] = "ToInt16",
        [SpecialType.System_UInt16] = "ToUInt16",
        [SpecialType.System_Int32] = "ToInt32",
        [SpecialType.System_UInt32] = "ToUInt32",
        [SpecialType.System_Int64] = "ToInt64",
        [SpecialType.System_UInt64] = "ToUInt64",
        [SpecialType.System_Single] = "ToSingle",
        [SpecialType.System_Double] = "ToDouble",
        [SpecialType.System_Char] = "ToChar",
    };

    public static string GetConvertMethodName(ITypeSymbol targetType)
        => ConvertMethodNames.TryGetValue(targetType.SpecialType, out var name) ? name : null;

    // Resolve the extern name for user-defined implicit/explicit conversion operators.
    // Udon's extern registration may place the operator under a different containing type
    // than C#'s OperatorMethod.ContainingType (e.g. Vector2→Vector3 is under Vector2, not Vector3).
    public static string ResolveConversionExtern(IMethodSymbol operatorMethod, ITypeSymbol srcType, ITypeSymbol dstType)
    {
        var srcUdon = GetUdonTypeName(srcType);
        var dstUdon = GetUdonTypeName(dstType);
        var opName = operatorMethod.Name; // op_Implicit or op_Explicit
        var containingUdon = GetUdonTypeName(operatorMethod.ContainingType);

        // Try ContainingType first, then source type, then destination type
        var isValid = IsExternValid;
        var seen = new HashSet<string>();
        foreach (var candidate in new[] { containingUdon, srcUdon, dstUdon })
        {
            if (!seen.Add(candidate)) continue;
            var externName = $"{candidate}.__{opName}__{srcUdon}__{dstUdon}";
            if (isValid == null || isValid(externName))
                return externName;
        }

        // Fallback to ContainingType
        return $"{containingUdon}.__{opName}__{srcUdon}__{dstUdon}";
    }

    // ── Binary operator extern resolution ──

    static readonly Dictionary<BinaryOperatorKind, string> BinaryOperatorNames = new()
    {
        [BinaryOperatorKind.Add] = "op_Addition",
        [BinaryOperatorKind.Subtract] = "op_Subtraction",
        [BinaryOperatorKind.Multiply] = "op_Multiplication",
        [BinaryOperatorKind.Divide] = "op_Division",
        [BinaryOperatorKind.Remainder] = "op_Remainder",
        [BinaryOperatorKind.Equals] = "op_Equality",
        [BinaryOperatorKind.NotEquals] = "op_Inequality",
        [BinaryOperatorKind.LessThan] = "op_LessThan",
        [BinaryOperatorKind.LessThanOrEqual] = "op_LessThanOrEqual",
        [BinaryOperatorKind.GreaterThan] = "op_GreaterThan",
        [BinaryOperatorKind.GreaterThanOrEqual] = "op_GreaterThanOrEqual",
        [BinaryOperatorKind.And] = "op_LogicalAnd",
        [BinaryOperatorKind.Or] = "op_LogicalOr",
        [BinaryOperatorKind.ExclusiveOr] = "op_LogicalXor",
        [BinaryOperatorKind.LeftShift] = "op_LeftShift",
        [BinaryOperatorKind.RightShift] = "op_RightShift",
    };

    public static string ResolveBinaryExtern(
        BinaryOperatorKind operatorKind, IMethodSymbol operatorMethod,
        ITypeSymbol leftType, ITypeSymbol rightType, ITypeSymbol resultType)
    {
        var left = GetUdonTypeName(leftType);
        var right = GetUdonTypeName(rightType);
        var result = GetUdonTypeName(resultType);

        // String concat: mixed-type addition → Concat(object, object)
        if (operatorKind == BinaryOperatorKind.Add
            && (result == "SystemString" || left == "SystemString" || right == "SystemString")
            && !(left == "SystemString" && right == "SystemString"))
            return "SystemString.__Concat__SystemObject_SystemObject__SystemString";

        // Custom operator method
        if (operatorMethod != null)
        {
            var containingType = GetUdonTypeName(operatorMethod.ContainingType);
            var methodName = GetOperatorExternName(operatorMethod.Name);
            var paramTypes = operatorMethod.Parameters.Select(p => GetUdonTypeName(p.Type)).ToArray();
            var retType = GetUdonTypeName(operatorMethod.ReturnType);
            return BuildMethodSignature(containingType, methodName, paramTypes, retType);
        }

        // Enum operations → use underlying type (Udon VM has no enum-typed operators). Covers equality,
        // bitwise (&/|/^) AND relational (< > <= >=) — SDK enums keep their type name otherwise, so
        // `KeyCode.A < KeyCode.B` would emit a nonexistent UnityEngineKeyCode.__op_LessThan extern.
        if (leftType?.TypeKind == TypeKind.Enum
            && (operatorKind == BinaryOperatorKind.Equals || operatorKind == BinaryOperatorKind.NotEquals
                || operatorKind == BinaryOperatorKind.And || operatorKind == BinaryOperatorKind.Or
                || operatorKind == BinaryOperatorKind.ExclusiveOr
                || operatorKind == BinaryOperatorKind.LessThan || operatorKind == BinaryOperatorKind.GreaterThan
                || operatorKind == BinaryOperatorKind.LessThanOrEqual || operatorKind == BinaryOperatorKind.GreaterThanOrEqual))
        {
            var underlying = GetUdonTypeName(((INamedTypeSymbol)leftType).EnumUnderlyingType);
            var opName2 = BinaryOperatorNames[operatorKind];
            // Bitwise ops on enums return the enum type in C#, but Udon uses the underlying type
            var resultUnderlying = resultType?.TypeKind == TypeKind.Enum
                ? underlying : result;
            return BuildMethodSignature(underlying, $"__{opName2}", new[] { underlying, underlying }, resultUnderlying);
        }

        // Small integer types: Udon VM has no byte/sbyte/short/ushort operators;
        // C# promotes them to int, so use int operators.
        PromoteSmallInt(ref left);
        PromoteSmallInt(ref right);
        PromoteSmallInt(ref result);

        // Built-in operator
        var opName = BinaryOperatorNames.TryGetValue(operatorKind, out var name) ? name : operatorKind.ToString();
        // Decimal uses C# method names: op_Multiply (not op_Multiplication), op_Modulus (not op_Remainder)
        if (left == "SystemDecimal")
            opName = operatorKind switch
            {
                BinaryOperatorKind.Multiply => "op_Multiply",
                BinaryOperatorKind.Remainder => "op_Modulus",
                _ => opName
            };
        return BuildMethodSignature(left, $"__{opName}", new[] { left, right }, result);
    }

    /// <summary>Integer type facts — the SINGLE SOURCE OF TRUTH for Udon integer classification: bit rank and
    /// signedness of a Udon integer type (rank 0 = not an integer). Every integer predicate (IsSmallIntOrChar,
    /// lossless-widen checks, etc.) derives from THIS table; do not re-list the integer type set anywhere else.</summary>
    public static (int rank, bool signed) IntInfo(string udonType) => udonType switch
    {
        "SystemByte" => (8, false),
        "SystemSByte" => (8, true),
        "SystemInt16" => (16, true),
        "SystemUInt16" => (16, false),
        "SystemChar" => (16, false),
        "SystemInt32" => (32, true),
        "SystemUInt32" => (32, false),
        "SystemInt64" => (64, true),
        "SystemUInt64" => (64, false),
        _ => (0, false),
    };

    /// <summary>True for integer/char types narrower than int32 (byte/sbyte/short/ushort/char): Udon VM has no
    /// operators on them, so arithmetic promotes through int32 then narrows back. (E.g. Udon's SystemChar
    /// op_Addition returns SystemInt32 — there is no SystemChar+SystemChar→SystemChar extern — so char must be
    /// treated as a small int here.) Derived from <see cref="IntInfo"/>.</summary>
    public static bool IsSmallIntOrChar(string udonType) => IntInfo(udonType).rank is > 0 and < 32;

    static void PromoteSmallInt(ref string udonType)
    {
        if (IsSmallIntOrChar(udonType))
            udonType = "SystemInt32";
    }

}
