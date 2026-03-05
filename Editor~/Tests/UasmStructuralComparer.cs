using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace USugar.Tests;

public static class UasmStructuralComparer
{
    public static HashSet<string> ExtractExterns(string uasm)
    {
        return new HashSet<string>(
            Regex.Matches(uasm, @"EXTERN ""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value));
    }

    public static List<string> ExtractExternList(string uasm)
    {
        return Regex.Matches(uasm, @"EXTERN ""([^""]+)""")
            .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
    }

    public static HashSet<string> ExtractExports(string uasm)
    {
        return new HashSet<string>(
            Regex.Matches(uasm, @"\.export (\S+)")
                .Cast<Match>().Select(m => m.Groups[1].Value));
    }

    public static Dictionary<string, (string type, bool isExport, bool isSync)> ExtractDataSection(string uasm)
    {
        var result = new Dictionary<string, (string, bool, bool)>();
        var dataMatch = Regex.Match(uasm, @"\.data_start(.*?)\.data_end", RegexOptions.Singleline);
        if (!dataMatch.Success) return result;
        var data = dataMatch.Groups[1].Value;
        foreach (Match m in Regex.Matches(data, @"(\S+): %(\S+),"))
        {
            var name = m.Groups[1].Value;
            var type = m.Groups[2].Value;
            var isExport = data.Contains($".export {name}");
            var isSync = Regex.IsMatch(data, $@"\.sync {Regex.Escape(name)},");
            result[name] = (type, isExport, isSync);
        }
        return result;
    }

    public struct ComparisonResult
    {
        public bool ExternsMatch;
        public bool ExportsMatch;
        public bool DataTypesMatch;
        public List<string> Differences;
    }

    public static ComparisonResult Compare(string expected, string actual)
    {
        var diffs = new List<string>();

        var expectedExterns = ExtractExterns(expected);
        var actualExterns = ExtractExterns(actual);
        var externsMatch = expectedExterns.SetEquals(actualExterns);
        if (!externsMatch)
        {
            foreach (var e in expectedExterns.Except(actualExterns))
                diffs.Add($"Missing extern: {e}");
            foreach (var e in actualExterns.Except(expectedExterns))
                diffs.Add($"Extra extern: {e}");
        }

        var expectedExports = ExtractExports(expected);
        var actualExports = ExtractExports(actual);
        var exportsMatch = expectedExports.SetEquals(actualExports);
        if (!exportsMatch)
        {
            foreach (var e in expectedExports.Except(actualExports))
                diffs.Add($"Missing export: {e}");
            foreach (var e in actualExports.Except(expectedExports))
                diffs.Add($"Extra export: {e}");
        }

        var expectedData = ExtractDataSection(expected);
        var actualData = ExtractDataSection(actual);
        bool dataMatch = true;
        var exportedExpected = expectedData.Where(kv => kv.Value.isExport).ToDictionary(kv => kv.Key, kv => kv.Value);
        var exportedActual = actualData.Where(kv => kv.Value.isExport).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var kv in exportedExpected)
        {
            if (!exportedActual.TryGetValue(kv.Key, out var actualVal))
            {
                diffs.Add($"Missing exported var: {kv.Key}");
                dataMatch = false;
            }
            else if (kv.Value.type != actualVal.type)
            {
                diffs.Add($"Type mismatch for {kv.Key}: expected {kv.Value.type}, got {actualVal.type}");
                dataMatch = false;
            }
        }

        return new ComparisonResult
        {
            ExternsMatch = externsMatch,
            ExportsMatch = exportsMatch,
            DataTypesMatch = dataMatch,
            Differences = diffs,
        };
    }
}
