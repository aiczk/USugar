using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Per-program type authority used only while semantic inputs are being
/// materialized. Every answer is recorded; publishing severs the SDK catalog
/// and mutable verifier-fact dependencies.
/// </summary>
internal sealed class MaterializingUdonTypeSystem : IUdonTypeSystem
{
    readonly UdonTypeSystem _source;
    readonly Dictionary<string, UdonTypeLowering> _lowerings =
        new(StringComparer.Ordinal);
    bool _published;

    public MaterializingUdonTypeSystem(UdonTypeSystem source)
        => _source = source
            ?? throw new ArgumentNullException(nameof(source));

    public UdonTypeLowering Describe(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        RequireMutable();
        var key = BoundTypeKey.Create(type, typeParameterMap);
        var lowering = _source.Describe(type, typeParameterMap);
        if (_lowerings.TryGetValue(key, out var existing))
        {
            if (existing.Storage != lowering.Storage
                || existing.Representation != lowering.Representation
                || existing.RuntimeTypeTest != lowering.RuntimeTypeTest)
                throw new InvalidOperationException(
                    $"Type decision '{key}' changed while materializing "
                    + "the bound program.");
            return existing;
        }
        _lowerings.Add(key, lowering);
        return lowering;
    }

    public bool IsFoldedEnum(ITypeSymbol type)
        => Describe(type).IsFoldedEnum;

