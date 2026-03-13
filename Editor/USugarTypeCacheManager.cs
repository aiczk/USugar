using System;
using System.Collections.Concurrent;
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
    // Cached reverse lookup: SanitizeTypeName(FullName) → CLR Type (built once per domain, thread-safe)
    static readonly Lazy<ConcurrentDictionary<string, Type>> _udonTypeCache = new(() =>
    {
        var cache = new ConcurrentDictionary<string, Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try
            {
                foreach (var type in asm.GetExportedTypes())
                {
                    if (type.FullName == null) continue;
                    cache.TryAdd(ExternResolver.SanitizeTypeName(type.FullName), type);
                }
            }
            catch { }
        }
        return cache;
    });
    // Cached CLR type lookups (persists across compiles, assemblies don't change in-session)
    static readonly ConcurrentDictionary<string, Type> _clrTypeCache = new();

    // ── Udon type resolution ──

    internal static Type ResolveUdonType(string udonTypeName)
    {
        if (_udonTypeCache.Value.TryGetValue(udonTypeName, out var t))
            return t;

        // Array types are constructed types not returned by GetExportedTypes().
        // Resolve by stripping "Array" suffix and constructing the array type.
        if (udonTypeName.EndsWith("Array"))
        {
            var elemType = ResolveUdonType(udonTypeName.Substring(0, udonTypeName.Length - 5));
            if (elemType != null)
            {
                var arrayType = elemType.MakeArrayType();
                _udonTypeCache.Value.TryAdd(udonTypeName, arrayType);
                return arrayType;
            }
        }

        return null;
    }

    // ── CLR type resolution ──

    internal static Type ResolveClrType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elemType = ResolveClrType(arrayType.ElementType);
            return elemType?.MakeArrayType();
        }

        switch (typeSymbol.SpecialType)
        {
            case SpecialType.System_Boolean: return typeof(bool);
            case SpecialType.System_Byte: return typeof(byte);
            case SpecialType.System_SByte: return typeof(sbyte);
            case SpecialType.System_Int16: return typeof(short);
            case SpecialType.System_UInt16: return typeof(ushort);
            case SpecialType.System_Int32: return typeof(int);
            case SpecialType.System_UInt32: return typeof(uint);
            case SpecialType.System_Int64: return typeof(long);
            case SpecialType.System_UInt64: return typeof(ulong);
            case SpecialType.System_Single: return typeof(float);
            case SpecialType.System_Double: return typeof(double);
            case SpecialType.System_String: return typeof(string);
            case SpecialType.System_Object: return typeof(object);
            case SpecialType.System_Char: return typeof(char);
        }

        // Build CLR type name from Roslyn symbol
        var ns = typeSymbol.ContainingNamespace;
        var fullName = (ns != null && !ns.IsGlobalNamespace)
            ? $"{ns.ToDisplayString()}.{typeSymbol.MetadataName}"
            : typeSymbol.MetadataName;

        if (_clrTypeCache.TryGetValue(fullName, out var cached)) return cached;

        Type result = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try { var tp = asm.GetType(fullName); if (tp != null) { result = tp; break; } }
            catch { }
        }

        // Fall back to Udon type resolution
        result ??= ResolveUdonType(ExternResolver.GetUdonTypeName(typeSymbol));
        _clrTypeCache[fullName] = result;
        return result;
    }

    // ── Field definitions ──

    internal static Dictionary<string, FieldDefinition> BuildFieldDefinitions(INamedTypeSymbol symbol)
    {
        var defs = new Dictionary<string, FieldDefinition>();

        foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared || member.IsConst) continue;

            var userType = ResolveClrType(member.Type);
            var systemType = ResolveUdonType(ExternResolver.GetUdonTypeName(member.Type));

            defs[member.Name] = new FieldDefinition(
                member.Name,
                userType ?? systemType ?? typeof(object),
                systemType ?? userType ?? typeof(object),
                GetFieldSyncMode(member),
                GetIsSerialized(member),
                new List<Attribute>()
            );
        }

        // Auto-properties and public properties (mirroring UasmEmitter.EmitFields)
        foreach (var prop in symbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.IsImplicitlyDeclared) continue;
            var isAuto = prop.GetMethod?.IsImplicitlyDeclared == true
                || prop.SetMethod?.IsImplicitlyDeclared == true;
            if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;

            var userType = ResolveClrType(prop.Type);
            var systemType = ResolveUdonType(ExternResolver.GetUdonTypeName(prop.Type));

            defs[prop.Name] = new FieldDefinition(
                prop.Name,
                userType ?? systemType ?? typeof(object),
                systemType ?? userType ?? typeof(object),
                null,
                prop.DeclaredAccessibility == Accessibility.Public,
                new List<Attribute>()
            );
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
                var userType = ResolveClrType(member.Type);
                var systemType = ResolveUdonType(ExternResolver.GetUdonTypeName(member.Type));
                defs[member.Name] = new FieldDefinition(
                    member.Name,
                    userType ?? systemType ?? typeof(object),
                    systemType ?? userType ?? typeof(object),
                    GetFieldSyncMode(member),
                    GetIsSerialized(member),
                    new List<Attribute>());
            }
            foreach (var prop in baseType.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsImplicitlyDeclared) continue;
                if (!declaredNames.Add(prop.Name)) continue;
                var isAuto = prop.GetMethod?.IsImplicitlyDeclared == true
                    || prop.SetMethod?.IsImplicitlyDeclared == true;
                if (!isAuto && prop.DeclaredAccessibility != Accessibility.Public) continue;
                var userType = ResolveClrType(prop.Type);
                var systemType = ResolveUdonType(ExternResolver.GetUdonTypeName(prop.Type));
                defs[prop.Name] = new FieldDefinition(
                    prop.Name,
                    userType ?? systemType ?? typeof(object),
                    systemType ?? userType ?? typeof(object),
                    null,
                    prop.DeclaredAccessibility == Accessibility.Public,
                    new List<Attribute>());
            }
            baseType = baseType.BaseType;
        }

        // Supplement with CLR reflection to match the formatter's fieldLayout exactly.
        SupplementFromClrReflection(defs, symbol);

        return defs;
    }

    static void SupplementFromClrReflection(Dictionary<string, FieldDefinition> defs, INamedTypeSymbol symbol)
    {
        var clrType = ResolveClrType(symbol);
        if (clrType == null || !typeof(UdonSharpBehaviour).IsAssignableFrom(clrType)) return;

        // Walk hierarchy exactly like the formatter does (base → derived)
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
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.IsStatic || defs.ContainsKey(field.Name)) continue;

                var userType = field.FieldType;
                var systemType = ResolveUdonStorageType(userType);

                defs[field.Name] = new FieldDefinition(
                    field.Name,
                    userType,
                    systemType,
                    GetFieldSyncModeClr(field),
                    IsFieldSerializedClr(field),
                    new List<Attribute>());
            }
        }
    }

    // ── Storage type helpers ──

    internal static Type ResolveUdonStorageType(Type clrType)
    {
        if (clrType.IsArray)
        {
            var elem = clrType.GetElementType();
            if (elem.IsArray) return typeof(object[]);
            if (typeof(UdonSharpBehaviour).IsAssignableFrom(elem)) return typeof(Component[]);
        }
        return clrType;
    }

    static bool IsFieldSerializedClr(FieldInfo field)
    {
        if (field.IsInitOnly || field.IsStatic) return false;
        if (field.IsDefined(typeof(NonSerializedAttribute), false)) return false;
        return field.IsPublic
            || field.IsDefined(typeof(SerializeField), false);
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

    // ── Program asset lookup ──

    internal static UdonSharpProgramAsset FindProgramAsset(string className, string sourceFilePath,
        Dictionary<string, List<(UdonSharpProgramAsset asset, string scriptPath)>> lookup)
    {
        if (lookup == null || !lookup.TryGetValue(className, out var candidates)) return null;
        UdonSharpProgramAsset fallback = null;
        foreach (var (asset, scriptPath) in candidates)
        {
            if (sourceFilePath != null
                && sourceFilePath.Replace('\\', '/').EndsWith(scriptPath.Replace('\\', '/')))
                return asset;
            fallback ??= asset;
        }
        if (candidates.Count > 1 && fallback != null)
            USugarLog.Warn($"Multiple UdonSharpProgramAssets found for class '{className}'. Using first match. Consider using unique class names.");
        return fallback;
    }
}
