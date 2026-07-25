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
/// Independent evidence supplied by the installed SDK for one Udon type.
/// These capabilities are deliberately not collapsed into "registered":
/// Type_* nodes, extern owners, and extern operands are different sets.
/// </summary>
[Flags]
public enum UdonTypeCapabilities
{
    None = 0,
    TypeNode = 1 << 0,
    ExternOwner = 1 << 1,
    ExternOperand = 1 << 2,
}

/// <summary>
/// Immutable installed-SDK evidence for one canonical Udon type identity.
/// Representation facts are optional because the public node registry does
/// not expose a CLR type for every Type_* entry.
/// </summary>
public sealed class UdonTypeDescriptor
{
    readonly UdonTypeFactRegistry.TypeFact? _fact;

    public UdonTypeId Id { get; }
    public UdonTypeCapabilities Capabilities { get; }
    public bool HasTypeNode
        => (Capabilities & UdonTypeCapabilities.TypeNode) != 0;
    public bool AppearsAsExternOwner
        => (Capabilities & UdonTypeCapabilities.ExternOwner) != 0;
    public bool AppearsAsExternOperand
        => (Capabilities & UdonTypeCapabilities.ExternOperand) != 0;
    public bool? IsEnum => _fact?.IsEnum;
    public bool? IsValueType => _fact?.IsValueType;

    internal UdonTypeDescriptor(UdonTypeId id,
        UdonTypeCapabilities capabilities,
        UdonTypeFactRegistry.TypeFact? fact)
    {
        Id = id;
        Capabilities = capabilities;
        _fact = fact;
    }

    internal bool TryGetFact(out UdonTypeFactRegistry.TypeFact fact)
    {
        if (_fact.HasValue)
        {
            fact = _fact.Value;
            return true;
        }
        fact = default;
        return false;
    }
}

/// <summary>Exact Udon storage type supplied by the installed SDK for one extern stack operand.</summary>
public sealed class UdonAbiType
{
    public StorageType ExactType { get; }

    UdonAbiType(StorageType exactType) => ExactType = exactType;

    public static UdonAbiType Exact(string storageType)
        => new(new StorageType(storageType));

    public bool TryMatch(StorageType actual, UdonAbiParameterMode mode,
        UdonTypeFactRegistry typeFacts,
        out string reason)
    {
        if (typeFacts == null) throw new ArgumentNullException(nameof(typeFacts));
        reason = ExternOperandCompatibility.WhyIncompatible(
            ExactType.Name, actual.Name, mode, typeFacts);
        return reason == null;
    }

    public override string ToString() => ExactType.Name;
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
    readonly Dictionary<UdonTypeId, UdonTypeDescriptor> _types;
    readonly HashSet<UdonTypeFactRegistry.AssignabilityFact>
        _assignability;

    public UdonAbiCatalog(IEnumerable<UdonExternPrototype> prototypes)
        : this(prototypes, null, null)
    {
    }

    internal UdonAbiCatalog(IEnumerable<UdonExternPrototype> prototypes,
        IEnumerable<KeyValuePair<string, UdonTypeFactRegistry.TypeFact>> typeFacts)
        : this(prototypes, typeFacts, null)
    {
    }

    internal UdonAbiCatalog(IEnumerable<UdonExternPrototype> prototypes,
        IEnumerable<KeyValuePair<string, UdonTypeFactRegistry.TypeFact>> typeFacts,
        IEnumerable<string> registeredTypes,
        IEnumerable<UdonTypeFactRegistry.AssignabilityFact>
            assignability = null)
    {
        if (prototypes == null) throw new ArgumentNullException(nameof(prototypes));
        _assignability = new HashSet<
            UdonTypeFactRegistry.AssignabilityFact>(
            assignability
            ?? Array.Empty<UdonTypeFactRegistry.AssignabilityFact>());
        _externs = new Dictionary<string, UdonExternPrototype>(StringComparer.Ordinal);
        foreach (var prototype in prototypes)
        {
            if (prototype == null) continue;
            if (!_externs.TryAdd(prototype.RegisteredName, prototype))
                throw new InvalidOperationException(
                    $"Duplicate Udon extern prototype '{prototype.RegisteredName}'.");
        }

        var evidence = new Dictionary<UdonTypeId,
            (UdonTypeCapabilities Capabilities,
                UdonTypeFactRegistry.TypeFact? Fact)>();

        void Merge(string typeName, UdonTypeCapabilities capabilities,
            UdonTypeFactRegistry.TypeFact? fact = null)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return;
            var id = UdonTypeIdentity.FromCanonicalStorageName(typeName);
            if (!evidence.TryGetValue(id, out var existing))
            {
                evidence.Add(id, (capabilities, fact));
                return;
            }
            if (existing.Fact.HasValue && fact.HasValue
                && !existing.Fact.Value.Equals(fact.Value))
                throw new InvalidOperationException(
                    $"Udon type '{id}' has conflicting installed-SDK facts.");
            evidence[id] = (
                existing.Capabilities | capabilities,
                existing.Fact ?? fact);
        }

