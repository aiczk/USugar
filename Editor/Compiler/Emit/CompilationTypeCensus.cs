using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>Immutable source-type census shared by every emitter for one Roslyn Compilation.</summary>
public sealed class CompilationTypeCensus
{
    static readonly ConditionalWeakTable<Compilation, CompilationTypeCensus> Cache = new();

    public static CompilationTypeCensus For(Compilation compilation)
        => Cache.GetValue(compilation, c => new CompilationTypeCensus(c));

    public IReadOnlyList<INamedTypeSymbol> Classes { get; }
    public IReadOnlyList<INamedTypeSymbol> Interfaces { get; }
    public IReadOnlyList<INamedTypeSymbol> Structs { get; }
    public IReadOnlyList<INamedTypeSymbol> PortableClassCandidates { get; }

    CompilationTypeCensus(Compilation compilation)
    {
        var classes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var interfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var structs = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type) continue;
                switch (type.TypeKind)
                {
                    case TypeKind.Class: classes.Add(type); break;
                    case TypeKind.Interface: interfaces.Add(type); break;
                    case TypeKind.Struct: structs.Add(type); break;
                }
            }
        }
        Classes = classes.OrderBy(ClassTypeObjectContext.SpecKey, System.StringComparer.Ordinal).ToArray();
        Interfaces = interfaces.OrderBy(ClassTypeObjectContext.SpecKey, System.StringComparer.Ordinal).ToArray();
        Structs = structs.OrderBy(ClassTypeObjectContext.SpecKey, System.StringComparer.Ordinal).ToArray();

        var roots = Classes.Where(ExternResolver.IsUdonSharpBehaviour)
            .SelectMany(PortableSurfaceTypes).SelectMany(ExpandPortableTypes)
            .OfType<INamedTypeSymbol>().ToArray();
        var classRoots = roots.Where(TypeClassifier.IsUserClass).ToArray();
        var interfaceRoots = roots.Where(t => t.TypeKind == TypeKind.Interface).ToArray();
        var portable = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var root in classRoots)
            if (!root.IsAbstract && !ClassTypeObjectContext.ContainsTypeParameter(root)) portable.Add(root);
        foreach (var type in Classes)
            if (!type.IsAbstract && !ClassTypeObjectContext.ContainsTypeParameter(type)
                && TypeClassifier.IsUserClass(type)
                && (classRoots.Any(root => VirtualDispatch.IsAssignable(type, root))
                    || interfaceRoots.Any(iface => type.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i, iface)))))
                portable.Add(type);
        PortableClassCandidates = portable.OrderBy(ClassTypeObjectContext.SpecKey,
            System.StringComparer.Ordinal).ToArray();
    }

    static IEnumerable<ITypeSymbol> PortableSurfaceTypes(INamedTypeSymbol behaviour)
    {
        for (var owner = behaviour; owner != null && owner.DeclaringSyntaxReferences.Length > 0;
             owner = owner.BaseType)
        {
            foreach (var field in owner.GetMembers().OfType<IFieldSymbol>())
                if (!field.IsStatic && (field.DeclaredAccessibility == Accessibility.Public
                    || field.GetAttributes().Any(a => a.AttributeClass?.Name is
                        "SerializeField" or "SerializeFieldAttribute"))) yield return field.Type;
            foreach (var property in owner.GetMembers().OfType<IPropertySymbol>())
                if (!property.IsStatic && property.DeclaredAccessibility == Accessibility.Public)
                    yield return property.Type;
            foreach (var method in owner.GetMembers().OfType<IMethodSymbol>())
                if (!method.IsStatic && method.DeclaredAccessibility == Accessibility.Public)
                {
                    if (!method.ReturnsVoid) yield return method.ReturnType;
                    foreach (var parameter in method.Parameters) yield return parameter.Type;
                }
        }
    }

    static IEnumerable<ITypeSymbol> ExpandPortableTypes(ITypeSymbol root)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (type == null || ClassTypeObjectContext.ContainsTypeParameter(type) || !seen.Add(type)) continue;
            yield return type;
            if (type is IArrayTypeSymbol array) { pending.Push(array.ElementType); continue; }
            if (type is not INamedTypeSymbol named) continue;
            foreach (var argument in named.TypeArguments) pending.Push(argument);
            if (!TypeClassifier.IsUserClass(named)) continue;
            foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
                if (!field.IsStatic) pending.Push(field.Type);
        }
    }
}
