using System;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Canonical identity of one Udon type name. Strings enter this domain only
/// through <see cref="UdonTypeIdentity"/> or an installed-SDK snapshot.
/// </summary>
public readonly struct UdonTypeId : IEquatable<UdonTypeId>
{
    public string Name { get; }

    internal UdonTypeId(string canonicalName)
    {
        if (string.IsNullOrWhiteSpace(canonicalName))
            throw new ArgumentException(
                "A canonical Udon type name is required.", nameof(canonicalName));
        Name = canonicalName;
    }

    public bool Equals(UdonTypeId other)
        => string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object obj)
        => obj is UdonTypeId other && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(Name ?? "");

    public override string ToString() => Name ?? "";

    public static bool operator ==(UdonTypeId left, UdonTypeId right)
        => left.Equals(right);

    public static bool operator !=(UdonTypeId left, UdonTypeId right)
        => !left.Equals(right);
}

/// <summary>
/// The sole canonical spelling producer for CLR and Roslyn type identities.
/// Each frontend first builds the same small structural shape; formatting and
/// Udon remapping happen once after that boundary.
/// </summary>
internal static class UdonTypeIdentity
{
    public static UdonTypeId From(Type type)
        => Shape.From(type).ToId();

    public static UdonTypeId From(ITypeSymbol type)
        => Shape.From(type).ToId();

    public static UdonTypeId FromCanonicalName(string canonicalName)
        => new(canonicalName);

    sealed class Shape
    {
        enum ShapeKind
        {
            Raw,
            Array,
            Generic,
        }

        readonly ShapeKind _kind;
        readonly string _baseName;
        readonly Shape _element;
        readonly Shape[] _arguments;

        Shape(ShapeKind kind, string baseName,
            Shape element = null, Shape[] arguments = null)
        {
            _kind = kind;
            _baseName = baseName;
            _element = element;
            _arguments = arguments ?? Array.Empty<Shape>();
        }

        public static Shape From(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.IsByRef)
                type = type.GetElementType()
                    ?? throw new InvalidOperationException(
                        "A by-ref CLR type has no element type.");
            if (type.IsArray)
                return new Shape(ShapeKind.Array, null,
                    From(type.GetElementType()
                         ?? throw new InvalidOperationException(
                             "A CLR array has no element type.")));
            if (type.IsGenericParameter)
                return new Shape(ShapeKind.Raw, type.Name);
            if (!type.IsGenericType)
                return new Shape(
                    ShapeKind.Raw, type.FullName ?? type.Name);

            var definition = type.GetGenericTypeDefinition();
            return new Shape(
                ShapeKind.Generic,
                QualifiedDefinitionName(
                    definition.Namespace, definition.Name),
                arguments: Array.ConvertAll(
                    type.GetGenericArguments(), From));
        }

        public static Shape From(ITypeSymbol type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type is IArrayTypeSymbol array)
                return new Shape(
                    ShapeKind.Array, null, From(array.ElementType));
            if (type is ITypeParameterSymbol parameter)
                return new Shape(ShapeKind.Raw, parameter.Name);
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                var definition = named.ConstructedFrom;
                return new Shape(
                    ShapeKind.Generic,
                    QualifiedDefinitionName(
                        definition.ContainingNamespace?.ToDisplayString(),
                        definition.Name),
                    arguments: Array.ConvertAll(
                        named.TypeArguments.ToArray(), From));
            }

            var fullName = type.SpecialType != SpecialType.None
                ? ExternResolver.GetSpecialTypeName(type.SpecialType)
                : type.ToDisplayString(
                    SymbolDisplayFormat.CSharpErrorMessageFormat);
            return new Shape(ShapeKind.Raw, fullName);
        }

        public UdonTypeId ToId()
        {
            string canonicalName;
            switch (_kind)
            {
                case ShapeKind.Raw:
                    canonicalName = ExternResolver.SanitizeTypeName(_baseName);
                    break;
                case ShapeKind.Array:
                    canonicalName = _element.ToId().Name + "Array";
                    break;
                case ShapeKind.Generic:
                    canonicalName = ExternResolver.SanitizeTypeName(_baseName);
                    foreach (var argument in _arguments)
                        canonicalName += argument.ToId().Name;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown Udon type shape '{_kind}'.");
            }
            return new UdonTypeId(
                ExternResolver.RemapUdonType(canonicalName));
        }

        static string QualifiedDefinitionName(
            string typeNamespace, string definitionName)
        {
            if (string.IsNullOrEmpty(definitionName))
                throw new InvalidOperationException(
                    "A generic type definition has no name.");
            var arityMarker = definitionName.IndexOf('`');
            if (arityMarker >= 0)
                definitionName = definitionName.Substring(0, arityMarker);
            return string.IsNullOrEmpty(typeNamespace)
                ? definitionName
                : typeNamespace + "." + definitionName;
        }
    }
}