        if (registeredTypes != null)
            foreach (var registeredType in registeredTypes)
                Merge(registeredType, UdonTypeCapabilities.TypeNode);

        if (typeFacts != null)
            foreach (var pair in typeFacts)
                Merge(pair.Key, UdonTypeCapabilities.None, pair.Value);

        foreach (var prototype in _externs.Values)
        {
            var ownerBoundary = prototype.RegisteredName.IndexOf(
                ".__", StringComparison.Ordinal);
            if (ownerBoundary > 0)
                Merge(prototype.RegisteredName.Substring(0, ownerBoundary),
                    UdonTypeCapabilities.ExternOwner);
            foreach (var parameter in prototype.Parameters)
                Merge(parameter.Type.ExactType.Name,
                    UdonTypeCapabilities.ExternOperand);
        }

        _types = evidence.ToDictionary(
            pair => pair.Key,
            pair => new UdonTypeDescriptor(
                pair.Key, pair.Value.Capabilities, pair.Value.Fact));
    }

    internal static UdonAbiCatalog FromNamesForTests(IEnumerable<string> externNames)
        => new((externNames ?? throw new ArgumentNullException(nameof(externNames)))
            .Where(IsExternRegistryName)
            .Select(UdonExternPrototype.UntypedFixture));

    internal UdonAbiCatalog WithTestPrototypes(
        IEnumerable<UdonExternPrototype> prototypes)
    {
        if (prototypes == null) throw new ArgumentNullException(nameof(prototypes));
        var additions = prototypes
            .Where(prototype => prototype != null)
            .Where(prototype => !_externs.ContainsKey(prototype.RegisteredName));
        return new UdonAbiCatalog(
            _externs.Values.Concat(additions), TypeFacts, RegisteredTypes,
            AssignabilityFacts);
    }

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
    public bool IsRegisteredType(string udonTypeName)
        => !string.IsNullOrWhiteSpace(udonTypeName)
           && IsRegisteredType(
               UdonTypeIdentity.FromCanonicalStorageName(udonTypeName));
    public bool IsRegisteredType(UdonTypeId id)
        => _types.TryGetValue(id, out var descriptor)
           && descriptor.HasTypeNode;
    public bool TryGetType(UdonTypeId id,
        out UdonTypeDescriptor descriptor)
        => _types.TryGetValue(id, out descriptor);
    internal IReadOnlyCollection<UdonExternPrototype> Prototypes => _externs.Values;
    internal IReadOnlyList<KeyValuePair<string, UdonTypeFactRegistry.TypeFact>> TypeFacts
        => _types.Values
            .Where(descriptor => descriptor.TryGetFact(out _))
            .Select(descriptor =>
            {
                descriptor.TryGetFact(out var fact);
                return new KeyValuePair<string,
                    UdonTypeFactRegistry.TypeFact>(
                    descriptor.Id.Name, fact);
            })
            .ToArray();
    internal IReadOnlyCollection<string> RegisteredTypes
        => _types.Values
            .Where(descriptor => descriptor.HasTypeNode)
            .Select(descriptor => descriptor.Id.Name)
            .ToArray();
    internal IReadOnlyCollection<
        UdonTypeFactRegistry.AssignabilityFact> AssignabilityFacts
        => _assignability;
    public IReadOnlyCollection<UdonTypeDescriptor> Types => _types.Values;

    internal IReadOnlyCollection<string> GetAssignableOwners(
        string actualOwner)
    {
        if (string.IsNullOrWhiteSpace(actualOwner))
            return Array.Empty<string>();
        var actual = UdonTypeIdentity.FromCanonicalStorageName(
            actualOwner);
        return _assignability
            .Where(fact => fact.Actual == actual)
            .Select(fact => fact.Expected.Name)
            .Append(actualOwner)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(owner => owner, StringComparer.Ordinal)
            .ToArray();
    }

    internal bool IsAssignableOwner(
        string actualOwner, string expectedOwner)
    {
        if (string.IsNullOrWhiteSpace(actualOwner)
            || string.IsNullOrWhiteSpace(expectedOwner))
            return false;
        var actual = UdonTypeIdentity.FromCanonicalStorageName(
            actualOwner);
        var expected = UdonTypeIdentity.FromCanonicalStorageName(
            expectedOwner);
        return actual == expected
               || _assignability.Contains(
                   new UdonTypeFactRegistry.AssignabilityFact(
                       actual, expected));
    }

    /// <summary>Seed one compilation's mutable registry from the immutable SDK ABI snapshot. Source
    /// lowering then appends Roslyn facts to the same session-owned registry.</summary>
    internal void SeedTypeFacts(UdonTypeFactRegistry target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        target.Import(TypeFacts, "installed SDK ABI catalog");
        target.ImportAssignability(AssignabilityFacts);
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
