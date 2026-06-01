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
}
