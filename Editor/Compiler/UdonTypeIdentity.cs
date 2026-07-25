using System;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Canonical identity of one Udon storage type after Udon remapping. Strings
/// enter this domain only through <see cref="UdonTypeIdentity"/> or an
/// installed-SDK snapshot.
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
/// Canonical spelling of an exact CLR <see cref="Type"/> token before Udon
/// storage remapping. Storage identity is many-to-one (for example,
/// UdonSharpBehaviour and UdonBehaviour both lower to IUdonEventReceiver);
/// runtime SystemType lookup must retain this distinct identity.
/// </summary>
public readonly struct UdonClrTypeId : IEquatable<UdonClrTypeId>
{
    public string Name { get; }

    internal UdonClrTypeId(string canonicalName)
    {
        if (string.IsNullOrWhiteSpace(canonicalName))
            throw new ArgumentException(
                "A canonical CLR type-token name is required.",
                nameof(canonicalName));
        Name = canonicalName;
    }

    public bool Equals(UdonClrTypeId other)
        => string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object obj)
        => obj is UdonClrTypeId other && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(Name ?? "");

    public override string ToString() => Name ?? "";

    public static bool operator ==(UdonClrTypeId left, UdonClrTypeId right)
        => left.Equals(right);

    public static bool operator !=(UdonClrTypeId left, UdonClrTypeId right)
        => !left.Equals(right);
}

/// <summary>
/// The sole structural spelling producer for storage identities and CLR
/// SystemType tokens. Both frontends build the same shape; the caller must
/// choose explicitly whether Udon storage remapping is applied.
/// </summary>
internal static class UdonTypeIdentity
{
    public static UdonTypeId FromStorage(Type type)
        => Shape.From(type).ToStorageId();

    public static UdonTypeId FromStorage(ITypeSymbol type)
        => Shape.From(type).ToStorageId();

    public static UdonTypeId FromCanonicalStorageName(string canonicalName)
        => new(canonicalName);

    public static UdonClrTypeId FromClrToken(Type type)
        => Shape.From(type).ToClrTypeId();

    public static UdonClrTypeId FromCanonicalClrTokenName(
        string canonicalName)
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

        public UdonTypeId ToStorageId()
            => new(ToCanonicalName(applyStorageRemap: true));

        public UdonClrTypeId ToClrTypeId()
            => new(ToCanonicalName(applyStorageRemap: false));

        string ToCanonicalName(bool applyStorageRemap)
        {
            string canonicalName;
            switch (_kind)
            {
                case ShapeKind.Raw:
                    canonicalName = ExternResolver.SanitizeTypeName(_baseName);
                    break;
                case ShapeKind.Array:
                    canonicalName = _element.ToCanonicalName(
                        applyStorageRemap) + "Array";
                    break;
                case ShapeKind.Generic:
                    canonicalName = ExternResolver.SanitizeTypeName(_baseName);
                    foreach (var argument in _arguments)
                        canonicalName += argument.ToCanonicalName(
                            applyStorageRemap);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown Udon type shape '{_kind}'.");
            }
            return applyStorageRemap
                ? ExternResolver.RemapUdonType(canonicalName)
                : canonicalName;
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
