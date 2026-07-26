using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using UdonSharp;
using UdonSharp.Compiler;
using UnityEngine;

/// <summary>
/// Manages CLR/Udon type resolution and caching, and builds field definitions
/// for UdonSharpProgramAsset serialization.
/// </summary>
static class USugarTypeCacheManager
{
    // Cached reverse lookup: exact, non-remapped SystemType token -> CLR Type.
    static Dictionary<UdonClrTypeId, Type> _udonTypeCache;
    static Dictionary<UdonClrTypeId, List<Type>> _udonTypeConflicts;
    // Cached CLR type lookups (persists across compiles, assemblies don't change in-session)
    static readonly Dictionary<string, Type> _clrTypeCache = new();

    // ── Udon type resolution ──

    internal static Type ResolveClrTypeToken(string clrTypeTokenName)
    {
        if (string.IsNullOrWhiteSpace(clrTypeTokenName)) return null;
        return ResolveClrTypeToken(
            UdonTypeIdentity.FromCanonicalClrTokenName(clrTypeTokenName));
    }

    internal static Type ResolveStorageClrType(UdonTypeId storageType,
        Type exactSourceType = null)
    {
        // Constructed generic types and arrays are not returned by
        // Assembly.GetExportedTypes(). Use the source CLR type only when its
        // non-remapped SystemType token exactly equals the storage identity.
        // A remapped match is insufficient: UdonSharpBehaviour,
        // UdonBehaviour, and IUdonEventReceiver share one storage identity.
        if (exactSourceType != null)
        {
            var exactToken = UdonTypeIdentity.FromClrToken(exactSourceType);
            if (string.Equals(
                    exactToken.Name, storageType.Name,
                    StringComparison.Ordinal))
            {
                EnsureUdonTypeCache();
                AddUdonTypeCandidate(exactToken, exactSourceType);
                return exactSourceType;
            }
        }
        return ResolveClrTypeToken(
            UdonTypeIdentity.FromCanonicalClrTokenName(storageType.Name));
    }

    static Type ResolveClrTypeToken(UdonClrTypeId typeToken)
    {
        EnsureUdonTypeCache();

        if (_udonTypeConflicts.TryGetValue(typeToken, out var ambiguous))
        {
            var registered = ambiguous.Where(IsRegisteredUdonType).ToArray();
            if (registered.Length == 1)
            {
                _udonTypeCache[typeToken] = registered[0];
                _udonTypeConflicts.Remove(typeToken);
                return registered[0];
            }
            IEnumerable<Type> candidates = registered.Length > 0
                ? registered
                : ambiguous;
            throw new InvalidOperationException(
                $"Udon CLR type token '{typeToken}' maps to multiple "
                + $"{(registered.Length > 1 ? "registered " : "")}CLR types: "
                + string.Join(", ", candidates
                    .Select(type => type.AssemblyQualifiedName)
                    .OrderBy(name => name, StringComparer.Ordinal)));
        }
        if (_udonTypeCache.TryGetValue(typeToken, out var t))
            return t;

        // Array types are constructed types not returned by GetExportedTypes().
        // Resolve by stripping "Array" suffix and constructing the array type.
        if (typeToken.Name.EndsWith("Array"))
        {
            var elemType = ResolveClrTypeToken(
                UdonTypeIdentity.FromCanonicalClrTokenName(
                    typeToken.Name.Substring(
                        0, typeToken.Name.Length - 5)));
            if (elemType != null)
            {
                var arrayType = elemType.MakeArrayType();
                _udonTypeCache[typeToken] = arrayType;
                return arrayType;
            }
        }

        return null;
    }

