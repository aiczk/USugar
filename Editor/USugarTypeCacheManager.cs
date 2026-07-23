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
    // Cached reverse lookup: SanitizeTypeName(FullName) → CLR Type (built once per domain)
    static Dictionary<string, Type> _udonTypeCache;
    static Dictionary<string, List<Type>> _udonTypeConflicts;
    // Cached CLR type lookups (persists across compiles, assemblies don't change in-session)
    static readonly Dictionary<string, Type> _clrTypeCache = new();

    // ── Udon type resolution ──

    internal static Type ResolveUdonType(string udonTypeName)
    {
        if (_udonTypeCache == null)
        {
            _udonTypeCache = new Dictionary<string, Type>();
            _udonTypeConflicts = new Dictionary<string, List<Type>>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                foreach (var type in GetLoadableExportedTypes(asm))
                {
                    if (type.FullName == null) continue;
                    var key = ExternResolver.SanitizeTypeName(type.FullName);
                    if (!_udonTypeCache.TryGetValue(key, out var existing))
                    {
                        _udonTypeCache.Add(key, type);
                        continue;
                    }
                    if (existing == type) continue;
                    if (!_udonTypeConflicts.TryGetValue(key, out var conflicts))
                    {
                        conflicts = new List<Type> { existing };
                        _udonTypeConflicts.Add(key, conflicts);
                    }
                    if (!conflicts.Contains(type))
                        conflicts.Add(type);
                }
            }
        }
        if (_udonTypeConflicts.TryGetValue(udonTypeName, out var ambiguous))
        {
            var registered = ambiguous.Where(IsRegisteredUdonType).ToArray();
            if (registered.Length == 1)
            {
                _udonTypeCache[udonTypeName] = registered[0];
                _udonTypeConflicts.Remove(udonTypeName);
                return registered[0];
            }
            IEnumerable<Type> candidates = registered.Length > 0
                ? registered
                : ambiguous;
            throw new InvalidOperationException(
                $"Udon type name '{udonTypeName}' maps to multiple "
                + $"{(registered.Length > 1 ? "registered " : "")}CLR types: "
                + string.Join(", ", candidates
                    .Select(type => type.AssemblyQualifiedName)
                    .OrderBy(name => name, StringComparer.Ordinal)));
        }
        if (_udonTypeCache.TryGetValue(udonTypeName, out var t))
            return t;

        // Array types are constructed types not returned by GetExportedTypes().
        // Resolve by stripping "Array" suffix and constructing the array type.
        if (udonTypeName.EndsWith("Array"))
        {
            var elemType = ResolveUdonType(udonTypeName.Substring(0, udonTypeName.Length - 5));
            if (elemType != null)
            {
                var arrayType = elemType.MakeArrayType();
                _udonTypeCache[udonTypeName] = arrayType;
                return arrayType;
            }
        }

        return null;
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

    internal static Dictionary<string, FieldDefinition> BuildFieldDefinitions(INamedTypeSymbol symbol)
    {
        var defs = new Dictionary<string, FieldDefinition>();

        foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared || member.IsConst) continue;
            defs[member.Name] = CreateFieldDefinition(member);
        }

        // Auto-properties and public properties (mirroring UasmEmitter.EmitFields)
        foreach (var prop in symbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.IsImplicitlyDeclared) continue;
            var isAuto = !UasmEmitter.IsComputedProperty(prop);
            if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
            defs[prop.Name] = CreatePropertyDefinition(prop);
        }

        // Inherited fields and properties from user-defined base classes
        var declaredNames = new HashSet<string>(defs.Keys);
        var baseType = symbol.BaseType;
        while (baseType != null)
        {
            if (USugarCompilerHelper.IsFrameworkNamespace(baseType.ContainingNamespace)
                || baseType.Name == "UdonSharpBehaviour") break;

            foreach (var member in baseType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared || member.IsConst) continue;
                if (!declaredNames.Add(member.Name)) continue;
                defs[member.Name] = CreateFieldDefinition(member);
            }
            foreach (var prop in baseType.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsImplicitlyDeclared) continue;
                if (!declaredNames.Add(prop.Name)) continue;
                var isAuto = !UasmEmitter.IsComputedProperty(prop);
                if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
                defs[prop.Name] = CreatePropertyDefinition(prop);
            }
            baseType = baseType.BaseType;
        }

        // Supplement with CLR reflection to match the formatter's fieldLayout exactly.
        SupplementFromClrReflection(defs, symbol);

        return defs;
    }

    static FieldDefinition CreateFieldDefinition(IFieldSymbol field)
    {
        var userType = ResolveClrType(field.Type)
            ?? throw new InvalidOperationException(
                $"Could not resolve CLR type for field '{field.ToDisplayString()}'.");
        var systemType = IsDelegate(field.Type)
            ? typeof(object[])
            : ResolveUdonType(ExternResolver.GetUdonTypeName(field.Type))
              ?? throw new InvalidOperationException(
                  $"Could not resolve Udon storage type for field "
                  + $"'{field.ToDisplayString()}'.");
        var isSerialized = GetIsSerialized(field);
        RejectOpaqueSerializedField(field.ToDisplayString(), userType, systemType, isSerialized);
        return new FieldDefinition(
            field.Name,
            userType,
            systemType,
            GetFieldSyncMode(field),
            isSerialized,
            GetRuntimeAttributes(field));
    }

    static FieldDefinition CreatePropertyDefinition(IPropertySymbol property)
    {
        var userType = ResolveClrType(property.Type)
            ?? throw new InvalidOperationException(
                $"Could not resolve CLR type for property '{property.ToDisplayString()}'.");
        var systemType = ResolveUdonType(ExternResolver.GetUdonTypeName(property.Type))
            ?? throw new InvalidOperationException(
                $"Could not resolve Udon storage type for property "
                + $"'{property.ToDisplayString()}'.");
        var isSerialized = property.DeclaredAccessibility == Accessibility.Public;
        RejectOpaqueSerializedField(
            property.ToDisplayString(), userType, systemType, isSerialized);
        return new FieldDefinition(
            property.Name,
            userType,
            systemType,
            null,
            isSerialized,
            GetRuntimeAttributes(property));
    }

    static void SupplementFromClrReflection(Dictionary<string, FieldDefinition> defs, INamedTypeSymbol symbol)
    {
        var clrType = ResolveClrType(symbol);
        if (clrType == null || !typeof(UdonSharpBehaviour).IsAssignableFrom(clrType)) return;

        var symbolByClrType = new Dictionary<Type, INamedTypeSymbol>();
        for (var currentSymbol = symbol;
             currentSymbol != null && currentSymbol.Name != "UdonSharpBehaviour";
             currentSymbol = currentSymbol.BaseType)
        {
            var currentClrType = ClrTypeResolver.Resolve(currentSymbol);
            if (currentClrType != null)
                symbolByClrType[currentClrType] = currentSymbol;
        }

        // Walk hierarchy exactly like the formatter does (base -> derived).
        var baseTypes = new Stack<Type>();
        var current = clrType;
        while (current != null && current != typeof(UdonSharpBehaviour))
        {
            baseTypes.Push(current);
            current = current.BaseType;
        }

        while (baseTypes.Count > 0)
        {
            current = baseTypes.Pop();
            foreach (var field in current.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || defs.ContainsKey(field.Name)) continue;
                var userType = field.FieldType;
                symbolByClrType.TryGetValue(current, out var ownerSymbol);
                var fieldSymbol = ownerSymbol?.GetMembers(field.Name)
                    .OfType<IFieldSymbol>()
                    .FirstOrDefault();
                var systemType = fieldSymbol != null
                    ? ResolveUdonType(ExternResolver.GetUdonTypeName(fieldSymbol.Type))
                    : ResolveUdonStorageType(userType);
                if (systemType == null)
                    throw new InvalidOperationException(
                        $"Could not resolve Udon storage type for CLR field "
                        + $"'{current.FullName}.{field.Name}'.");
                var isSerialized = IsFieldSerializedClr(field);
                RejectOpaqueSerializedField(
                    $"{current.FullName}.{field.Name}",
                    userType,
                    systemType,
                    isSerialized);

                defs[field.Name] = new FieldDefinition(
                    field.Name,
                    userType,
                    systemType,
                    GetFieldSyncModeClr(field),
                    isSerialized,
                    field.GetCustomAttributes(false).OfType<Attribute>().ToList());
            }
        }
    }

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

    static bool IsDelegate(ITypeSymbol type)
        => type is INamedTypeSymbol named && named.DelegateInvokeMethod != null;

    // ── Storage type helpers ──

    internal static Type ResolveUdonStorageType(Type clrType)
    {
        if (clrType == null) throw new ArgumentNullException(nameof(clrType));
        if (typeof(Delegate).IsAssignableFrom(clrType))
            return typeof(object[]);
        if (clrType.IsArray)
        {
            var elem = clrType.GetElementType();
            if (elem.IsArray) return typeof(object[]);
            if (typeof(UdonSharpBehaviour).IsAssignableFrom(elem)) return typeof(Component[]);
        }
        if (clrType.IsEnum)
            return Enum.GetUnderlyingType(clrType);
        return IsRegisteredUdonType(clrType) ? clrType : typeof(object[]);
    }

    static bool IsFieldSerializedClr(FieldInfo field)
    {
        if (field.IsInitOnly || field.IsStatic) return false;
        var attributes = field.GetCustomAttributes(false).OfType<Attribute>().ToArray();
        if (attributes.Any(attribute =>
                attribute.GetType().Name == "OdinSerializeAttribute"))
            return true;
        if (attributes.Any(attribute => attribute is NonSerializedAttribute))
            return false;
        return field.IsPublic
            || attributes.Any(attribute =>
                attribute is SerializeField
                || attribute.GetType().Name == "SerializeReference");
    }

    static UdonSyncMode? GetFieldSyncModeClr(FieldInfo field)
    {
        var attr = field.GetCustomAttribute<UdonSyncedAttribute>();
        if (attr == null) return null;
        return attr.NetworkSyncType;
    }

    static bool GetIsSerialized(IFieldSymbol field)
    {
        if (field.IsConst || field.IsStatic || field.IsReadOnly) return false;
        var attrs = field.GetAttributes();
        if (attrs.Any(a => a.AttributeClass?.Name is "OdinSerializeAttribute")) return true;
        if (attrs.Any(a => a.AttributeClass?.Name is "NonSerializedAttribute")) return false;
        return field.DeclaredAccessibility == Accessibility.Public
            || attrs.Any(a => a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute"
                or "SerializeReference" or "SerializeReferenceAttribute");
    }

    static UdonSyncMode? GetFieldSyncMode(IFieldSymbol field)
    {
        var syncAttr = field.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "UdonSyncedAttribute");
        if (syncAttr == null) return null;
        if (syncAttr.ConstructorArguments.Length > 0 && syncAttr.ConstructorArguments[0].Value is int modeVal)
            return (UdonSyncMode)modeVal;
        return UdonSyncMode.None;
    }

}
