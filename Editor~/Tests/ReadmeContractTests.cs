using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

public class ReadmeContractTests
{
    [Fact]
    public void Scoreboard_CompilesAsDocumented()
    {
        var readmePath = Path.GetFullPath(Path.Combine(TestPaths.TestsDir, "..", "..", "README.md"));
        var readme = File.ReadAllText(readmePath);
        var block = Regex.Match(readme, @"```csharp\s*\r?\n(?<source>.*?)\r?\n```",
            RegexOptions.Singleline);

        Assert.True(block.Success, "README must contain a csharp example");
        TestHelper.CompileToUasm(block.Groups["source"].Value, "Scoreboard");
    }
}
