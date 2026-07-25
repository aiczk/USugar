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

/// <summary>Exact Udon storage type supplied by the installed SDK for one extern stack operand.</summary>
public sealed class UdonAbiType
{
    public StorageType ExactType { get; }

    UdonAbiType(StorageType exactType) => ExactType = exactType;

    public static UdonAbiType Exact(string storageType)
        => new(new StorageType(storageType));

    public bool TryMatch(StorageType actual, UdonTypeFactRegistry typeFacts,
        out string reason)
    {
        if (typeFacts == null) throw new ArgumentNullException(nameof(typeFacts));
        reason = RawCopyCompatibility.WhyIncompatible(
            ExactType.Name, actual.Name, typeFacts);
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
    readonly KeyValuePair<string, UdonTypeFactRegistry.TypeFact>[] _typeFacts;
    readonly HashSet<string> _registeredTypes;

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
        IEnumerable<string> registeredTypes)
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
        _registeredTypes = new HashSet<string>(
            registeredTypes?.Where(name => !string.IsNullOrWhiteSpace(name))
            ?? Array.Empty<string>(),
            StringComparer.Ordinal);
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
            _externs.Values.Concat(additions), _typeFacts, _registeredTypes);
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
        => udonTypeName != null && _registeredTypes.Contains(udonTypeName);
    internal IReadOnlyCollection<UdonExternPrototype> Prototypes => _externs.Values;
    internal IReadOnlyList<KeyValuePair<string, UdonTypeFactRegistry.TypeFact>> TypeFacts
        => _typeFacts;
    internal IReadOnlyCollection<string> RegisteredTypes => _registeredTypes;

    /// <summary>Seed one compilation's mutable registry from the immutable SDK ABI snapshot. Source
    /// lowering then appends Roslyn facts to the same session-owned registry.</summary>
    internal void SeedTypeFacts(UdonTypeFactRegistry target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        target.Import(_typeFacts, "installed SDK ABI catalog");
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
