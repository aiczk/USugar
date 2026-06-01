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

        var uasm1 = TestHelper.CompileToUasm(c.Source, c.ClassName);
        var uasm2 = TestHelper.CompileToUasm(c.Source, c.ClassName);
        Assert.True(uasm1 == uasm2,
            $"Nondeterministic UASM for '{name}': two compiles differ. Fix determinism before snapshotting.");

        var canon = UasmCanonicalizer.Canonicalize(uasm1);
        var path = Path.Combine(TestPaths.SnapshotDir, name + ".uasm");

        if (UpdateMode)
        {
            Directory.CreateDirectory(TestPaths.SnapshotDir);
            File.WriteAllText(path, canon);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing baseline '{path}'. Run with UPDATE_SNAPSHOTS=1 to capture.");
        Assert.Equal(File.ReadAllText(path), canon);
    }
}
