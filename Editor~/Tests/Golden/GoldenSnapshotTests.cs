using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Full-text UASM snapshot oracle. For each corpus case: (1) assert two compiles are
/// byte-identical (determinism gate — a failure here is a nondeterminism bug to fix), then
/// (2) canonicalize benign scratch renumbering and compare against the committed baseline.
/// Run with env UPDATE_SNAPSHOTS=1 to (re)capture baselines. This is the regression gate
/// for every Core IR migration phase: byte-identical (modulo canonicalized __intnl_) UASM
/// end-to-end.
/// </summary>
public class GoldenSnapshotTests
{
    static bool UpdateMode =>
        Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";

    public static IEnumerable<object[]> Corpus()
        => GoldenCorpus.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Snapshot_MatchesBaseline(string name)
    {
        var c = GoldenCorpus.ByName(name);

        var uasm1 = TestHelper.CompileToUasm(c.Source, c.ClassName, out var emitter1);
        var uasm2 = TestHelper.CompileToUasm(c.Source, c.ClassName, out var emitter2);
        Assert.True(uasm1 == uasm2,
            $"Nondeterministic UASM for '{name}': two compiles differ. Fix determinism before snapshotting.");

        var consts = ConstantsSection(emitter1.CodeGenResult.Constants);
        Assert.True(consts == ConstantsSection(emitter2.CodeGenResult.Constants),
            $"Nondeterministic constants for '{name}': two compiles differ.");

        var canon = Lf(UasmCanonicalizer.Canonicalize(uasm1 + consts));
        var path = Path.Combine(TestPaths.SnapshotDir, name + ".uasm");

        if (UpdateMode)
        {
            Directory.CreateDirectory(TestPaths.SnapshotDir);
            File.WriteAllText(path, canon);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing baseline '{path}'. Run with UPDATE_SNAPSHOTS=1 to capture.");
        // Normalize line endings on read too: core.autocrlf=true can check baselines out as
        // CRLF on other machines, but the compiler emits LF — compare on a common LF footing.
        Assert.Equal(Lf(File.ReadAllText(path)), canon);
    }

    static string ConstantsSection(List<(string Id, string UdonType, object Value)> constants)
    {
        var sb = new System.Text.StringBuilder("\n# constants\n");
        foreach (var (id, udonType, value) in constants)
            sb.Append("#   ").Append(id).Append(": %").Append(udonType)
              .Append(" = ").Append(Literal(value)).Append('\n');
        return sb.ToString();
    }

    static string Literal(object value)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        switch (value)
        {
            case null: return "null";
            case string s:
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
            case char c: return "'" + c.ToString() + "'";
            case bool b: return b ? "true" : "false";
            case float f: return f.ToString("R", invariant);
            case double d: return d.ToString("R", invariant);
            case decimal m: return m.ToString(invariant);
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, invariant);
            case object[] elements:
                return "[" + string.Join(", ", elements.Select(Literal)) + "]";
            default:
                throw new Xunit.Sdk.XunitException(
                    $"No constant-literal arm for '{value.GetType().FullName}'. Add one — a silent "
                    + "fallback would hide the value from the snapshot oracle.");
        }
    }

    static string Lf(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
}
