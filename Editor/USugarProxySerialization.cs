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
        return TryCreateStorage(behaviour, fieldName, out storage);
    }

    internal static bool TryCreateStorage(
        UdonVariableStorageInterface variableStorage,
        string fieldName,
        out IValueStorage storage)
    {
        var behaviour = RequireBinding(
            USugarReflectionTargets.VariableStorageBehaviour,
            nameof(USugarReflectionTargets.VariableStorageBehaviour)).GetValue(variableStorage) as UdonBehaviour;
        return TryCreateStorage(behaviour, fieldName, out storage);
    }

    static bool TryCreateStorage(
        UdonBehaviour behaviour,
        string fieldName,
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
            type =>
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
                    // A failed SDK probe is handled conservatively: isolate the field instead of
                    // letting UdonSharp write CLR objects into a USugar ABI bundle.
                    return false;
                }
            },
            type => typeof(UdonSharpBehaviour).IsAssignableFrom(type));
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
}
