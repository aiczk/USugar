using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Stable text boundary for the installed SDK's typed extern surface. The snapshot contains only
/// node-definition metadata: registered Udon types, extern names, ordered stack operands,
/// operand modes/types, and representation facts. It contains no VM implementation.
/// </summary>
public static class UdonAbiSnapshotCodec
{
    const string Header =
        "# USugar typed Udon ABI snapshot v2: R<TAB>registeredType; "
        + "T<TAB>name<TAB>enum<TAB>value; "
        + "E<TAB>registeredName<TAB>count<TAB>(name<TAB>mode<TAB>E:type)*";
    const string SourceHeader =
        "# Source: UdonEditorManager.Instance.GetNodeDefinitions(); "
        + "regenerate via USugarCompiler.DumpAbiRegistry.";

    public static IReadOnlyList<string> Encode(UdonAbiCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        var lines = new List<string> { Header, SourceHeader };
        foreach (var registeredType in catalog.RegisteredTypes
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            ValidateCell(registeredType, "registered Udon type name");
            lines.Add("R\t" + registeredType);
        }
        foreach (var pair in catalog.TypeFacts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ValidateCell(pair.Key, "type fact name");
            lines.Add(string.Join("\t",
                "T",
                pair.Key,
                pair.Value.IsEnum ? "1" : "0",
                pair.Value.IsValueType ? "1" : "0"));
        }

        foreach (var prototype in catalog.Prototypes
                     .OrderBy(prototype => prototype.RegisteredName, StringComparer.Ordinal))
        {
            if (!prototype.HasTypedParameters)
                throw new InvalidOperationException(
                    $"Cannot snapshot untyped extern prototype '{prototype.RegisteredName}'.");
            ValidateCell(prototype.RegisteredName, "extern registry name");
            var fields = new List<string>
            {
                "E",
                prototype.RegisteredName,
                prototype.Parameters.Count.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var parameter in prototype.Parameters)
            {
                ValidateCell(parameter.Name, "ABI parameter name");
                fields.Add(parameter.Name);
                fields.Add(parameter.Mode.ToString());
                fields.Add("E:" + parameter.Type.ExactType.Name);
            }
            lines.Add(string.Join("\t", fields));
        }
        return lines;
    }

    public static UdonAbiCatalog Decode(IEnumerable<string> lines)
    {
        if (lines == null) throw new ArgumentNullException(nameof(lines));
        var prototypes = new List<UdonExternPrototype>();
        var facts = new Dictionary<string, UdonTypeFactRegistry.TypeFact>(StringComparer.Ordinal);
        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#", StringComparison.Ordinal))
                continue;
            var fields = raw.Split('\t');
            switch (fields[0])
            {
                case "R":
                    if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[1]))
                        throw InvalidLine(lineNumber,
                            "registered type must contain one non-empty name");
                    if (!registeredTypes.Add(fields[1]))
                        throw InvalidLine(lineNumber,
                            $"duplicate registered type '{fields[1]}'");
                    break;

                case "T":
                    if (fields.Length != 4)
                        throw InvalidLine(lineNumber, "type fact must contain four fields");
                    if (!TryBit(fields[2], out var isEnum)
                        || !TryBit(fields[3], out var isValueType))
                        throw InvalidLine(lineNumber, "type fact flags must be 0 or 1");
                    if (!facts.TryAdd(fields[1],
                            new UdonTypeFactRegistry.TypeFact(isEnum, isValueType)))
                        throw InvalidLine(lineNumber, $"duplicate type fact '{fields[1]}'");
                    break;

                case "E":
                    if (fields.Length < 3
                        || !int.TryParse(fields[2], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var count)
                        || count < 0)
                        throw InvalidLine(lineNumber, "extern operand count is invalid");
                    if (fields.Length != 3 + count * 3)
                        throw InvalidLine(lineNumber,
                            $"extern declares {count} operand(s) but has {fields.Length - 3} operand fields");
                    var parameters = new UdonAbiParameter[count];
                    for (var i = 0; i < count; i++)
                    {
                        var offset = 3 + i * 3;
                        if (!Enum.TryParse(fields[offset + 1], ignoreCase: false,
                                out UdonAbiParameterMode mode))
                            throw InvalidLine(lineNumber,
                                $"unknown ABI parameter mode '{fields[offset + 1]}'");
                        parameters[i] = new UdonAbiParameter(
                            fields[offset], DecodeType(fields[offset + 2], lineNumber), mode);
                    }
                    prototypes.Add(new UdonExternPrototype(fields[1], parameters));
                    break;

                default:
                    throw InvalidLine(lineNumber, $"unknown row kind '{fields[0]}'");
            }
        }
        return new UdonAbiCatalog(prototypes, facts, registeredTypes);
    }

    static UdonAbiType DecodeType(string encoded, int lineNumber)
    {
        if (encoded == null || encoded.Length < 3 || encoded[1] != ':')
            throw InvalidLine(lineNumber, $"invalid ABI type '{encoded}'");
        var payload = encoded.Substring(2);
        if (payload.Length == 0)
            throw InvalidLine(lineNumber, "ABI type payload is empty");
        if (encoded[0] != 'E')
            throw InvalidLine(lineNumber,
                $"non-concrete ABI type '{encoded}' is not supported");
        return UdonAbiType.Exact(payload);
    }

    static void ValidateCell(string value, string description)
    {
        if (value == null || value.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0)
            throw new InvalidOperationException(
                $"The {description} cannot contain tabs or line breaks.");
    }

    static bool TryBit(string value, out bool result)
    {
        result = value == "1";
        return result || value == "0";
    }

    static FormatException InvalidLine(int lineNumber, string message)
        => new($"Invalid typed Udon ABI snapshot line {lineNumber}: {message}.");
}
