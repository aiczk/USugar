using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Direction of one stack operand in an SDK extern node definition.</summary>
public enum UdonAbiParameterMode
{
    In,
    Out,
    InOut,
}

/// <summary>
/// Directional compatibility between an SDK extern operand and the Core-IR storage supplied for
/// it. This is intentionally separate from <see cref="RawCopyCompatibility"/>: Udon COPY may move
/// any reference strongbox, while a wrapper input must be able to read the value as its declared
/// CLR type and a wrapper output must produce a value valid for the destination representation.
/// </summary>
public static class ExternOperandCompatibility
{
    public static string WhyIncompatible(string abiType, string storageType,
        UdonAbiParameterMode mode, UdonTypeFactRegistry facts)
    {
        if (facts == null) throw new ArgumentNullException(nameof(facts));
        switch (mode)
        {
            case UdonAbiParameterMode.In:
                return WhyNotAssignable(storageType, abiType, facts);
            case UdonAbiParameterMode.Out:
                return WhyNotAssignable(abiType, storageType, facts);
            case UdonAbiParameterMode.InOut:
            {
                var input = WhyNotAssignable(storageType, abiType, facts);
                if (input != null) return "input direction: " + input;
                var output = WhyNotAssignable(abiType, storageType, facts);
                return output == null ? null : "output direction: " + output;
            }
            default:
                throw new InvalidOperationException($"Unknown ABI parameter mode: {mode}");
        }
    }

    static string WhyNotAssignable(string from, string to, UdonTypeFactRegistry facts)
    {
        if (from == to) return null;

        // These are representation identities, not CLR conversions. User enums lower to Int32 and
        // Nullable<T> is erased to its bare/object representation before reaching the wrapper.
        if (IsNullableErasure(from, to) || IsNullableErasure(to, from)) return null;
        if (to == "SystemInt32" && facts.IsEnumFact(from) == true) return null;
        if (from == "SystemInt32" && facts.IsEnumFact(to) == true) return null;

        // object is a top type only in this direction. A statically-object value contains no proof
        // that a typed wrapper can retrieve it as an arbitrary T.
        if (to == "SystemObject") return null;
        if (from == "SystemObject")
            return $"'SystemObject' does not prove a value readable as '{to}'";

        var assignable = facts.IsAssignableFact(from, to);
        if (assignable == true) return null;
        if (assignable == null)
        {
            if (facts.IsReferenceFact(from) == null) return NoFact(from);
            if (facts.IsReferenceFact(to) == null) return NoFact(to);
        }
        return $"no directed assignability fact from '{from}' to '{to}'";
    }

    static bool IsNullableErasure(string boxed, string bare) =>
        boxed.StartsWith("SystemNullable", StringComparison.Ordinal)
        && boxed.Substring("SystemNullable".Length) == bare;

    static string NoFact(string name) =>
        $"no fact recorded for '{name}' (neither source type minting nor the installed SDK ABI "
        + "snapshot classified it)";
}

/// <summary>
/// Type pattern supplied by the SDK for one extern stack operand. Most operands
/// are exact storage types; generic node definitions retain their placeholder
/// identity instead of erasing it to SystemObject.
/// </summary>
public sealed class UdonAbiType
{
    public enum PatternKind
    {
        Exact,
        GenericParameter,
        Array,
    }

    public PatternKind Kind { get; }
    public StorageType ExactType { get; }
    public string GenericName { get; }
    public UdonAbiType ElementType { get; }

    UdonAbiType(PatternKind kind, StorageType exactType, string genericName,
        UdonAbiType elementType)
    {
        Kind = kind;
        ExactType = exactType;
        GenericName = genericName;
        ElementType = elementType;
    }

    public static UdonAbiType Exact(string storageType)
        => new(PatternKind.Exact, new StorageType(storageType), null, null);

    public static UdonAbiType Generic(string name)
        => new(PatternKind.GenericParameter, default,
            !string.IsNullOrEmpty(name)
                ? name
                : throw new ArgumentException("A generic ABI placeholder name is required.", nameof(name)),
            null);

