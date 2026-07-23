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
        => new((definitions ?? throw new ArgumentNullException(nameof(definitions)))
            // GetNodeDefinitions also returns graph-control, event, variable,
            // and constant nodes. Those are not CALL_EXTERN targets and may
            // intentionally carry typeless ports.
            .Where(definition => definition != null
                                 && UdonAbiCatalog.IsExternRegistryName(
                                     definition.fullName))
            .Select(CreatePrototype));

    static UdonExternPrototype CreatePrototype(UdonNodeDefinition definition)
    {
        var owner = ToAbiTypeName(definition.type);
        var parameters = definition.parameters.Select(parameter =>
            new UdonAbiParameter(
                parameter.name,
                ToAbiTypePattern(
                    parameter.type, definition.fullName, parameter.name),
                parameter.parameterType switch
                {
                    UdonNodeParameter.ParameterType.IN => UdonAbiParameterMode.In,
                    UdonNodeParameter.ParameterType.OUT => UdonAbiParameterMode.Out,
                    UdonNodeParameter.ParameterType.IN_OUT => UdonAbiParameterMode.InOut,
                    _ => throw new InvalidOperationException(
                        $"Unknown SDK parameter mode '{parameter.parameterType}' "
                        + $"on extern '{definition.fullName}'."),
                }));
        return new UdonExternPrototype(
            definition.fullName, owner, definition.name, parameters);
    }

    static UdonAbiType ToAbiTypePattern(Type type,
        string registeredName, string parameterName)
    {
        if (type == null)
            throw new InvalidOperationException(
                $"Installed SDK extern '{registeredName}' parameter "
                + $"'{parameterName}' has no CLR type.");
        if (type.IsByRef) type = type.GetElementType();
        if (type.IsArray)
            return UdonAbiType.Array(ToAbiTypePattern(
                type.GetElementType(), registeredName, parameterName));
        if (type.IsGenericParameter)
            return UdonAbiType.Generic(type.Name);
        return UdonAbiType.Exact(ToAbiTypeName(type));
    }

    static string ToAbiTypeName(Type type)
    {
        if (type == null) return "";
        if (type == typeof(void)) return StorageTypes.Void.Name;
        if (type.IsByRef) type = type.GetElementType();
        if (type.IsArray)
            return ToAbiTypeName(type.GetElementType()) + "Array";
        if (type.IsGenericParameter) return type.Name;
        return ExternResolver.RemapUdonType(
            ExternResolver.SanitizeTypeName(type.FullName ?? type.Name));
    }
}
