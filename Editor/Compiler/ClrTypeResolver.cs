using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

/// <summary>
/// Resolves a Roslyn type symbol to its loaded CLR type without depending on Unity. Metadata names
/// are built explicitly because Roslyn's MetadataName covers only the innermost type, while
/// reflection requires Namespace.Outer`N+Inner`N and all outer-to-inner generic arguments.
/// </summary>
public static class ClrTypeResolver
{
    public static string CacheKey(ITypeSymbol symbol)
    {
        if (symbol == null) return null;
        var assembly = symbol.ContainingAssembly?.Identity?.Name ?? "";
        return assembly + "|" + symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    public static Type Resolve(ITypeSymbol symbol, IEnumerable<Assembly> assemblies = null)
    {
        if (symbol == null) return null;

        if (symbol is IArrayTypeSymbol array)
        {
            var element = Resolve(array.ElementType, assemblies);
            if (element == null) return null;
            return array.Rank == 1 ? element.MakeArrayType() : element.MakeArrayType(array.Rank);
        }

        if (symbol is IDynamicTypeSymbol) return typeof(object);

        var special = ResolveSpecialType(symbol.SpecialType);
        if (special != null) return special;
        if (symbol is not INamedTypeSymbol named) return null;

        var loaded = (assemblies ?? AppDomain.CurrentDomain.GetAssemblies())
            .Where(assembly => assembly != null && !assembly.IsDynamic)
            .Distinct()
            .ToArray();
        var metadataName = GetMetadataName(named.OriginalDefinition);
        var preferredAssemblyName = named.ContainingAssembly?.Identity?.Name;
        var definition = FindType(loaded, metadataName, preferredAssemblyName);
        if (definition == null || !definition.IsGenericTypeDefinition)
            return definition;

        var arguments = GetGenericArguments(named);
        if (arguments.Count == 0 || arguments.Any(argument => argument is ITypeParameterSymbol))
            return definition;

        var clrArguments = new Type[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            clrArguments[i] = Resolve(arguments[i], loaded);
            if (clrArguments[i] == null) return null;
        }

        if (definition.GetGenericArguments().Length != clrArguments.Length)
            return null;
        try
        {
            return definition.MakeGenericType(clrArguments);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string GetMetadataName(INamedTypeSymbol symbol)
    {
        if (symbol == null) return null;

        var segments = new Stack<string>();
        var current = symbol.OriginalDefinition;
        while (current != null)
        {
            segments.Push(current.MetadataName);
            current = current.ContainingType?.OriginalDefinition;
        }

        var outermost = symbol;
        while (outermost.ContainingType != null)
            outermost = outermost.ContainingType;
        var ns = outermost.ContainingNamespace;
        var typeName = string.Join("+", segments);
        return ns == null || ns.IsGlobalNamespace
            ? typeName
            : ns.ToDisplayString() + "." + typeName;
    }

    static List<ITypeSymbol> GetGenericArguments(INamedTypeSymbol symbol)
    {
        var chain = new Stack<INamedTypeSymbol>();
        for (var current = symbol; current != null; current = current.ContainingType)
            chain.Push(current);

        var arguments = new List<ITypeSymbol>();
        while (chain.Count > 0)
        {
            var current = chain.Pop();
            for (int i = 0; i < current.TypeArguments.Length; i++)
                arguments.Add(current.TypeArguments[i]);
        }
        return arguments;
    }

    static Type FindType(IEnumerable<Assembly> assemblies, string metadataName,
        string preferredAssemblyName)
    {
        if (!string.IsNullOrEmpty(preferredAssemblyName))
        {
            foreach (var assembly in assemblies)
                if (string.Equals(assembly.GetName().Name, preferredAssemblyName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    var preferred = TryGetType(assembly, metadataName);
                    if (preferred != null) return preferred;
                }
        }

        foreach (var assembly in assemblies)
        {
            var type = TryGetType(assembly, metadataName);
            if (type != null) return type;
        }
        return null;
    }

    static Type TryGetType(Assembly assembly, string metadataName)
    {
        try
        {
            return assembly.GetType(metadataName, throwOnError: false, ignoreCase: false);
        }
        catch
        {
            return null;
        }
    }

    static Type ResolveSpecialType(SpecialType specialType)
    {
        switch (specialType)
        {
            case SpecialType.System_Void: return typeof(void);
            case SpecialType.System_Boolean: return typeof(bool);
            case SpecialType.System_Byte: return typeof(byte);
            case SpecialType.System_SByte: return typeof(sbyte);
            case SpecialType.System_Int16: return typeof(short);
            case SpecialType.System_UInt16: return typeof(ushort);
            case SpecialType.System_Int32: return typeof(int);
            case SpecialType.System_UInt32: return typeof(uint);
            case SpecialType.System_Int64: return typeof(long);
            case SpecialType.System_UInt64: return typeof(ulong);
            case SpecialType.System_Decimal: return typeof(decimal);
            case SpecialType.System_Single: return typeof(float);
            case SpecialType.System_Double: return typeof(double);
            case SpecialType.System_String: return typeof(string);
            case SpecialType.System_Object: return typeof(object);
            case SpecialType.System_Char: return typeof(char);
            case SpecialType.System_IntPtr: return typeof(IntPtr);
            case SpecialType.System_UIntPtr: return typeof(UIntPtr);
            default: return null;
        }
    }
}