    public static UdonAbiType Array(UdonAbiType elementType)
        => new(PatternKind.Array, default, null,
            elementType ?? throw new ArgumentNullException(nameof(elementType)));

    /// <summary>
    /// Match a concrete Core-IR storage type against this SDK pattern. Generic
    /// placeholders unify within one invocation, so T/TArray relationships are
    /// checked rather than treated as independent wildcards.
    /// </summary>
    public bool TryMatch(StorageType actual, UdonAbiParameterMode mode,
        IDictionary<string, StorageType> genericBindings,
        UdonTypeFactRegistry typeFacts, out string reason)
    {
        if (genericBindings == null) throw new ArgumentNullException(nameof(genericBindings));
        if (typeFacts == null) throw new ArgumentNullException(nameof(typeFacts));

        switch (Kind)
        {
            case PatternKind.Exact:
                reason = ExternOperandCompatibility.WhyIncompatible(
                    ExactType.Name, actual.Name, mode, typeFacts);
                return reason == null;

            case PatternKind.GenericParameter:
                if (genericBindings.TryGetValue(GenericName, out var bound))
                {
                    if (bound.Name != actual.Name)
                        reason = $"generic ABI placeholder '{GenericName}' was already bound to "
                                 + $"'{bound}', but received '{actual}'";
                    else
                        reason = null;
                    return bound.Name == actual.Name;
                }
                genericBindings.Add(GenericName, actual);
                reason = null;
                return true;

            case PatternKind.Array:
                const string suffix = "Array";
                if (!actual.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    reason = $"expected an array matching '{this}', got '{actual}'";
                    return false;
                }
                var element = new StorageType(
                    actual.Name.Substring(0, actual.Name.Length - suffix.Length));
                return ElementType.TryMatch(
                    element, mode, genericBindings, typeFacts, out reason);

            default:
                throw new InvalidOperationException($"Unknown ABI type pattern kind: {Kind}");
        }
    }

    public override string ToString() => Kind switch
    {
        PatternKind.Exact => ExactType.Name,
        PatternKind.GenericParameter => GenericName,
        PatternKind.Array => ElementType + "[]",
        _ => throw new InvalidOperationException($"Unknown ABI type pattern kind: {Kind}"),
    };
}

/// <summary>One ordered stack operand in an SDK extern node definition.</summary>
public sealed class UdonAbiParameter
{
    public string Name { get; }
    public UdonAbiType Type { get; }
    public UdonAbiParameterMode Mode { get; }

    public UdonAbiParameter(string name, UdonAbiType type, UdonAbiParameterMode mode)
    {
        Name = name ?? "";
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Mode = mode;
    }
}

/// <summary>
/// Typed prototype for one installed SDK extern. Parameters are in the exact
/// PUSH order consumed by CALL_EXTERN; a value-returning Core call contributes
/// its destination as the final stack operand.
/// </summary>
public sealed class UdonExternPrototype
{
    public string RegisteredName { get; }
    public IReadOnlyList<UdonAbiParameter> Parameters { get; }
    public bool HasTypedParameters { get; }

    public UdonExternPrototype(string registeredName, IEnumerable<UdonAbiParameter> parameters)
        : this(registeredName, parameters, hasTypedParameters: true)
    {
    }

    UdonExternPrototype(string registeredName, IEnumerable<UdonAbiParameter> parameters,
        bool hasTypedParameters)
    {
        RegisteredName = !string.IsNullOrEmpty(registeredName)
            ? registeredName
            : throw new ArgumentException("An extern registry name is required.", nameof(registeredName));
        Parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToArray();
        HasTypedParameters = hasTypedParameters;
    }

    /// <summary>
    /// Legacy flat fixtures contain registry names only. This authority exists
    /// solely for the compiler's isolated test assembly; production constructs
    /// prototypes from UdonNodeDefinition and is always typed.
    /// </summary>
    internal static UdonExternPrototype UntypedFixture(string registeredName)
        => new(registeredName, Array.Empty<UdonAbiParameter>(), hasTypedParameters: false);
}

