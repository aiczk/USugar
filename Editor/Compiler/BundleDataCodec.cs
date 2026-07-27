using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

/// <summary>
/// Reflection bridge between Inspector-side CLR data and compiler-owned object[] bundles. The codec
/// preserves reference identity and cycles by allocating each destination before visiting its fields.
/// Native/SDK leaves are supplied by the editor integration layer and pass through unchanged.
/// </summary>
internal static class BundleDataCodec
{
    public static bool CanSerializeType(
        Type type, Func<Type, bool> isNativeLeaf,
        out string error)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));
        if (isNativeLeaf == null)
            throw new ArgumentNullException(nameof(isNativeLeaf));
        try
        {
            RequireSerializableType(
                type, isNativeLeaf, new HashSet<Type>());
            error = null;
            return true;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryEncode(
        object value, Type declaredType,
        Func<Type, bool> isNativeLeaf,
        out object encoded, out string error)
    {
        try
        {
            encoded = Encode(
                value, declaredType, isNativeLeaf,
                new Dictionary<object, object[]>(
                    ReferenceComparer.Instance));
            error = null;
            return true;
        }
        catch (NotSupportedException ex)
        {
            encoded = null;
            error = ex.Message;
            return false;
        }
    }

    public static bool TryDecode(
        object encoded, Type declaredType,
        Func<Type, bool> isNativeLeaf,
        out object value, out string error)
    {
        try
        {
            value = Decode(
                encoded, declaredType, isNativeLeaf,
                new Dictionary<object[], object>(
                    ReferenceComparer.Instance));
            error = null;
            return true;
        }
        catch (NotSupportedException ex)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    static object Encode(
        object value, Type declaredType,
        Func<Type, bool> isNativeLeaf,
        Dictionary<object, object[]> seen)
    {
        if (value == null) return null;
        var runtimeType = value.GetType();
        if (IsNative(runtimeType, isNativeLeaf))
            return value;
        if (typeof(Delegate).IsAssignableFrom(runtimeType))
            throw Unsupported(runtimeType, "delegates carry executable state");

        if (runtimeType.IsArray)
            return EncodeArray(
                (Array)value, runtimeType, isNativeLeaf, seen);
        if (!IsDataBundle(runtimeType))
            throw Unsupported(
                runtimeType, "it is neither a native leaf nor serializable data");

        if (!runtimeType.IsValueType
            && seen.TryGetValue(value, out var prior))
            return prior;

        var fields = InstanceFields(runtimeType);
        var bundle = new object[BundleAbi.HeaderSize + fields.Count];
        bundle[BundleAbi.Type] = BundleAbi.RuntimeTypeId(
            runtimeType, runtimeType.IsValueType
                ? RuntimeBundleKind.Aggregate
                : RuntimeBundleKind.Class);
        if (!runtimeType.IsValueType) seen.Add(value, bundle);
        for (var i = 0; i < fields.Count; i++)
            bundle[BundleAbi.HeaderSize + i] = Encode(
                fields[i].GetValue(value), fields[i].FieldType,
                isNativeLeaf, seen);
        return bundle;
    }

    static object EncodeArray(
        Array source, Type arrayType,
        Func<Type, bool> isNativeLeaf,
        Dictionary<object, object[]> seen)
    {
        var elementType = arrayType.GetElementType();
        if (arrayType.GetArrayRank() == 1)
        {
            if (IsNative(elementType, isNativeLeaf))
                return source;
            if (seen.TryGetValue(source, out var prior))
                return prior;
            var encoded = new object[source.Length];
            seen.Add(source, encoded);
            for (var i = 0; i < source.Length; i++)
                encoded[i] = Encode(
                    source.GetValue(i), elementType,
                    isNativeLeaf, seen);
            return encoded;
        }

        throw Unsupported(
            arrayType,
            "multidimensional arrays have no Udon representation");
    }

    static object Decode(
        object encoded, Type declaredType,
        Func<Type, bool> isNativeLeaf,
        Dictionary<object[], object> seen)
    {
        if (encoded == null) return null;
        if (IsNative(declaredType, isNativeLeaf))
            return encoded;
        if (declaredType.IsArray
            && declaredType.GetArrayRank() == 1
            && IsNative(
                declaredType.GetElementType(), isNativeLeaf))
            return encoded;
        if (encoded is not object[] bundle)
            throw Unsupported(
                declaredType, "the stored value has no USugar bundle header");
        if (seen.TryGetValue(bundle, out var prior))
            return prior;

        var runtimeType = ResolveRuntimeType(
            bundle, declaredType, isNativeLeaf);
        if (runtimeType.IsArray)
            return DecodeArray(
                bundle, runtimeType, isNativeLeaf, seen);
        if (!IsDataBundle(runtimeType))
            throw Unsupported(
                runtimeType, "the runtime identity is not data-only");

        var value = runtimeType.IsValueType
            ? Activator.CreateInstance(runtimeType)
#pragma warning disable SYSLIB0050
            : FormatterServices.GetUninitializedObject(runtimeType);
#pragma warning restore SYSLIB0050
        seen.Add(bundle, value);
        var fields = InstanceFields(runtimeType);
        if (bundle.Length != BundleAbi.HeaderSize + fields.Count)
            throw Unsupported(
                runtimeType,
                $"stored slot count {bundle.Length} does not match "
                + $"layout {BundleAbi.HeaderSize + fields.Count}");
        for (var i = 0; i < fields.Count; i++)
            fields[i].SetValue(
                value,
                Decode(
                    bundle[BundleAbi.HeaderSize + i],
                    fields[i].FieldType,
                    isNativeLeaf, seen));
        return value;
    }

    static object DecodeArray(
        object[] encoded, Type runtimeType,
        Func<Type, bool> isNativeLeaf,
        Dictionary<object[], object> seen)
    {
        var elementType = runtimeType.GetElementType();
        if (runtimeType.GetArrayRank() == 1)
        {
            var result = Array.CreateInstance(
                elementType, encoded.Length);
            seen.Add(encoded, result);
            for (var i = 0; i < encoded.Length; i++)
                result.SetValue(
                    Decode(
                        encoded[i], elementType,
                        isNativeLeaf, seen),
                    i);
            return result;
        }

        var rank = runtimeType.GetArrayRank();
        if (encoded.Length != BundleAbi.HeaderSize + 1 + rank
            || encoded[BundleAbi.HeaderSize] is not Array backing)
            throw Unsupported(
                runtimeType, "the multidimensional array header is malformed");
        var dimensions = new int[rank];
        for (var i = 0; i < rank; i++)
            dimensions[i] = Convert.ToInt32(
                encoded[BundleAbi.HeaderSize + 1 + i]);
        var resultArray = Array.CreateInstance(
            elementType, dimensions);
        seen.Add(encoded, resultArray);
        var indices = new int[rank];
        for (var flat = 0; flat < backing.Length; flat++)
        {
            var remainder = flat;
            for (var d = rank - 1; d >= 0; d--)
            {
                indices[d] = remainder % dimensions[d];
                remainder /= dimensions[d];
            }
            resultArray.SetValue(
                Decode(
                    backing.GetValue(flat), elementType,
                    isNativeLeaf, seen),
                indices);
        }
        return resultArray;
    }

    static Type ResolveRuntimeType(
        object[] bundle, Type declaredType,
        Func<Type, bool> isNativeLeaf)
    {
        if (declaredType.IsArray
            && declaredType.GetArrayRank() > 1)
            throw Unsupported(
                declaredType,
                "multidimensional arrays have no Udon representation");
        if (bundle.Length <= BundleAbi.Type
            || bundle[BundleAbi.Type] is not string runtimeTypeId)
            throw Unsupported(
                declaredType, "the stored bundle has no runtime identity");
        if (!declaredType.IsAbstract
            && !declaredType.IsInterface
            && declaredType != typeof(object))
        {
            var declaredKind = declaredType.IsValueType
                ? RuntimeBundleKind.Aggregate
                : RuntimeBundleKind.Class;
            if (BundleAbi.RuntimeTypeId(
                    declaredType, declaredKind)
                == runtimeTypeId)
                return declaredType;
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var candidate in USugarCompilerHelper.LoadableTypes(assembly))
            {
                if (candidate == null
                    || IsNative(candidate, isNativeLeaf)
                    || typeof(Delegate).IsAssignableFrom(candidate)
                    || candidate.IsArray
                        && candidate.GetArrayRank() > 1
                    || !declaredType.IsAssignableFrom(candidate))
                    continue;
                var kind = candidate.IsValueType
                    ? RuntimeBundleKind.Aggregate
                    : RuntimeBundleKind.Class;
                if (BundleAbi.RuntimeTypeId(candidate, kind)
                    == runtimeTypeId)
                    return candidate;
            }
        }
        throw Unsupported(
            declaredType,
            $"runtime identity '{runtimeTypeId}' is not loadable");
    }

    static IReadOnlyList<FieldInfo> InstanceFields(Type type)
    {
        var chain = new Stack<Type>();
        for (var current = type;
             current != null && current != typeof(object);
             current = current.BaseType)
            chain.Push(current);
        var fields = new List<FieldInfo>();
        while (chain.Count > 0)
            fields.AddRange(
                chain.Pop()
                    .GetFields(
                        BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsStatic
                                    && !field.IsLiteral)
                    .OrderBy(field => field.MetadataToken));
        return fields;
    }

    static bool IsDataBundle(Type type)
        => type.IsValueType
           || type.IsClass
              && type != typeof(string)
              && !typeof(Delegate).IsAssignableFrom(type);

    static void RequireSerializableType(
        Type type, Func<Type, bool> isNativeLeaf,
        HashSet<Type> seen)
    {
        if (IsNative(type, isNativeLeaf)) return;
        if (type.IsPointer || type.IsByRef
            || type.ContainsGenericParameters)
            throw Unsupported(type, "the declared type is open or unmanaged");
        if (typeof(Delegate).IsAssignableFrom(type))
            throw Unsupported(type, "delegates carry executable state");
        if (type == typeof(object)
            || type.IsInterface || type.IsAbstract)
            return;
        if (type.IsArray)
        {
            RequireSerializableType(
                type.GetElementType(), isNativeLeaf, seen);
            return;
        }
        if (!IsDataBundle(type))
            throw Unsupported(
                type, "it is neither a native leaf nor serializable data");
        if (!seen.Add(type)) return;
        foreach (var field in InstanceFields(type))
            RequireSerializableType(
                field.FieldType, isNativeLeaf, seen);
    }

    static bool IsNative(
        Type type, Func<Type, bool> isNativeLeaf)
        => type.IsPrimitive || type.IsEnum
           || type == typeof(string)
           || type == typeof(decimal)
           || isNativeLeaf(type);


    static NotSupportedException Unsupported(
        Type type, string reason)
        => new NotSupportedException(
            $"'{type?.FullName ?? "<unknown>"}' cannot be serialized "
            + $"as a USugar data bundle: {reason}.");

    sealed class ReferenceComparer
        : IEqualityComparer<object>, IEqualityComparer<object[]>
    {
        public static readonly ReferenceComparer Instance = new();

        bool IEqualityComparer<object>.Equals(
            object x, object y) => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj)
            => RuntimeHelpers.GetHashCode(obj);

        bool IEqualityComparer<object[]>.Equals(
            object[] x, object[] y) => ReferenceEquals(x, y);

        int IEqualityComparer<object[]>.GetHashCode(object[] obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
