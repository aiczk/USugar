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
    static readonly FieldInfo HeapStorageBehaviour = typeof(UdonHeapStorageInterface)
        .GetField("behaviour", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo VariableStorageBehaviour = typeof(UdonVariableStorageInterface)
        .GetField("udonBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly MethodInfo IsExternType = typeof(UdonSharpProgramAsset).Assembly
        .GetType("UdonSharp.Compiler.Udon.CompilerUdonInterface")
        ?.GetMethod("IsExternType", BindingFlags.Public | BindingFlags.Static);

    internal static bool TryCreateStorage(
        UdonHeapStorageInterface heapStorage,
        string fieldName,
        out IValueStorage storage)
    {
        var behaviour = HeapStorageBehaviour?.GetValue(heapStorage) as UdonBehaviour;
        return TryCreateStorage(behaviour, fieldName, out storage);
    }

    internal static bool TryCreateStorage(
        UdonVariableStorageInterface variableStorage,
        string fieldName,
        out IValueStorage storage)
    {
        var behaviour = VariableStorageBehaviour?.GetValue(variableStorage) as UdonBehaviour;
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
            || definition.SystemType != typeof(object[])
            || !TryGetProxyFieldType(behaviour, fieldName, out var proxyType)
            || proxyType == typeof(object[])
            || IsUdonSharpJaggedArray(proxyType))
            return false;
        storage = (IValueStorage)Activator.CreateInstance(
            typeof(OpaqueProxyStorage<>).MakeGenericType(proxyType),
            nonPublic: true);
        return true;
    }

    static bool IsUdonSharpJaggedArray(Type type)
    {
        if (!type.IsArray || type.GetElementType()?.IsArray != true)
            return false;

        var leaf = type;
        while (leaf.IsArray)
            leaf = leaf.GetElementType();

        if (typeof(Delegate).IsAssignableFrom(leaf)
            || leaf.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            return false;
        if (leaf.IsEnum || typeof(UdonSharpBehaviour).IsAssignableFrom(leaf))
            return true;

        try
        {
            return IsExternType?.Invoke(null, new object[] { leaf }) as bool? == true;
        }
        catch
        {
            // A failed SDK probe is handled conservatively: isolate the field instead of letting
            // UdonSharp write CLR objects into an object[] that uses the USugar bundle ABI.
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
            var field = type.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            fieldType = field.FieldType;
            return true;
        }
        return false;
    }

    sealed class OpaqueProxyStorage<T> : ValueStorage<T>
    {
        public override T Value
        {
            get => default;
            set { }
        }
    }
}
