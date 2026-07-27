using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class GoldenCorpusSanityTests
{
    public static IEnumerable<object[]> Names()
        => GoldenCorpus.Cases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void CorpusCase_CompilesToUasm(string name)
    {
        var c = GoldenCorpus.ByName(name);
        var uasm = TestHelper.CompileToUasm(c.Source, c.ClassName);
        Assert.False(string.IsNullOrWhiteSpace(uasm), $"Empty UASM for {name}");
    }

    [Fact]
    public void CorpusNames_AreUnique()
    {
        var names = GoldenCorpus.Cases.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void SnapshotDir_HasNoOrphanBaselines()
    {
        var live = GoldenCorpus.Cases.Select(c => c.Name).ToHashSet(System.StringComparer.Ordinal);
        var orphans = System.IO.Directory.EnumerateFiles(TestPaths.SnapshotDir, "*.uasm")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .Where(name => !live.Contains(name))
            .OrderBy(name => name, System.StringComparer.Ordinal)
            .ToList();
        Assert.True(orphans.Count == 0,
            "Baselines with no corpus case (delete them, or restore the case):\n  "
            + string.Join("\n  ", orphans));
    }
}
