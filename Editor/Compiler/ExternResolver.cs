using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public static class ExternResolver
{
    // Called when a type falls back to display-string sanitization (informational).
    static Action<ITypeSymbol, string> _onTypeFallback;
    public static Action<ITypeSymbol, string> OnTypeFallback
    {
        get => Volatile.Read(ref _onTypeFallback);
        set => Volatile.Write(ref _onTypeFallback, value);
    }

    // Optional extern existence check (set by test harness or Unity editor).
    // Used to resolve ambiguous containing types for conversion operators.
    static Func<string, bool> _isExternValid;
    public static Func<string, bool> IsExternValid
    {
        get => Volatile.Read(ref _isExternValid);
        set => Volatile.Write(ref _isExternValid, value);
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
            if (arrayType.ElementType is IArrayTypeSymbol)
                return "SystemObjectArray";
            var elemTypeName = GetUdonTypeName(arrayType.ElementType, typeParamMap);
            if (elemTypeName == "VRCUdonCommonInterfacesIUdonEventReceiver")
                return "UnityEngineComponentArray";
            return RemapUdonType(elemTypeName) + "Array";
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

    public static string GetUdonTypeName(ITypeSymbol type)
    {
        // Array types
        if (type is IArrayTypeSymbol arrayType)
        {
            if (arrayType.ElementType is IArrayTypeSymbol)
                return "SystemObjectArray";
            // All types that resolve to IUdonEventReceiver use ComponentArray at runtime:
            // UdonSharpBehaviour[], derived[], UdonBehaviour[], user-interface[]
            var elemTypeName = GetUdonTypeName(arrayType.ElementType);
            if (elemTypeName == "VRCUdonCommonInterfacesIUdonEventReceiver")
                return "UnityEngineComponentArray";
            return RemapUdonType(elemTypeName) + "Array";
        }

        // UdonSharpBehaviour derivatives (not UdonSharpBehaviour itself) → IUdonEventReceiver
        if (type.Name != "UdonSharpBehaviour" && IsUdonSharpBehaviour(type))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

        // User-defined interfaces → IUdonEventReceiver (runtime is always UdonBehaviour)
        if (type.TypeKind == TypeKind.Interface && type.SpecialType == SpecialType.None
            && type.ContainingNamespace?.ToDisplayString() is not ("System" or "System.Collections" or "System.Collections.Generic"))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

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
        var sanitized = RemapUdonType(SanitizeTypeName(full));
        OnTypeFallback?.Invoke(type, sanitized);
        return sanitized;
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
        var seen = new HashSet<string>();
        foreach (var candidate in new[] { containingUdon, srcUdon, dstUdon })
        {
            if (!seen.Add(candidate)) continue;
            var externName = $"{candidate}.__{opName}__{srcUdon}__{dstUdon}";
            if (IsExternValid == null || IsExternValid(externName))
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

        // Enum comparison → use underlying type (Udon VM has no enum-typed operators)
        if (leftType?.TypeKind == TypeKind.Enum
            && (operatorKind == BinaryOperatorKind.Equals || operatorKind == BinaryOperatorKind.NotEquals))
        {
            var underlying = GetUdonTypeName(((INamedTypeSymbol)leftType).EnumUnderlyingType);
            var opName2 = BinaryOperatorNames[operatorKind];
            return BuildMethodSignature(underlying, $"__{opName2}", new[] { underlying, underlying }, result);
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

    static readonly HashSet<string> SmallIntTypes = new()
    {
        "SystemByte", "SystemSByte", "SystemInt16", "SystemUInt16",
    };

    static void PromoteSmallInt(ref string udonType)
    {
        if (SmallIntTypes.Contains(udonType))
            udonType = "SystemInt32";
    }

}
