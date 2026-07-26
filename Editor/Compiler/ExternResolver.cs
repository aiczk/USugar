using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public static class ExternResolver
{
    /// <summary>A source-defined class whose shape is outside class ABI v1.</summary>
    public static bool IsUnsupportedUserClass(ITypeSymbol type)
    {
        if (!IsPlainUserClass(type)) return false;
        if (TypeClassifier.IsUserClass(type)) return false; // CA-M1: a v1 class is now a supported object[] type
        return true;
    }

    // Loud reject for a source class outside class ABI v1: a native base, a record, or another
    // unsupported source shape. Foreign SDK types are identified semantically by their assembly/namespace,
    // never by a coincidentally matching extern owner name.
    static void RejectIfUnsupportedUserClass(ITypeSymbol type, string udonName)
    {
        if (!IsPlainUserClass(type)) return;
        if (TypeClassifier.IsUserClass(type)) return; // CA-M1: v1 class is supported (object[1+F] bundle)
        var named = (INamedTypeSymbol)type;
        if (named.BaseType != null && named.BaseType.SpecialType != SpecialType.System_Object)
            throw new NotSupportedException(
                $"User-defined class '{named.Name}' has a base type ('{named.BaseType.Name}') other than "
                + "System.Object. Class ABI v1 supports only a class with no explicit base — a UdonSharpBehaviour "
                + "subclass is a behaviour (reference it through a scene object), and an SDK/native base type is "
                + "not supported on a plain class.");
        if (named.IsRecord)
            throw new NotSupportedException(
                $"Record type '{named.Name}' is not supported. User-defined reference types have no Udon heap "
                + "representation in this compiler (class ABI v1). Use a struct (value type) instead.");
        throw new NotSupportedException(
            $"User-defined reference types (class '{named.Name}') are not supported yet: the Udon VM has no heap "
            + "representation for a user class in this compiler (class ABI v1). Convert it to a struct (value "
            + "type), or reference a UdonSharpBehaviour through a scene object.");
    }

    // Rank>1 array type (int[,], …) has no native Udon representation — it is emulated as an
    // object[1+r] bundle: [0] = typed flat backing (T[], row-major), [1..r] = boxed dimension
    // lengths (N-dim array design, 2026-07-04 §0). GetUdonTypeName folds it to the SAME
    // SystemObjectArray tag as delegates/structs/tuples — every runtime-type-test /sync-type check
    // that already rejects those (IsRuntimeDistinguishable, IsSyncableType) rejects a Rank>1 array
    // for free, with no new code (N-R2/N-R3).
    internal const string MultidimExternArgMessage =
        "A multi-dimensional array (T[,], …) cannot be passed at an extern call boundary: its runtime "
        + "value is an object[] bundle (flat backing + dimension lengths), not a real multi-rank array, "
        + "so an extern parameter would silently receive the wrong shape. Pass the flat backing "
        + "explicitly, or restructure the call.";

    /// <summary>A user-authored reference type (class Foo {...}, record Foo): TypeKind.Class, source-defined
    /// in this compilation, not an SDK/Unity/System stand-in, and not a UdonSharpBehaviour. Distinct from a
    /// genuine foreign/SDK class (VRCUrl, DataList, …), which routes through the SAME extern-name-based
    /// method/ctor dispatch but has REAL registered externs — that distinction can only be checked at a
    /// specific call site (via UdonAbiBinder against the exact candidate name), not from the type alone, so
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

    // CLR/storage identity and extern ownership are not always the same in the SDK. VRC_Pickup
    // remains a real return/storage type, but its instance members are registered under VRCPickup.
    static readonly Dictionary<string, string> ExternOwnerTypeRemap = new()
    {
        ["VRCSDKBaseVRC_Pickup"] = "VRCSDK3ComponentsVRCPickup",
    };

    public static string RemapUdonType(string sanitizedType)
    {
        return UdonTypeRemap.TryGetValue(sanitizedType, out var remapped) ? remapped : sanitizedType;
    }

    public static string RemapExternOwnerType(string sanitizedType)
    {
        var runtimeType = RemapUdonType(sanitizedType);
        return ExternOwnerTypeRemap.TryGetValue(runtimeType, out var remapped)
            ? remapped
            : runtimeType;
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

    internal static string LowerUdonStorageName(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        Func<UdonTypeId, bool> isRegisteredUdonType)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (isRegisteredUdonType == null)
            throw new ArgumentNullException(nameof(isRegisteredUdonType));
        if (type is ITypeParameterSymbol tp && typeParamMap != null
            && typeParamMap.TryGetValue(tp, out var resolved))
        {
            // B70 armor: a self-referential binding (T→T, from an UNCLOSED containing-type spec whose
            // bindings were composed as identity) makes this recursion infinite — a process-killing stack
            // overflow. Never recurse into it; fail loud and named. A correctly CLOSED map maps T→a concrete
            // type, so this never fires on a well-formed spec.
            if (SymbolEqualityComparer.Default.Equals(resolved, tp))
                throw new NotSupportedException(
                    $"Type parameter '{tp.Name}' resolves to itself in the monomorphization map — a "
                    + "self-referential binding from an unclosed containing-type specialization. The generic "
                    + "was not fully monomorphized to a concrete type at this emit site.");
            return LowerUdonStorageName(
                resolved, typeParamMap, isRegisteredUdonType);
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            // N-dim bundle (design §0/§2): the array VALUE itself is an object[1+r] bundle, same
            // runtime tag as struct/tuple/delegate arrays — never the per-element "...Array" name
            // computed below (that name is for the FLAT BACKING array, a distinct rank-1 symbol
            // callers synthesize via _compilation.CreateArrayTypeSymbol(elementType, 1)).
            if (arrayType.Rank > 1) return "SystemObjectArray";
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
            // struct[] / tuple[] / class[] → object[] of boxed object[] elements (no SystemObjectArrayArray).
            if (TypeClassifier.IsObjectArrayEmulated(elementType))
                return "SystemObjectArray";
            var elemTypeName = LowerUdonStorageName(
                elementType, typeParamMap, isRegisteredUdonType);
            if (elemTypeName == "VRCUdonCommonInterfacesIUdonEventReceiver")
                return "UnityEngineComponentArray";
            return RemapUdonType(elemTypeName) + "Array";
        }

        // Delegate type → SystemObjectArray: the runtime value is a reference to the tagged
        // {kind, target, method, addr, env} object[] bundle. Must precede the constructed-generic
        // branch below, which would otherwise fabricate fake names like SystemFuncSystemInt32SystemInt32.
        if (type is INamedTypeSymbol dlgWithMap && dlgWithMap.DelegateInvokeMethod != null)
            return "SystemObjectArray";

        // A constructed generic UdonSharpBehaviour is still represented by its scene
        // IUdonEventReceiver. This must precede generic-name construction below.
        if (type.Name != "UdonSharpBehaviour"
            && IsUdonSharpBehaviour(type, typeParamMap))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

        // Constructed generic carrying type-param args (e.g. a delegate parameter Func<T,int> of a generic
        // method): the no-map overload's generic branch would resolve the args WITHOUT the map, leaving a
        // literal "T" in the name → an invalid extern type. Recurse the args WITH the map here. Nullable /
        // aggregate / enum generics are intentionally left to the no-map overload, which ignores their type
        // args (→ SystemObject / SystemObjectArray / underlying) and so needs no substitution.
        if (typeParamMap != null && type is INamedTypeSymbol named && named.IsGenericType
            && named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            && type.TypeKind != TypeKind.Enum
            && !IsUserInterface(type)
            && !TypeClassifier.IsObjectArrayEmulated(type))
        {
            var def = named.ConstructedFrom;
            var ns = def.ContainingNamespace?.ToDisplayString();
            var baseName = SanitizeTypeName(string.IsNullOrEmpty(ns) ? def.Name : $"{ns}.{def.Name}");
            foreach (var arg in named.TypeArguments)
                baseName += LowerUdonStorageName(
                    arg, typeParamMap, isRegisteredUdonType);
            var genericName = RemapUdonType(baseName);
            RejectIfUnsupportedUserClass(type, genericName); // a generic user class (Node<int>) has no externs
            return genericName;
        }

        return LowerUdonStorageName(type, isRegisteredUdonType);
    }

    public static bool IsUdonSharpBehaviour(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        if (type is ITypeParameterSymbol tp2 && typeParamMap != null
            && typeParamMap.TryGetValue(tp2, out var resolved2))
            return IsUdonSharpBehaviour(resolved2);
        return IsUdonSharpBehaviour(type);
    }

    static string LowerUdonStorageName(ITypeSymbol type,
        Func<UdonTypeId, bool> isRegisteredUdonType)
    {
        var name = ComputeUdonTypeName(
            type, isRegisteredUdonType);
        RejectIfUnsupportedUserClass(type, name);
        return name;
    }

    static string ComputeUdonTypeName(ITypeSymbol type,
        Func<UdonTypeId, bool> isRegisteredUdonType)
    {
        // Array types
        if (type is IArrayTypeSymbol arrayType)
        {
            if (arrayType.Rank > 1) return "SystemObjectArray"; // N-dim bundle, see the with-map overload above
            if (arrayType.ElementType is IArrayTypeSymbol)
                return "SystemObjectArray";
            // Delegate-element array (Func<T>[], …) → object[] of boxed bundle references, same shape as
            // aggregate arrays (element extern type SystemObject). MUST be an explicit case — the element
            // recursion would otherwise produce a bogus SystemObjectArrayArray (design §1.2).
            if (arrayType.ElementType is INamedTypeSymbol arrElemDlg && arrElemDlg.DelegateInvokeMethod != null)
                return "SystemObjectArray";
            // struct[] / tuple[] / class[] → object[] whose elements are the boxed per-element object[]. Udon
            // has no SystemObjectArrayArray (object[][]) externs, so a nested-array element type cannot be
            // used; a plain object[] holds the object[] elements as boxed objects.
            if (TypeClassifier.IsObjectArrayEmulated(arrayType.ElementType))
                return "SystemObjectArray";
            // All types that resolve to IUdonEventReceiver use ComponentArray at runtime:
            // UdonSharpBehaviour[], derived[], UdonBehaviour[], user-interface[]
            var elemTypeName = LowerUdonStorageName(
                arrayType.ElementType, isRegisteredUdonType);
            if (elemTypeName == "VRCUdonCommonInterfacesIUdonEventReceiver")
                return "UnityEngineComponentArray";
            return RemapUdonType(elemTypeName) + "Array";
        }

        // Delegate type → SystemObjectArray: a delegate value is a reference to the tagged
        // {kind, target, method, addr, env} object[] bundle. Must precede the generic branch,
        // which would otherwise fabricate fake names like SystemFuncSystemInt32SystemInt32 / SystemAction.
        if (type is INamedTypeSymbol dlgNamed && dlgNamed.DelegateInvokeMethod != null)
            return "SystemObjectArray";

        // UdonSharpBehaviour derivatives (not UdonSharpBehaviour itself) → IUdonEventReceiver
        if (type.Name != "UdonSharpBehaviour" && IsUdonSharpBehaviour(type))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

        // User-defined interfaces → IUdonEventReceiver (runtime is always UdonBehaviour)
        if (IsUserInterface(type))
            return "VRCUdonCommonInterfacesIUdonEventReceiver";

        // Nullable<T> → SystemObject: Udon has no Nullable type, so a nullable value is emulated as a
        // boxed object that is either null or holds the (boxed) underlying value. HasValue is a null check,
        // Value is the unboxed object. Lifted operators propagate null explicitly.
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return "SystemObject";

        // Aggregate (tuple/user struct) OR v1 user class → SystemObjectArray (object[] emulation)
        if (TypeClassifier.IsObjectArrayEmulated(type))
            return "SystemObjectArray";

        // Enum storage is a registry question, not a source-origin question.
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var exactEnumType = UdonTypeIdentity.FromStorage(type);
            if (!isRegisteredUdonType(exactEnumType))
                return LowerUdonStorageName(
                    enumType.EnumUnderlyingType, isRegisteredUdonType);
        }

        // Generic types: recursively process type arguments
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var def = named.ConstructedFrom;
            var ns = def.ContainingNamespace?.ToDisplayString();
            var baseName = string.IsNullOrEmpty(ns) ? def.Name : $"{ns}.{def.Name}";
            baseName = SanitizeTypeName(baseName);
            foreach (var arg in named.TypeArguments)
                baseName += LowerUdonStorageName(
                    arg, isRegisteredUdonType);
            return RemapUdonType(baseName);
        }

        // Non-generic type-name path
        var full = type.SpecialType != SpecialType.None
            ? GetSpecialTypeName(type.SpecialType)
            : type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return RemapUdonType(SanitizeTypeName(full));
    }

    internal static string GetSpecialTypeName(SpecialType st) => st switch
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

    // THE single SDK-namespace predicate (hand-enumeration audit 2026-07-17, item ③) — every
    // enum/struct/class classification (here and in EmitPolicy) routes through it, so the SDK
    // boundary cannot diverge per consumer. Chain-walk with EXACT segment match, never a
    // display-string prefix: a namespace named "SystemFoo" is a USER namespace, while ANY segment
    // named after an SDK root (Outer.System.Inner) marks the chain SDK. In tests these SDK types
    // are source-defined stubs (DeclaringSyntaxReferences non-empty), so the namespace check is
    // needed in addition to the syntax-reference check.
    internal static bool IsSdkNamespace(INamespaceSymbol ns)
    {
        for (var n = ns; n != null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
        {
            if (n.Name is "System" or "UnityEngine" or "VRC" or "Cinemachine"
                or "TMPro" or "Unity" or "Microsoft")
                return true;
        }
        return false;
    }

    public static bool IsUserInterface(ITypeSymbol type)
        => type is INamedTypeSymbol named
           && named.TypeKind == TypeKind.Interface
           && named.SpecialType == SpecialType.None
           && !IsSdkNamespace(named.ContainingNamespace);

    public static string SanitizeTypeName(string fullName)
    {
        if (fullName.EndsWith("[]"))
            return SanitizeTypeName(fullName.Substring(0, fullName.Length - 2)) + "Array";
        return fullName.Replace(".", "").Replace("+", "").Replace(",", "").Replace(" ", "")
                       .Replace("<", "").Replace(">", "").Replace("?", "");
    }

    // The IUdonEventReceiver cross-program marshalling externs — the single source for the
    // SetProgramVariable / SendCustomEvent / GetProgramVariable signatures shared by every copy-in / dispatch
    // producer (CoreBuilder + the handlers) and the FlatVerify recognizer, which formerly substring-matched
    // the literal and would silently stop matching if the format drifted.
    public static readonly UdonAbiKey EventReceiverSetProgramVariable =
        UdonAbiKey.VoidMethod("VRCUdonCommonInterfacesIUdonEventReceiver",
            "SetProgramVariable", "SystemString", "SystemObject");
    public static readonly UdonAbiKey EventReceiverSendCustomEvent =
        UdonAbiKey.VoidMethod("VRCUdonCommonInterfacesIUdonEventReceiver",
            "SendCustomEvent", "SystemString");
    public static readonly UdonAbiKey EventReceiverGetProgramVariable =
        UdonAbiKey.Method("VRCUdonCommonInterfacesIUdonEventReceiver",
            "GetProgramVariable", new[] { "SystemString" }, "SystemObject");

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

    /// <summary>Numeric for CONVERSION purposes — the System.Convert extern surface includes Decimal,
    /// unlike the operator-oriented <see cref="IsNumericType"/> set (Udon's Decimal operators are named
    /// and gated per-site). Conversion guards must use this one: gating on IsNumericType lets a
    /// decimal↔numeric cast fall through to the identity passthrough as a raw COPY into a mistyped slot
    /// (B72 family, caught by the UasmValidator COPY declared-type axis).</summary>
    public static bool IsConvertibleNumericType(ITypeSymbol type)
        => IsNumericType(type) || type?.SpecialType == SpecialType.System_Decimal;

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
        [SpecialType.System_Decimal] = "ToDecimal",
        [SpecialType.System_Char] = "ToChar",
    };

    public static string GetConvertMethodName(ITypeSymbol targetType)
        => ConvertMethodNames.TryGetValue(targetType.SpecialType, out var name) ? name : null;

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

    static readonly Dictionary<UnaryOperatorKind, string> UnaryOperatorNames = new()
    {
        [UnaryOperatorKind.Minus] = "op_UnaryMinus",
        [UnaryOperatorKind.Not] = "op_UnaryNegation",
    };

    public static UdonAbiKey ResolveBuiltInUnaryExtern(
        UnaryOperatorKind operatorKind,
        ITypeSymbol operandType,
        ITypeSymbol resultType,
        Func<ITypeSymbol, string> getUdonType)
    {
        if (getUdonType == null)
            throw new ArgumentNullException(nameof(getUdonType));
        var operand = getUdonType(operandType);
        var result = getUdonType(resultType);
        if (!UnaryOperatorNames.TryGetValue(operatorKind, out var opName))
            throw new NotSupportedException(
                $"Unary operator kind '{operatorKind}' has no Udon "
                + $"extern mapping for '{operand}'.");
        if (operand == "SystemDecimal"
            && operatorKind == UnaryOperatorKind.Minus)
            opName = "op_UnaryNegation";
        return UdonAbiKey.Method(
            operand, opName, new[] { operand }, result);
    }

    public static UdonAbiKey ResolveBuiltInBinaryExtern(
        BinaryOperatorKind operatorKind,
        ITypeSymbol leftType, ITypeSymbol rightType, ITypeSymbol resultType,
        Func<ITypeSymbol, string> getUdonType)
    {
        if (getUdonType == null)
            throw new ArgumentNullException(nameof(getUdonType));
        var left = getUdonType(leftType);
        var right = getUdonType(rightType);
        var result = getUdonType(resultType);

        // String concat: mixed-type addition → Concat(object, object)
        if (operatorKind == BinaryOperatorKind.Add
            && (result == "SystemString" || left == "SystemString" || right == "SystemString")
            && !(left == "SystemString" && right == "SystemString"))
            return UdonAbiKey.Method("SystemString", "Concat",
                new[] { "SystemObject", "SystemObject" }, "SystemString");

        // Enum operations → use underlying type (Udon VM has no enum-typed operators). Covers compound
        // arithmetic (+/-), equality, bitwise (&/|/^), and relational (< > <= >=). SDK enums keep their
        // type name otherwise, so resolving any of these through that name fabricates a nonexistent
        // enum-owner extern.
        if (leftType?.TypeKind == TypeKind.Enum
            && (operatorKind == BinaryOperatorKind.Add || operatorKind == BinaryOperatorKind.Subtract
                || operatorKind == BinaryOperatorKind.Equals || operatorKind == BinaryOperatorKind.NotEquals
                || operatorKind == BinaryOperatorKind.And || operatorKind == BinaryOperatorKind.Or
                || operatorKind == BinaryOperatorKind.ExclusiveOr
                || operatorKind == BinaryOperatorKind.LessThan || operatorKind == BinaryOperatorKind.GreaterThan
                || operatorKind == BinaryOperatorKind.LessThanOrEqual || operatorKind == BinaryOperatorKind.GreaterThanOrEqual))
        {
            var underlying = getUdonType(
                ((INamedTypeSymbol)leftType).EnumUnderlyingType);
            var opName2 = BinaryOperatorNames[operatorKind];
            // Every value-producing operation promotes a small underlying through Int32. Udon has no
            // byte/sbyte/short/ushort value operators, and the emitter computes their arithmetic and
            // bitwise results in Int32-promoted slots. Bool-producing comparisons retain the underlying
            // width because those equality/relational externs exist.
            if (operatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
                or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr)
                PromoteSmallInt(ref underlying);
            // Value-producing operations on enums return the enum type in C#, but Udon uses the
            // underlying representation.
            var resultUnderlying = resultType?.TypeKind == TypeKind.Enum
                ? underlying : result;
            return UdonAbiKey.Method(underlying, opName2,
                new[] { underlying, underlying }, resultUnderlying);
        }

        // Small integer types: Udon VM has no byte/sbyte/short/ushort operators;
        // C# promotes them to int, so use int operators.
        PromoteSmallInt(ref left);
        PromoteSmallInt(ref right);
        PromoteSmallInt(ref result);

        // Built-in operator. A kind outside the table must fail loudly: the old fallback minted
        // "{left}.__{kind}__…" from ToString() — a bogus extern name only the extern gate could catch,
        // opaque in a production SDK assembly. Every unlisted kind is unreachable from the handler
        // layer today: ConditionalAnd/ConditionalOr are lowered to short-circuit control flow in
        // OperatorHandler.VisitBinary before any extern resolution (so neither the plain, lifted, nor
        // compound paths can pass them here), and Power/IntegerDivide/ObjectValueEquals/
        // ObjectValueNotEquals/Like/Concatenate are VB-only kinds a C# IOperation tree never produces
        // — this throw is a backstop for a new kind, not a live path.
        if (!BinaryOperatorNames.TryGetValue(operatorKind, out var opName))
            throw new NotSupportedException(
                $"Binary operator kind '{operatorKind}' has no Udon extern operator-name mapping.");
        // Decimal uses C# method names: op_Multiply (not op_Multiplication), op_Modulus (not op_Remainder)
        if (left == "SystemDecimal")
            opName = operatorKind switch
            {
                BinaryOperatorKind.Multiply => "op_Multiply",
                BinaryOperatorKind.Remainder => "op_Modulus",
                _ => opName
            };
        return UdonAbiKey.Method(left, opName, new[] { left, right }, result);
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
