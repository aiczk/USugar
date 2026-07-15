using System.IO;

namespace USugar.Tests;

/// <summary>
/// Resolves source-tree directories from the running test assembly location, robust to
/// the bin/Debug/net9.0 nesting depth by walking up to the directory containing the csproj.
/// </summary>
public static class TestPaths
{
    public static string TestsDir { get; } = Resolve();

    static string Resolve()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(TestPaths).Assembly.Location));
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "USugarTests.csproj")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("Could not locate USugarTests.csproj from assembly location.");
        return dir.FullName;
    }

    /// <summary>Committed baseline UASM snapshots: Editor~/Tests/Golden/Snapshots.</summary>
    public static string SnapshotDir => Path.Combine(TestsDir, "Golden", "Snapshots");

    /// <summary>Committed recursion-facet census fixtures (M5b scaffolding): Editor~/Tests/Golden/FacetCensus.</summary>
    public static string FacetCensusDir => Path.Combine(TestsDir, "Golden", "FacetCensus");

    /// <summary>The compiler source under audit: USugar/Editor/Compiler.</summary>
    public static string EditorCompilerDir =>
        Path.GetFullPath(Path.Combine(TestsDir, "..", "..", "Editor", "Compiler"));
}
