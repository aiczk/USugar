using System.IO;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Guards the non-negotiable Phi-avoidance constraint (design spec §2.1 / §6 gate 1): the
/// deleted SSA design must never be reintroduced. Targets reintroduction-indicating compound
/// identifiers (NOT bare 'Phi', which legitimately appears in design comments explaining
/// Phi-avoidance).
/// </summary>
public class NoSsaResidueTests
{
    static readonly string[] Forbidden =
        { "PhiNode", "VReg", "Mem2Reg", "IsExternOutput", "SsaForm", "SsaBuilder" };

    [Fact]
    public void EditorCompiler_ContainsNoSsaResidue()
    {
        var compilerDir = TestPaths.EditorCompilerDir;
        Assert.True(Directory.Exists(compilerDir), $"Editor/Compiler not found at {compilerDir}");

        var hits = Directory.EnumerateFiles(compilerDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (file: Path.GetFileName(f), no: i + 1, line)))
            .Where(t => Forbidden.Any(w => t.line.Contains(w)))
            .Select(t => $"{t.file}:{t.no}: {t.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, "SSA/Phi residue reintroduced:\n" + string.Join("\n", hits));
    }
}
