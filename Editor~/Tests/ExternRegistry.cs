using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace USugar.Tests;

public static class ExternRegistry
{
    static readonly HashSet<string> ValidExterns = LoadRegistry();
    static readonly HashSet<string> ValidTypePrefixes =
        new(ValidExterns.Select(TestHelper.RegistryOwner));
    static readonly HashSet<string> ValidRegisteredTypes = new(
        ValidExterns
            .Where(name => name.StartsWith("Type_", StringComparison.Ordinal))
            .Select(name => name.Substring("Type_".Length)),
        StringComparer.Ordinal);

    static HashSet<string> LoadRegistry()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "udon_extern_registry.txt");
        return new HashSet<string>(File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    public static bool IsValid(string externName) => ValidExterns.Contains(externName);
    public static IReadOnlyCollection<string> All => ValidExterns;
    public static IReadOnlyCollection<string> RegisteredTypes => ValidRegisteredTypes;

    public static UdonAbiCatalog LoadSdkTypedCatalog()
    {
        var path = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Fixtures",
            "udon_abi_registry.tsv");
        return UdonAbiSnapshotCodec.Decode(File.ReadAllLines(path));
    }

    public static UdonAbiCatalog LoadTypedCatalog()
        => LoadSdkTypedCatalog().WithTestPrototypes(SyntheticTestPrototypes());

    static IEnumerable<UdonExternPrototype> SyntheticTestPrototypes()
    {
        const string resource = "VRCTestStubsDisposableResource";
        yield return Prototype(
            resource + ".__ctor____" + resource,
            Parameter("result", resource, UdonAbiParameterMode.Out));
        yield return Prototype(
            resource + ".__Dispose__SystemVoid",
            Parameter("instance", resource, UdonAbiParameterMode.In));
        yield return Prototype(
            resource + ".__get_Value__SystemInt32",
            Parameter("instance", resource, UdonAbiParameterMode.In),
            Parameter("result", "SystemInt32", UdonAbiParameterMode.Out));
        yield return Prototype(
            resource + ".__set_Value__SystemInt32__SystemVoid",
            Parameter("instance", resource, UdonAbiParameterMode.In),
            Parameter("value", "SystemInt32", UdonAbiParameterMode.In));
    }

    static UdonExternPrototype Prototype(string name, params UdonAbiParameter[] parameters)
        => new(name, parameters);

    static UdonAbiParameter Parameter(
        string name, string type, UdonAbiParameterMode mode)
        => new(name, UdonAbiType.Exact(type), mode);

    // CA-M0 B79: registry-truth — does any extern live under this udon type name?
    public static bool HasExternForType(string typeName) => ValidTypePrefixes.Contains(typeName);
}