/// <summary>
/// Immutable typed view of the extern surface registered by the installed Udon
/// SDK. Compiler code submits a semantic <see cref="UdonAbiKey"/>; only this
/// boundary serializes it and returns a <see cref="BoundExtern"/>.
/// </summary>
public sealed class UdonAbiCatalog
{
    internal static readonly UdonAbiCatalog Empty
        = new(Array.Empty<UdonExternPrototype>());

    readonly Dictionary<string, UdonExternPrototype> _externs;
    readonly KeyValuePair<string, UdonTypeFactRegistry.TypeFact>[] _typeFacts;
    readonly UdonTypeFactRegistry.AssignabilityFact[] _assignabilityFacts;

    public UdonAbiCatalog(IEnumerable<UdonExternPrototype> prototypes)
        : this(prototypes, null)
    {
    }

    internal UdonAbiCatalog(IEnumerable<UdonExternPrototype> prototypes,
        IEnumerable<KeyValuePair<string, UdonTypeFactRegistry.TypeFact>> typeFacts,
        IEnumerable<UdonTypeFactRegistry.AssignabilityFact> assignabilityFacts = null)
    {
        if (prototypes == null) throw new ArgumentNullException(nameof(prototypes));
        _externs = new Dictionary<string, UdonExternPrototype>(StringComparer.Ordinal);
        foreach (var prototype in prototypes)
        {
            if (prototype == null) continue;
            if (!_externs.TryAdd(prototype.RegisteredName, prototype))
                throw new InvalidOperationException(
                    $"Duplicate Udon extern prototype '{prototype.RegisteredName}'.");
        }
        _typeFacts = typeFacts?.ToArray()
            ?? Array.Empty<KeyValuePair<string, UdonTypeFactRegistry.TypeFact>>();
        _assignabilityFacts = assignabilityFacts?.ToArray()
            ?? Array.Empty<UdonTypeFactRegistry.AssignabilityFact>();
    }

    internal static UdonAbiCatalog FromNamesForTests(IEnumerable<string> externNames)
        => new((externNames ?? throw new ArgumentNullException(nameof(externNames)))
            .Where(IsExternRegistryName)
            .Select(UdonExternPrototype.UntypedFixture));

    internal static bool IsExternRegistryName(string registeredName)
    {
        if (string.IsNullOrWhiteSpace(registeredName)) return false;
        var memberBoundary = registeredName.IndexOf(
            ".__", StringComparison.Ordinal);
        return memberBoundary > 0
               && memberBoundary + 3 < registeredName.Length;
    }

    public bool Contains(UdonAbiKey key)
        => _externs.ContainsKey(key.ToRegistryName());

    public BoundExtern Require(UdonAbiKey key)
    {
        var registryName = key.ToRegistryName();
        if (!_externs.TryGetValue(registryName, out var prototype))
            throw new NotSupportedException(
                $"Udon extern '{registryName}' is not registered by the installed SDK.");
        return new BoundExtern(key, prototype);
    }

    public IReadOnlyCollection<string> ExternNames => _externs.Keys;

    /// <summary>Seed one compilation's mutable registry from the immutable SDK ABI snapshot. Source
    /// lowering then appends Roslyn facts to the same session-owned registry.</summary>
    internal void SeedTypeFacts(UdonTypeFactRegistry target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        target.Import(
            _typeFacts, _assignabilityFacts, "installed SDK ABI catalog");
    }
}

/// <summary>An exact extern proven to exist in a typed SDK ABI catalog.</summary>
public sealed class BoundExtern
{
    public UdonAbiKey Key { get; }
    public UdonExternPrototype Prototype { get; }

    /// <summary>The serialized registry name, which is the prototype's own: the catalog
    /// dictionary is keyed on it, so it is the string that matched this key.</summary>
    public string Text => Prototype.RegisteredName;

    internal BoundExtern(UdonAbiKey key, UdonExternPrototype prototype)
    {
        Key = key;
        Prototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
    }

    public override string ToString() => Text;
}
