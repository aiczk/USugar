using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;

/// <summary>Owns the stable runtime type-id registry for user classes. Bundle slot 0 stores an
/// injective structural string key, so identity survives transport between Udon programs.</summary>
public sealed class ClassTypeObjectContext
{
    readonly Dictionary<INamedTypeSymbol, string> _vars = new(SymbolEqualityComparer.Default);
    bool _published;

    /// <summary>The minted concrete user classes in this program (constructed-spec identity). Drives
    /// both the _start typeobj allocation and the is-test enumeration set.</summary>
    public IReadOnlyCollection<INamedTypeSymbol> RuntimeClasses => _vars.Keys;

    public void Seed(IEnumerable<INamedTypeSymbol> runtimeClasses)
    {
        if (_published)
            throw new InvalidOperationException(
                "The class type-object table is frozen.");
        var unique = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var c in runtimeClasses)
            if (c != null && !c.IsImplicitlyDeclared && c.Name != "<Module>"
                && c.Name != "<PrivateImplementationDetails>") unique.Add(c);
        var classes = unique.ToList();
        var legacyCounts = classes.GroupBy(LegacyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var c in classes)
            if (!_vars.ContainsKey(c))
            {
                ClassAbiPolicy.AssertClosed(c, "typeobj registry seed");
                var legacy = LegacyName(c);
                var name = "__typeobj_" + (legacyCounts[legacy] == 1 ? legacy : SpecKey(c));
                if (_vars.Values.Contains(name))
                    throw new System.InvalidOperationException(
                        $"Typeobj key collision for '{c.ToDisplayString()}': '{name}'.");
                _vars[c] = name;
            }
    }

    internal ClassTypeObjectContext Publish()
    {
        if (_published)
            throw new InvalidOperationException(
                "The class type-object table was published twice.");
        _published = true;
        return this;
    }

    /// <summary>The heap-var name holding this class's typeobj, or null if the class is never minted in
    /// this program (so no instance of it can exist here — an is-test against it is vacuously false).</summary>
    public string TryGetTypeObjVar(INamedTypeSymbol classTy)
        => _vars.TryGetValue(classTy, out var v) ? v : null;

    /// <summary>The typeobj vars of every minted class whose runtime type would satisfy `is targetType`
    /// (the class IS targetType or derives from it). Compile-time closed-world enumeration (charter #2).</summary>
    public IEnumerable<string> TypeObjVarsAssignableTo(INamedTypeSymbol targetType)
    {
        foreach (var kv in _vars)
            if (IsAssignable(kv.Key, targetType)) yield return kv.Value;
    }

    /// <summary>Deep type-parameter test: `Box&lt;T&gt;` nested as `Outer&lt;Box&lt;T&gt;&gt;`'s argument is just as
    /// open as a bare `T` — the shallow `is ITypeParameterSymbol` test misses it.</summary>
    public static bool ContainsTypeParameter(ITypeSymbol t) => t switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
        INamedTypeSymbol n => ContainsTypeParameter(n.ContainingType)
            || n.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    static bool IsAssignable(INamedTypeSymbol concrete, INamedTypeSymbol target)
    {
        for (var t = concrete; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target)
                || SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, target.OriginalDefinition)
                   && SymbolEqualityComparer.Default.Equals(t, target))
                return true;
        return false;
    }

    /// <summary>Traversal-independent, injective structural key used for typeobj names and ordering.</summary>
    public static string SpecKey(ITypeSymbol type)
    {
        var b = new StringBuilder();
        AppendTypeKey(b, type);
        return b.ToString();
    }

    /// <summary>Reflection-side spelling of <see cref="SpecKey(ITypeSymbol)"/> used by the Unity
    /// serializer. It deliberately mirrors Roslyn documentation IDs instead of using
    /// AssemblyQualifiedName, so recompiling an asmdef does not invalidate serialized bundles.</summary>
    public static string SpecKey(Type type)
    {
        var b = new StringBuilder();
        AppendTypeKey(b, type);
        return b.ToString();
    }

    public static string RuntimeTypeId(INamedTypeSymbol type)
        => BundleAbi.KindTag(RuntimeBundleKind.Class) + SpecKey(type);

    static string LegacyName(INamedTypeSymbol type)
    {
        var name = type.Name;
        if (type.IsGenericType)
            name += "_" + string.Join("_", type.TypeArguments.Select(a => a.Name));
        return name;
    }

    static void AppendTypeKey(StringBuilder b, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol a:
                b.Append('A').Append(a.Rank).Append('_');
                AppendTypeKey(b, a.ElementType);
                return;
            case ITypeParameterSymbol p:
                b.Append('P');
                AppendSegment(b, p.ContainingSymbol?.GetDocumentationCommentId() ?? "");
                AppendSegment(b, p.Name);
                return;
            case INamedTypeSymbol n:
                if (n.ContainingType != null)
                {
                    b.Append('O');
                    AppendTypeKey(b, n.ContainingType);
                }
                b.Append('N');
                AppendSegment(b, n.OriginalDefinition.GetDocumentationCommentId()
                    ?? n.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                b.Append(n.TypeArguments.Length).Append('_');
                foreach (var arg in n.TypeArguments) AppendTypeKey(b, arg);
                return;
            default:
                b.Append('T');
                AppendSegment(b, type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "null");
                return;
        }
    }

    static void AppendTypeKey(StringBuilder b, Type type)
    {
        if (type.IsArray)
        {
            b.Append('A').Append(type.GetArrayRank()).Append('_');
            AppendTypeKey(b, type.GetElementType());
            return;
        }
        if (type.IsGenericParameter)
        {
            b.Append('P');
            AppendSegment(b, ReflectionOwnerId(type.DeclaringMethod)
                             ?? ReflectionOwnerId(type.DeclaringType)
                             ?? "");
            AppendSegment(b, type.Name);
            return;
        }

        var definition = type.IsGenericType
            ? type.GetGenericTypeDefinition()
            : type;
        if (definition.DeclaringType != null)
        {
            b.Append('O');
            AppendTypeKey(b, ClosedDeclaringType(type));
        }
        b.Append('N');
        AppendSegment(b, "T:" + ReflectionDeclarationName(definition));
        var ownArguments = OwnGenericArguments(type);
        b.Append(ownArguments.Length).Append('_');
        foreach (var argument in ownArguments)
            AppendTypeKey(b, argument);
    }

    static Type ClosedDeclaringType(Type type)
    {
        var declaring = type.DeclaringType;
        if (declaring == null || !declaring.IsGenericTypeDefinition)
            return declaring;
        var parentArity = declaring.GetGenericArguments().Length;
        var arguments = type.GetGenericArguments();
        if (parentArity == 0 || arguments.Length < parentArity)
            return declaring;
        return declaring.MakeGenericType(
            arguments.Take(parentArity).ToArray());
    }

    static Type[] OwnGenericArguments(Type type)
    {
        if (!type.IsGenericType) return Array.Empty<Type>();
        var all = type.GetGenericArguments();
        var inherited = type.DeclaringType?.GetGenericArguments().Length ?? 0;
        return inherited == 0
            ? all
            : all.Skip(Math.Min(inherited, all.Length)).ToArray();
    }

    static string ReflectionDeclarationName(Type type)
    {
        var segments = new Stack<string>();
        for (var current = type; current != null;
             current = current.DeclaringType)
            segments.Push(current.Name);
        var prefix = string.IsNullOrEmpty(type.Namespace)
            ? ""
            : type.Namespace + ".";
        return prefix + string.Join(".", segments);
    }

    static string ReflectionOwnerId(MemberInfo member)
        => member switch
        {
            Type t => "T:" + ReflectionDeclarationName(
                t.IsGenericType ? t.GetGenericTypeDefinition() : t),
            MethodBase m => "M:"
                + ReflectionDeclarationName(m.DeclaringType)
                + "." + m.Name,
            _ => null,
        };

    static void AppendSegment(StringBuilder b, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        b.Append(bytes.Length).Append('_');
        foreach (var x in bytes) b.Append(x.ToString("x2"));
        b.Append('_');
    }
}