    static void EnsureUdonTypeCache()
    {
        if (_udonTypeCache != null) return;
        _udonTypeCache = new Dictionary<UdonClrTypeId, Type>();
        _udonTypeConflicts = new Dictionary<UdonClrTypeId, List<Type>>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            foreach (var type in GetLoadableExportedTypes(asm))
            {
                if (type.FullName == null) continue;
                AddUdonTypeCandidate(
                    UdonTypeIdentity.FromClrToken(type), type);
            }
        }
    }

    static void AddUdonTypeCandidate(UdonClrTypeId key, Type type)
    {
        if (!_udonTypeCache.TryGetValue(key, out var existing))
        {
            _udonTypeCache.Add(key, type);
            return;
        }
        if (existing == type) return;
        if (!_udonTypeConflicts.TryGetValue(key, out var conflicts))
        {
            conflicts = new List<Type> { existing };
            _udonTypeConflicts.Add(key, conflicts);
        }
        if (!conflicts.Contains(type))
            conflicts.Add(type);
    }

    // ── CLR type resolution ──

    internal static Type ResolveClrType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null) return null;
        var cacheKey = ClrTypeResolver.CacheKey(typeSymbol);
        if (_clrTypeCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = ClrTypeResolver.Resolve(typeSymbol);
        if (result != null)
            _clrTypeCache[cacheKey] = result;
        return result;
    }

    // ── Field definitions ──

    internal static Dictionary<string, FieldDefinition> BuildFieldDefinitions(
        FieldDiscoveryPlan fields)
    {
        if (fields == null) throw new ArgumentNullException(nameof(fields));
        var defs = new Dictionary<string, FieldDefinition>();
        foreach (var field in fields.SourceFields)
        {
            var userType = ResolveClrType(field.UserType)
                ?? throw new InvalidOperationException(
                    $"Could not resolve CLR type for field "
                    + $"'{field.Member.ToDisplayString()}'.");
            var systemType = ResolveStorageClrType(
                field.StorageType.Id, userType)
                ?? throw new InvalidOperationException(
                    $"Could not resolve Udon storage type for field "
                    + $"'{field.Member.ToDisplayString()}'.");
            RejectOpaqueSerializedField(
                field.Member.ToDisplayString(),
                userType,
                systemType,
                field.IsSerialized);
            defs.Add(
                field.Name,
                new FieldDefinition(
                    field.Name,
                    userType,
                    systemType,
                    ParseSyncMode(field.SyncMode),
                    field.IsSerialized,
                    GetRuntimeAttributes(field.Member)));
        }
        return defs;
    }

    static UdonSyncMode? ParseSyncMode(string mode)
        => mode switch
        {
            null => null,
            "none" => UdonSyncMode.None,
            "linear" => UdonSyncMode.Linear,
            "smooth" => UdonSyncMode.Smooth,
            _ => throw new InvalidOperationException(
                $"Unknown Udon sync mode '{mode}'."),
        };

    static void RejectOpaqueSerializedField(
        string fieldName,
        Type userType,
        Type systemType,
        bool isSerialized)
    {
        if (!isSerialized
            || !USugarProxySerialization.RequiresOpaqueStorage(userType, systemType))
            return;
        throw new InvalidOperationException(
            $"Serialized field '{fieldName}' uses USugar's opaque object[] ABI and cannot "
            + "round-trip through the standard UdonSharp proxy/Inspector. Mark it "
            + "[NonSerialized] and initialize it at runtime.");
    }

    static List<Attribute> GetRuntimeAttributes(ISymbol symbol)
    {
        var containingType = ClrTypeResolver.Resolve(symbol.ContainingType);
        if (containingType != null)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.DeclaredOnly;
            MemberInfo member = symbol switch
            {
                IFieldSymbol => containingType.GetField(symbol.Name, flags),
                IPropertySymbol => containingType.GetProperty(symbol.Name, flags),
                _ => null,
            };
            if (member != null)
                return member.GetCustomAttributes(false).OfType<Attribute>().ToList();
        }

        return InstantiateSymbolAttributes(symbol);
    }

    static List<Attribute> InstantiateSymbolAttributes(ISymbol symbol)
    {
        var attributes = new List<Attribute>();
        foreach (var data in symbol.GetAttributes())
        {
            var attributeType = ClrTypeResolver.Resolve(data.AttributeClass);
            if (attributeType == null || !typeof(Attribute).IsAssignableFrom(attributeType))
                continue;
            try
            {
                var constructorArgs = data.ConstructorArguments
                    .Select(ConvertAttributeConstant)
                    .ToArray();
                var instance = (Attribute)Activator.CreateInstance(attributeType, constructorArgs);
                foreach (var named in data.NamedArguments)
                {
                    var value = ConvertAttributeConstant(named.Value);
                    var property = attributeType.GetProperty(
                        named.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (property?.CanWrite == true)
                        property.SetValue(instance, value);
                    else
                    {
                        var field = attributeType.GetField(
                            named.Key, BindingFlags.Public | BindingFlags.Instance);
                        field?.SetValue(instance, value);
                    }
                }
                attributes.Add(instance);
            }
            catch (Exception ex)
            {
                USugarLog.Warn(
                    $"Could not materialize attribute '{attributeType.FullName}' on "
                    + $"'{symbol.ToDisplayString()}': {ex.Message}");
            }
        }
        return attributes;
    }

    static object ConvertAttributeConstant(TypedConstant constant)
    {
        if (constant.IsNull) return null;
        if (constant.Kind == TypedConstantKind.Type)
            return ClrTypeResolver.Resolve((ITypeSymbol)constant.Value);
        if (constant.Kind == TypedConstantKind.Array)
        {
            var elementType = constant.Type is IArrayTypeSymbol arrayType
                ? ClrTypeResolver.Resolve(arrayType.ElementType)
                : null;
            if (elementType == null)
                return constant.Values.Select(ConvertAttributeConstant).ToArray();
            var values = Array.CreateInstance(elementType, constant.Values.Length);
            for (var i = 0; i < constant.Values.Length; i++)
                values.SetValue(ConvertAttributeConstant(constant.Values[i]), i);
            return values;
        }
        if (constant.Type?.TypeKind == TypeKind.Enum)
        {
            var enumType = ClrTypeResolver.Resolve(constant.Type);
            if (enumType != null)
                return Enum.ToObject(enumType, constant.Value);
        }
        return constant.Value;
    }

    static IEnumerable<Type> GetLoadableExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type =>
                type != null && (type.IsPublic || type.IsNestedPublic));
        }
        catch (NotSupportedException)
        {
            return Array.Empty<Type>();
        }
        catch (Exception ex)
        {
            USugarLog.Warn(
                $"Could not enumerate exported CLR types from "
                + $"'{assembly.GetName().Name}': {ex.Message}");
            return Array.Empty<Type>();
        }
    }

    static bool IsRegisteredUdonType(Type type)
        => USugarReflectionTargets.IsExternTypeMethod.Invoke(
            null, new object[] { type }) as bool? == true;

}
