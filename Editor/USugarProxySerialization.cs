using System;
using System.Reflection;
using UdonSharp;
using UdonSharp.Serialization;
using UdonSharpEditor;
using VRC.Udon;

/// <summary>
/// Keeps USugar's object[]-backed runtime values out of UdonSharp's CLR proxy serializer.
/// The proxy cannot reconstruct user classes or delegates from their USugar ABI bundles.
/// </summary>
static class USugarProxySerialization
{
    internal static bool TryCreateStorage(
        UdonHeapStorageInterface heapStorage,
        string fieldName,
        out IValueStorage storage)
    {
        var behaviour = RequireBinding(
            USugarReflectionTargets.HeapStorageBehaviour,
            nameof(USugarReflectionTargets.HeapStorageBehaviour)).GetValue(heapStorage) as UdonBehaviour;
        return TryCreateStorage(
            behaviour, fieldName,
            () => heapStorage.GetElementValueWeak(fieldName),
            value => heapStorage.SetElementValueWeak(
                fieldName, value),
            out storage);
    }

    internal static bool TryCreateStorage(
        UdonVariableStorageInterface variableStorage,
        string fieldName,
        out IValueStorage storage)
    {
        var behaviour = RequireBinding(
            USugarReflectionTargets.VariableStorageBehaviour,
            nameof(USugarReflectionTargets.VariableStorageBehaviour)).GetValue(variableStorage) as UdonBehaviour;
        return TryCreateStorage(
            behaviour, fieldName,
            () => variableStorage.GetElementValueWeak(fieldName),
            value => variableStorage.SetElementValueWeak(
                fieldName, value),
            out storage);
    }

    static bool TryCreateStorage(
        UdonBehaviour behaviour,
        string fieldName,
        Func<object> read,
        Action<object> write,
        out IValueStorage storage)
    {
        storage = null;
        if (behaviour?.programSource is not UdonSharpProgramAsset asset
            || asset.fieldDefinitions == null
            || !asset.fieldDefinitions.TryGetValue(fieldName, out var definition)
            || definition.SystemType != typeof(object[]))
            return false;
        if (!TryGetProxyFieldType(behaviour, fieldName, out var proxyType))
            throw new InvalidOperationException(
                $"USugar cannot resolve proxy field '{fieldName}' on '{behaviour.name}'. "
                + "Refusing to pass an object[] ABI field to the stock UdonSharp serializer.");
        if (!RequiresOpaqueStorage(proxyType, definition.SystemType))
            return false;
        if (BundleDataCodec.CanSerializeType(
                proxyType, IsNativeLeaf, out _))
        {
            storage = (IValueStorage)Activator.CreateInstance(
                typeof(BundleProxyStorage<>).MakeGenericType(proxyType),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { read, write },
                culture: null);
            return true;
        }
        storage = (IValueStorage)Activator.CreateInstance(
            typeof(OpaqueProxyStorage<>).MakeGenericType(proxyType),
            nonPublic: true);
        return true;
    }

    internal static bool RequiresOpaqueStorage(Type proxyType, Type systemType)
    {
        return USugarEditorIntegrationPolicy.RequiresOpaqueObjectArrayStorage(
            proxyType,
            systemType,
            IsExternType,
            type => typeof(UdonSharpBehaviour).IsAssignableFrom(type));
    }

    internal static bool CanSerializeDataBundle(
        Type proxyType, out string error)
        => BundleDataCodec.CanSerializeType(
            proxyType, IsNativeLeaf, out error);

    static bool IsNativeLeaf(Type type)
        => type.IsEnum
           || typeof(UdonSharpBehaviour)
               .IsAssignableFrom(type)
           || IsExternType(type);

    static bool IsExternType(Type type)
    {
        try
        {
            return RequireBinding(
                    USugarReflectionTargets.IsExternTypeMethod,
                    nameof(USugarReflectionTargets.IsExternTypeMethod))
                .Invoke(null, new object[] { type }) as bool? == true;
        }
        catch
        {
            return false;
        }
    }

    static bool TryGetProxyFieldType(
        UdonBehaviour behaviour,
        string fieldName,
        out Type fieldType)
    {
        fieldType = null;
        var proxyType = UdonSharpEditorUtility.GetUdonSharpBehaviourType(behaviour);
        for (var type = proxyType;
             type != null && type != typeof(UdonSharpBehaviour);
             type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var field = type.GetField(fieldName, flags);
            if (field != null)
            {
                fieldType = field.FieldType;
                return true;
            }
            var property = type.GetProperty(fieldName, flags);
            if (property != null)
            {
                fieldType = property.PropertyType;
                return true;
            }
        }
        return false;
    }

    static T RequireBinding<T>(T binding, string name) where T : MemberInfo
        => binding ?? throw new MissingMemberException(
            $"Required UdonSharp reflection target '{name}' is unavailable. "
            + "USugar proxy serialization is disabled to protect object[] ABI fields.");

    sealed class OpaqueProxyStorage<T> : ValueStorage<T>
    {
        public override T Value
        {
            get => default;
            set { }
        }
    }

    sealed class BundleProxyStorage<T> : ValueStorage<T>
    {
        readonly Func<object> _read;
        readonly Action<object> _write;

        BundleProxyStorage(
            Func<object> read, Action<object> write)
        {
            _read = read
                ?? throw new ArgumentNullException(nameof(read));
            _write = write
                ?? throw new ArgumentNullException(nameof(write));
        }

        public override T Value
        {
            get
            {
                if (!BundleDataCodec.TryDecode(
                        _read(), typeof(T), IsNativeLeaf,
                        out var value, out var error))
                    throw new InvalidOperationException(error);
                return value == null ? default : (T)value;
            }
            set
            {
                if (!BundleDataCodec.TryEncode(
                        value, typeof(T), IsNativeLeaf,
                        out var encoded, out var error))
                    throw new InvalidOperationException(error);
                _write(encoded);
            }
        }
    }
}