    public bool IsRuntimeDistinguishable(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => Describe(type, typeParameterMap).IsRuntimeDistinguishable;

    public string GetUdonTypeName(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage.Name;

    public StorageType GetStorageType(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage;

    public BoundUdonTypeSystem Publish()
    {
        RequireMutable();
        _published = true;
        return new BoundUdonTypeSystem(_lowerings);
    }

    void RequireMutable()
    {
        if (_published)
            throw new InvalidOperationException(
                "The type system was already published.");
    }
}

/// <summary>
/// Exhaustive source-tree type census. It records operation result types and
/// the complete symbol signatures that body lowering can reference even when
/// a parameter or return value has no child operation of its own.
/// </summary>
internal sealed class TypeDemandPlanner
{
    readonly IUdonTypeSystem _types;
    readonly Compilation _compilation;
    readonly HashSet<string> _recording =
        new(StringComparer.Ordinal);

    public TypeDemandPlanner(
        IUdonTypeSystem types,
        Compilation compilation)
    {
        _types = types
            ?? throw new ArgumentNullException(nameof(types));
        _compilation = compilation
            ?? throw new ArgumentNullException(nameof(compilation));
        var intrinsicTypes = new[]
        {
            SpecialType.System_Object,
            SpecialType.System_Boolean,
            SpecialType.System_Char,
            SpecialType.System_SByte,
            SpecialType.System_Byte,
            SpecialType.System_Int16,
            SpecialType.System_UInt16,
            SpecialType.System_Int32,
            SpecialType.System_UInt32,
            SpecialType.System_Int64,
            SpecialType.System_UInt64,
            SpecialType.System_Decimal,
            SpecialType.System_Single,
            SpecialType.System_Double,
            SpecialType.System_String,
        };
        foreach (var special in intrinsicTypes)
        {
            var type = compilation.GetSpecialType(special);
            if (type.TypeKind != TypeKind.Error)
                Record(type, null);
        }
        Record(
            compilation.CreateArrayTypeSymbol(
                compilation.GetSpecialType(
                    SpecialType.System_Object)),
            null);
    }

    public void Plan(
        IOperation operation,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap)
    {
        if (operation == null) return;
        Record(operation.Type, typeParameterMap);
        switch (operation)
        {
            case IInvocationOperation invocation:
                Plan(invocation.TargetMethod, typeParameterMap);
                break;
            case IObjectCreationOperation creation:
                Plan(creation.Constructor, typeParameterMap);
                break;
            case IMethodReferenceOperation methodReference:
                Plan(methodReference.Method, typeParameterMap);
                break;
            case IFieldReferenceOperation field:
                Record(field.Field.Type, typeParameterMap);
                Record(field.Field.ContainingType, typeParameterMap);
                break;
            case IPropertyReferenceOperation property:
                Record(property.Property.Type, typeParameterMap);
                Plan(property.Property.GetMethod, typeParameterMap);
                Plan(property.Property.SetMethod, typeParameterMap);
                break;
            case IEventReferenceOperation eventReference:
                Record(eventReference.Event.Type, typeParameterMap);
                Plan(eventReference.Event.AddMethod, typeParameterMap);
                Plan(eventReference.Event.RemoveMethod, typeParameterMap);
                break;
            case IConversionOperation conversion:
                Record(conversion.Operand.Type, typeParameterMap);
                Plan(conversion.OperatorMethod, typeParameterMap);
                break;
            case IBinaryOperation binary:
                Plan(binary.OperatorMethod, typeParameterMap);
                break;
            case IUnaryOperation unary:
                Plan(unary.OperatorMethod, typeParameterMap);
                break;
            case ITypeOfOperation typeOf:
                Record(typeOf.TypeOperand, typeParameterMap);
                break;
            case IIsTypeOperation isType:
                Record(isType.TypeOperand, typeParameterMap);
                break;
            case IDeclarationPatternOperation declarationPattern:
                Record(declarationPattern.MatchedType, typeParameterMap);
                break;
            case IRecursivePatternOperation recursivePattern:
                Record(recursivePattern.MatchedType, typeParameterMap);
                break;
            case ITypePatternOperation typePattern:
                Record(typePattern.MatchedType, typeParameterMap);
                break;
        }
    }

    public void Plan(
        IMethodSymbol method,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap)
    {
        if (method == null) return;
        Record(method.ContainingType, typeParameterMap);
        Record(method.ReturnType, typeParameterMap);
        foreach (var parameter in method.Parameters)
            Record(parameter.Type, typeParameterMap);
        foreach (var argument in method.TypeArguments)
            Record(argument, typeParameterMap);
    }

    void Record(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap)
    {
        if (type == null || type.SpecialType == SpecialType.System_Void)
            return;
        if (type is INamedTypeSymbol { IsAnonymousType: true })
            return;
        var key = BoundTypeKey.Create(type, typeParameterMap);
        if (!_recording.Add(key)) return;
        try
        {
            _types.Describe(type, typeParameterMap);
            if (typeParameterMap != null
                && type is INamedTypeSymbol openNamed
                && ClassTypeObjectContext.ContainsTypeParameter(openNamed)
                && (TypeClassifier.IsObjectArrayEmulated(openNamed)
                    || ExternResolver.IsUdonSharpBehaviour(openNamed)))
                _types.Describe(type);
            switch (type)
            {
                case IArrayTypeSymbol array:
                    Record(array.ElementType, typeParameterMap);
                    if (array.Rank > 1)
                        Record(
                            NdimArrayAbi.BackingType(
                                _compilation, array),
                            typeParameterMap);
                    break;
                case INamedTypeSymbol named:
                    if (named.EnumUnderlyingType != null)
                        Record(
                            named.EnumUnderlyingType,
                            typeParameterMap);
                    foreach (var argument in named.TypeArguments)
                        Record(argument, typeParameterMap);
                    if (named.DelegateInvokeMethod is { } invoke)
                        Plan(invoke, typeParameterMap);
                    if (TypeClassifier.IsObjectArrayEmulated(named))
                    {
                        foreach (var field in named.GetMembers()
                                     .OfType<IFieldSymbol>())
                            Record(field.Type, typeParameterMap);
                        foreach (var property in named.GetMembers()
                                     .OfType<IPropertySymbol>())
                            Record(property.Type, typeParameterMap);
                    }
                    break;
            }
        }
        finally
        {
            _recording.Remove(key);
        }
    }
}

/// <summary>
/// Immutable type decisions owned by <see cref="BoundProgram"/>. An
/// unplanned type query is a compiler bug, never a request to fall back to
/// the compilation session.
/// </summary>
internal sealed class BoundUdonTypeSystem : IUdonTypeSystem
{
    readonly IReadOnlyDictionary<string, UdonTypeLowering> _lowerings;

    public BoundUdonTypeSystem(
        IDictionary<string, UdonTypeLowering> lowerings)
        => _lowerings =
            new ReadOnlyDictionary<string, UdonTypeLowering>(
                new Dictionary<string, UdonTypeLowering>(
                    lowerings
                    ?? throw new ArgumentNullException(nameof(lowerings)),
                    StringComparer.Ordinal));

    public UdonTypeLowering Describe(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        var key = BoundTypeKey.Create(type, typeParameterMap);
        if (_lowerings.TryGetValue(key, out var lowering))
            return lowering;
        throw new InvalidOperationException(
            $"Type decision '{key}' was absent from the bound program.");
    }

    public bool IsFoldedEnum(ITypeSymbol type)
        => Describe(type).IsFoldedEnum;

    public bool IsRuntimeDistinguishable(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => Describe(type, typeParameterMap).IsRuntimeDistinguishable;

    public string GetUdonTypeName(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage.Name;

    public StorageType GetStorageType(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
        => Describe(type, typeParameterMap).Storage;
}

internal static class BoundTypeKey
{
    public static string Create(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
#pragma warning disable RS1024 // Declaration identity is required for fresh specialization twins.
        return Create(
            type, typeParameterMap,
            new HashSet<ITypeParameterSymbol>(
                TypeParamIdComparer.Instance));
#pragma warning restore RS1024
    }

    static string Create(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap,
        HashSet<ITypeParameterSymbol> resolving)
    {
        if (type is ITypeParameterSymbol parameter
            && TryResolve(typeParameterMap, parameter, out var resolved)
            && !SymbolEqualityComparer.Default.Equals(parameter, resolved))
        {
            if (!resolving.Add(parameter))
                throw new InvalidOperationException(
                    $"Cyclic type-parameter mapping for '{parameter}'.");
            var result = Create(resolved, typeParameterMap, resolving);
            resolving.Remove(parameter);
            return result;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                return "array:"
                    + array.Rank + ":"
                    + Create(
                        array.ElementType,
                        typeParameterMap,
                        resolving);
            case IPointerTypeSymbol pointer:
                return "pointer:"
                    + Create(
                        pointer.PointedAtType,
                        typeParameterMap,
                        resolving);
            case ITypeParameterSymbol open:
                return "parameter:"
                    + (open.ContainingSymbol
                        ?.OriginalDefinition
                        .GetDocumentationCommentId()
                       ?? open.ContainingSymbol?.ToDisplayString()
                       ?? "?")
                    + ":" + open.Ordinal;
            case INamedTypeSymbol named:
                var definition = named.OriginalDefinition;
                var identity =
                    definition.ContainingAssembly?.Identity.Name
                    + ":"
                    + (definition.GetDocumentationCommentId()
                       ?? definition.ToDisplayString(
                           SymbolDisplayFormat.FullyQualifiedFormat));
                if (named.TypeArguments.Length == 0)
                    return "named:" + identity;
                return "named:" + identity + "<"
                    + string.Join(
                        ",",
                        named.TypeArguments.Select(argument =>
                            Create(
                                argument,
                                typeParameterMap,
                                resolving)))
                    + ">";
            default:
                return type.TypeKind + ":"
                    + type.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }

    static bool TryResolve(
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        ITypeParameterSymbol parameter,
        out ITypeSymbol resolved)
    {
        if (map != null)
            foreach (var pair in map)
                if (TypeParamIdComparer.Instance.Equals(
                        pair.Key, parameter))
                {
                    resolved = pair.Value;
                    return true;
                }
        resolved = null;
        return false;
    }
}
