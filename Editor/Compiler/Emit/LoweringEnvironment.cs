using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Immutable services and source identity for one program compilation. This object contains no
/// current method, control-flow stack, local binding, pending work, or output graph.
/// </summary>
public sealed class LoweringEnvironment
{
    public readonly CompilationSession Session;
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly UdonAbiCatalog AbiCatalog;
    public readonly FrozenLayoutPlan Planner;

    public LoweringEnvironment(CompilationSession session, INamedTypeSymbol classSymbol,
        FrozenLayoutPlan planner)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Compilation = session.Compilation;
        ClassSymbol = classSymbol ?? throw new ArgumentNullException(nameof(classSymbol));
        AbiCatalog = session.AbiCatalog;
        Planner = planner ?? throw new ArgumentNullException(nameof(planner));
        if (!ReferenceEquals(planner.TypeFacts, session.TypeFacts))
            throw new InvalidOperationException(
                "Frozen layout plan and lowering environment must share one compilation session's type facts.");
    }

    public StorageType ResolveStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap)
    {
        var resolved = TypeEnvironment.CloseType(Compilation, type, typeParameterMap);
        if (resolved is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface
            && Planner.InterfaceIsLocalUserClassOnly(iface))
            return StorageTypes.ObjectArray;
        return Session.Types.GetStorageType(type, typeParameterMap);
    }

    public string SourceStorageName(ISymbol member)
    {
        if (member is IPropertySymbol explicitProperty
            && explicitProperty.ExplicitInterfaceImplementations.Length > 0)
            return "__ifaceprop_"
                + NameAllocator.Sanitize(ClassTypeObjectContext.SpecKey(explicitProperty.ContainingType))
                + "_" + NameAllocator.Sanitize(explicitProperty.MetadataName);
        if (member == null || member.ContainingType == null
            || SymbolEqualityComparer.Default.Equals(member.ContainingType, ClassSymbol))
            return member?.Name;
        for (var type = ClassSymbol; type != null
             && !SymbolEqualityComparer.Default.Equals(type, member.ContainingType); type = type.BaseType)
            if (type.GetMembers(member.Name).Any(candidate => candidate is IFieldSymbol or IPropertySymbol
                && !candidate.IsStatic))
                return member is IPropertySymbol
                    ? "__baseprop_" + NameAllocator.Sanitize(ClassTypeObjectContext.SpecKey(member.ContainingType))
                      + "_" + NameAllocator.Sanitize(member.MetadataName)
                    : "__basefield_" + NameAllocator.Sanitize(ClassTypeObjectContext.SpecKey(member.ContainingType))
                      + "_" + NameAllocator.Sanitize(member.MetadataName);
        return member.Name;
    }
}
