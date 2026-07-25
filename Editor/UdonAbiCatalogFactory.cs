using System;
using System.Collections.Generic;
using System.Linq;
using VRC.Udon.Graph;

/// <summary>
/// Editor boundary that snapshots the installed SDK's node definitions into
/// compiler-owned immutable ABI prototypes. The compiler never reverse-parses
/// extern signature strings to recover operand types.
/// </summary>
static class UdonAbiCatalogFactory
{
    public static UdonAbiCatalog Create(IEnumerable<UdonNodeDefinition> definitions)
    {
        if (definitions == null) throw new ArgumentNullException(nameof(definitions));
        var typeFacts = new UdonTypeFactRegistry();
        var prototypes = definitions
            // GetNodeDefinitions also returns graph-control, event, variable,
            // and constant nodes. Those are not CALL_EXTERN targets and may
            // intentionally carry typeless ports.
            .Where(definition => definition != null
                                 && UdonAbiCatalog.IsExternRegistryName(
                                     definition.fullName))
            .Select(definition => CreatePrototype(definition, typeFacts))
            .ToArray();
        return new UdonAbiCatalog(
            prototypes, typeFacts.Snapshot());
    }

    static UdonExternPrototype CreatePrototype(UdonNodeDefinition definition,
        UdonTypeFactRegistry typeFacts)
    {
        _ = ToAbiTypeNameAndRecord(definition.type, typeFacts);
        var parameters = definition.parameters.Select(parameter =>
            new UdonAbiParameter(
                parameter.name,
                ToAbiTypePattern(
                    parameter.type, definition.fullName, parameter.name,
                    typeFacts),
                parameter.parameterType switch
                {
                    UdonNodeParameter.ParameterType.IN => UdonAbiParameterMode.In,
                    UdonNodeParameter.ParameterType.OUT => UdonAbiParameterMode.Out,
                    UdonNodeParameter.ParameterType.IN_OUT => UdonAbiParameterMode.InOut,
                    _ => throw new InvalidOperationException(
                        $"Unknown SDK parameter mode '{parameter.parameterType}' "
                        + $"on extern '{definition.fullName}'."),
                }));
        return new UdonExternPrototype(definition.fullName, parameters);
    }

    static UdonAbiType ToAbiTypePattern(Type type,
        string registeredName, string parameterName,
        UdonTypeFactRegistry typeFacts)
    {
        if (type == null)
            throw new InvalidOperationException(
                $"Installed SDK extern '{registeredName}' parameter "
                + $"'{parameterName}' has no CLR type.");
        if (type.IsByRef) type = type.GetElementType();
        if (type.IsArray)
            return UdonAbiType.Array(ToAbiTypePattern(
                type.GetElementType(), registeredName, parameterName,
                typeFacts));
        if (type.IsGenericParameter)
            return UdonAbiType.Generic(type.Name);
        return UdonAbiType.Exact(ToAbiTypeNameAndRecord(type, typeFacts));
    }

    static string ToAbiTypeNameAndRecord(Type type,
        UdonTypeFactRegistry typeFacts)
    {
        var name = ToAbiTypeName(type);
        typeFacts.Record(name, type);
        return name;
    }

    static string ToAbiTypeName(Type type)
        => ExternResolver.GetUdonTypeName(type);
}
